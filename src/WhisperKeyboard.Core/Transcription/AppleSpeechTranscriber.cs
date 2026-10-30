using System.Diagnostics;
using System.Text.Json;

namespace WhisperKeyboard.Core.Transcription;

/// <summary>
/// Last-resort backend on macOS: Apple's speech recognizer (SFSpeechRecognizer), on-device
/// when the Mac has the model for the language. It runs in the whisper-keyboard-speech
/// helper built next to the app (src/WhisperKeyboard/macOS/SpeechHelper), because macOS
/// kills a process that uses Speech without permission; that way it only fails this backend.
/// </summary>
public sealed class AppleSpeechTranscriber : ITranscriber
{
    public const string HelperName = "whisper-keyboard-speech";

    private readonly string _helperPath;

    public string Id => TranscriptionSettings.System;
    public string DisplayName => "macOS speech";

    public AppleSpeechTranscriber(string? helperPath = null)
    {
        _helperPath = helperPath ?? Path.Combine(AppContext.BaseDirectory, HelperName);
    }

    /// <summary>Helper exists, Speech Recognition isn't denied and a recognizer is available. Never prompts.</summary>
    public async Task<string?> CheckAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_helperPath))
            return "speech helper not built (needs Xcode Command Line Tools)";

        try
        {
            await RunHelperAsync(cancellationToken, "check");
            return null;
        }
        catch (TranscriberException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>The first use shows macOS's Speech Recognition permission prompt.</summary>
    public async Task<TranscriptionResult> TranscribeAsync(Recording recording, string language, CancellationToken cancellationToken)
    {
        var wavPath = Path.Combine(Path.GetTempPath(), $"whisper-keyboard-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(wavPath, recording.Wav, cancellationToken);
        try
        {
            var reply = await RunHelperAsync(cancellationToken, "transcribe", wavPath, language ?? "");
            if (reply.Text == null)
                throw new TranscriberException("speech helper reply had no text");

            return new TranscriptionResult
            {
                Text = reply.Text,
                Language = reply.Locale?.Split('-', '_')[0] ?? language ?? "",
                Confidence = 1.0
            };
        }
        finally
        {
            try { File.Delete(wavPath); } catch (IOException) { }
        }
    }

    private sealed record HelperReply(string? Text, string? Locale, string? Error);

    private async Task<HelperReply> RunHelperAsync(CancellationToken cancellationToken, params string[] args)
    {
        var startInfo = new ProcessStartInfo(_helperPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new TranscriberException($"couldn't start the speech helper: {ex.Message}", ex);
        }

        // Drain stderr too (Speech logs there) so the helper never blocks on a full pipe.
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch (Exception) { /* already gone */ }
            throw;
        }

        var reply = ParseReply(await stdout);
        await stderr;

        if (reply?.Error is { } error)
            throw new TranscriberException(error);
        if (process.ExitCode != 0 || reply == null)
        {
            // .NET reports a signal as 128 + its number: macOS stops a process that
            // uses Speech without a usage description or permission this way.
            throw new TranscriberException(process.ExitCode > 128
                ? $"speech helper was stopped by macOS (signal {process.ExitCode - 128}); check the Speech Recognition permission"
                : $"speech helper exited with code {process.ExitCode}");
        }
        return reply;
    }

    private static HelperReply? ParseReply(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith('{'));
        if (line == null) return null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            string? Get(string name) =>
                doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            return new HelperReply(Get("text"), Get("locale"), Get("error"));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The offline "system" backend for this OS.</summary>
public static class SystemTranscriber
{
    /// <summary>Apple speech on macOS; Windows speech elsewhere (it reports itself unavailable off Windows).</summary>
    public static ITranscriber ForCurrentPlatform() =>
        OperatingSystem.IsMacOS() ? new AppleSpeechTranscriber() : new SystemSpeechTranscriber();
}
