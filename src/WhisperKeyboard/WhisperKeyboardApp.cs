using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using WhisperKeyboard.Core;
using WhisperKeyboard.Core.Transcription;

namespace WhisperKeyboard;

/// <summary>
/// Main application controller that manages the tray icon, recording indicator,
/// settings window, and coordinates audio capture/transcription.
/// </summary>
public class WhisperKeyboardApp : IDisposable
{
    private readonly Config _config;
    private readonly IAudioCapture _audioCapture;
    private readonly SpeechTranscriber _transcriber;
    private readonly ChatGptTranscriber _chatGptTranscriber;
    private readonly TranscriptionChain _transcriptionChain;
    private readonly ClipboardTextTyper _textTyper;
    private readonly TranscriptionHistory _history;
    private readonly RecordingIndicator _recordingIndicator;
    private readonly ToggleToTalkIndicator _toggleToTalkIndicator;
    private readonly GlobalHotkey _globalHotkey;
    private readonly TextProcessor _textProcessor;
    private IPushToTalkHook? _pushToTalkHook;

    private bool _isListening;
    private bool _isPaused;
    private bool _isStandby; // True when wake words enabled and waiting for wake word
    private bool _isTranscribing;
    private bool _isLongRecording;
    private bool _isPushToTalking;
    private bool _wasPushToTalking; // Track if audio came from PTT for post-transcription behavior
    private bool _wasLongRecording; // Track if audio came from long recording for transcription method
    private bool _toggleToTalkAutoStarted; // Toggle-to-Talk auto-started listening; bar stays hidden, stop listening after txn
    private DateTime _ignoreUntil = DateTime.MinValue;
    private bool _disposed;
    private CancellationTokenSource? _transcriptionCts;
    private CancellationTokenSource? _longRecordingCts;
    private CancellationTokenSource? _pttPostRollCts;

    // Stashed audio from a failed transcription, awaiting user Retry/Discard.
    private byte[]? _lastFailedAudio;
    private bool _lastFailedWasLong;
    private bool _lastFailedWasPtt;
    private DateTime _pushToTalkStartedAt;
    private bool _discardNextAudio; // set while a too-short push-to-talk tap is being thrown away

    // Tray icon (defined in App.axaml)
    private NativeMenuItem? _statusMenuItem;
    private NativeMenuItem? _toggleMenuItem;
    private NativeMenuItem? _pauseMenuItem;

    public WhisperKeyboardApp(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _config = Config.Load();
        _history = new TranscriptionHistory();
        _transcriber = new SpeechTranscriber(_config);
        _chatGptTranscriber = new ChatGptTranscriber(
            () => ChatGptTranscriber.ResolveAuthPath(_config.Transcription.ChatGptBackend.CodexAuthPath));
        _transcriptionChain = new TranscriptionChain(
            () => _config.Transcription,
            () => _config.Language,
            new ITranscriber[] { _chatGptTranscriber, new LanTranscriber(_transcriber), SystemTranscriber.ForCurrentPlatform() });
        
        if (OperatingSystem.IsWindows())
        {
            _audioCapture = new NAudioCapture(_config);
        }
        else
        {
            _audioCapture = new OpenALAudioCapture(_config);
        }

        _recordingIndicator = new RecordingIndicator();
        _toggleToTalkIndicator = new ToggleToTalkIndicator();
        _toggleToTalkIndicator.OnToggleClicked += ToggleLongRecording;
        _globalHotkey = new GlobalHotkey();
        _textProcessor = new TextProcessor(_config);

        // Get clipboard from the main window (we'll create a hidden one)
        var clipboard = desktop.MainWindow?.Clipboard;
        _textTyper = new ClipboardTextTyper(_config, clipboard);

        // Setup audio events
        _audioCapture.VolumeChanged += OnVolumeChanged;
        _audioCapture.SpeechDetected += OnSpeechDetected;
        _audioCapture.AudioReady += OnAudioReady;

        // Setup recording indicator events
        _recordingIndicator.OnPauseClicked += () =>
        {
            if (_isPaused || _isStandby)
                ResumeListening();
            else
                PauseListening();
        };
        _recordingIndicator.OnStopClicked += StopListening;
        _recordingIndicator.OnSettingsClicked += () => ShowSettings();
        _recordingIndicator.OnLongRecordClicked += ToggleLongRecording;
        _recordingIndicator.OnHistoryClicked += () => ShowSettings(initialTab: 5);
        _recordingIndicator.OnCalibrateClicked += ShowCalibration;
        _recordingIndicator.OnPinClicked += PinFromPushToTalk;
        _recordingIndicator.OnRetryClicked += RetryFailedTranscription;
        _recordingIndicator.OnDiscardClicked += DiscardFailedTranscription;
        _recordingIndicator.OnCancelClicked += CancelTranscription;
        _recordingIndicator.OnSaveAudioClicked += SaveFailedAudio;

        // Set long record button visibility based on config
        _recordingIndicator.SetLongRecordButtonVisible(false);

        // Register global hotkeys
        RegisterHotkeys();

        // Setup push-to-talk hook
        if (_config.PushToTalkEnabled)
        {
            if (OperatingSystem.IsWindows())
                _pushToTalkHook = new PushToTalkHook(_config.PushToTalkKeys);
            else if (OperatingSystem.IsMacOS())
                _pushToTalkHook = new PushToTalkHookMacOS(_config.PushToTalkKeys);

            if (_pushToTalkHook != null)
            {
                _pushToTalkHook.Started += StartPushToTalk;
                _pushToTalkHook.Stopped += StopPushToTalk;
            }
        }

        // Verify CGEvent posting works (detects Dock-launch permission issue on macOS)
        if (OperatingSystem.IsMacOS())
        {
            MacOSInputSender.VerifyPostingWorks();
        }

        // Start listening automatically if configured
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            Console.WriteLine("API Key not configured. Please set OPENAI_API_KEY or configure in settings.");
            // Show startup notification so user knows the app is running
            _recordingIndicator.ShowStartupNotification();
        }
        else if (_config.StartListeningOnLaunch)
        {
            StartListening();
        }
        else
        {
            // App is idle — show brief startup notification
            _recordingIndicator.ShowStartupNotification();

            // Keep the mic warm so pre-roll audio is available for push-to-talk.
            if (_config.PreRollEnabled)
                _audioCapture.StartMonitoring();
        }
    }

    private void RegisterHotkeys()
    {
        // Toggle recording hotkey
        if (!string.IsNullOrWhiteSpace(_config.ToggleRecordingHotkey))
        {
            _globalHotkey.Register(_config.ToggleRecordingHotkey, ToggleListening);
        }

        // Pause/Resume hotkey
        if (!string.IsNullOrWhiteSpace(_config.PauseResumeHotkey))
        {
            _globalHotkey.Register(_config.PauseResumeHotkey, () =>
            {
                if (_isPaused)
                    ResumeListening();
                else if (_isListening)
                    PauseListening();
            });
        }

        // Open settings hotkey
        if (!string.IsNullOrWhiteSpace(_config.OpenSettingsHotkey))
        {
            _globalHotkey.Register(_config.OpenSettingsHotkey, () => ShowSettings());
        }

        // Long recording hotkey
        if (!string.IsNullOrWhiteSpace(_config.LongRecordHotkey))
        {
            _globalHotkey.Register(_config.LongRecordHotkey, ToggleLongRecording);
        }
    }

    public void SetupTrayMenu(NativeMenu menu)
    {
        menu.Items.Clear();

        _statusMenuItem = new NativeMenuItem("Status: Idle") { IsEnabled = false };
        menu.Items.Add(_statusMenuItem);
        menu.Items.Add(new NativeMenuItemSeparator());

        _toggleMenuItem = new NativeMenuItem("Start Listening");
        _toggleMenuItem.Click += (s, e) => ToggleListening();
        menu.Items.Add(_toggleMenuItem);

        _pauseMenuItem = new NativeMenuItem("Pause") { IsEnabled = false };
        _pauseMenuItem.Click += (s, e) =>
        {
            if (_isPaused) ResumeListening();
            else PauseListening();
        };
        menu.Items.Add(_pauseMenuItem);

        menu.Items.Add(new NativeMenuItemSeparator());

        var transcribeFileItem = new NativeMenuItem("Transcribe File...");
        transcribeFileItem.Click += (s, e) => ShowFileTranscription();
        menu.Items.Add(transcribeFileItem);

        var settingsItem = new NativeMenuItem("Settings...");
        settingsItem.Click += (s, e) => ShowSettings();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new NativeMenuItemSeparator());

        var quitItem = new NativeMenuItem("Quit");
        quitItem.Click += (s, e) => Quit();
        menu.Items.Add(quitItem);
    }

    private void ToggleListening()
    {
        if (_isListening)
            StopListening();
        else
            StartListening();
    }

    public void StartListening()
    {
        if (_isListening) return;

        try
        {
            // Dismiss startup notification if active
            _recordingIndicator.DismissStartupNotification();

            _audioCapture.Start();
            _isListening = true;
            _isPaused = false;
            _isStandby = false;

            UpdateStatus("Listening...");
            _recordingIndicator.ResetToDefaultPosition();
            _recordingIndicator.ShowListening();

            UpdateMenuState();

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Started listening");

            // Optionally begin paused — user must Resume to start VAD.
            if (_config.StartListeningPaused)
            {
                PauseListening();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ERROR starting: {ex.Message}");
        }
    }

    public void StopListening()
    {
        if (!_isListening) return;

        _audioCapture.Stop();
        _isListening = false;
        _isPaused = false;
        _isStandby = false;

        // Cancel any in-flight transcription and pending push-to-talk post-roll
        _transcriptionCts?.Cancel();
        _pttPostRollCts?.Cancel();
        _isPushToTalking = false;

        UpdateStatus("Stopped");
        UpdateMenuState();

        _recordingIndicator.HideCompletely();

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Stopped listening");
    }

    public void PauseListening()
    {
        if (!_isListening || _isPaused || _isStandby) return;

        if (_config.WakeWordsEnabled)
        {
            // Go to Standby (mic stays ON, waiting for wake word)
            _isStandby = true;
            _isPaused = false;

            UpdateStatus("Standby");
            UpdateMenuState();

            _recordingIndicator.SetPauseState(true); // Show play button
            _recordingIndicator.ShowStandby();

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Entered Standby mode");
        }
        else
        {
            // Original pause behavior (mic OFF)
            _audioCapture.Pause();
            _isPaused = true;
            _isStandby = false;

            // Don't cancel transcription - let it complete in background
            // When resumed, it will continue from where it left off

            UpdateStatus("Paused");
            UpdateMenuState();

            _recordingIndicator.SetPauseState(true);
            _recordingIndicator.ShowPaused();

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Paused");
        }
    }

    public void ResumeListening()
    {
        if (!_isListening) return;

        // Handle both Paused (wake words disabled) and Standby (wake words enabled)
        if (!_isPaused && !_isStandby) return;

        if (_isPaused)
        {
            // Resume from Paused (mic was OFF)
            bool wasRecording = _audioCapture.IsSpeechDetected;
            _audioCapture.Resume();
            _isPaused = false;

            _recordingIndicator.SetPauseState(false);

            // Restore the correct state based on what we were doing before pause
            if (wasRecording)
            {
                UpdateStatus("Recording...");
                _recordingIndicator.ShowRecording();
            }
            else
            {
                UpdateStatus("Listening...");
                _recordingIndicator.ShowListening();
            }

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Resumed from pause (was recording: {wasRecording})");
        }
        else if (_isStandby)
        {
            // Resume from Standby to Active (mic was already ON)
            _isStandby = false;

            _recordingIndicator.SetPauseState(false);
            UpdateStatus("Listening...");
            _recordingIndicator.ShowListening();

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Resumed from standby");
        }

        UpdateMenuState();
    }

    private void UpdateStatus(string status)
    {
        if (_statusMenuItem != null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _statusMenuItem.Header = $"Status: {status}";
            });
        }
    }

    private void UpdateMenuState()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_toggleMenuItem != null)
            {
                _toggleMenuItem.Header = _isListening ? "Stop Listening" : "Start Listening";
            }
            if (_pauseMenuItem != null)
            {
                _pauseMenuItem.IsEnabled = _isListening;

                // Update text based on current state
                if (_isPaused || _isStandby)
                {
                    _pauseMenuItem.Header = "Resume";
                }
                else
                {
                    _pauseMenuItem.Header = _config.WakeWordsEnabled ? "Standby" : "Pause";
                }
            }
        });
    }

    private void OnVolumeChanged(object? sender, double volume)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _recordingIndicator.UpdateVolume(volume);
            _toggleToTalkIndicator.UpdateVolume(volume);
        });
    }

    private bool _showingTemporaryStatus;

    private void OnSpeechDetected(object? sender, bool isSpeaking)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (isSpeaking)
            {
                if (_isStandby)
                {
                    // In standby mode, show that we're checking for wake word
                    UpdateStatus("Checking for wake word...");
                    _recordingIndicator.ShowRecordingStandby();
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Speech detected in standby - checking for wake word");
                }
                else
                {
                    UpdateStatus("Recording...");
                    _recordingIndicator.ShowRecording();
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Speech detected - recording started");
                }
            }
            else if (_isListening && !_isPaused && !_isTranscribing && !_showingTemporaryStatus)
            {
                if (_isStandby)
                {
                    UpdateStatus("Standby");
                    _recordingIndicator.ShowStandby();
                }
                else
                {
                    UpdateStatus("Listening...");
                    _recordingIndicator.ShowListening();
                }
            }
        });
    }

    private async void OnAudioReady(object? sender, AudioReadyEventArgs e)
    {
        if (_discardNextAudio)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Discarding push-to-talk tap ({e.AudioData.Length} bytes)");
            return;
        }
        if (_isTranscribing)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Skipping - already transcribing");
            return;
        }
        if (_lastFailedAudio != null)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Skipping - pending failed transcription awaiting user action");
            return;
        }
        if (DateTime.Now < _ignoreUntil)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Skipping - in ignore period");
            return;
        }

        var audioData = e.AudioData;
        var totalDuration = e.TotalDuration;
        var isFromLongRecording = _wasLongRecording;
        var isFromPushToTalk = _wasPushToTalking;
        _wasLongRecording = false;
        _wasPushToTalking = false;

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Audio ready - {audioData.Length} bytes, total duration: {totalDuration.TotalSeconds:F2}s");

        await RunTranscriptionAsync(audioData, isFromLongRecording, isFromPushToTalk);
    }

    private async Task RunTranscriptionAsync(byte[] audioData, bool isFromLongRecording, bool isFromPushToTalk)
    {
        _isTranscribing = true;

        // Pause mic capture during transcription so we don't pick up stray audio
        _audioCapture.Pause();

        // Create a new cancellation token source for this transcription
        _transcriptionCts?.Dispose();
        _transcriptionCts = new CancellationTokenSource();
        var cancellationToken = _transcriptionCts.Token;

        Dispatcher.UIThread.Post(() =>
        {
            UpdateStatus("Transcribing...");
            _recordingIndicator.ShowTranscribing();
        });

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Transcribing {audioData.Length} bytes, long recording: {isFromLongRecording}, PTT: {isFromPushToTalk}, sending to API...");

        bool transcriptionFailed = false;
        try
        {
            // Clips this small hold no speech; drop them rather than spend a request.
            if (audioData.Length < MinClipBytes)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Clip too short ({audioData.Length} bytes) - skipping");
                return;
            }

            // Try each speech-to-text backend in turn (ChatGPT, your server, Windows/macOS speech).
            var recording = new Recording(audioData, _config.SampleRate, _config.Channels);
            var chainResult = await _transcriptionChain.TranscribeAsync(recording, cancellationToken);
            TranscriptionResult? result = chainResult.Result;

            // Check if we were cancelled or stopped before typing
            // Note: paused is OK - transcription should complete if user spoke before pausing
            if (cancellationToken.IsCancellationRequested || !_isListening)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Transcription completed but cancelled/stopped - not typing");
                return;
            }

            if (result != null && !string.IsNullOrWhiteSpace(result.Text))
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Transcribed via {result.Backend}: \"{result.Text}\"");

                // "Repeat" keyword: if user says just "repeat", re-type the previous transcription
                if (IsRepeatKeyword(result.Text))
                {
                    var prev = _history.GetEntries().FirstOrDefault();
                    if (prev != null && !string.IsNullOrWhiteSpace(prev.FullText))
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Repeat keyword detected - reusing previous: \"{prev.FullText}\"");
                        result.Text = prev.FullText;
                    }
                    else
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Repeat keyword detected but no previous transcription available");
                    }
                }

                // Confidence check (skip for long recordings which are more reliable)
                if (!isFromLongRecording && IsLowConfidence(result))
                {
                    ShowLowConfidence();
                }
                // Handle wake word / pause word logic
                else if (_config.WakeWordsEnabled)
                {
                    await HandleWakeWordModeAsync(result);
                }
                else
                {
                    await TypeTranscriptionAsync(result);
                }
            }
            else
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Transcription returned empty result");
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Transcription cancelled");
        }
        catch (Exception ex)
        {
            // Never lose audio: keep the clip until a transcription succeeds or the user discards it.
            var details = ex is AllBackendsFailedException all ? all.Details : ex.Message;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Transcription error: {ex.Message}{Environment.NewLine}{details}");
            transcriptionFailed = true;
            _lastFailedAudio = audioData;
            _lastFailedWasLong = isFromLongRecording;
            _lastFailedWasPtt = isFromPushToTalk;

            var errMsg = ex is AllBackendsFailedException failed
                ? FailureSummary(failed)
                : ex.Message;
            Dispatcher.UIThread.Post(() =>
            {
                UpdateStatus("Transcription failed");
                _recordingIndicator.ShowTranscriptionFailed(errMsg, details);
            });
        }
        finally
        {
            _isTranscribing = false;

            // If failed, keep mic paused and UI in failed state — user will Retry or Discard.
            if (!transcriptionFailed)
            {
                if (isFromPushToTalk)
                {
                    // PTT: stop listening and hide the toolbar after a brief delay
                    await Task.Delay(500);
                    Dispatcher.UIThread.Post(() =>
                    {
                        StopListening();
                    });
                }
                else if (isFromLongRecording && _toggleToTalkAutoStarted)
                {
                    _toggleToTalkAutoStarted = false;
                    await Task.Delay(500);
                    Dispatcher.UIThread.Post(() =>
                    {
                        StopListening();
                    });
                }
                else if (_isListening && !_isPaused)
                {
                    // Resume mic capture and return to listening/standby mode
                    _audioCapture.Resume();

                    await Task.Delay(500);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_isStandby)
                        {
                            UpdateStatus("Standby");
                            _recordingIndicator.ShowStandby();
                        }
                        else
                        {
                            UpdateStatus("Listening...");
                            _recordingIndicator.ShowListening();
                        }
                    });
                }
            }
        }
    }

    private const int MinClipBytes = 200;

    /// <summary>One line for the bar: the first backend that actually failed, else the first skip reason.</summary>
    private static string FailureSummary(AllBackendsFailedException ex)
    {
        var first = ex.Attempts.FirstOrDefault(a => a.Outcome == AttemptOutcome.Failed) ?? ex.Attempts.FirstOrDefault();
        if (first == null) return ex.Message;
        var more = ex.Attempts.Count > 1 ? $" (+{ex.Attempts.Count - 1} more, hover for all)" : "";
        return first + more;
    }

    /// <summary>Write the failed clip to a WAV file and show it in the file manager.</summary>
    private void SaveFailedAudio()
    {
        var audio = _lastFailedAudio;
        if (audio == null) return;

        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(Config.GetConfigPath())!, "Recordings");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"recording-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
            File.WriteAllBytes(path, new Recording(audio, _config.SampleRate, _config.Channels).Wav);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Saved failed recording to {path}");
            UpdateStatus($"Saved {Path.GetFileName(path)}");
            RevealInFileManager(path);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Could not save recording: {ex.Message}");
            UpdateStatus("Could not save audio");
        }
    }

    private static void RevealInFileManager(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", new[] { "-R", path });
            else
                System.Diagnostics.Process.Start("xdg-open", Path.GetDirectoryName(path)!);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Could not open file manager: {ex.Message}");
        }
    }

    private void RetryFailedTranscription()
    {
        if (_lastFailedAudio == null || _isTranscribing) return;

        var audio = _lastFailedAudio;
        var wasLong = _lastFailedWasLong;
        var wasPtt = _lastFailedWasPtt;
        // Clear up-front so RunTranscriptionAsync's finally block can resume the mic on success.
        _lastFailedAudio = null;

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Retrying failed transcription ({audio.Length} bytes)");
        _ = RunTranscriptionAsync(audio, wasLong, wasPtt);
    }

    private void CancelTranscription()
    {
        if (!_isTranscribing) return;

        // Cancelling throws OperationCanceledException inside RunTranscriptionAsync,
        // which is treated as a non-failure — the finally block then runs the normal
        // post-transcription cleanup (StopListening for PTT, resume listening otherwise).
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Transcription cancelled by user");
        _transcriptionCts?.Cancel();
    }

    private void DiscardFailedTranscription()
    {
        if (_lastFailedAudio == null) return;

        var wasPtt = _lastFailedWasPtt;
        _lastFailedAudio = null;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Transcription discarded by user");

        if (wasPtt)
        {
            Dispatcher.UIThread.Post(StopListening);
            return;
        }

        if (_isListening && !_isPaused)
        {
            _audioCapture.Resume();
            Dispatcher.UIThread.Post(() =>
            {
                if (_isStandby)
                {
                    UpdateStatus("Standby");
                    _recordingIndicator.ShowStandby();
                }
                else
                {
                    UpdateStatus("Listening...");
                    _recordingIndicator.ShowListening();
                }
            });
        }
    }

    private static bool IsRepeatKeyword(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var cleaned = new string(text.Where(c => char.IsLetter(c) || char.IsWhiteSpace(c)).ToArray())
            .ToLowerInvariant();
        var normalized = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return normalized == "repeat previous"
            || normalized == "repeat the previous"
            || normalized == "repeat that";
    }

    private bool IsLowConfidence(TranscriptionResult result)
    {
        // Ramped confidence thresholds based on word count:
        //   1-3 words:  strictest  (avg_logprob >= -0.5, no_speech_prob <= 0.4)
        //   4-10 words: moderate   (avg_logprob >= -0.7, no_speech_prob <= 0.5)
        //   11-20 words: lenient   (avg_logprob >= -1.0, no_speech_prob <= 0.7)
        //   21+ words:  floor only (avg_logprob >= -1.5, no_speech_prob <= 0.8)
        // Compression ratio and absolute floor always apply.

        int words = result.WordCount;

        double logProbThreshold;
        double noSpeechThreshold;

        if (words <= 3)
        {
            logProbThreshold = -0.5;
            noSpeechThreshold = 0.4;
        }
        else if (words <= 10)
        {
            // Lerp from strict (-0.5) to moderate (-0.7) over 4-10 words
            double t = (words - 3.0) / 7.0;
            logProbThreshold = -0.5 + t * (-0.7 - (-0.5));
            noSpeechThreshold = 0.4 + t * (0.5 - 0.4);
        }
        else if (words <= 20)
        {
            // Lerp from moderate (-0.7) to lenient (-1.0) over 11-20 words
            double t = (words - 10.0) / 10.0;
            logProbThreshold = -0.7 + t * (-1.0 - (-0.7));
            noSpeechThreshold = 0.5 + t * (0.7 - 0.5);
        }
        else
        {
            // Floor: most lenient, but still filters garbage
            logProbThreshold = -1.5;
            noSpeechThreshold = 0.8;
        }

        double compressionThreshold = 2.4;

        if (result.AvgLogProb < logProbThreshold)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Low confidence (avg_logprob {result.AvgLogProb:F3} < {logProbThreshold:F2}, words={words}): \"{result.Text}\"");
            return true;
        }

        if (result.NoSpeechProb > noSpeechThreshold)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Low confidence (no_speech_prob {result.NoSpeechProb:F3} > {noSpeechThreshold:F2}, words={words}): \"{result.Text}\"");
            return true;
        }

        if (result.CompressionRatio > compressionThreshold)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Low confidence (compression_ratio {result.CompressionRatio:F2} > {compressionThreshold}, words={words}): \"{result.Text}\"");
            return true;
        }

        return false;
    }

    private void ShowLowConfidence()
    {
        Dispatcher.UIThread.Post(async () =>
        {
            _showingTemporaryStatus = true;
            _recordingIndicator.ShowLowConfidence();
            await Task.Delay(1000);
            _showingTemporaryStatus = false;
        });
    }

    private async Task HandleWakeWordModeAsync(TranscriptionResult result)
    {
        var text = result.Text;

        if (_isStandby)
        {
            // In Standby mode - check for wake word (no minimum duration check)
            var (isWakeWord, remainingText) = _textProcessor.CheckWakeWord(text);

            if (isWakeWord)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Wake word detected! Transitioning to Active mode.");

                // Transition to Active
                _isStandby = false;
                Dispatcher.UIThread.Post(() =>
                {
                    _recordingIndicator.SetPauseState(false);
                    UpdateStatus("Listening...");
                    _recordingIndicator.ShowListening();
                    UpdateMenuState();
                });

                // If there's remaining text after the wake word, type it
                if (!string.IsNullOrWhiteSpace(remainingText))
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Typing remaining text after wake word: \"{remainingText}\"");
                    await TypeTextAsync(remainingText);
                }
            }
            else
            {
                // Not a wake word - discard transcription
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] In Standby mode - discarding transcription (no wake word)");
            }
        }
        else
        {
            // In Active mode - check for pause word first (pause words bypass min duration)
            if (_textProcessor.CheckPauseWord(text))
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Pause word detected! Transitioning to Standby mode.");

                // Transition to Standby
                _isStandby = true;
                Dispatcher.UIThread.Post(() =>
                {
                    _recordingIndicator.SetPauseState(true);
                    UpdateStatus("Standby");
                    _recordingIndicator.ShowStandby();
                    UpdateMenuState();
                });

                // Don't type anything
            }
            else
            {
                // Normal transcription - type it
                await TypeTranscriptionAsync(result);
            }
        }
    }

    private async Task TypeTranscriptionAsync(TranscriptionResult result)
    {
        _history.AddEntry(result);

        Dispatcher.UIThread.Post(() =>
        {
            UpdateStatus(string.IsNullOrEmpty(result.Backend) ? "Typing..." : $"Typing ({result.Backend})...");
            _recordingIndicator.ShowTyping(result.Backend);
        });

        await _textTyper.TypeTextAsync(result.Text);
        _ignoreUntil = DateTime.Now.AddSeconds(2);

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Typed successfully");
    }

    private async Task TypeTextAsync(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateStatus("Typing...");
            _recordingIndicator.ShowTyping();
        });

        await _textTyper.TypeTextAsync(text);
        _ignoreUntil = DateTime.Now.AddSeconds(2);

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Typed successfully");
    }

    private void ToggleLongRecording()
    {
        if (_isLongRecording)
            StopLongRecording();
        else
            StartLongRecording();
    }

    private void StartLongRecording()
    {
        if (_isTranscribing)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Cannot start long recording - transcription in progress");
            return;
        }

        bool autoStarted = false;
        if (!_isListening)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Toggle-to-Talk: auto-starting listening");
            StartListening();
            autoStarted = true;
        }

        if (_isPaused || _isStandby)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Toggle-to-Talk: auto-resuming");
            ResumeListening();
        }

        if (!_isListening)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Cannot start toggle-to-talk - listening failed to start");
            return;
        }

        if (autoStarted)
        {
            _toggleToTalkAutoStarted = true;
            _recordingIndicator.HideCompletely();
        }

        _isLongRecording = true;
        _longRecordingCts = new CancellationTokenSource();

        // Set max duration timeout
        var maxDuration = TimeSpan.FromMinutes(_config.MaxLongRecordMinutes);
        _longRecordingCts.CancelAfter(maxDuration);

        _audioCapture.StartLongRecording();

        Dispatcher.UIThread.Post(() =>
        {
            UpdateStatus("Toggle-to-Talk...");
            _toggleToTalkIndicator.ShowRecording();
        });

        // Monitor for timeout
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(maxDuration, _longRecordingCts.Token);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Long recording max duration reached ({_config.MaxLongRecordMinutes} min)");
                Dispatcher.UIThread.Post(() => StopLongRecording());
            }
            catch (TaskCanceledException)
            {
                // Normal cancellation - user stopped recording
            }
        });

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Long recording started (max: {_config.MaxLongRecordMinutes} min)");
    }

    private void StopLongRecording()
    {
        if (!_isLongRecording) return;

        _isLongRecording = false;
        _wasLongRecording = true; // Mark that the next audio should use long recording transcription
        _longRecordingCts?.Cancel();
        _longRecordingCts?.Dispose();
        _longRecordingCts = null;

        _audioCapture.StopLongRecording();

        Dispatcher.UIThread.Post(() =>
        {
            UpdateStatus("Transcribing...");
            _toggleToTalkIndicator.ShowStopped();
            if (!_toggleToTalkAutoStarted)
            {
                _recordingIndicator.ShowLongRecordingStopped();
                _recordingIndicator.ShowTranscribing();
            }
        });

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Long recording stopped");
    }

    private void StartPushToTalk()
    {
        // Block PTT while a transcription is in flight or a failed one is still
        // awaiting the user's Retry/Discard choice — otherwise the new audio would
        // be silently dropped by OnAudioReady.
        if (_isPushToTalking || _isLongRecording || _isTranscribing || _lastFailedAudio != null)
            return;

        // Re-pressed during the post-roll window — cancel the pending finalize and
        // keep the existing recording going.
        if (_pttPostRollCts is { IsCancellationRequested: false } && _audioCapture.IsLongRecording)
        {
            _pttPostRollCts.Cancel();
            _isPushToTalking = true;
            Dispatcher.UIThread.Post(() =>
            {
                UpdateStatus("Push to Talk...");
                _recordingIndicator.ShowPushToTalk();
            });
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Push-to-Talk resumed during post-roll");
            return;
        }

        // Auto-start audio capture if not already listening
        if (!_isListening)
        {
            try
            {
                _audioCapture.Start();
                _isListening = true;
                _isPaused = false;
                _isStandby = false;
                UpdateMenuState();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] PTT: Error starting audio: {ex.Message}");
                return;
            }
        }

        if (_isPaused || _isStandby)
            return;

        _isPushToTalking = true;
        _pushToTalkStartedAt = DateTime.Now;
        _audioCapture.StartLongRecording(); // Reuse long recording buffer (captures all audio, bypasses VAD)

        Dispatcher.UIThread.Post(() =>
        {
            UpdateStatus("Push to Talk...");
            _recordingIndicator.ShowPushToTalk();
        });

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Push-to-Talk started (Right Ctrl + Right Shift)");
    }

    private void PinFromPushToTalk()
    {
        if (!_isPushToTalking)
            return;

        // Stop PTT recording but keep the bar up in normal listening mode
        _isPushToTalking = false;
        _wasLongRecording = true;
        _wasPushToTalking = false; // NOT a PTT exit — stay in listening mode after transcription
        _audioCapture.StopLongRecording();

        Dispatcher.UIThread.Post(() =>
        {
            _recordingIndicator.ShowPushToTalkStopped();
            UpdateStatus("Transcribing...");
            _recordingIndicator.ShowTranscribing();
        });

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Push-to-Talk pinned — staying in listening mode");
    }

    private void StopPushToTalk()
    {
        if (!_isPushToTalking) return;

        _isPushToTalking = false;

        // A tap shorter than the minimum hold is an accident: throw the clip away.
        var held = DateTime.Now - _pushToTalkStartedAt;
        if (held.TotalSeconds < _config.PushToTalkMinHoldSeconds && _audioCapture.IsLongRecording)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Push-to-Talk held {held.TotalMilliseconds:F0} ms - discarding");
            _pttPostRollCts?.Cancel();
            _discardNextAudio = true;
            try { _audioCapture.StopLongRecording(); }
            finally { _discardNextAudio = false; }
            Dispatcher.UIThread.Post(StopListening);
            return;
        }

        var postRoll = TimeSpan.FromSeconds(Math.Max(0, _config.PostRollSeconds));
        if (postRoll <= TimeSpan.Zero)
        {
            FinishPushToTalkRecording();
            return;
        }

        // Keep capturing for a short post-roll window after the keys are released.
        _pttPostRollCts?.Cancel();
        _pttPostRollCts?.Dispose();
        _pttPostRollCts = new CancellationTokenSource();
        var token = _pttPostRollCts.Token;

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Push-to-Talk released — capturing {postRoll.TotalSeconds:F1}s post-roll");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(postRoll, token);
                Dispatcher.UIThread.Post(FinishPushToTalkRecording);
            }
            catch (TaskCanceledException) { }
        });
    }

    private void FinishPushToTalkRecording()
    {
        // May have already been finalized (or cancelled) by a re-press or Stop.
        if (!_audioCapture.IsLongRecording) return;

        _wasPushToTalking = true;
        _wasLongRecording = true; // Route through long recording transcription path
        _audioCapture.StopLongRecording();

        Dispatcher.UIThread.Post(() =>
        {
            UpdateStatus("Transcribing...");
            _recordingIndicator.ShowPushToTalkStopped();
            _recordingIndicator.ShowTranscribing();
        });

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Push-to-Talk stopped");
    }

    public void ShowFileTranscription()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var window = new FileTranscriptionWindow(_transcriber);
            window.Show();
        });
    }

    public void ShowSettings(int initialTab = 0)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var settingsWindow = new SettingsWindow(_config, _history, _audioCapture, initialTab: initialTab);
            settingsWindow.SettingsSaved += OnSettingsSaved;
            settingsWindow.Show();
        });
    }

    private void ShowCalibration()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var calibrationWindow = new CalibrationWindow(_audioCapture, _config);
            calibrationWindow.Show();
        });
    }

    private void OnSettingsSaved(object? sender, EventArgs e)
    {
        // Update long record button visibility based on new settings
        Dispatcher.UIThread.Post(() =>
        {
            _recordingIndicator.SetLongRecordButtonVisible(false);

            // Apply the pre-roll mic policy while idle: keep the mic warm when
            // enabled, release it when disabled.
            if (!_isListening)
            {
                if (_config.PreRollEnabled && !_audioCapture.IsRecording)
                    _audioCapture.StartMonitoring();
                else if (!_config.PreRollEnabled && _audioCapture.IsMonitoring)
                    _audioCapture.Stop(); // PreRollEnabled now false → fully stops
            }
        });
    }

    public void Quit()
    {
        Dispose();

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Disposing WhisperKeyboardApp...");

        try { _transcriptionCts?.Cancel(); } catch { }
        try { _transcriptionCts?.Dispose(); } catch { }
        try { _longRecordingCts?.Cancel(); } catch { }
        try { _longRecordingCts?.Dispose(); } catch { }
        try { _pttPostRollCts?.Cancel(); } catch { }
        try { _pttPostRollCts?.Dispose(); } catch { }

        try { _audioCapture.Stop(); } catch (Exception ex) { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error stopping audio: {ex.Message}"); }
        try { _audioCapture.Dispose(); } catch (Exception ex) { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error disposing audio: {ex.Message}"); }

        try { _pushToTalkHook?.Dispose(); } catch (Exception ex) { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error disposing PTT hook: {ex.Message}"); }
        try { _globalHotkey.Dispose(); } catch (Exception ex) { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error disposing hotkey: {ex.Message}"); }
        try { _chatGptTranscriber.Dispose(); } catch { }
        try { _transcriber.Dispose(); } catch (Exception ex) { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error disposing transcriber: {ex.Message}"); }

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Dispose complete");
    }
}
