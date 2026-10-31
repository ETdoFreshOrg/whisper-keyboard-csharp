namespace WhisperKeyboard.Core;

/// <summary>What happens to the text when a push-to-talk press ends.</summary>
public enum PushToTalkMode
{
    /// <summary>Type the text, then press Enter.</summary>
    Submit,
    /// <summary>Type the text only.</summary>
    TypeOnly
}

/// <summary>
/// Push-to-talk key specs: "+"-joined modifier names such as "RightCommand+RightShift".
/// </summary>
public static class PushToTalkKeySpec
{
    /// <summary>Canonical key names, in display order.</summary>
    public static readonly string[] AllKeys =
    {
        "RightShift", "RightCommand", "RightOption", "RightControl",
        "LeftShift", "LeftCommand", "LeftOption", "LeftControl", "Fn"
    };

    /// <summary>
    /// Canonical names of the keys in a spec, in <see cref="AllKeys"/> order.
    /// Accepts aliases (RightAlt, rctrl, RightWin, ...); unknown names are dropped,
    /// so an empty or unrecognised spec yields no keys (push-to-talk off).
    /// </summary>
    public static string[] Parse(string? spec)
    {
        var keys = new HashSet<string>();
        foreach (var raw in (spec ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var key = raw.ToLowerInvariant() switch
            {
                "leftcontrol" or "leftctrl" or "lctrl" => "LeftControl",
                "rightcontrol" or "rightctrl" or "rctrl" => "RightControl",
                "leftshift" or "lshift" => "LeftShift",
                "rightshift" or "rshift" => "RightShift",
                "leftoption" or "leftalt" or "lalt" or "loption" => "LeftOption",
                "rightoption" or "rightalt" or "ralt" or "roption" => "RightOption",
                "leftcommand" or "leftcmd" or "lcmd" or "leftwin" => "LeftCommand",
                "rightcommand" or "rightcmd" or "rcmd" or "rightwin" => "RightCommand",
                "fn" => "Fn",
                _ => null
            };
            if (key != null) keys.Add(key);
        }
        return AllKeys.Where(keys.Contains).ToArray();
    }

    /// <summary>The spec in canonical form, so equivalent specs compare equal.</summary>
    public static string Normalize(string? spec) => string.Join("+", Parse(spec));

    /// <summary>Human-readable form, e.g. "Right Shift + Right Command" (Alt/Win on Windows).</summary>
    public static string Describe(string? spec, bool windows)
    {
        var names = Parse(spec).Select(key =>
        {
            if (key == "Fn") return "Fn";
            var side = key.StartsWith("Right") ? "Right" : "Left";
            var name = key[side.Length..];
            if (windows) name = name switch { "Option" => "Alt", "Command" => "Win", _ => name };
            return $"{side} {name}";
        });
        return string.Join(" + ", names);
    }
}

/// <summary>
/// Turns "which push-to-talk chords are held" into start/stop/mode changes, for a
/// submit chord and a type-only chord that may overlap (e.g. Command+Shift submits,
/// Command+Shift+Option only types). Keys go down one at a time, so the smaller
/// chord usually matches first and recording starts right away. If the type-only
/// chord is held at any point during the press, the whole press is type-only:
/// when in doubt, not submitting is the safer outcome.
/// </summary>
public sealed class PushToTalkChordTracker
{
    public enum Change { None, Started, ModeChanged, Stopped }

    public bool IsActive { get; private set; }
    public PushToTalkMode Mode { get; private set; }
    /// <summary>The press in progress was cancelled; it stays silent until the keys are released.</summary>
    public bool IsCancelled { get; private set; }

    /// <summary>
    /// Cancel the press in progress (Escape). Nothing more is reported for it: no
    /// mode change and no Stopped when the keys come up, so nothing gets typed.
    /// The next press after all keys are released starts fresh.
    /// Returns false when there was no press to cancel.
    /// </summary>
    public bool Cancel()
    {
        if (!IsActive || IsCancelled) return false;
        IsCancelled = true;
        return true;
    }

    /// <summary>Call on every key change with whether each chord is fully held.</summary>
    public Change Update(bool submitHeld, bool typeOnlyHeld)
    {
        if (IsCancelled)
        {
            if (!submitHeld && !typeOnlyHeld)
            {
                IsActive = false;
                IsCancelled = false;
            }
            return Change.None;
        }

        if (!IsActive)
        {
            if (!submitHeld && !typeOnlyHeld) return Change.None;
            IsActive = true;
            Mode = typeOnlyHeld ? PushToTalkMode.TypeOnly : PushToTalkMode.Submit;
            return Change.Started;
        }

        if (!submitHeld && !typeOnlyHeld)
        {
            IsActive = false;
            return Change.Stopped;
        }

        if (typeOnlyHeld && Mode == PushToTalkMode.Submit)
        {
            Mode = PushToTalkMode.TypeOnly;
            return Change.ModeChanged;
        }

        return Change.None;
    }
}

/// <summary>
/// Decides what happens to Escape while a recording can be cancelled. The press
/// that cancels is swallowed, and so are its key repeats and its key-up, so the
/// focused app never sees it (no dialog closes, no Alt+Esc / Win+Esc shortcut
/// fires). When nothing is recording, Escape passes through untouched.
/// </summary>
public sealed class CancelKeyFilter
{
    public enum Decision { PassThrough, Cancel, Swallow }

    /// <summary>
    /// How long after the last swallowed Escape event a key-down still counts as a
    /// repeat of the same press. Key repeats arrive every ~30-100 ms; this only
    /// guards against a lost key-up leaving Escape swallowed for good.
    /// </summary>
    public const long RepeatWindowMs = 1000;

    private bool _swallowing;
    private long _lastSwallowedMs;

    /// <param name="cancelArmed">A push-to-talk or toggle-to-talk recording is running.</param>
    /// <param name="nowMs">A monotonic clock in milliseconds.</param>
    public Decision KeyDown(bool cancelArmed, long nowMs)
    {
        if (_swallowing && nowMs - _lastSwallowedMs <= RepeatWindowMs)
        {
            _lastSwallowedMs = nowMs; // a key repeat of the press that cancelled
            return Decision.Swallow;
        }

        _swallowing = false;
        if (!cancelArmed) return Decision.PassThrough;

        _swallowing = true;
        _lastSwallowedMs = nowMs;
        return Decision.Cancel;
    }

    public Decision KeyUp()
    {
        if (!_swallowing) return Decision.PassThrough;
        _swallowing = false;
        return Decision.Swallow;
    }
}
