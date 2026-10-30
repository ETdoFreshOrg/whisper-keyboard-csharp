using System.Text.Json;
using WhisperKeyboard.Core.Transcription;
using Xunit;

namespace WhisperKeyboard.Core.Tests;

// The fixture test sets a process-wide env var; keep chain tests from running alongside it.
[Collection("env")]
public class TranscriptionChainTests
{
    private sealed class FakeBackend : ITranscriber
    {
        public string Id { get; }
        public string DisplayName => Id;
        public string? SkipReason { get; set; }
        public Func<CancellationToken, Task<TranscriptionResult>> Behaviour { get; set; }
        public int Calls { get; private set; }

        public FakeBackend(string id, string? text = null)
        {
            Id = id;
            Behaviour = _ => text == null
                ? throw new TranscriberException($"{id} broke")
                : Task.FromResult(new TranscriptionResult { Text = text });
        }

        public Task<string?> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(SkipReason);

        public Task<TranscriptionResult> TranscribeAsync(Recording recording, string language, CancellationToken cancellationToken)
        {
            Calls++;
            return Behaviour(cancellationToken);
        }
    }

    private static (TranscriptionChain Chain, TranscriptionSettings Settings) Build(
        params FakeBackend[] backends) => Build(() => DateTime.UtcNow, backends);

    private static (TranscriptionChain Chain, TranscriptionSettings Settings) Build(
        Func<DateTime> clock, params FakeBackend[] backends)
    {
        var settings = new TranscriptionSettings();
        return (new TranscriptionChain(() => settings, () => "en", backends, clock), settings);
    }

    [Fact]
    public async Task First_backend_wins()
    {
        var chatgpt = new FakeBackend("chatgpt", "from chatgpt");
        var lan = new FakeBackend("lan", "from lan");
        var (chain, _) = Build(chatgpt, lan, new FakeBackend("system", "x"));

        var result = await chain.TranscribeAsync(TestAudio.Clip(), CancellationToken.None);

        Assert.Equal("from chatgpt", result.Result.Text);
        Assert.Equal("chatgpt", result.BackendId);
        Assert.Equal("chatgpt", result.Result.Backend);
        Assert.Equal(0, lan.Calls);
    }

    [Fact]
    public async Task Falls_through_failures_and_skips_unusable_backends()
    {
        var chatgpt = new FakeBackend("chatgpt") { SkipReason = "not signed in" };
        var lan = new FakeBackend("lan");
        var system = new FakeBackend("system", "offline text");
        var (chain, _) = Build(chatgpt, lan, system);

        var result = await chain.TranscribeAsync(TestAudio.Clip(), CancellationToken.None);

        Assert.Equal("offline text", result.Result.Text);
        Assert.Equal(0, chatgpt.Calls);
        Assert.Equal(1, lan.Calls);
        Assert.Collection(result.Attempts,
            a => Assert.Equal(AttemptOutcome.Skipped, a.Outcome),
            a => Assert.Equal(AttemptOutcome.Failed, a.Outcome),
            a => Assert.Equal(AttemptOutcome.Succeeded, a.Outcome));
    }

    [Fact]
    public async Task Empty_text_stops_the_chain()
    {
        var lan = new FakeBackend("lan", "should not run");
        var (chain, _) = Build(new FakeBackend("chatgpt", ""), lan, new FakeBackend("system", "x"));

        var result = await chain.TranscribeAsync(TestAudio.Clip(), CancellationToken.None);

        Assert.Equal("", result.Result.Text);
        Assert.Equal(0, lan.Calls);
    }

    [Fact]
    public async Task All_failing_throws_with_one_line_per_backend()
    {
        var (chain, _) = Build(new FakeBackend("chatgpt"), new FakeBackend("lan"), new FakeBackend("system") { SkipReason = "Windows only" });

        var ex = await Assert.ThrowsAsync<AllBackendsFailedException>(() => chain.TranscribeAsync(TestAudio.Clip(), CancellationToken.None));

        Assert.Equal(3, ex.Attempts.Count);
        Assert.Contains("chatgpt: chatgpt broke", ex.Details);
        Assert.Contains("system: skipped, Windows only", ex.Details);
    }

    [Fact]
    public async Task Respects_order_and_enabled_flags()
    {
        var chatgpt = new FakeBackend("chatgpt", "a");
        var lan = new FakeBackend("lan", "b");
        var system = new FakeBackend("system", "c");
        var (chain, settings) = Build(chatgpt, lan, system);
        settings.Order = new List<string> { "system", "lan", "chatgpt" };
        settings.SystemBackend.Enabled = false;

        var result = await chain.TranscribeAsync(TestAudio.Clip(), CancellationToken.None);

        Assert.Equal("lan", result.BackendId);
        Assert.Equal(0, system.Calls);
    }

    [Fact]
    public async Task Nothing_enabled_throws()
    {
        var (chain, settings) = Build(new FakeBackend("chatgpt", "a"), new FakeBackend("lan", "b"), new FakeBackend("system", "c"));
        settings.ChatGptBackend.Enabled = settings.LanBackend.Enabled = settings.SystemBackend.Enabled = false;

        var ex = await Assert.ThrowsAsync<AllBackendsFailedException>(() => chain.TranscribeAsync(TestAudio.Clip(), CancellationToken.None));
        Assert.Empty(ex.Attempts);
    }

    [Fact]
    public async Task Failed_backend_cools_down_and_goes_last()
    {
        var now = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
        var chatgpt = new FakeBackend("chatgpt");
        var lan = new FakeBackend("lan", "lan text");
        var (chain, _) = Build(() => now, chatgpt, lan, new FakeBackend("system", "x"));

        await chain.TranscribeAsync(TestAudio.Clip(), CancellationToken.None);
        Assert.Equal(new[] { "lan", "system", "chatgpt" }, chain.PlannedOrder().Select(b => b.Id));

        // Still cooling: lan is tried first and chatgpt isn't called.
        await chain.TranscribeAsync(TestAudio.Clip(), CancellationToken.None);
        Assert.Equal(1, chatgpt.Calls);

        now = now.AddSeconds(121);
        Assert.Equal(new[] { "chatgpt", "lan", "system" }, chain.PlannedOrder().Select(b => b.Id));
    }

    [Fact]
    public async Task Slow_backend_times_out_and_falls_through()
    {
        var slow = new FakeBackend("chatgpt")
        {
            Behaviour = async ct => { await Task.Delay(Timeout.Infinite, ct); return new TranscriptionResult(); }
        };
        var (chain, settings) = Build(slow, new FakeBackend("lan", "fast"), new FakeBackend("system", "x"));
        settings.ChatGptBackend.TimeoutSeconds = 1;

        var result = await chain.TranscribeAsync(new Recording(Array.Empty<byte>(), 16000, 1), CancellationToken.None);

        Assert.Equal("fast", result.Result.Text);
        Assert.Contains("timed out", result.Attempts[0].Message);
    }

    [Fact]
    public async Task User_cancel_stops_without_falling_through()
    {
        using var cts = new CancellationTokenSource();
        var slow = new FakeBackend("chatgpt")
        {
            Behaviour = async ct => { cts.Cancel(); await Task.Delay(Timeout.Infinite, ct); return new TranscriptionResult(); }
        };
        var lan = new FakeBackend("lan", "nope");
        var (chain, _) = Build(slow, lan, new FakeBackend("system", "x"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chain.TranscribeAsync(TestAudio.Clip(), cts.Token));
        Assert.Equal(0, lan.Calls);
    }

    [Fact]
    public async Task Fixture_env_var_short_circuits()
    {
        var chatgpt = new FakeBackend("chatgpt", "real");
        var (chain, _) = Build(chatgpt, new FakeBackend("lan", "b"), new FakeBackend("system", "c"));
        Environment.SetEnvironmentVariable(TranscriptionChain.FixtureEnvVar, "fixed text");
        try
        {
            var result = await chain.TranscribeAsync(TestAudio.Clip(), CancellationToken.None);
            Assert.Equal("fixed text", result.Result.Text);
            Assert.Equal(TranscriptionChain.FixtureId, result.BackendId);
            Assert.Equal(0, chatgpt.Calls);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TranscriptionChain.FixtureEnvVar, null);
        }
    }

    [Fact]
    public void Normalized_order_drops_unknowns_and_appends_missing()
    {
        var settings = new TranscriptionSettings { Order = new List<string> { "LAN", "bogus", "lan" } };
        Assert.Equal(new[] { "lan", "chatgpt", "system" }, settings.NormalizedOrder());
    }

    [Fact]
    public void Old_config_without_transcription_block_gets_defaults()
    {
        var config = JsonSerializer.Deserialize<Config>("""{"ApiBaseUrl":"https://stt.etdofresh.com","Language":"en"}""")!;
        Assert.Equal(new[] { "chatgpt", "lan", "system" }, config.Transcription.Order);
        Assert.True(config.Transcription.ChatGptBackend.Enabled);
        Assert.Equal(60, config.Transcription.SystemBackend.TimeoutSeconds);

        var roundTrip = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(new Config
        {
            Transcription = new TranscriptionSettings { Order = new List<string> { "lan", "chatgpt", "system" } }
        }))!;
        Assert.Equal(new[] { "lan", "chatgpt", "system" }, roundTrip.Transcription.Order);
    }
}
