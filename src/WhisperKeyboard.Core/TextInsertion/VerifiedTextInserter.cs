namespace WhisperKeyboard.Core.TextInsertion;

public enum InsertMethod { Paste, Type }

/// <summary>Reads the field that currently has keyboard focus. Platform-specific.</summary>
public interface IFocusedFieldProbe
{
    /// <summary>Null when no focused element can be found at all.</summary>
    Task<FieldSnapshot?> CaptureAsync();

    /// <summary>Gets the focused app ready to be read, ahead of the insert. Called when
    /// recording starts, because a Chromium app takes about two seconds to expose its
    /// web content after being asked.</summary>
    void Prepare() { }
}

/// <summary>The keystroke side of inserting text. Platform-specific.</summary>
public interface IInsertionBackend
{
    /// <summary>Waits (bounded) until no modifier key is held, so Ctrl/Cmd+V or typed
    /// characters aren't combined with still-held push-to-talk keys.</summary>
    Task WaitForModifiersReleasedAsync();

    /// <summary>Puts the text on the clipboard, confirms it's there, then sends paste.
    /// Returns false (without sending paste) when the clipboard didn't take the text.</summary>
    Task<bool> PasteAsync(string text);

    Task TypeAsync(string text);
}

public record InsertionOutcome(LandingVerdict Verdict, IReadOnlyList<InsertMethod> Attempts)
{
    /// <summary>True only when every attempt was checked and none arrived.</summary>
    public bool Failed => Verdict == LandingVerdict.NotLanded;
}

/// <summary>
/// Inserts text, checks it reached the focused field, and retries when the field is
/// provably unchanged. Paste mode retries a paste, then falls back to typing; typing
/// mode retries typing. An unreadable field is never retried, so a doubtful check
/// can't insert the text twice.
/// </summary>
public class VerifiedTextInserter
{
    private readonly IInsertionBackend _backend;
    private readonly IFocusedFieldProbe? _probe;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly Action<string> _log;

    public bool PasteMode { get; set; } = true;
    public bool Verify { get; set; } = true;
    public int RetryCount { get; set; } = 2;
    public TimeSpan VerifyTimeout { get; set; } = TimeSpan.FromMilliseconds(1500);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    public VerifiedTextInserter(IInsertionBackend backend, IFocusedFieldProbe? probe,
        Func<TimeSpan, Task>? delay = null, Action<string>? log = null)
    {
        _backend = backend;
        _probe = probe;
        _delay = delay ?? (t => Task.Delay(t));
        _log = log ?? (_ => { });
    }

    /// <summary>The method used for each attempt, first attempt included.</summary>
    public static IReadOnlyList<InsertMethod> Plan(bool pasteMode, int retryCount)
    {
        var plan = new List<InsertMethod>();
        for (int i = 0; i <= Math.Max(0, retryCount); i++)
            plan.Add(pasteMode && i < 2 ? InsertMethod.Paste : InsertMethod.Type);
        return plan;
    }

    public async Task<InsertionOutcome> InsertAsync(string text)
    {
        var attempts = new List<InsertMethod>();
        var plan = Plan(PasteMode, RetryCount);

        FieldSnapshot? before = null;
        if (Verify && _probe != null)
        {
            before = await SafeCaptureAsync();
            _log(before?.Text == null
                ? "Insert check: focused field not readable - text will be sent once, unchecked"
                : $"Insert check: focused field has {before.Text.Length} chars before insert");
        }

        var verdict = LandingVerdict.Unknown;
        for (int i = 0; i < plan.Count; i++)
        {
            await _backend.WaitForModifiersReleasedAsync();

            var method = plan[i];
            if (method == InsertMethod.Paste && !await _backend.PasteAsync(text))
            {
                _log("Clipboard didn't take the text - typing it instead");
                method = InsertMethod.Type;
            }
            if (method == InsertMethod.Type)
                await _backend.TypeAsync(text);
            attempts.Add(method);

            // Without a readable "before" there's nothing to prove a miss against.
            if (before?.Text == null)
                return new InsertionOutcome(LandingVerdict.Unknown, attempts);

            verdict = await WaitForLandingAsync(before, text);
            _log($"Insert check: attempt {i + 1} ({method}) -> {verdict}");
            if (verdict != LandingVerdict.NotLanded)
                return new InsertionOutcome(verdict, attempts);
        }

        return new InsertionOutcome(verdict, attempts);
    }

    private async Task<LandingVerdict> WaitForLandingAsync(FieldSnapshot before, string text)
    {
        // Poll until the text shows up; browsers apply a paste and update their
        // accessibility tree asynchronously, so one immediate read would be too early.
        var verdict = LandingVerdict.Unknown;
        var waited = TimeSpan.Zero;
        while (true)
        {
            await _delay(PollInterval);
            waited += PollInterval;
            verdict = LandingCheck.Evaluate(before, await SafeCaptureAsync(), text);
            if (verdict == LandingVerdict.Landed || waited >= VerifyTimeout)
                return verdict;
        }
    }

    private async Task<FieldSnapshot?> SafeCaptureAsync()
    {
        try { return await _probe!.CaptureAsync(); }
        catch (Exception ex)
        {
            _log($"Insert check: reading the focused field failed: {ex.Message}");
            return null;
        }
    }
}
