using System.Globalization;
using System.Runtime.Versioning;
using System.Speech.Recognition;

namespace WhisperKeyboard.Core.Transcription;

/// <summary>
/// Last-resort offline backend: Windows' built-in speech recognizer (System.Speech
/// dictation). Much less accurate than Whisper, but works with no network.
/// Unavailable on macOS (see <see cref="AppleSpeechTranscriber"/>) and Linux.
/// </summary>
public sealed class SystemSpeechTranscriber : ITranscriber
{
    public string Id => TranscriptionSettings.System;
    public string DisplayName => "Windows speech";

    public Task<string?> CheckAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult<string?>("Windows only");

        try
        {
            return Task.FromResult(FindRecognizer(null) == null
                ? "no Windows speech recognizer installed"
                : null);
        }
        catch (Exception ex)
        {
            return Task.FromResult<string?>($"Windows speech unavailable: {ex.Message}");
        }
    }

    public Task<TranscriptionResult> TranscribeAsync(Recording recording, string language, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new TranscriberException("Windows only");

        return Task.Run(() => Recognize(recording, language, cancellationToken), cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private static RecognizerInfo? FindRecognizer(string? language)
    {
        var installed = SpeechRecognitionEngine.InstalledRecognizers();
        if (installed.Count == 0) return null;

        var wanted = string.IsNullOrWhiteSpace(language) || language == "auto"
            ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            : language;

        return installed.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en")
            ?? installed[0];
    }

    [SupportedOSPlatform("windows")]
    private static TranscriptionResult Recognize(Recording recording, string language, CancellationToken cancellationToken)
    {
        var recognizer = FindRecognizer(language)
            ?? throw new TranscriberException("no Windows speech recognizer installed");

        using var engine = new SpeechRecognitionEngine(recognizer);
        engine.LoadGrammar(new DictationGrammar());
        using var wav = new MemoryStream(recording.Wav);
        engine.SetInputToWaveStream(wav);

        var phrases = new List<string>();
        var done = new ManualResetEventSlim(false);
        Exception? error = null;
        engine.SpeechRecognized += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Result?.Text))
                phrases.Add(e.Result.Text.Trim());
        };
        engine.RecognizeCompleted += (_, e) =>
        {
            error = e.Error;
            done.Set();
        };

        // Multiple mode keeps recognizing phrase after phrase until the stream ends.
        engine.RecognizeAsync(RecognizeMode.Multiple);
        using (cancellationToken.Register(() => engine.RecognizeAsyncCancel()))
        {
            done.Wait(cancellationToken);
        }

        if (error != null)
            throw new TranscriberException($"Windows speech failed: {error.Message}", error);

        return new TranscriptionResult
        {
            Text = string.Join(" ", phrases),
            Language = recognizer.Culture.TwoLetterISOLanguageName,
            Confidence = 1.0
        };
    }
}
