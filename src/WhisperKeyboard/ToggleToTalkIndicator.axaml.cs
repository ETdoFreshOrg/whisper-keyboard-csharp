using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace WhisperKeyboard;

public partial class ToggleToTalkIndicator : Window
{
    private readonly DispatcherTimer _timerTick;
    private readonly DispatcherTimer _waveformTimer;
    private const int MaxVolumeHistory = 40;
    private readonly double[] _volumeHistory = new double[MaxVolumeHistory];
    private int _volumeIndex;
    private double _currentVolume;
    private DateTime _recordingStart;
    private bool _isRecording;
    private bool _isPinned;

    private bool _isDragging;
    private PixelPoint _dragStartScreenPoint;
    private PixelPoint _windowStartPosition;
    private bool _hasBeenShown;
    private bool _isOffScreen;
    private PixelPoint _savedPosition;

    public event Action? OnToggleClicked;
    public event Action? OnPinClicked;

    public bool IsPinned => _isPinned;

    public ToggleToTalkIndicator()
    {
        InitializeComponent();

        // macOS: the first Show() must not pull focus from the app being dictated into.
        if (OperatingSystem.IsMacOS())
            ShowActivated = false;

        _timerTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timerTick.Tick += (s, e) => UpdateTimerText();

        _waveformTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _waveformTimer.Tick += (s, e) =>
        {
            _volumeHistory[_volumeIndex] = _currentVolume;
            _volumeIndex = (_volumeIndex + 1) % MaxVolumeHistory;
            DrawWaveform();
        };
        _waveformTimer.Start();

        ToggleButton.Click += (s, e) => OnToggleClicked?.Invoke();
        PinButton.Click += (s, e) => { SetPinned(!_isPinned); OnPinClicked?.Invoke(); };

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;

        Opened += (s, e) =>
        {
            MakeNonActivating();
            _hasBeenShown = true;
        };

        // No pre-init at startup — would steal focus from the user's current window.
        // We defer the first Show() to the moment the user actually triggers recording,
        // at which point our process is not foreground and Windows blocks activation.
        _savedPosition = ComputeDefaultPosition();
    }

    public void ShowRecording()
    {
        _isRecording = true;
        _recordingStart = DateTime.Now;
        Array.Clear(_volumeHistory, 0, _volumeHistory.Length);
        _volumeIndex = 0;
        StartIcon.IsVisible = false;
        StopIcon.IsVisible = true;
        StatusText.Text = "Recording";
        UpdateTimerText();
        _timerTick.Start();
        EnsureVisible();
    }

    public void ShowStopped()
    {
        _isRecording = false;
        StartIcon.IsVisible = true;
        StopIcon.IsVisible = false;
        StatusText.Text = "Toggle-to-Talk";
        _timerTick.Stop();
        if (!_isPinned)
        {
            HideToOffScreen();
        }
    }

    /// <summary>Escape threw the recording away: say so briefly, then settle as stopped.</summary>
    public async void ShowCancelled()
    {
        _isRecording = false;
        _timerTick.Stop();
        StartIcon.IsVisible = true;
        StopIcon.IsVisible = false;
        StatusText.Text = "Cancelled";
        await System.Threading.Tasks.Task.Delay(800);
        if (!_isRecording) ShowStopped();
    }

    public void SetPinned(bool pinned)
    {
        _isPinned = pinned;
        PinIcon.Fill = new SolidColorBrush(pinned ? Color.Parse("#64FF64") : Color.Parse("#B4B4B4"));
        if (pinned)
        {
            EnsureVisible();
        }
        else if (!_isRecording)
        {
            HideToOffScreen();
        }
    }

    public void UpdateVolume(double volume)
    {
        _currentVolume = volume;
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
            double normalizedVolume = Math.Min(1.0, vol / 400.0);
            double barHeight = Math.Max(2, normalizedVolume * (height - 4));

            Color barColor = _isRecording
                ? Color.FromRgb(255, (byte)(100 + normalizedVolume * 100), 100)
                : Color.FromRgb(90, 60, 60);

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
    }

    private void UpdateTimerText()
    {
        var elapsed = DateTime.Now - _recordingStart;
        TimerText.Text = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";
    }

    private void EnsureVisible()
    {
        if (!_hasBeenShown)
        {
            IntPtr prevForeground = IntPtr.Zero;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                prevForeground = GetForegroundWindow();

            Position = _savedPosition;
            Show();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && prevForeground != IntPtr.Zero)
            {
                Dispatcher.UIThread.Post(() => RestoreForeground(prevForeground), DispatcherPriority.Background);
            }
            return;
        }

        Position = _savedPosition;
        _isOffScreen = false;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd != IntPtr.Zero)
            {
                var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                if ((exStyle & WS_EX_NOACTIVATE) == 0)
                    SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);
                ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                SetWindowPos(hwnd, HWND_TOPMOST, _savedPosition.X, _savedPosition.Y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
        }
    }

    private void HideToOffScreen()
    {
        if (!_hasBeenShown) return;
        // Save current position (user may have dragged) then move off-screen.
        // Don't call Hide() - that would destroy the activation state.
        if (!_isOffScreen)
        {
            _savedPosition = Position;
        }
        Position = new PixelPoint(-20000, -20000);
        _isOffScreen = true;
    }

    private PixelPoint ComputeDefaultPosition()
    {
        var screen = Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        int x = screen.X + (screen.Width - (int)Width) / 2;
        int y = screen.Y + screen.Height - (int)Height - 40;
        return new PixelPoint(x, y);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            _dragStartScreenPoint = this.PointToScreen(e.GetPosition(this));
            _windowStartPosition = Position;
            e.Pointer.Capture(this);
        }
    }
    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging) return;
        var cur = this.PointToScreen(e.GetPosition(this));
        var dx = cur.X - _dragStartScreenPoint.X;
        var dy = cur.Y - _dragStartScreenPoint.Y;
        Position = new PixelPoint(_windowStartPosition.X + dx, _windowStartPosition.Y + dy);
    }
    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isDragging = false;
        e.Pointer.Capture(null);
    }

    private void MakeNonActivating()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            MacOSHelper.MakeNonActivatingPanel(this);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero) return;
            var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);
        }
        catch { }
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_HIDE = 0;
    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    private static void RestoreForeground(IntPtr hwnd)
    {
        // Windows blocks SetForegroundWindow when the calling process didn't own input recently.
        // AttachThreadInput trick lets us hand foreground back to the previous app reliably.
        try
        {
            uint targetThread = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
            uint currentThread = GetCurrentThreadId();
            if (targetThread == 0 || targetThread == currentThread)
            {
                SetForegroundWindow(hwnd);
                return;
            }
            AttachThreadInput(currentThread, targetThread, true);
            SetForegroundWindow(hwnd);
            AttachThreadInput(currentThread, targetThread, false);
        }
        catch { }
    }
}
