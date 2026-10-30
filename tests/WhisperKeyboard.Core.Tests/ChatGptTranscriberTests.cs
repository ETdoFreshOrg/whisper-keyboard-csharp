using System.Net;
using System.Text;
using WhisperKeyboard.Core.Transcription;
using Xunit;

namespace WhisperKeyboard.Core.Tests;

public class ChatGptTranscriberTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wk-tests-" + Guid.NewGuid().ToString("N"));
    private string AuthPath => Path.Combine(_dir, "auth.json");

    public ChatGptTranscriberTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WriteToken(string token) =>
        File.WriteAllText(AuthPath, $$$"""{"OPENAI_API_KEY":null,"tokens":{"access_token":"{{{token}}}","refresh_token":"r"}}""");

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _replies = new();
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = new();
        public Action? OnSend { get; set; }

        public FakeHandler Reply(HttpStatusCode status, string body)
        {
            _replies.Enqueue(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? "" : Encoding.Latin1.GetString(await request.Content.ReadAsByteArrayAsync(cancellationToken));
            Requests.Add((request, body));
            OnSend?.Invoke();
            return _replies.Dequeue()(request);
        }
    }

    [Fact]
    public async Task Sends_ogg_over_http11_with_codex_headers()
    {
        WriteToken("tok-1");
        var handler = new FakeHandler().Reply(HttpStatusCode.OK, """{"text":"hello world"}""");
        using var t = new ChatGptTranscriber(() => AuthPath, handler);

        var result = await t.TranscribeAsync(TestAudio.Clip(), "en", CancellationToken.None);

        Assert.Equal("hello world", result.Text);
        var (req, body) = Assert.Single(handler.Requests);
        Assert.Equal(ChatGptTranscriber.Endpoint, req.RequestUri!.ToString());
        Assert.Equal(HttpVersion.Version11, req.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionExact, req.VersionPolicy);
        Assert.Equal("Bearer tok-1", req.Headers.Authorization!.ToString());
        Assert.Equal("Codex Desktop", string.Join(" ", req.Headers.GetValues("User-Agent")));
        Assert.Equal("Codex Desktop", Assert.Single(req.Headers.GetValues("originator")));
        Assert.Contains("filename=codex.ogg", body);
        Assert.Contains("audio/ogg", body);
        Assert.Contains("name=language", body);
    }

    [Fact]
    public async Task Rereads_auth_file_once_on_401()
    {
        WriteToken("stale");
        var handler = new FakeHandler()
            .Reply(HttpStatusCode.Unauthorized, "{}")
            .Reply(HttpStatusCode.OK, """{"text":"ok"}""");
        handler.OnSend = () => { if (handler.Requests.Count == 1) WriteToken("fresh"); };
        using var t = new ChatGptTranscriber(() => AuthPath, handler);

        var result = await t.TranscribeAsync(TestAudio.Clip(), "en", CancellationToken.None);

        Assert.Equal("ok", result.Text);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("Bearer fresh", handler.Requests[1].Request.Headers.Authorization!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, """{"text":"x"}""")]
    [InlineData(HttpStatusCode.TooManyRequests, "{}")]
    [InlineData(HttpStatusCode.Forbidden, "<html>cloudflare</html>")]
    [InlineData(HttpStatusCode.OK, """{"detail":"no text field"}""")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task Failures_throw_so_the_chain_falls_through(HttpStatusCode status, string body)
    {
        WriteToken("tok");
        var handler = new FakeHandler().Reply(status, body);
        using var t = new ChatGptTranscriber(() => AuthPath, handler);

        await Assert.ThrowsAsync<TranscriberException>(() => t.TranscribeAsync(TestAudio.Clip(), "en", CancellationToken.None));
    }

    [Fact]
    public async Task Empty_text_is_a_success()
    {
        WriteToken("tok");
        var handler = new FakeHandler().Reply(HttpStatusCode.OK, """{"text":""}""");
        using var t = new ChatGptTranscriber(() => AuthPath, handler);

        var result = await t.TranscribeAsync(TestAudio.Clip(), "en", CancellationToken.None);

        Assert.Equal("", result.Text);
    }

    [Fact]
    public async Task Check_reports_missing_sign_in()
    {
        using var t = new ChatGptTranscriber(() => AuthPath, new FakeHandler());
        Assert.NotNull(await t.CheckAsync(CancellationToken.None));

        File.WriteAllText(AuthPath, """{"tokens":{}}""");
        Assert.NotNull(await t.CheckAsync(CancellationToken.None));

        WriteToken("tok");
        Assert.Null(await t.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public void Auth_path_prefers_explicit_then_codex_home()
    {
        Assert.Equal("/x/auth.json", ChatGptTranscriber.ResolveAuthPath("/x/auth.json"));

        var old = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", _dir);
            Assert.Equal(AuthPath, ChatGptTranscriber.ResolveAuthPath(""));
            Environment.SetEnvironmentVariable("CODEX_HOME", null);
            Assert.EndsWith(Path.Combine(".codex", "auth.json"), ChatGptTranscriber.ResolveAuthPath(null));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", old);
        }
    }
}
