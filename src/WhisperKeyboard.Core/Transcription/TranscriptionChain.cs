namespace WhisperKeyboard.Core.Transcription;

public enum AttemptOutcome
{
    Skipped,
    Failed,
    Succeeded
}

/// <summary>What happened with one backend during one chain run.</summary>
public sealed record BackendAttempt(string Id, string Name, AttemptOutcome Outcome, string Message)
{
    public override string ToString() => Outcome switch
    {
        AttemptOutcome.Skipped => $"{Name}: skipped, {Message}",
        AttemptOutcome.Failed => $"{Name}: {Message}",
        _ => $"{Name}: ok"
    };
}

public sealed class ChainResult
{
    public required TranscriptionResult Result { get; init; }
    public required string BackendId { get; init; }
    public required string BackendName { get; init; }
    public required IReadOnlyList<BackendAttempt> Attempts { get; init; }
}

/// <summary>Every enabled backend was skipped or failed. The clip should be kept for Retry.</summary>
public sealed class AllBackendsFailedException : Exception
{
    public IReadOnlyList<BackendAttempt> Attempts { get; }

    public AllBackendsFailedException(IReadOnlyList<BackendAttempt> attempts)
        : base(attempts.Count == 0 ? "no speech-to-text backend is enabled" : "all speech-to-text backends failed")
    {
        Attempts = attempts;
    }

    /// <summary>One line per backend, for a tooltip or log.</summary>
    public string Details => Attempts.Count == 0
        ? Message
        : string.Join(Environment.NewLine, Attempts.Select(a => a.ToString()));
}

/// <summary>
/// Runs the configured backends in order until one returns text.
/// Rules (ported from ETcode's dictation):
/// - a backend whose cheap check says it's unusable is skipped quietly;
/// - each backend has its own timeout under an overall budget, both stretched for long clips;
/// - any failure (timeout, network, non-2xx, missing text) falls through to the next backend;
/// - the first reply that carries a text field wins, even if the text is empty;
/// - a backend that just failed cools down and is tried last until the cooldown ends;
/// - user cancel stops the chain at once.
/// Setting WHISPER_KEYBOARD_TRANSCRIBE_FIXTURE returns that text without calling any backend (tests).
/// </summary>
public sealed class TranscriptionChain
{
    public const string FixtureEnvVar = "WHISPER_KEYBOARD_TRANSCRIBE_FIXTURE";
    public const string FixtureId = "fixture";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(2);

    private readonly Func<TranscriptionSettings> _settings;
    private readonly Func<string> _language;
    private readonly IReadOnlyDictionary<string, ITranscriber> _backends;
    private readonly Func<DateTime> _clock;
    private readonly Dictionary<string, DateTime> _lastFailure = new();
    private readonly object _lock = new();

    public TranscriptionChain(
        Func<TranscriptionSettings> settings,
        Func<string> language,
        IEnumerable<ITranscriber> backends,
        Func<DateTime>? clock = null)
    {
        _settings = settings;
        _language = language;
        _backends = backends.ToDictionary(b => b.Id);
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public ITranscriber? Get(string id) => _backends.TryGetValue(id, out var b) ? b : null;

    /// <summary>Enabled backends in the order they'll be tried right now (cooling-down ones last).</summary>
    public IReadOnlyList<ITranscriber> PlannedOrder()
    {
        var settings = _settings();
        var cooldown = TimeSpan.FromSeconds(Math.Max(0, settings.FailureCooldownSeconds));
        var now = _clock();

        var enabled = settings.NormalizedOrder()
            .Where(id => settings.For(id).Enabled && _backends.ContainsKey(id))
            .Select(id => _backends[id])
            .ToList();

        lock (_lock)
        {
            bool Cooling(ITranscriber b) =>
                _lastFailure.TryGetValue(b.Id, out var at) && now - at < cooldown;
            return enabled.Where(b => !Cooling(b)).Concat(enabled.Where(Cooling)).ToList();
        }
    }

    public async Task<ChainResult> TranscribeAsync(Recording recording, CancellationToken cancellationToken)
    {
        var fixture = Environment.GetEnvironmentVariable(FixtureEnvVar);
        if (fixture != null)
        {
            return new ChainResult
            {
                Result = new TranscriptionResult { Text = fixture, Confidence = 1.0 },
                BackendId = FixtureId,
                BackendName = "test fixture",
                Attempts = new[] { new BackendAttempt(FixtureId, "test fixture", AttemptOutcome.Succeeded, "") }
            };
        }

        var settings = _settings();
        var language = _language();
        var audioSeconds = recording.Duration.TotalSeconds;
        var attempts = new List<BackendAttempt>();

        // Long clips take longer to upload and transcribe: stretch the budgets.
        var total = TimeSpan.FromSeconds(Math.Max(1, settings.TotalTimeoutSeconds) + audioSeconds / 2);
        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalCts.CancelAfter(total);

        foreach (var backend in PlannedOrder())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (totalCts.IsCancellationRequested)
            {
                attempts.Add(new BackendAttempt(backend.Id, backend.DisplayName, AttemptOutcome.Skipped,
                    $"out of time ({total.TotalSeconds:0} s total)"));
                continue;
            }

            var skipReason = await CheckAsync(backend, totalCts.Token, cancellationToken);
            if (skipReason != null)
            {
                Log($"{backend.DisplayName} skipped: {skipReason}");
                attempts.Add(new BackendAttempt(backend.Id, backend.DisplayName, AttemptOutcome.Skipped, skipReason));
                continue;
            }

            var timeout = TimeSpan.FromSeconds(Math.Max(1, settings.For(backend.Id).TimeoutSeconds) + audioSeconds / 4);
            using var backendCts = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token);
            backendCts.CancelAfter(timeout);

            Log($"Trying {backend.DisplayName} ({audioSeconds:F1}s of audio)...");
            try
            {
                var result = await backend.TranscribeAsync(recording, language, backendCts.Token);
                lock (_lock) _lastFailure.Remove(backend.Id);
                attempts.Add(new BackendAttempt(backend.Id, backend.DisplayName, AttemptOutcome.Succeeded, ""));
                result.Backend = backend.DisplayName;
                Log($"{backend.DisplayName} transcribed {result.WordCount} words");
                return new ChainResult
                {
                    Result = result,
                    BackendId = backend.Id,
                    BackendName = backend.DisplayName,
                    Attempts = attempts
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var message = ex switch
                {
                    OperationCanceledException when totalCts.IsCancellationRequested
                        => $"out of time ({total.TotalSeconds:0} s total)",
                    OperationCanceledException => $"timed out after {timeout.TotalSeconds:0} s",
                    TranscriberException te => te.Message,
                    HttpRequestException he => $"network error: {he.Message}",
                    _ => ex.Message
                };
                Log($"{backend.DisplayName} failed: {message}");
                lock (_lock) _lastFailure[backend.Id] = _clock();
                attempts.Add(new BackendAttempt(backend.Id, backend.DisplayName, AttemptOutcome.Failed, message));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new AllBackendsFailedException(attempts);
    }

    /// <summary>
    /// Settings "Test" button: run one backend's check, then send it half a second of
    /// silence. Returns whether it answered, with a one-line message.
    /// </summary>
    public static async Task<(bool Ok, string Message)> TestBackendAsync(
        ITranscriber backend, string language, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var started = DateTime.UtcNow;
        try
        {
            var skip = await backend.CheckAsync(cts.Token);
            if (skip != null)
                return (false, $"Not available: {skip}");

            var silence = new Recording(new byte[Recording.TargetSampleRate * 2 / 2], Recording.TargetSampleRate, 1);
            await backend.TranscribeAsync(silence, language, cts.Token);
            return (true, $"OK, answered in {(DateTime.UtcNow - started).TotalSeconds:F1} s");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, $"Timed out after {timeout.TotalSeconds:0} s");
        }
        catch (Exception ex)
        {
            return (false, $"Failed: {ex.Message}");
        }
    }

    private static async Task<string?> CheckAsync(ITranscriber backend, CancellationToken budget, CancellationToken userCancel)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(budget);
        cts.CancelAfter(CheckTimeout);
        try
        {
            return await backend.CheckAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!userCancel.IsCancellationRequested)
        {
            return "availability check timed out";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.Message;
        }
    }

    private static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
}
