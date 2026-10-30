namespace WhisperKeyboard.Core.Transcription;

/// <summary>
/// One speech-to-text backend in the fallback chain.
/// </summary>
public interface ITranscriber
{
    /// <summary>Stable id used in settings: "chatgpt", "lan" or "system".</summary>
    string Id { get; }

    /// <summary>Short name shown to the user.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Cheap availability check (no audio sent). Returns null when usable,
    /// otherwise a one-line reason the backend is skipped.
    /// </summary>
    Task<string?> CheckAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Transcribe the clip. Returns the result on success (text may be empty).
    /// Throws <see cref="TranscriberException"/> (or any other exception) on failure,
    /// which makes the chain fall through to the next backend.
    /// </summary>
    Task<TranscriptionResult> TranscribeAsync(Recording recording, string language, CancellationToken cancellationToken);
}

/// <summary>A backend failure with a one-line, user-facing message.</summary>
public class TranscriberException : Exception
{
    public TranscriberException(string message, Exception? inner = null) : base(message, inner) { }
}
