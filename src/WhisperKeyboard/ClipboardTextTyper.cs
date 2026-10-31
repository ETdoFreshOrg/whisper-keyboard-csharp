using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Input.Platform;
using WhisperKeyboard.Core;
using WhisperKeyboard.Core.TextInsertion;

namespace WhisperKeyboard;

/// <summary>
/// Cross-platform text typer using clipboard paste.
/// Uses Avalonia's built-in clipboard support and platform-specific key simulation.
/// </summary>
public class ClipboardTextTyper : ITextTyper, IInsertionBackend
{
    private static readonly TimeSpan ModifierReleaseTimeout = TimeSpan.FromMilliseconds(1000);

    private readonly Config _config;
    private readonly TextProcessor _textProcessor;
    private readonly IClipboard? _clipboard;
    private readonly IFocusedFieldProbe? _probe;
    private string _lastTypedText = "";
    private DateTime _lastTypeTime = DateTime.MinValue;

    /// <summary>The last text could not be inserted after every retry. It is left on
    /// the clipboard; the argument is the text.</summary>
    public event Action<string>? InsertionFailed;

    public ClipboardTextTyper(Config config, IClipboard? clipboard)
    {
        _config = config;
        _textProcessor = new TextProcessor(config);
        _clipboard = clipboard;
        _probe = OperatingSystem.IsWindows() ? new WindowsFocusedFieldProbe()
               : OperatingSystem.IsMacOS() ? new MacOSFocusedFieldProbe()
               : null;

        // Log clipboard state at initialization
        if (_clipboard == null)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] WARNING: Clipboard is NULL - paste mode will fall back to direct typing");
        }
        else
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Clipboard initialized successfully");
        }
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] PasteMode={config.PasteMode}");
    }

    /// <summary>Called when recording starts, so the focused app is ready to be read by
    /// the time the text is inserted (see <see cref="IFocusedFieldProbe.Prepare"/>).</summary>
    public void PrepareForInsert()
    {
        if (_config.VerifyTextInsertion) _probe?.Prepare();
    }

    public Task TypeTextAsync(string text, PushToTalkMode? pushToTalk = null) =>
        TypeTextAsync(text, pushToTalk, explicitRequest: false);

    /// <summary>
    /// Insert the text into the focused field. <paramref name="pushToTalk"/> decides Enter
    /// as described on <see cref="ITextTyper.TypeTextAsync"/>. <paramref name="explicitRequest"/>
    /// is true for push-to-talk and toggle recordings: the user asked for exactly this text,
    /// so the repeat guard against double-fired voice detection doesn't apply.
    /// </summary>
    public async Task TypeTextAsync(string text, PushToTalkMode? pushToTalk, bool explicitRequest)
    {
        if (string.IsNullOrEmpty(text)) return;

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] TypeTextAsync called with: \"{text}\" (push-to-talk: {pushToTalk?.ToString() ?? "no"})");

        // Process and clean text
        // Push-to-talk: the key alone decides about Enter, so exit words are just words.
        var (processedText, shouldEnter) = _textProcessor.ProcessText(text, detectExitWords: pushToTalk == null);
        bool pressEnter = pushToTalk == null
            ? _config.AutoEnter || shouldEnter
            : pushToTalk == PushToTalkMode.Submit;
        text = processedText;

        // Check for duplicate text (within 2 seconds)
        if (!explicitRequest && !string.IsNullOrEmpty(text) && text == _lastTypedText && (DateTime.Now - _lastTypeTime).TotalSeconds < 2)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] SKIPPING duplicate text: \"{text}\" (last typed {(DateTime.Now - _lastTypeTime).TotalSeconds:F1}s ago)");
            return;
        }

        if (!string.IsNullOrEmpty(text))
        {
            _lastTypedText = text;
            _lastTypeTime = DateTime.Now;
        }

        try
        {
            if (!string.IsNullOrEmpty(text))
            {
                bool pasteMode = _config.PasteMode && _clipboard != null;
                Console.WriteLine(pasteMode
                    ? $"[{DateTime.Now:HH:mm:ss}] Using PASTE mode (clipboard available)"
                    : $"[{DateTime.Now:HH:mm:ss}] Using TYPING mode (PasteMode={_config.PasteMode}, clipboard={(_clipboard != null ? "available" : "NULL")}, delayMs={_config.TypingDelayMs}, vkSpace={_config.UseVirtualSpaceKey}, vkSpaceHoldMs={_config.VirtualSpaceHoldMs})");

                var inserter = new VerifiedTextInserter(this, _probe, log: m => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m}"))
                {
                    PasteMode = pasteMode,
                    Verify = _config.VerifyTextInsertion,
                    RetryCount = Math.Max(0, _config.InsertionRetryCount),
                    VerifyTimeout = TimeSpan.FromMilliseconds(Math.Max(200, _config.InsertionVerifyTimeoutMs)),
                };
                var outcome = await inserter.InsertAsync(text);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Insert result: {outcome.Verdict} after {outcome.Attempts.Count} attempt(s) ({string.Join(", ", outcome.Attempts)})");

                if (outcome.Failed)
                {
                    // Leave the text where the user can paste it by hand, and don't press
                    // Enter on a field that never got it.
                    await TrySetClipboardAsync(text);
                    InsertionFailed?.Invoke(text);
                    return;
                }
            }

            if (pressEnter)
            {
                await Task.Delay(50);
                await SimulateEnterAsync();
            }

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] TypeTextAsync completed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ERROR in TypeTextAsync: {ex.Message}");
            throw;
        }
    }

    // IInsertionBackend: the keystroke side of VerifiedTextInserter.

    public Task WaitForModifiersReleasedAsync()
    {
        if (OperatingSystem.IsWindows())
            return WindowsInputSender.WaitForModifiersReleasedAsync(ModifierReleaseTimeout);
        if (OperatingSystem.IsMacOS())
            return MacOSInputSender.WaitForModifiersReleasedAsync(ModifierReleaseTimeout);
        return Task.CompletedTask;
    }

    public async Task<bool> PasteAsync(string text)
    {
        if (!await TrySetClipboardAsync(text))
            return false;

        // Delay to ensure clipboard/pasteboard is fully updated
        await Task.Delay(200);
        await SimulatePasteAsync();
        return true;
    }

    public Task TypeAsync(string text)
    {
        return SimulateTypingAsync(text, _config.TypingDelayMs, _config.UseVirtualSpaceKey, _config.VirtualSpaceHoldMs);
    }

    /// <summary>
    /// Put the text on the clipboard and read it back. Another app (a clipboard manager,
    /// the browser itself) can hold the clipboard open, and then the set quietly fails and
    /// Ctrl/Cmd+V pastes whatever was there before, which in a text box may be nothing.
    /// </summary>
    private async Task<bool> TrySetClipboardAsync(string text)
    {
        if (_clipboard == null) return false;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await _clipboard.SetTextAsync(text);
                var check = await _clipboard.GetTextAsync();
                if (check == text)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Text copied to clipboard ({text.Length} chars)");
                    return true;
                }
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Clipboard read-back didn't match (attempt {attempt})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Clipboard set failed (attempt {attempt}): {ex.Message}");
            }
            await Task.Delay(100);
        }
        return false;
    }

    private static async Task SimulatePasteAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            WindowsInputSender.SendPaste();
            await Task.CompletedTask;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            MacOSInputSender.SendPaste();
            await Task.CompletedTask;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Use xdotool on Linux
            await RunProcessAsync("xdotool", "key ctrl+v");
        }
    }

    private static async Task SimulateEnterAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            WindowsInputSender.SendEnter();
            await Task.CompletedTask;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            MacOSInputSender.SendEnter();
            await Task.CompletedTask;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            await RunProcessAsync("xdotool", "key Return");
        }
    }

    private static async Task SimulateTypingAsync(string text, int delayMs = 0, bool useVirtualSpaceKey = false, int virtualSpaceHoldMs = 5)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            WindowsInputSender.SendText(text, delayMs, useVirtualSpaceKey, virtualSpaceHoldMs);
            await Task.CompletedTask;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            MacOSInputSender.SendText(text);
            await Task.CompletedTask;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // xdotool supports a per-key delay via --delay (in ms)
            var delayArg = delayMs > 0 ? $" --delay {delayMs}" : "";
            await RunProcessAsync("xdotool", $"type{delayArg} -- \"{text}\"");
        }
    }

    private static async Task RunProcessAsync(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                await process.WaitForExitAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Process error: {ex.Message}");
        }
    }

    public void SendBackspace(int count = 1)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            for (int i = 0; i < count; i++)
                MacOSInputSender.SendBackspace();
        }
    }

    public void SendEnter()
    {
        _ = SimulateEnterAsync();
    }
}
