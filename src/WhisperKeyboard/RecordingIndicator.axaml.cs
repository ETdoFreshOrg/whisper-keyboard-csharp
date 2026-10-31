using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace WhisperKeyboard;

public partial class RecordingIndicator : Window
{
    private readonly DispatcherTimer _updateTimer;
    private readonly DispatcherTimer _recordingTimer;
    private readonly double[] _volumeHistory;
    private int _volumeIndex;
    private const int MaxVolumeHistory = 50;
    private double _currentVolume;

    private bool _isRecording;
    private DateTime _recordingStartTime;
    private bool _isPaused;
    private bool _isLongRecording;
    private DateTime _longRecordingStartTime;

    // Drag state
    private bool _isDragging;
    private PixelPoint _dragStartScreenPoint;
    private PixelPoint _windowStartPosition;

    // Opacity settings
    private const double IdleOpacity = 0.35;
    private const double ListeningOpacity = 0.45;
    private const double PausedOpacity = 0.50;
    private const double ActiveOpacity = 0.95;
    private double _targetOpacity = ListeningOpacity;

    // Track if window has been shown at least once (has valid handle)
    private bool _hasBeenShown;

    // Fade timing settings
    private const double FadeInSpeed = 0.15;      // Fast fade in
    private const double FadeOutSpeed = 0.03;     // Slower fade out
    private const int FadeOutDelayMs = 800;       // Delay before fade out starts
    private DateTime? _fadeOutDelayStart;

    // Events
    public event Action? OnPauseClicked;
    public event Action? OnStopClicked;
    public event Action? OnSettingsClicked;
    public event Action? OnLongRecordClicked;
    public event Action? OnHistoryClicked;
    public event Action? OnCalibrateClicked;
    public event Action? OnPinClicked;
    public event Action? OnRetryClicked;
    public event Action? OnDiscardClicked;
    public event Action? OnCancelClicked;
    public event Action? OnSaveAudioClicked;

    public RecordingIndicator()
    {
        InitializeComponent();

        _volumeHistory = new double[MaxVolumeHistory];

        // Setup timers
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _updateTimer.Tick += UpdateTimer_Tick;

        _recordingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _recordingTimer.Tick += RecordingTimer_Tick;

        // Wire up button events
        PauseButton.Click += (s, e) => OnPauseClicked?.Invoke();
        StopButton.Click += (s, e) => OnStopClicked?.Invoke();
        SettingsButton.Click += (s, e) => OnSettingsClicked?.Invoke();
        LongRecordButton.Click += (s, e) => OnLongRecordClicked?.Invoke();
        HistoryButton.Click += (s, e) => OnHistoryClicked?.Invoke();
        CalibrateButton.Click += (s, e) => OnCalibrateClicked?.Invoke();
        PinButton.Click += (s, e) => OnPinClicked?.Invoke();
        RetryButton.Click += (s, e) => OnRetryClicked?.Invoke();
        DiscardButton.Click += (s, e) => OnDiscardClicked?.Invoke();
        CancelButton.Click += (s, e) => OnCancelClicked?.Invoke();
        SaveAudioButton.Click += (s, e) => OnSaveAudioClicked?.Invoke();

        // Enable dragging
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;

        // Position at bottom center of screen and make non-activating on Windows
        Opened += (s, e) =>
        {
            ResetToDefaultPosition();
            MakeWindowNonActivating();
            _hasBeenShown = true;
        };

        // Pre-initialize the window so we can use non-activating show later
        // This happens after the window is fully constructed
        Dispatcher.UIThread.Post(PreInitializeWindow, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Pre-initializes the window by showing it off-screen briefly.
    /// This creates the native window handle and applies WS_EX_NOACTIVATE,
    /// so subsequent shows won't steal focus.
    /// </summary>
    private void PreInitializeWindow()
    {
        if (_hasBeenShown) return;

        // Position off-screen so user doesn't see it
        Position = new PixelPoint(-10000, -10000);
        Opacity = 0;

        // Show briefly to create handle and trigger Opened event
        Show();

        // Hide immediately
        Dispatcher.UIThread.Post(() =>
        {
            Hide();
            // Opacity will be set properly when actually shown
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Makes this window non-activating so it doesn't steal focus when clicked.
    /// This allows the user to click buttons without losing focus on their text editor.
    /// </summary>
    private void MakeWindowNonActivating()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            MakeWindowNonActivatingWindows();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            MakeWindowNonActivatingMacOS();
        }
        // Linux: No reliable cross-desktop way to do this, skip for now
    }

    #region Windows Non-Activating Window

    private void MakeWindowNonActivatingWindows()
    {
        try
        {
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] RecordingIndicator: Could not get window handle");
                return;
            }

            // Get current extended style
            var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);

            // Add WS_EX_NOACTIVATE to prevent the window from activating when clicked
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] RecordingIndicator: Window set to non-activating (Windows)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] RecordingIndicator: Error setting non-activating: {ex.Message}");
        }
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_SHOWNA = 8;
    private const int SW_SHOW = 5;

    // SetWindowPos flags
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;

    // Track whether we're "visually hidden" (off-screen) vs actually hidden
    private bool _isOffScreen;
    private PixelPoint _savedPosition;

    /// <summary>
    /// Shows the window without activating it or stealing focus.
    /// On first show, uses normal Show() which triggers Opened event to set WS_EX_NOACTIVATE.
    /// On subsequent shows, moves the window back from off-screen (avoids activation entirely).
    /// </summary>
    private void ShowWithoutActivation()
    {
        // First time showing - must use Show() to create the window
        // The Opened event will set WS_EX_NOACTIVATE style
        if (!_hasBeenShown)
        {
            Show();
            return;
        }

        if (_isOffScreen)
        {
            // Window was hidden off-screen — move it back without any show/activate calls
            Position = _savedPosition;
            _isOffScreen = false;
            // Opacity will be managed by the fade system (_targetOpacity)
            return;
        }

        // Window has been shown before, use platform-specific non-activating show
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd != IntPtr.Zero)
            {
                // Re-apply WS_EX_NOACTIVATE in case Avalonia cleared it
                var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                if ((exStyle & WS_EX_NOACTIVATE) == 0)
                {
                    SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);
                }

                ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                IsVisible = true;
                return;
            }
        }

        // macOS: use orderFront: + refocus previous app to avoid stealing focus
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var nsWindow = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (nsWindow != IntPtr.Zero)
            {
                // Remember the currently active app before we show our window
                var workspace = objc_msgSend_IntPtr(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
                var frontApp = objc_msgSend_IntPtr(workspace, sel_registerName("frontmostApplication"));

                // Show our window without activating the app
                objc_msgSend_void_IntPtr(nsWindow, sel_registerName("orderFront:"), IntPtr.Zero);
                IsVisible = true;

                // Re-activate the previous app so it keeps keyboard focus
                if (frontApp != IntPtr.Zero)
                {
                    // [frontApp activateWithOptions: NSApplicationActivateIgnoringOtherApps]
                    objc_msgSend_void_IntPtr(frontApp, sel_registerName("activateWithOptions:"), (IntPtr)2);
                }
                return;
            }
        }

        // Fallback
        Show();
    }

    #endregion

    #region macOS Non-Activating Window

    private void MakeWindowNonActivatingMacOS()
    {
        try
        {
            var nsWindow = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (nsWindow == IntPtr.Zero)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] RecordingIndicator: Could not get NSWindow handle");
                return;
            }

            // Get the NSWindow's styleMask and add NSWindowStyleMaskNonactivatingPanel
            // We use Objective-C runtime to call: [nsWindow setStyleMask:([nsWindow styleMask] | NSWindowStyleMaskNonactivatingPanel)]

            // Get current style mask
            var currentStyleMask = objc_msgSend_IntPtr(nsWindow, sel_registerName("styleMask"));

            // Add NSWindowStyleMaskNonactivatingPanel (1 << 7 = 128)
            var newStyleMask = (IntPtr)((long)currentStyleMask | NSWindowStyleMaskNonactivatingPanel);

            // Set the new style mask
            objc_msgSend_void_IntPtr(nsWindow, sel_registerName("setStyleMask:"), newStyleMask);

            // Also set the window level to floating panel level so it stays on top
            // NSFloatingWindowLevel = 5
            objc_msgSend_void_IntPtr(nsWindow, sel_registerName("setLevel:"), (IntPtr)5);

            // NSWindowCollectionBehaviorCanJoinAllSpaces (1 << 0) = appear on all virtual desktops
            // NSWindowCollectionBehaviorTransient (1 << 3) = not in Spaces/Mission Control
            // NSWindowCollectionBehaviorIgnoresCycle (1 << 6) = not in Cmd+Tab / window cycling
            var collectionBehavior = (IntPtr)(NSWindowCollectionBehaviorCanJoinAllSpaces | NSWindowCollectionBehaviorTransient | NSWindowCollectionBehaviorIgnoresCycle);
            objc_msgSend_void_IntPtr(nsWindow, sel_registerName("setCollectionBehavior:"), collectionBehavior);

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] RecordingIndicator: Window set to non-activating (macOS)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] RecordingIndicator: Error setting non-activating on macOS: {ex.Message}");
        }
    }

    private const long NSWindowStyleMaskNonactivatingPanel = 1 << 7;  // 128
    private const long NSWindowCollectionBehaviorCanJoinAllSpaces = 1 << 0; // 1 - appear on all virtual desktops
    private const long NSWindowCollectionBehaviorTransient = 1 << 3;  // 8 - not managed by Mission Control
    private const long NSWindowCollectionBehaviorIgnoresCycle = 1 << 6; // 64 - not in Cmd+Tab cycling

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_getClass")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

    #endregion

    public void ResetToDefaultPosition()
    {
        if (Screens.Primary is { } screen)
        {
            var workArea = screen.WorkingArea;
            Position = new PixelPoint(
                workArea.X + (workArea.Width - (int)Width) / 2,
                workArea.Y + workArea.Height - (int)Height - 40
            );
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed)
        {
            // Check if clicking on a button area (buttons are on the right)
            var pos = point.Position;
            if (pos.X < Width - 240) // Not in button area
            {
                _isDragging = true;
                // Store screen position for accurate tracking
                var screenPos = this.PointToScreen(pos);
                _dragStartScreenPoint = new PixelPoint((int)screenPos.X, (int)screenPos.Y);
                _windowStartPosition = Position;
                e.Pointer.Capture(this);
                e.Handled = true;
            }
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDragging)
        {
            var currentPos = e.GetPosition(this);
            var screenPos = this.PointToScreen(currentPos);
            var currentScreenPoint = new PixelPoint((int)screenPos.X, (int)screenPos.Y);

            Position = new PixelPoint(
                _windowStartPosition.X + (currentScreenPoint.X - _dragStartScreenPoint.X),
                _windowStartPosition.Y + (currentScreenPoint.Y - _dragStartScreenPoint.Y)
            );
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
        }
    }

    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        // Add current volume to history
        _volumeHistory[_volumeIndex] = _currentVolume;
        _volumeIndex = (_volumeIndex + 1) % MaxVolumeHistory;

        // Smooth opacity transition with asymmetric fade speeds
        var currentOpacity = Opacity;
        bool isFadingOut = _targetOpacity < currentOpacity;

        if (isFadingOut)
        {
            // Check if we need to wait for delay
            if (_fadeOutDelayStart == null)
            {
                _fadeOutDelayStart = DateTime.Now;
            }

            var delayElapsed = (DateTime.Now - _fadeOutDelayStart.Value).TotalMilliseconds;
            if (delayElapsed >= FadeOutDelayMs)
            {
                // Delay complete, do slow fade out
                if (Math.Abs(currentOpacity - _targetOpacity) > 0.01)
                {
                    Opacity = currentOpacity + (_targetOpacity - currentOpacity) * FadeOutSpeed;
                }
                else if (Math.Abs(currentOpacity - _targetOpacity) > 0.001)
                {
                    Opacity = _targetOpacity;
                }
            }
            // else: still in delay period, don't change opacity
        }
        else
        {
            // Fading in - reset delay and use fast fade
            _fadeOutDelayStart = null;

            if (Math.Abs(currentOpacity - _targetOpacity) > 0.01)
            {
                Opacity = currentOpacity + (_targetOpacity - currentOpacity) * FadeInSpeed;
            }
            else if (Math.Abs(currentOpacity - _targetOpacity) > 0.001)
            {
                Opacity = _targetOpacity;
            }
        }

        // Redraw waveform
        DrawWaveform();
    }

    private void RecordingTimer_Tick(object? sender, EventArgs e)
    {
        var elapsed = _isLongRecording
            ? DateTime.Now - _longRecordingStartTime
            : DateTime.Now - _recordingStartTime;

        if (_isLongRecording)
        {
            // Show as M:SS for long recordings
            TimerText.Text = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";
        }
        else
        {
            TimerText.Text = $"{elapsed.TotalSeconds:F1}s";
        }
    }

    private void DrawWaveform()
    {
        WaveformCanvas.Children.Clear();

        var width = WaveformCanvas.Bounds.Width;
        var height = WaveformCanvas.Bounds.Height;
        if (width <= 0 || height <= 0) return;

        var barWidth = width / MaxVolumeHistory;
        var centerY = height / 2;

        for (int i = 0; i < MaxVolumeHistory; i++)
        {
            int idx = (_volumeIndex + i) % MaxVolumeHistory;
            double vol = _volumeHistory[idx];

            // Normalize volume (0-500 range to 0-1)
            double normalizedVolume = Math.Min(1.0, vol / 400.0);
            double barHeight = Math.Max(2, normalizedVolume * (height - 6));

            // Color based on recording state
            Color barColor;
            if (_isRecording)
            {
                int green = (int)(120 + normalizedVolume * 135);
                barColor = Color.FromRgb(50, (byte)green, 50);
            }
            else
            {
                barColor = Color.FromRgb(60, 90, 60);
            }

            var rect = new Rectangle
            {
                Width = Math.Max(1, barWidth - 1),
                Height = barHeight,
                Fill = new SolidColorBrush(barColor)
            };

            Canvas.SetLeft(rect, i * barWidth);
            Canvas.SetTop(rect, centerY - barHeight / 2);
            WaveformCanvas.Children.Add(rect);
        }

        // Draw center line
        var centerLine = new Line
        {
            StartPoint = new Point(0, centerY),
            EndPoint = new Point(width, centerY),
            Stroke = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
            StrokeThickness = 1
        };
        WaveformCanvas.Children.Add(centerLine);
    }

    public void UpdateVolume(double volume)
    {
        _currentVolume = volume;
    }

    public void ShowRecording()
    {
        _isRecording = true;
        _recordingStartTime = DateTime.Now;
        StatusText.Text = "Recording";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(100, 255, 100));
        TimerText.Text = "0.0s";
        TimerText.IsVisible = true;
        _targetOpacity = ActiveOpacity;

        _updateTimer.Start();
        _recordingTimer.Start();

        if (!IsVisible) ShowWithoutActivation();
    }

    public void ShowListening()
    {
        _isRecording = false;
        _isPaused = false;
        StatusText.Text = "Listening...";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(150, 180, 150));
        TimerText.IsVisible = false;
        _targetOpacity = ListeningOpacity;

        ShowNormalButtons();
        UpdatePauseButtonState();
        _recordingTimer.Stop();
        _updateTimer.Start();

        if (!IsVisible) ShowWithoutActivation();
    }

    public void ShowTranscribing()
    {
        _isRecording = false;
        StatusText.Text = "Transcribing...";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 255));
        TimerText.IsVisible = false;
        _targetOpacity = ActiveOpacity;

        _recordingTimer.Stop();

        // Show only the Cancel button while the API call is in flight.
        LongRecordButton.IsVisible = false;
        PauseButton.IsVisible = false;
        StopButton.IsVisible = false;
        HistoryButton.IsVisible = false;
        CalibrateButton.IsVisible = false;
        SettingsButton.IsVisible = false;
        PinButton.IsVisible = false;
        RetryButton.IsVisible = false;
        DiscardButton.IsVisible = false;
        SaveAudioButton.IsVisible = false;
        CancelButton.IsVisible = true;
    }

    public void ShowTyping(string? source = null)
    {
        _isRecording = false;
        StatusText.Text = string.IsNullOrEmpty(source) ? "Typing..." : $"Typing ({source})...";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 255, 150));
        TimerText.IsVisible = false;
        _targetOpacity = ActiveOpacity;

        _recordingTimer.Stop();

        // Typing can't be cancelled — drop the Cancel button.
        CancelButton.IsVisible = false;
    }

    public void ShowPaused()
    {
        _isRecording = false;
        _isPaused = true;
        StatusText.Text = "Paused";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 180, 100));
        // Keep timer text visible to show where we paused
        Opacity = PausedOpacity; // Set immediately for pause

        UpdatePauseButtonState();
        // Stop all timers - freeze everything in place
        _recordingTimer.Stop();
        _updateTimer.Stop();

        if (!IsVisible) ShowWithoutActivation();
    }

    public void ShowStandby()
    {
        _isRecording = false;
        _isPaused = true; // Reuse pause visual state for button
        StatusText.Text = "Standby";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 180, 100)); // Same yellow/orange as Paused
        TimerText.IsVisible = false;
        Opacity = PausedOpacity; // Set immediately

        UpdatePauseButtonState();
        // Stop timers - freeze waveform like paused state
        _recordingTimer.Stop();
        _updateTimer.Stop();

        if (!IsVisible) ShowWithoutActivation();
    }

    public void ShowRecordingStandby()
    {
        _isRecording = true; // Show waveform animation
        _recordingStartTime = DateTime.Now;
        StatusText.Text = "Wake word?";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 200, 100)); // Orange/yellow to indicate standby recording
        TimerText.Text = "0.0s";
        TimerText.IsVisible = true;
        _targetOpacity = ActiveOpacity;

        _updateTimer.Start();
        _recordingTimer.Start();

        if (!IsVisible) ShowWithoutActivation();
    }

    public async void ShowTooShort()
    {
        _isRecording = false;
        StatusText.Text = "Too short";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 220, 100)); // Yellow
        TimerText.IsVisible = false;
        Opacity = ActiveOpacity; // Make it visible

        _recordingTimer.Stop();

        if (!IsVisible) ShowWithoutActivation();

        // Wait briefly then return to listening
        await Task.Delay(800);

        // Only return to listening if we're still in "Too short" state
        if (StatusText.Text == "Too short")
        {
            ShowListening();
        }
    }

    /// <summary>Dictated text never reached the focused field; it was left on the clipboard.</summary>
    public async void ShowInsertionFailed()
    {
        const string message = "Didn't land - paste it";
        _isRecording = false;
        StatusText.Text = message;
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 160, 80)); // Orange
        TimerText.IsVisible = false;
        Opacity = ActiveOpacity;

        _recordingTimer.Stop();

        if (!IsVisible) ShowWithoutActivation();

        await Task.Delay(3000);

        if (StatusText.Text == message)
        {
            ShowListening();
        }
    }

    private const string CancelledMessage = "Cancelled";

    /// <summary>
    /// Brief "Cancelled" notice after Escape threw a recording away.
    /// With <paramref name="returnToListening"/>, the bar goes back to Listening
    /// afterwards; otherwise the caller hides it (push-to-talk).
    /// </summary>
    public async void ShowCancelled(bool returnToListening = false)
    {
        _isRecording = false;
        _isLongRecording = false;
        StatusText.Text = CancelledMessage;
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)); // Grey
        TimerText.IsVisible = false;
        Opacity = ActiveOpacity;
        _targetOpacity = ActiveOpacity;
        PinButton.IsVisible = false;

        _recordingTimer.Stop();

        if (!IsVisible) ShowWithoutActivation();

        if (!returnToListening) return;
        await Task.Delay(800);
        if (StatusText.Text == CancelledMessage)
        {
            ShowListening();
        }
    }

    public async void ShowLowConfidence()
    {
        _isRecording = false;
        StatusText.Text = "Low confidence";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 160, 80)); // Orange
        TimerText.IsVisible = false;
        Opacity = ActiveOpacity;

        _recordingTimer.Stop();

        if (!IsVisible) ShowWithoutActivation();

        await Task.Delay(800);

        if (StatusText.Text == "Low confidence")
        {
            ShowListening();
        }
    }

    public void ClearVolumeHistory()
    {
        Array.Clear(_volumeHistory, 0, _volumeHistory.Length);
        _currentVolume = 0;
    }

    public void SetPauseState(bool isPaused)
    {
        _isPaused = isPaused;
        UpdatePauseButtonState();
    }

    private void UpdatePauseButtonState()
    {
        PauseIcon.IsVisible = !_isPaused;
        PlayIcon.IsVisible = _isPaused;
    }

    private bool _longRecordButtonConfigured;

    public void SetLongRecordButtonVisible(bool visible)
    {
        _longRecordButtonConfigured = visible;
        LongRecordButton.IsVisible = visible;
    }

    public void ShowLongRecording()
    {
        _isLongRecording = true;
        _isRecording = true; // Keep waveform active
        _longRecordingStartTime = DateTime.Now;

        StatusText.Text = "Long Recording";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 100, 100)); // Red
        TimerText.Text = "0:00";
        TimerText.IsVisible = true;
        _targetOpacity = ActiveOpacity;

        // Toggle button icon to stop state
        LongRecordIcon.IsVisible = false;
        LongRecordStopIcon.IsVisible = true;

        // Disable other buttons during long recording
        PauseButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        SettingsButton.IsEnabled = false;
        HistoryButton.IsEnabled = false;
        CalibrateButton.IsEnabled = false;

        _updateTimer.Start();
        _recordingTimer.Start();

        if (!IsVisible) ShowWithoutActivation();
    }

    private const double NormalWidth = 500;
    private const double PttWidth = 280;

    public void ShowPushToTalk()
    {
        _isLongRecording = true; // Reuse long recording timer logic
        _isRecording = true;
        _longRecordingStartTime = DateTime.Now;

        StatusText.Text = "Push to Talk";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(100, 200, 255)); // Cyan/teal
        TimerText.Text = "0:00";
        TimerText.IsVisible = true;
        _targetOpacity = ActiveOpacity;

        // Compact layout: hide all normal buttons, show only Pin
        Width = PttWidth;
        LongRecordButton.IsVisible = false;
        PauseButton.IsVisible = false;
        StopButton.IsVisible = false;
        HistoryButton.IsVisible = false;
        CalibrateButton.IsVisible = false;
        SettingsButton.IsVisible = false;
        PinButton.IsVisible = true;

        _updateTimer.Start();
        _recordingTimer.Start();

        // Center the narrow bar on screen
        if (Screens.Primary is { } screen)
        {
            var workArea = screen.WorkingArea;
            Position = new PixelPoint(
                workArea.X + (workArea.Width - (int)PttWidth) / 2,
                Position.Y >= 0 ? Position.Y : workArea.Y + workArea.Height - (int)Height - 40
            );
        }

        if (!IsVisible && !_isOffScreen) ShowWithoutActivation();
        if (_isOffScreen)
        {
            // Bring back from off-screen
            _isOffScreen = false;
            if (Screens.Primary is { } s)
            {
                var wa = s.WorkingArea;
                Position = new PixelPoint(
                    wa.X + (wa.Width - (int)PttWidth) / 2,
                    wa.Y + wa.Height - (int)Height - 40
                );
            }
        }
    }

    public void ShowPushToTalkStopped()
    {
        _isLongRecording = false;
        // Don't call ShowNormalButtons() here — keep narrow width through
        // the transcribing/typing phase. Width is restored when transitioning
        // to ShowListening() (pin case) or HideCompletely() (normal PTT).
    }

    /// <summary>
    /// Restores the normal button layout and full width (hides Pin, shows all standard buttons).
    /// </summary>
    private void ShowNormalButtons()
    {
        Width = NormalWidth;
        PinButton.IsVisible = false;
        RetryButton.IsVisible = false;
        DiscardButton.IsVisible = false;
        SaveAudioButton.IsVisible = false;
        CancelButton.IsVisible = false;
        LongRecordButton.IsVisible = _longRecordButtonConfigured;
        PauseButton.IsVisible = true;
        StopButton.IsVisible = true;
        HistoryButton.IsVisible = true;
        CalibrateButton.IsVisible = true;
        SettingsButton.IsVisible = true;
    }

    /// <param name="errorMessage">Short summary shown on the bar.</param>
    /// <param name="details">Longer text (one line per backend) shown on hover.</param>
    public void ShowTranscriptionFailed(string? errorMessage = null, string? details = null)
    {
        _isRecording = false;
        StatusText.Text = string.IsNullOrWhiteSpace(errorMessage)
            ? "Transcription failed"
            : $"Failed: {errorMessage}";
        ToolTip.SetTip(StatusText, string.IsNullOrWhiteSpace(details) ? null : details);
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 100, 100));
        TimerText.IsVisible = false;
        Opacity = ActiveOpacity;
        _targetOpacity = ActiveOpacity;

        _recordingTimer.Stop();
        _updateTimer.Start();

        Width = NormalWidth;
        LongRecordButton.IsVisible = false;
        PauseButton.IsVisible = false;
        StopButton.IsVisible = false;
        HistoryButton.IsVisible = false;
        CalibrateButton.IsVisible = false;
        SettingsButton.IsVisible = false;
        PinButton.IsVisible = false;
        CancelButton.IsVisible = false;
        RetryButton.IsVisible = true;
        DiscardButton.IsVisible = true;
        SaveAudioButton.IsVisible = true;

        if (!IsVisible) ShowWithoutActivation();
    }

    public void ShowLongRecordingStopped()
    {
        _isLongRecording = false;

        // Re-enable buttons
        PauseButton.IsEnabled = true;
        StopButton.IsEnabled = true;
        SettingsButton.IsEnabled = true;
        HistoryButton.IsEnabled = true;
        CalibrateButton.IsEnabled = true;

        // Restore button icon
        LongRecordIcon.IsVisible = true;
        LongRecordStopIcon.IsVisible = false;
    }

    private DispatcherTimer? _startupDismissTimer;

    public void ShowStartupNotification()
    {
        _isRecording = false;
        StatusText.Text = "Whisper Keyboard";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200));
        TimerText.Text = "Look for icon in system tray";
        TimerText.Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140));
        TimerText.FontSize = 12;
        TimerText.FontWeight = FontWeight.Normal;
        TimerText.IsVisible = true;

        // Hide all buttons
        LongRecordButton.IsVisible = false;
        PauseButton.IsVisible = false;
        StopButton.IsVisible = false;
        HistoryButton.IsVisible = false;
        CalibrateButton.IsVisible = false;
        SettingsButton.IsVisible = false;
        PinButton.IsVisible = false;

        Width = 320;
        Opacity = 0;
        _targetOpacity = 0.85;
        _updateTimer.Start();

        ResetToDefaultPosition();
        ShowWithoutActivation();

        // Auto-dismiss after 3 seconds
        _startupDismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _startupDismissTimer.Tick += (s, e) =>
        {
            _startupDismissTimer.Stop();
            _startupDismissTimer = null;
            // Fade out then hide
            _targetOpacity = 0;
            _fadeOutDelayStart = DateTime.Now.AddMilliseconds(-FadeOutDelayMs); // Skip delay

            // Schedule hide after fade completes
            var hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            hideTimer.Tick += (s2, e2) =>
            {
                ((DispatcherTimer)s2!).Stop();
                _updateTimer.Stop();
                // Restore normal timer text styling
                TimerText.Foreground = new SolidColorBrush(Color.FromRgb(100, 255, 100));
                TimerText.FontSize = 18;
                TimerText.FontWeight = FontWeight.Bold;
                TimerText.IsVisible = false;
                ShowNormalButtons();
                HideCompletely();
            };
            hideTimer.Start();
        };
        _startupDismissTimer.Start();
    }

    public void DismissStartupNotification()
    {
        if (_startupDismissTimer != null)
        {
            _startupDismissTimer.Stop();
            _startupDismissTimer = null;
        }
        // Restore normal timer text styling
        TimerText.Foreground = new SolidColorBrush(Color.FromRgb(100, 255, 100));
        TimerText.FontSize = 18;
        TimerText.FontWeight = FontWeight.Bold;
        TimerText.IsVisible = false;
        ShowNormalButtons();
    }

    public void HideCompletely()
    {
        _updateTimer.Stop();
        _recordingTimer.Stop();

        if (_hasBeenShown && (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)))
        {
            // Move off-screen instead of hiding to avoid focus-stealing on re-show
            _savedPosition = Position;
            _isOffScreen = true;
            Opacity = 0;
            Position = new PixelPoint(-10000, -10000);
        }
        else
        {
            Hide();
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Prevent closing, just hide
        e.Cancel = true;
        HideCompletely();
    }
}
