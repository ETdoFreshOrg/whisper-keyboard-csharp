namespace WhisperKeyboard.Core;

/// <summary>
/// Platform-agnostic interface for typing/pasting text.
/// Implementations will be platform-specific.
/// </summary>
public interface ITextTyper
{
    /// <summary>
    /// Type or paste the given text. pushToTalk is null for hands-free dictation,
    /// where exit words ("over") and AutoEnter decide whether Enter is pressed.
    /// For push-to-talk only the key decides (Submit presses Enter, TypeOnly
    /// doesn't) and exit words are left in the text as spoken.
    /// </summary>
    Task TypeTextAsync(string text, PushToTalkMode? pushToTalk = null);

    /// <summary>
    /// Send backspace key(s).
    /// </summary>
    void SendBackspace(int count = 1);

    /// <summary>
    /// Send enter/return key.
    /// </summary>
    void SendEnter();
}
