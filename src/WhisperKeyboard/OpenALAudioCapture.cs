using WhisperKeyboard.Core;

namespace WhisperKeyboard;

/// <summary>
/// Cross-platform audio capture using OpenAL via pure P/Invoke.
/// Uses openal-soft on macOS/Linux for capture support.
/// </summary>
public class OpenALAudioCapture : IAudioCapture
{
    // How often the capture thread checks whether the open device is still the one
    // it should be (system default changed, or a different mic picked in Settings).
    private static readonly TimeSpan DeviceCheckInterval = TimeSpan.FromSeconds(1);

    private readonly Config _config;
    private IntPtr _captureDevice;
    // Guards _captureDevice: the capture thread may swap it out while other threads
    // pause/resume it, and native calls on a closed handle crash.
    private readonly object _deviceLock = new();
    // Name of the device currently open, and whether it was opened as the system default.
    private string? _openedDeviceName;
    private bool _openedAsDefault;
    private string? _lastReopenFailure;
    private Thread? _captureThread;
    private volatile bool _isRunning;
    private volatile bool _isPaused;
    private bool _disposed;

    private readonly List<byte[]> _audioBuffer = new();
    private readonly List<byte[]> _voiceBuffer = new();
    private readonly object _bufferLock = new();
    private bool _isSpeechDetected;
    private DateTime _lastSpeechTime;
    private DateTime _speechStartTime;
    private TimeSpan _accumulatedSpeechDuration;
    private bool _isLongRecording;
    private DateTime _longRecordingStartTime;
    private volatile bool _isMonitoring;

    public event EventHandler<AudioReadyEventArgs>? AudioReady;
    public event EventHandler<double>? VolumeChanged;
    public event EventHandler<bool>? SpeechDetected;

    public bool IsRecording => _isRunning;
    public bool IsPaused => _isPaused;
    public bool IsSpeechDetected => _isSpeechDetected;
    public bool IsLongRecording => _isLongRecording;
    public bool IsMonitoring => _isMonitoring;

    public OpenALAudioCapture(Config config)
    {
        _config = config;
    }

    public List<string> GetAudioDevices()
    {
        try
        {
            return OpenALNative.GetCaptureDeviceNames();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error enumerating audio devices: {ex.Message}");
            return new List<string>();
        }
    }

    public void Start()
    {
        // Already running (possibly monitor mode) — just upgrade to full capture.
        if (_isRunning)
        {
            _isMonitoring = false;
            EnsureDeviceCapturing();
            return;
        }

        StartDevice();
        _isMonitoring = false;
    }

    /// <summary>
    /// Ensures the capture device is actively recording. The device can be left
    /// paused (e.g. stopped mid-transcription), so monitor/full transitions must
    /// resume it or no audio is captured.
    /// </summary>
    private void EnsureDeviceCapturing()
    {
        if (!_isPaused) return;
        lock (_deviceLock)
        {
            if (_captureDevice != IntPtr.Zero)
            {
                OpenALNative.CaptureStart(_captureDevice);
            }
            _isPaused = false;
        }
    }

    public void StartMonitoring()
    {
        if (_isRunning) return;

        StartDevice();
        _isMonitoring = true;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Audio monitoring started (mic warm for pre-roll)");
    }

    private void StartDevice()
    {
        try
        {
            lock (_deviceLock)
            {
                _captureDevice = OpenConfiguredDevice();

                if (_captureDevice == IntPtr.Zero)
                {
                    throw new Exception("Failed to open audio capture device. Make sure a microphone is connected and permissions are granted.");
                }

                OpenALNative.CaptureStart(_captureDevice);
                _isRunning = true;
                _isPaused = false;
            }

            // Start capture thread
            _captureThread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "OpenAL Capture"
            };
            _captureThread.Start();

            Console.WriteLine($"Audio capture started (device: {DescribeOpenedDevice()})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error starting audio capture: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// The configured mic if it's connected, otherwise null (= the system default).
    /// </summary>
    private string? ConnectedConfiguredDevice()
    {
        var name = _config.DeviceName;
        return !string.IsNullOrEmpty(name) && GetAudioDevices().Contains(name) ? name : null;
    }

    /// <summary>
    /// Opens the configured mic, or the current system default when none is set or
    /// it isn't connected, and records what was opened. Returns IntPtr.Zero on failure.
    /// Caller must hold _deviceLock.
    /// </summary>
    private IntPtr OpenConfiguredDevice()
    {
        // Get device name (null = default)
        string? deviceName = ConnectedConfiguredDevice();

        // Read the default's name before opening: if it changes in between, the next
        // device check sees a mismatch and reopens instead of missing the change.
        string? openedName = deviceName ?? OpenALNative.GetDefaultCaptureDeviceName();

        // Open capture device
        // Format: 16-bit mono PCM
        int bufferSize = _config.SampleRate; // 1 second buffer
        var device = OpenALNative.CaptureOpenDevice(
            deviceName,
            _config.SampleRate,
            OpenALNative.AL_FORMAT_MONO16,
            bufferSize
        );

        if (device != IntPtr.Zero)
        {
            _openedDeviceName = openedName;
            _openedAsDefault = deviceName == null;
        }
        return device;
    }

    private string DescribeOpenedDevice() =>
        _openedAsDefault ? $"default: {_openedDeviceName ?? "unknown"}" : _openedDeviceName ?? "unknown";

    /// <summary>
    /// The device stays open between recordings (pre-roll keeps it warm), and
    /// OpenAL resolves "default" only when opening, so without this a Default mic
    /// stays pinned to whatever was default at launch. Reopens when the system
    /// default input changes, a different mic is picked in Settings, or the
    /// configured mic is plugged in or unplugged. Runs on the capture thread.
    /// </summary>
    private void ReopenIfDeviceChanged()
    {
        var configured = ConnectedConfiguredDevice();
        var wanted = configured ?? OpenALNative.GetDefaultCaptureDeviceName();
        // No input devices at all — keep whatever is open rather than dropping it.
        if (wanted == null) return;
        if (wanted == _openedDeviceName)
        {
            // Same device, now picked by name (or back to default): nothing to reopen.
            _openedAsDefault = configured == null;
            return;
        }
        string target = configured ?? $"default: {wanted}";

        string previous = DescribeOpenedDevice();
        lock (_deviceLock)
        {
            if (!_isRunning) return;

            // Open the new device before closing the old one so a failed open
            // (e.g. a Bluetooth headset still switching profiles) keeps capturing
            // from the old device; the next check retries.
            var newDevice = OpenConfiguredDevice();
            if (newDevice == IntPtr.Zero)
            {
                // Log once per target, not on every retry.
                if (_lastReopenFailure != target)
                {
                    _lastReopenFailure = target;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Failed to switch microphone to {target}; staying on {previous}");
                }
                return;
            }
            _lastReopenFailure = null;

            if (_captureDevice != IntPtr.Zero)
            {
                OpenALNative.CaptureStop(_captureDevice);
                OpenALNative.CaptureCloseDevice(_captureDevice);
            }
            _captureDevice = newDevice;
            if (!_isPaused)
            {
                OpenALNative.CaptureStart(_captureDevice);
            }
        }

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Microphone switched from {previous} to {DescribeOpenedDevice()}");
    }

    public void Stop()
    {
        if (!_isRunning) return;

        // With pre-roll enabled, keep the mic running in monitor-only mode so the
        // pre-roll buffer stays warm for the next push-to-talk.
        if (_config.PreRollEnabled)
        {
            _isMonitoring = true;
            _isLongRecording = false;
            _isSpeechDetected = false;
            lock (_bufferLock)
            {
                _audioBuffer.Clear();
            }
            // Device may be paused (stopped mid-transcription) — resume it so the
            // pre-roll buffer actually keeps filling.
            EnsureDeviceCapturing();
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Audio downgraded to monitor mode");
            return;
        }

        StopDevice();
    }

    private void StopDevice()
    {
        _isRunning = false;
        _isMonitoring = false;
        _captureThread?.Join(1000);

        lock (_deviceLock)
        {
            if (_captureDevice != IntPtr.Zero)
            {
                OpenALNative.CaptureStop(_captureDevice);
                OpenALNative.CaptureCloseDevice(_captureDevice);
                _captureDevice = IntPtr.Zero;
            }
        }

        Console.WriteLine("Audio capture stopped");
    }

    public void Pause()
    {
        lock (_deviceLock)
        {
            _isPaused = true;
            if (_captureDevice != IntPtr.Zero)
            {
                OpenALNative.CaptureStop(_captureDevice);
            }
        }
    }

    public void Resume()
    {
        if (!_isPaused) return;

        // Reset timing so silence duration doesn't include pause time
        if (_isSpeechDetected)
        {
            _lastSpeechTime = DateTime.Now;
        }

        lock (_deviceLock)
        {
            if (_captureDevice != IntPtr.Zero)
            {
                OpenALNative.CaptureStart(_captureDevice);
            }
            _isPaused = false;
        }
    }

    private void CaptureLoop()
    {
        // Buffer for ~50ms of audio at 16kHz mono 16-bit = 800 samples
        int samplesPerBuffer = _config.SampleRate / 20; // 50ms worth
        var buffer = new short[samplesPerBuffer];
        var byteBuffer = new byte[samplesPerBuffer * 2]; // 16-bit = 2 bytes per sample
        var nextDeviceCheck = DateTime.UtcNow + DeviceCheckInterval;

        while (_isRunning)
        {
            // Checked while paused too, so resuming doesn't start on a stale mic.
            if (DateTime.UtcNow >= nextDeviceCheck)
            {
                nextDeviceCheck = DateTime.UtcNow + DeviceCheckInterval;
                try
                {
                    ReopenIfDeviceChanged();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Device check error: {ex.Message}");
                }
            }

            if (_isPaused)
            {
                Thread.Sleep(50);
                continue;
            }

            try
            {
                bool captured = false;
                lock (_deviceLock)
                {
                    // Check how many samples are available
                    if (_captureDevice != IntPtr.Zero &&
                        OpenALNative.GetCapturedSamples(_captureDevice) >= samplesPerBuffer)
                    {
                        // Capture samples
                        OpenALNative.CaptureSamples(_captureDevice, buffer, samplesPerBuffer);
                        captured = true;
                    }
                }

                if (captured)
                {
                    // Convert to bytes
                    Buffer.BlockCopy(buffer, 0, byteBuffer, 0, byteBuffer.Length);

                    // Process the audio data
                    ProcessAudioData(byteBuffer, byteBuffer.Length);
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Capture error: {ex.Message}");
                Thread.Sleep(100);
            }
        }
    }

    private void ProcessAudioData(byte[] buffer, int bytesRecorded)
    {
        // Calculate RMS volume
        double normalizedVolume = AudioProcessingUtils.GetNormalizedVolume(buffer, bytesRecorded);
        VolumeChanged?.Invoke(this, normalizedVolume);

        // Voice Activity Detection
        bool isSpeech = normalizedVolume > _config.VadThreshold;

        lock (_bufferLock)
        {
            var chunk = new byte[bytesRecorded];
            Array.Copy(buffer, chunk, bytesRecorded);

            // Monitor-only mode: just keep the rolling pre-roll buffer warm.
            if (_isMonitoring)
            {
                AddToPreRoll(chunk);
                return;
            }

            // In long recording mode, always buffer audio regardless of VAD
            if (_isLongRecording)
            {
                _audioBuffer.Add(chunk);

                // Calculate chunk duration for tracking
                double chunkDuration = (double)bytesRecorded / (_config.SampleRate * 2);
                _accumulatedSpeechDuration += TimeSpan.FromSeconds(chunkDuration);
                _lastSpeechTime = DateTime.Now;
                return;
            }

            if (isSpeech)
            {
                if (!_isSpeechDetected)
                {
                    // Speech just started
                    _isSpeechDetected = true;
                    _speechStartTime = DateTime.Now;
                    
                    // Calculate duration of voice buffer (pre-roll)
                    long bufferBytes = _voiceBuffer.Sum(b => b.Length);
                    double bufferDuration = (double)bufferBytes / (_config.SampleRate * 2);
                    _accumulatedSpeechDuration = TimeSpan.FromSeconds(bufferDuration);
                    
                    SpeechDetected?.Invoke(this, true);

                    // Add context from voice buffer
                    _audioBuffer.AddRange(_voiceBuffer);
                    _voiceBuffer.Clear();
                }

                // Calculate chunk duration
                double chunkDuration = (double)bytesRecorded / (_config.SampleRate * 2); // 16-bit = 2 bytes
                _accumulatedSpeechDuration += TimeSpan.FromSeconds(chunkDuration);

                _lastSpeechTime = DateTime.Now;
                _audioBuffer.Add(chunk);
            }
            else
            {
                // Silence - maintain pre-roll buffer
                AddToPreRoll(chunk);

                if (_isSpeechDetected)
                {
                    // Still recording after speech (trailing silence)
                    _audioBuffer.Add(chunk);

                    // In long recording mode, don't auto-finalize on silence
                    if (!_isLongRecording)
                    {
                        var silenceDuration = (DateTime.Now - _lastSpeechTime).TotalSeconds;
                        // Post-roll keeps recording a bit past the silence timeout.
                        var finalizeAfter = _config.MaxSilenceDuration + Math.Max(0, _config.PostRollSeconds);

                        if (silenceDuration > finalizeAfter)
                        {
                            // End of speech detected
                            FinalizeAudio();
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Maximum bytes to retain in the rolling pre-roll buffer.
    /// When pre-roll is disabled, keeps a small floor (~250ms) so VAD word-starts
    /// aren't clipped.
    /// </summary>
    private int PreRollMaxBytes()
    {
        double seconds = _config.PreRollEnabled ? Math.Max(0, _config.PreRollSeconds) : 0.25;
        return Math.Max(1, (int)(seconds * _config.SampleRate * 2));
    }

    /// <summary>
    /// Appends a chunk to the rolling pre-roll buffer, trimming the oldest chunks
    /// to stay within the configured pre-roll duration. Caller must hold _bufferLock.
    /// </summary>
    private void AddToPreRoll(byte[] chunk)
    {
        _voiceBuffer.Add(chunk);
        int maxBytes = PreRollMaxBytes();
        long total = _voiceBuffer.Sum(b => b.Length);
        while (total > maxBytes && _voiceBuffer.Count > 1)
        {
            total -= _voiceBuffer[0].Length;
            _voiceBuffer.RemoveAt(0);
        }
    }

    private void FinalizeAudio()
    {
        // Calculate duration from first speech to last speech (excluding trailing silence)
        // This is the "speech span" - from when speech started to when it last ended
        var speechSpanDuration = _lastSpeechTime - _speechStartTime;
        var speechDuration = _accumulatedSpeechDuration;

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Finalizing audio. Speech Span: {speechSpanDuration.TotalSeconds:F2}s, Active Speech: {speechDuration.TotalSeconds:F2}s");

        // Always send audio - let the application layer decide based on transcription result
        long totalBytes = _audioBuffer.Sum(b => b.Length);
        var combinedAudio = new byte[totalBytes];
        int offset = 0;

        foreach (var chunk in _audioBuffer)
        {
            Array.Copy(chunk, 0, combinedAudio, offset, chunk.Length);
            offset += chunk.Length;
        }

        AudioReady?.Invoke(this, new AudioReadyEventArgs(combinedAudio, speechSpanDuration, speechDuration));

        // Reset state
        _audioBuffer.Clear();
        _accumulatedSpeechDuration = TimeSpan.Zero;
        _isSpeechDetected = false;
        SpeechDetected?.Invoke(this, false);
    }

    public void StartLongRecording()
    {
        if (_isLongRecording || !_isRunning) return;

        lock (_bufferLock)
        {
            _isLongRecording = true;
            _isMonitoring = false;
            _longRecordingStartTime = DateTime.Now;
            _isSpeechDetected = true; // Treat as always "speaking"
            _speechStartTime = DateTime.Now;
            _accumulatedSpeechDuration = TimeSpan.Zero;

            _audioBuffer.Clear();

            // Prepend the rolling pre-roll buffer so audio from just before the
            // keypress is included.
            if (_config.PreRollEnabled && _voiceBuffer.Count > 0)
            {
                _audioBuffer.AddRange(_voiceBuffer);
                long preBytes = _voiceBuffer.Sum(b => b.Length);
                var preRoll = TimeSpan.FromSeconds((double)preBytes / (_config.SampleRate * 2));
                _accumulatedSpeechDuration = preRoll;
                _speechStartTime = DateTime.Now - preRoll;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Long recording STARTED with {preRoll.TotalSeconds:F2}s pre-roll");
            }
            else
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Long recording STARTED");
            }
            _voiceBuffer.Clear();

            SpeechDetected?.Invoke(this, true);
        }
    }

    public void StopLongRecording()
    {
        if (!_isLongRecording) return;

        lock (_bufferLock)
        {
            _isLongRecording = false;
            var duration = DateTime.Now - _longRecordingStartTime;

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Long recording STOPPED. Duration: {duration.TotalSeconds:F2}s");

            // Finalize and send all buffered audio
            if (_audioBuffer.Count > 0)
            {
                FinalizeAudio();
            }
            else
            {
                // No audio captured, reset state
                _isSpeechDetected = false;
                SpeechDetected?.Invoke(this, false);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        StopDevice();
        _disposed = true;
    }
}
