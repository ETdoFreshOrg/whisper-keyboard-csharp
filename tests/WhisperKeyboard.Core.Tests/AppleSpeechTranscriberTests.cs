using WhisperKeyboard.Core.Transcription;
using Xunit;

namespace WhisperKeyboard.Core.Tests;

/// <summary>
/// Drives AppleSpeechTranscriber against fake helper scripts, so the process plumbing is
/// tested without Apple's recognizer or its permission prompt. Unix only (shell scripts).
/// </summary>
public sealed class AppleSpeechTranscriberTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wk-speech-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Helper(string body)
    {
        var path = Path.Combine(_dir, AppleSpeechTranscriber.HelperName);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public async Task Missing_helper_is_skipped()
    {
        var transcriber = new AppleSpeechTranscriber(Path.Combine(_dir, "nope"));

        var reason = await transcriber.CheckAsync(CancellationToken.None);

        Assert.Contains("not built", reason);
    }

    [Fact]
    public async Task Check_passes_when_helper_says_ok()
    {
        if (OperatingSystem.IsWindows()) return;
        var transcriber = new AppleSpeechTranscriber(Helper(
            """[ "$1" = check ] && echo '{"ok":true,"locale":"en-US","authorization":"notDetermined"}'"""));

        Assert.Null(await transcriber.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Check_reports_the_helpers_error()
    {
        if (OperatingSystem.IsWindows()) return;
        var transcriber = new AppleSpeechTranscriber(Helper(
            """echo 'Speech logs noise here' >&2; echo '{"error":"Speech Recognition permission is off"}'; exit 1"""));

        Assert.Equal("Speech Recognition permission is off", await transcriber.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Transcribe_passes_a_wav_and_language_then_deletes_the_file()
    {
        if (OperatingSystem.IsWindows()) return;
        // Echo back the WAV path, its RIFF magic and the language so the test can see them.
        var transcriber = new AppleSpeechTranscriber(Helper(
            """[ "$1" = transcribe ] || exit 2; echo "{\"text\":\"$2|$(head -c 4 "$2")|$3\",\"locale\":\"en-US\"}" """));

        var result = await transcriber.TranscribeAsync(TestAudio.Clip(), "en", CancellationToken.None);

        var parts = result.Text.Split('|');
        Assert.Equal("RIFF", parts[1]);
        Assert.Equal("en", parts[2]);
        Assert.False(File.Exists(parts[0]));
        Assert.Equal("en", result.Language);
    }

    [Fact]
    public async Task Empty_text_is_a_valid_answer()
    {
        if (OperatingSystem.IsWindows()) return;
        var transcriber = new AppleSpeechTranscriber(Helper("""echo '{"text":"","locale":"en-US"}'"""));

        var result = await transcriber.TranscribeAsync(TestAudio.Clip(), "en", CancellationToken.None);

        Assert.Equal("", result.Text);
    }

    [Fact]
    public async Task Helper_killed_by_macOS_is_a_backend_failure()
    {
        if (OperatingSystem.IsWindows()) return;
        var transcriber = new AppleSpeechTranscriber(Helper("kill -9 $$"));

        var ex = await Assert.ThrowsAsync<TranscriberException>(
            () => transcriber.TranscribeAsync(TestAudio.Clip(), "en", CancellationToken.None));

        Assert.Contains("stopped by macOS (signal 9)", ex.Message);
    }

    [Fact]
    public async Task Garbage_output_is_a_backend_failure()
    {
        if (OperatingSystem.IsWindows()) return;
        var transcriber = new AppleSpeechTranscriber(Helper("echo not json; exit 3"));

        var ex = await Assert.ThrowsAsync<TranscriberException>(
            () => transcriber.TranscribeAsync(TestAudio.Clip(), "en", CancellationToken.None));

        Assert.Equal("speech helper exited with code 3", ex.Message);
    }

    [Fact]
    public async Task Cancel_kills_the_helper()
    {
        if (OperatingSystem.IsWindows()) return;
        var pidFile = Path.Combine(_dir, "pid");
        var transcriber = new AppleSpeechTranscriber(Helper($"echo $$ > '{pidFile}'; exec sleep 30"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transcriber.TranscribeAsync(TestAudio.Clip(), "en", cts.Token));

        var pid = int.Parse(File.ReadAllText(pidFile).Trim());
        Assert.True(SpinWait.SpinUntil(() => !IsRunning(pid), TimeSpan.FromSeconds(5)));
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Fact]
    public void Platform_picks_the_matching_system_backend()
    {
        var backend = SystemTranscriber.ForCurrentPlatform();

        Assert.Equal(TranscriptionSettings.System, backend.Id);
        Assert.Equal(OperatingSystem.IsMacOS() ? "macOS speech" : "Windows speech", backend.DisplayName);
    }
}
