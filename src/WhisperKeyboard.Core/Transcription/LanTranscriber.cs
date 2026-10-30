namespace WhisperKeyboard.Core.Transcription;

/// <summary>
/// ET's own speech-to-text server: the OpenAI-compatible endpoint already set under
/// Settings (API Provider / Model / API Key, default stt.etdofresh.com).
/// </summary>
public sealed class LanTranscriber : ITranscriber
{
    private static readonly TimeSpan ConnectCheckTimeout = TimeSpan.FromSeconds(1);

    private readonly SpeechTranscriber _server;

    public string Id => TranscriptionSettings.Lan;
    public string DisplayName => _server.ServerName;

    public LanTranscriber(SpeechTranscriber server)
    {
        _server = server;
    }

    public Task<string?> CheckAsync(CancellationToken cancellationToken) =>
        _server.CheckReachableAsync(ConnectCheckTimeout, cancellationToken);

    public Task<TranscriptionResult> TranscribeAsync(Recording recording, string language, CancellationToken cancellationToken) =>
        _server.TranscribeRecordingAsync(recording, language, cancellationToken);
}
