using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using AvaloniaShapes = Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Win32;
using WhisperKeyboard.Core;
using WhisperKeyboard.Core.Transcription;

namespace WhisperKeyboard;

public partial class SettingsWindow : Window
{
    private readonly Config _config;
    private readonly TranscriptionHistory? _history;
    private readonly IAudioCapture? _audioCapture;
    private readonly int _initialTab;
    private TextBox? _activeHotkeyBox;
    private double _currentVolume;

    // Speech-to-text backend list being edited (saved on OK).
    private readonly List<string> _backendOrder = new();
    private readonly Dictionary<string, bool> _backendEnabled = new();
    private readonly Dictionary<string, TextBlock> _backendTestStatus = new();

    public event EventHandler? SettingsSaved;

    public SettingsWindow() : this(Config.Load(), null) { }

    public SettingsWindow(Config config, TranscriptionHistory? history = null, IAudioCapture? audioCapture = null, int initialTab = 0)
    {
        InitializeComponent();
        _config = config;
        _history = history;
        _audioCapture = audioCapture;
        _initialTab = initialTab;

        // Threshold slider label update
        ThresholdSlider.PropertyChanged += (s, e) =>
        {
            if (e.Property.Name == "Value")
            {
                ThresholdLabel.Text = ((int)ThresholdSlider.Value).ToString();
                DrawVolumeMeter();
            }
        };

        // Show/hide API key toggle
        ShowApiKeyCheck.IsCheckedChanged += (s, e) =>
        {
            ApiKeyBox.PasswordChar = ShowApiKeyCheck.IsChecked == true ? '\0' : '*';
        };

        // Show custom URL textbox only when "Custom..." is selected
        ApiProviderBox.SelectionChanged += (s, e) =>
        {
            ApiBaseUrlBox.IsVisible = (ApiProviderBox.SelectedItem is ComboBoxItem item
                && item.Tag?.ToString() == "custom");
        };

        // Show space hold panel only when VK_SPACE is enabled
        UseVirtualSpaceKeyCheck.IsCheckedChanged += (s, e) =>
        {
            VirtualSpaceHoldPanel.IsVisible = UseVirtualSpaceKeyCheck.IsChecked == true;
        };

        LoadSettings();

        if (_initialTab > 0)
        {
            MainTabControl.SelectedIndex = _initialTab;
        }

        // Start volume monitoring
        if (_audioCapture != null)
        {
            _audioCapture.VolumeChanged += OnVolumeChanged;
        }

        Closed += OnWindowClosed;
    }

    private void OnVolumeChanged(object? sender, double volume)
    {
        _currentVolume = volume;
        Dispatcher.UIThread.Post(DrawVolumeMeter);
    }

    private void DrawVolumeMeter()
    {
        if (VolumeMeterCanvas == null) return;

        VolumeMeterCanvas.Children.Clear();

        var width = VolumeMeterCanvas.Bounds.Width;
        var height = VolumeMeterCanvas.Bounds.Height;
        if (width <= 0 || height <= 0) return;

        var threshold = ThresholdSlider.Value;
        var maxValue = ThresholdSlider.Maximum;

        // Draw background
        var bg = new AvaloniaShapes.Rectangle
        {
            Width = width,
            Height = height,
            Fill = new SolidColorBrush(Color.FromRgb(30, 30, 30))
        };
        VolumeMeterCanvas.Children.Add(bg);

        // Draw volume bar
        var volumeRatio = Math.Min(1.0, _currentVolume / maxValue);
        var volumeWidth = volumeRatio * width;

        if (volumeWidth > 0)
        {
            // Green below threshold, red above
            var thresholdX = (threshold / maxValue) * width;

            // Green portion (below threshold)
            var greenWidth = Math.Min(volumeWidth, thresholdX);
            if (greenWidth > 0)
            {
                var greenBar = new AvaloniaShapes.Rectangle
                {
                    Width = greenWidth,
                    Height = height,
                    Fill = new SolidColorBrush(Color.FromRgb(50, 180, 50))
                };
                VolumeMeterCanvas.Children.Add(greenBar);
            }

            // Red portion (above threshold)
            if (volumeWidth > thresholdX)
            {
                var redBar = new AvaloniaShapes.Rectangle
                {
                    Width = volumeWidth - thresholdX,
                    Height = height,
                    Fill = new SolidColorBrush(Color.FromRgb(220, 60, 60))
                };
                Canvas.SetLeft(redBar, thresholdX);
                VolumeMeterCanvas.Children.Add(redBar);
            }
        }

        // Draw threshold marker line
        var thresholdPos = (threshold / maxValue) * width;
        var markerLine = new AvaloniaShapes.Line
        {
            StartPoint = new Point(thresholdPos, 0),
            EndPoint = new Point(thresholdPos, height),
            Stroke = new SolidColorBrush(Color.FromRgb(255, 200, 50)),
            StrokeThickness = 2
        };
        VolumeMeterCanvas.Children.Add(markerLine);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_audioCapture != null)
        {
            _audioCapture.VolumeChanged -= OnVolumeChanged;
        }
    }

    private void LoadSettings()
    {
        // API Key (only show if set in config, not from env)
        if (!string.IsNullOrEmpty(_config.ApiKey) &&
            Environment.GetEnvironmentVariable("OPENAI_API_KEY") != _config.ApiKey)
        {
            ApiKeyBox.Text = _config.ApiKey;
        }

        // API Base URL — match preset, otherwise show as Custom
        var savedUrl = (_config.ApiBaseUrl ?? "").TrimEnd('/');
        int matchedIndex = -1;
        for (int i = 0; i < ApiProviderBox.Items.Count; i++)
        {
            if (ApiProviderBox.Items[i] is ComboBoxItem item)
            {
                var tag = item.Tag?.ToString() ?? "";
                if (tag != "custom" && tag.TrimEnd('/') == savedUrl)
                {
                    matchedIndex = i;
                    break;
                }
            }
        }
        if (matchedIndex >= 0)
        {
            ApiProviderBox.SelectedIndex = matchedIndex;
            ApiBaseUrlBox.Text = "";
            ApiBaseUrlBox.IsVisible = false;
        }
        else
        {
            ApiProviderBox.SelectedIndex = ApiProviderBox.Items.Count - 1; // Custom...
            ApiBaseUrlBox.Text = _config.ApiBaseUrl;
            ApiBaseUrlBox.IsVisible = true;
        }

        // Model
        ModelBox.Text = _config.Model;

        // Speech-to-text backends
        _backendOrder.Clear();
        _backendOrder.AddRange(_config.Transcription.NormalizedOrder());
        foreach (var id in TranscriptionSettings.AllBackendIds)
            _backendEnabled[id] = _config.Transcription.For(id).Enabled;
        RenderBackendList();

        // Language
        for (int i = 0; i < LanguageBox.Items.Count; i++)
        {
            if (LanguageBox.Items[i] is ComboBoxItem item &&
                item.Tag?.ToString() == _config.Language)
            {
                LanguageBox.SelectedIndex = i;
                break;
            }
        }

        // Populate audio devices
        try
        {
            List<string> devices;
            if (OperatingSystem.IsWindows())
            {
                devices = new List<string>();
                for (int i = 0; i < NAudio.Wave.WaveInEvent.DeviceCount; i++)
                {
                    var capabilities = NAudio.Wave.WaveInEvent.GetCapabilities(i);
                    devices.Add(capabilities.ProductName);
                }
            }
            else
            {
                devices = OpenALNative.GetCaptureDeviceNames();
            }

            // Default follows the system setting; mics are stored by name ("" = default).
            DeviceBox.Items.Clear();
            DeviceBox.Items.Add(new ComboBoxItem { Content = "Default (follows system setting)", Tag = "" });
            foreach (var device in devices)
            {
                DeviceBox.Items.Add(new ComboBoxItem { Content = device, Tag = device });
            }
            // Keep a saved mic selectable while it's unplugged, so saving doesn't lose it.
            if (!string.IsNullOrEmpty(_config.DeviceName) && !devices.Contains(_config.DeviceName))
            {
                DeviceBox.Items.Add(new ComboBoxItem { Content = $"{_config.DeviceName} (not connected)", Tag = _config.DeviceName });
            }
            DeviceBox.SelectedIndex = Math.Max(0, DeviceBox.Items.Cast<ComboBoxItem>()
                .ToList().FindIndex(item => (string?)item.Tag == _config.DeviceName));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to enumerate devices: {ex.Message}");
            DeviceBox.Items.Add(new ComboBoxItem { Content = "Default (follows system setting)", Tag = "" });
            DeviceBox.SelectedIndex = 0;
        }

        // Threshold
        ThresholdSlider.Value = _config.VadThreshold;
        ThresholdLabel.Text = _config.VadThreshold.ToString();

        // Audio duration settings
        MinAudioDuration.Value = (decimal)_config.MinAudioDuration;
        MaxSilenceDuration.Value = (decimal)_config.MaxSilenceDuration;

        // Pre-roll / post-roll settings
        PreRollEnabledCheck.IsChecked = _config.PreRollEnabled;
        PreRollSeconds.Value = (decimal)_config.PreRollSeconds;
        PostRollSeconds.Value = (decimal)_config.PostRollSeconds;

        // Typing settings
        PasteModeCheck.IsChecked = _config.PasteMode;
        TypingDelayMsBox.Value = _config.TypingDelayMs;
        UseVirtualSpaceKeyCheck.IsChecked = _config.UseVirtualSpaceKey;
        VirtualSpaceHoldMsBox.Value = _config.VirtualSpaceHoldMs;
        VirtualSpaceHoldPanel.IsVisible = _config.UseVirtualSpaceKey;
        PunctuationCheck.IsChecked = _config.AddPunctuation;
        CapitalizeCheck.IsChecked = _config.CapitalizeSentences;
        AutoEnterCheck.IsChecked = _config.AutoEnter;
        ExitWordsCheck.IsChecked = _config.ExitWordsEnabled;
        ExitWordsBox.Text = string.Join(", ", _config.ExitWords);

        // Wake/Pause word settings
        WakeWordsCheck.IsChecked = _config.WakeWordsEnabled;
        WakeWordsBox.Text = string.Join(", ", _config.WakeWords);
        PauseWordsBox.Text = string.Join(", ", _config.PauseWords);

        // General settings
        StartOnLoginCheck.IsChecked = _config.StartOnLogin;
        StartListeningOnLaunchCheck.IsChecked = _config.StartListeningOnLaunch;
        StartListeningPausedCheck.IsChecked = _config.StartListeningPaused;
        ShowNotificationsCheck.IsChecked = _config.ShowNotifications;
        ConfigPathLabel.Text = Config.GetConfigPath();

        // Hotkey settings
        ToggleRecordingHotkeyBox.Text = _config.ToggleRecordingHotkey;
        PauseResumeHotkeyBox.Text = _config.PauseResumeHotkey;
        OpenSettingsHotkeyBox.Text = _config.OpenSettingsHotkey;
        LongRecordHotkeyBox.Text = _config.LongRecordHotkey;

        // Push-to-talk keys
        PopulatePushToTalkBox(PushToTalkKeysBox, _config.PushToTalkKeys);
        PopulatePushToTalkBox(PushToTalkNoEnterKeysBox, _config.PushToTalkNoEnterKeys);

        // Long recording settings
        ShowLongRecordButtonCheck.IsChecked = _config.ShowLongRecordButton;
        MaxLongRecordMinutes.Value = _config.MaxLongRecordMinutes;

        // Load history
        LoadHistory();
    }

    /// <summary>
    /// Push-to-talk choices: every combination of the right-hand modifiers (fewest
    /// keys first) except Right Shift alone, which would fire on every capital letter,
    /// plus Left Shift + Left Control and Fn (macOS only). Tags are normalized specs.
    /// </summary>
    private static List<string> PushToTalkPresets()
    {
        string[] right = { "RightShift", "RightCommand", "RightOption", "RightControl" };
        var combos = new List<string>();
        for (int mask = 1; mask < 1 << right.Length; mask++)
        {
            var keys = right.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
            if (keys is ["RightShift"]) continue;
            combos.Add(PushToTalkKeySpec.Normalize(string.Join("+", keys)));
        }

        var presets = combos.OrderBy(c => c.Count(ch => ch == '+')).ToList();
        presets.Add(PushToTalkKeySpec.Normalize("LeftShift+LeftControl"));
        if (!OperatingSystem.IsWindows()) presets.Add("Fn");
        return presets;
    }

    private static void PopulatePushToTalkBox(ComboBox box, string configured)
    {
        bool windows = OperatingSystem.IsWindows();
        var current = PushToTalkKeySpec.Normalize(configured);
        var presets = PushToTalkPresets();
        // A hand-edited combination that isn't a preset stays selectable.
        if (current != "" && !presets.Contains(current)) presets.Add(current);

        box.Items.Clear();
        box.Items.Add(new ComboBoxItem { Content = "Off", Tag = "" });
        foreach (var spec in presets)
        {
            box.Items.Add(new ComboBoxItem { Content = PushToTalkKeySpec.Describe(spec, windows), Tag = spec });
        }
        box.SelectedIndex = Math.Max(0, box.Items.Cast<ComboBoxItem>()
            .ToList().FindIndex(item => (string?)item.Tag == current));
    }

    private void LoadHistory()
    {
        if (_history != null)
        {
            HistoryList.ItemsSource = _history.GetEntries();
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        // Save API key if changed
        if (!string.IsNullOrEmpty(ApiKeyBox.Text))
        {
            _config.ApiKey = ApiKeyBox.Text;
        }

        // API Base URL — preset tag or custom textbox
        if (ApiProviderBox.SelectedItem is ComboBoxItem providerItem)
        {
            var tag = providerItem.Tag?.ToString() ?? "";
            if (tag == "custom")
            {
                _config.ApiBaseUrl = string.IsNullOrWhiteSpace(ApiBaseUrlBox.Text)
                    ? "https://stt.etdofresh.com"
                    : ApiBaseUrlBox.Text.Trim();
            }
            else
            {
                _config.ApiBaseUrl = tag;
            }
        }

        // Model
        _config.Model = string.IsNullOrWhiteSpace(ModelBox.Text) ? "whisper-1" : ModelBox.Text.Trim();

        // Speech-to-text backends
        _config.Transcription.Order = new List<string>(_backendOrder);
        foreach (var (id, enabled) in _backendEnabled)
            _config.Transcription.For(id).Enabled = enabled;

        // Language
        if (LanguageBox.SelectedItem is ComboBoxItem langItem)
        {
            _config.Language = langItem.Tag?.ToString() ?? "en";
        }

        // Device
        if (DeviceBox.SelectedItem is ComboBoxItem deviceItem)
        {
            _config.DeviceName = (string?)deviceItem.Tag ?? "";
        }

        // Threshold
        _config.VadThreshold = (int)ThresholdSlider.Value;

        // Audio duration
        _config.MinAudioDuration = (double)(MinAudioDuration.Value ?? 1);
        _config.MaxSilenceDuration = (double)(MaxSilenceDuration.Value ?? 1);

        // Pre-roll / post-roll
        _config.PreRollEnabled = PreRollEnabledCheck.IsChecked ?? true;
        _config.PreRollSeconds = (double)(PreRollSeconds.Value ?? 1);
        _config.PostRollSeconds = (double)(PostRollSeconds.Value ?? 1);

        // Typing settings
        _config.PasteMode = PasteModeCheck.IsChecked ?? true;
        _config.TypingDelayMs = (int)(TypingDelayMsBox.Value ?? 0);
        _config.UseVirtualSpaceKey = UseVirtualSpaceKeyCheck.IsChecked ?? false;
        _config.VirtualSpaceHoldMs = (int)(VirtualSpaceHoldMsBox.Value ?? 15);
        _config.AddPunctuation = PunctuationCheck.IsChecked ?? true;
        _config.CapitalizeSentences = CapitalizeCheck.IsChecked ?? true;
        _config.AutoEnter = AutoEnterCheck.IsChecked ?? false;
        _config.ExitWordsEnabled = ExitWordsCheck.IsChecked ?? true;
        _config.ExitWords = (ExitWordsBox.Text ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        // Wake/Pause word settings
        _config.WakeWordsEnabled = WakeWordsCheck.IsChecked ?? false;
        _config.WakeWords = (WakeWordsBox.Text ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        _config.PauseWords = (PauseWordsBox.Text ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        // General settings
        var startOnLogin = StartOnLoginCheck.IsChecked ?? false;
        if (_config.StartOnLogin != startOnLogin)
        {
            _config.StartOnLogin = startOnLogin;
            SetStartOnLogin(startOnLogin);
        }
        _config.StartListeningOnLaunch = StartListeningOnLaunchCheck.IsChecked ?? true;
        _config.StartListeningPaused = StartListeningPausedCheck.IsChecked ?? false;
        _config.ShowNotifications = ShowNotificationsCheck.IsChecked ?? true;

        // Hotkey settings
        _config.ToggleRecordingHotkey = ToggleRecordingHotkeyBox.Text?.Trim() ?? "";
        _config.PauseResumeHotkey = PauseResumeHotkeyBox.Text?.Trim() ?? "";
        _config.OpenSettingsHotkey = OpenSettingsHotkeyBox.Text?.Trim() ?? "";
        _config.LongRecordHotkey = LongRecordHotkeyBox.Text?.Trim() ?? "";

        // Push-to-talk keys ("" = off)
        _config.PushToTalkKeys = (PushToTalkKeysBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        _config.PushToTalkNoEnterKeys = (PushToTalkNoEnterKeysBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

        // Long recording settings
        _config.ShowLongRecordButton = ShowLongRecordButtonCheck.IsChecked ?? false;
        _config.MaxLongRecordMinutes = (int)(MaxLongRecordMinutes.Value ?? 30);

        _config.Save();
        SettingsSaved?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// Resolve the STT base URL from the current (unsaved) UI selection.
    /// </summary>
    private string ResolveBaseUrlFromUi()
    {
        if (ApiProviderBox.SelectedItem is ComboBoxItem providerItem)
        {
            var tag = providerItem.Tag?.ToString() ?? "";
            if (tag == "custom")
            {
                return string.IsNullOrWhiteSpace(ApiBaseUrlBox.Text)
                    ? "https://stt.etdofresh.com"
                    : ApiBaseUrlBox.Text.Trim();
            }
            return tag;
        }
        return _config.ApiBaseUrl;
    }

    private async void OnTestEndpointClick(object? sender, RoutedEventArgs e)
    {
        var baseUrl = ResolveBaseUrlFromUi();
        var apiKey = !string.IsNullOrWhiteSpace(ApiKeyBox.Text)
            ? ApiKeyBox.Text.Trim()
            : (Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? _config.ApiKey);
        var model = string.IsNullOrWhiteSpace(ModelBox.Text) ? "whisper-1" : ModelBox.Text.Trim();

        TestEndpointButton.IsEnabled = false;
        TestEndpointStatus.Foreground = new SolidColorBrush(Color.Parse("#888888"));
        TestEndpointStatus.Text = $"Testing {baseUrl} …";

        try
        {
            var (ok, message) = await SpeechTranscriber.TestEndpointAsync(baseUrl, apiKey, model);
            TestEndpointStatus.Foreground = new SolidColorBrush(ok ? Color.Parse("#3CB371") : Color.Parse("#E05050"));
            TestEndpointStatus.Text = message;
        }
        catch (Exception ex)
        {
            TestEndpointStatus.Foreground = new SolidColorBrush(Color.Parse("#E05050"));
            TestEndpointStatus.Text = $"Error: {ex.Message}";
        }
        finally
        {
            TestEndpointButton.IsEnabled = true;
        }
    }

    private static string BackendLabel(string id) => id switch
    {
        TranscriptionSettings.ChatGpt => "ChatGPT (Codex sign-in)",
        TranscriptionSettings.Lan => "Your server (below)",
        TranscriptionSettings.System => OperatingSystem.IsMacOS() ? "macOS speech (Apple)" : "Windows speech (offline)",
        _ => id
    };

    /// <summary>One row per backend: enable checkbox, move up/down, Test, and a status line.</summary>
    private void RenderBackendList()
    {
        BackendListPanel.Children.Clear();
        _backendTestStatus.Clear();

        for (int i = 0; i < _backendOrder.Count; i++)
        {
            var id = _backendOrder[i];
            var index = i;

            var check = new CheckBox
            {
                Content = $"{i + 1}. {BackendLabel(id)}",
                IsChecked = _backendEnabled.GetValueOrDefault(id, true),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            check.IsCheckedChanged += (s, e) => _backendEnabled[id] = check.IsChecked == true;

            var up = new Button { Content = "▲", Padding = new Thickness(8, 2), IsEnabled = index > 0 };
            up.Click += (s, e) => MoveBackend(index, -1);
            ToolTip.SetTip(up, "Try earlier");

            var down = new Button { Content = "▼", Padding = new Thickness(8, 2), IsEnabled = index < _backendOrder.Count - 1 };
            down.Click += (s, e) => MoveBackend(index, +1);
            ToolTip.SetTip(down, "Try later");

            var test = new Button { Content = "Test", Padding = new Thickness(10, 2) };
            var status = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#888888")), TextWrapping = TextWrapping.Wrap, IsVisible = false };
            test.Click += async (s, e) => await TestBackendAsync(id, test, status);
            _backendTestStatus[id] = status;

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,4,Auto,4,Auto") };
            Grid.SetColumn(check, 0);
            Grid.SetColumn(up, 1);
            Grid.SetColumn(down, 3);
            Grid.SetColumn(test, 5);
            row.Children.Add(check);
            row.Children.Add(up);
            row.Children.Add(down);
            row.Children.Add(test);

            var block = new StackPanel { Spacing = 2 };
            block.Children.Add(row);
            block.Children.Add(status);
            BackendListPanel.Children.Add(block);
        }
    }

    private void MoveBackend(int index, int delta)
    {
        var target = index + delta;
        if (target < 0 || target >= _backendOrder.Count) return;
        (_backendOrder[index], _backendOrder[target]) = (_backendOrder[target], _backendOrder[index]);
        RenderBackendList();
    }

    /// <summary>Build a backend from the current (unsaved) UI values and send it a short silent clip.</summary>
    private async Task TestBackendAsync(string id, Button button, TextBlock status)
    {
        var language = LanguageBox.SelectedItem is ComboBoxItem langItem ? langItem.Tag?.ToString() ?? "en" : _config.Language;
        SpeechTranscriber? server = null;
        ChatGptTranscriber? chatGpt = null;
        ITranscriber backend;
        switch (id)
        {
            case TranscriptionSettings.ChatGpt:
                chatGpt = new ChatGptTranscriber(() => ChatGptTranscriber.ResolveAuthPath(_config.Transcription.ChatGptBackend.CodexAuthPath));
                backend = chatGpt;
                break;
            case TranscriptionSettings.Lan:
                server = new SpeechTranscriber(new Config
                {
                    ApiBaseUrl = ResolveBaseUrlFromUi(),
                    ApiKey = !string.IsNullOrWhiteSpace(ApiKeyBox.Text)
                        ? ApiKeyBox.Text.Trim()
                        : (Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? _config.ApiKey),
                    Model = string.IsNullOrWhiteSpace(ModelBox.Text) ? "whisper-1" : ModelBox.Text.Trim(),
                    Language = language
                });
                backend = new LanTranscriber(server);
                break;
            default:
                backend = SystemTranscriber.ForCurrentPlatform();
                break;
        }

        button.IsEnabled = false;
        status.IsVisible = true;
        status.Foreground = new SolidColorBrush(Color.Parse("#888888"));
        status.Text = "Testing…";
        try
        {
            var (ok, message) = await TranscriptionChain.TestBackendAsync(backend, language, TimeSpan.FromSeconds(30));
            status.Foreground = new SolidColorBrush(ok ? Color.Parse("#3CB371") : Color.Parse("#E05050"));
            status.Text = message;
        }
        finally
        {
            button.IsEnabled = true;
            server?.Dispose();
            chatGpt?.Dispose();
        }
    }

    private async void OnHistoryCopyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is HistoryEntry entry)
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(entry.FullText);

                // Show feedback
                var originalContent = button.Content;
                button.Content = "Copied!";
                button.IsEnabled = false;

                await Task.Delay(800);

                button.Content = originalContent;
                button.IsEnabled = true;
            }
        }
    }

    private void OnClearHistoryClick(object? sender, RoutedEventArgs e)
    {
        _history?.Clear();
        HistoryList.ItemsSource = null;
    }

    private void OnClearHotkeyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string tag)
        {
            switch (tag)
            {
                case "ToggleRecording":
                    ToggleRecordingHotkeyBox.Clear();
                    break;
                case "PauseResume":
                    PauseResumeHotkeyBox.Clear();
                    break;
                case "OpenSettings":
                    OpenSettingsHotkeyBox.Clear();
                    break;
                case "LongRecord":
                    LongRecordHotkeyBox.Clear();
                    break;
            }
        }
    }

    private void OnHotkeyBoxGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            _activeHotkeyBox = textBox;
            textBox.BorderBrush = new SolidColorBrush(Color.Parse("#0078D4"));
            textBox.BorderThickness = new global::Avalonia.Thickness(2);
            if (string.IsNullOrEmpty(textBox.Text))
            {
                textBox.Watermark = "Press a key combination...";
            }
        }
    }

    private void OnHotkeyBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            _activeHotkeyBox = null;
            textBox.BorderBrush = new SolidColorBrush(Color.Parse("#444444"));
            textBox.BorderThickness = new global::Avalonia.Thickness(1);
            textBox.Watermark = "Click to set hotkey";
        }
    }

    private void OnHotkeyBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox) return;

        e.Handled = true;

        // Escape clears the hotkey
        if (e.Key == Key.Escape)
        {
            textBox.Text = "";
            return;
        }

        // Ignore modifier-only key presses
        if (IsModifierKey(e.Key))
        {
            return;
        }

        // Handle F13-F24 which Avalonia doesn't recognize (use physical key scanning)
        string? hotkey = null;
        if (e.Key == Key.None && e.PhysicalKey != null)
        {
            // Try to map physical key to F13-F24
            var f13to24 = DetectF13toF24(e);
            if (!string.IsNullOrEmpty(f13to24))
            {
                var parts = new List<string>();
                if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Cmd");
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
                if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Option");
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
                parts.Add(f13to24);
                hotkey = parts.Count >= 2 ? string.Join("+", parts) : "";
            }
        }

        // Standard key handling
        if (string.IsNullOrEmpty(hotkey))
        {
            hotkey = BuildHotkeyString(e.KeyModifiers, e.Key);
        }

        if (!string.IsNullOrEmpty(hotkey))
        {
            textBox.Text = hotkey;
        }
    }

    private static string? DetectF13toF24(KeyEventArgs e)
    {
        // F13-F24 virtual key codes: 0x7C-0x87
        // When Avalonia doesn't recognize these, we try to get them from platform data
        // This is a best-effort workaround for extended function keys
        try
        {
            if (e.PhysicalKey != null)
            {
                var physicalKeyStr = e.PhysicalKey.ToString();
                // On Windows, physical key for F13 might be "F13", "IntlBackslash", or similar
                // Check if it contains F + number
                if (physicalKeyStr.StartsWith("F") && int.TryParse(physicalKeyStr.Substring(1), out int fNum))
                {
                    if (fNum >= 13 && fNum <= 24)
                    {
                        return $"F{fNum}";
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static bool IsModifierKey(Key key)
    {
        return key == Key.LeftShift || key == Key.RightShift ||
               key == Key.LeftCtrl || key == Key.RightCtrl ||
               key == Key.LeftAlt || key == Key.RightAlt ||
               key == Key.LWin || key == Key.RWin;
    }

    private static string BuildHotkeyString(KeyModifiers modifiers, Key key)
    {
        var parts = new List<string>();

        // Add modifiers in standard order (macOS convention)
        if (modifiers.HasFlag(KeyModifiers.Meta))
            parts.Add("Cmd");
        if (modifiers.HasFlag(KeyModifiers.Control))
            parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt))
            parts.Add("Option");
        if (modifiers.HasFlag(KeyModifiers.Shift))
            parts.Add("Shift");

        // Convert key to display string
        var keyName = GetKeyName(key);
        if (!string.IsNullOrEmpty(keyName))
        {
            parts.Add(keyName);
        }

        // Require at least one modifier for a valid hotkey, except for F13-F24
        bool isF13to24 = !string.IsNullOrEmpty(keyName) && keyName.StartsWith("F") &&
                         int.TryParse(keyName.Substring(1), out int fNum) &&
                         fNum >= 13 && fNum <= 24;

        if (parts.Count < 2 && !isF13to24)
        {
            return "";
        }

        return string.Join("+", parts);
    }

    private static string? GetKeyName(Key key)
    {
        return key switch
        {
            // Letters
            Key.A => "A", Key.B => "B", Key.C => "C", Key.D => "D", Key.E => "E",
            Key.F => "F", Key.G => "G", Key.H => "H", Key.I => "I", Key.J => "J",
            Key.K => "K", Key.L => "L", Key.M => "M", Key.N => "N", Key.O => "O",
            Key.P => "P", Key.Q => "Q", Key.R => "R", Key.S => "S", Key.T => "T",
            Key.U => "U", Key.V => "V", Key.W => "W", Key.X => "X", Key.Y => "Y",
            Key.Z => "Z",

            // Numbers
            Key.D0 => "0", Key.D1 => "1", Key.D2 => "2", Key.D3 => "3", Key.D4 => "4",
            Key.D5 => "5", Key.D6 => "6", Key.D7 => "7", Key.D8 => "8", Key.D9 => "9",

            // Function keys
            Key.F1 => "F1", Key.F2 => "F2", Key.F3 => "F3", Key.F4 => "F4",
            Key.F5 => "F5", Key.F6 => "F6", Key.F7 => "F7", Key.F8 => "F8",
            Key.F9 => "F9", Key.F10 => "F10", Key.F11 => "F11", Key.F12 => "F12",
            Key.F13 => "F13", Key.F14 => "F14", Key.F15 => "F15", Key.F16 => "F16",
            Key.F17 => "F17", Key.F18 => "F18", Key.F19 => "F19", Key.F20 => "F20",
            Key.F21 => "F21", Key.F22 => "F22", Key.F23 => "F23", Key.F24 => "F24",

            // Special keys
            Key.Space => "Space",
            Key.Return => "Return",
            Key.Tab => "Tab",
            Key.Delete => "Delete",
            Key.Back => "Backspace",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",

            // Punctuation
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemBackslash => "\\",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemTilde => "`",
            Key.OemQuestion => "/",

            _ => null
        };
    }

    private static void SetStartOnLogin(bool enable)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            SetWindowsStartup(enable);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            SetMacOSStartup(enable);
        }
        else
        {
            Console.WriteLine("Start on login not supported on this platform");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void SetWindowsStartup(bool enable)
    {
        try
        {
            const string keyName = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
            const string valueName = "WhisperKeyboard";

            using var key = Registry.CurrentUser.OpenSubKey(keyName, writable: true);
            if (key == null)
            {
                Console.WriteLine("Failed to open registry key for startup");
                return;
            }

            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(valueName, $"\"{exePath}\"");
                    Console.WriteLine($"Added to Windows startup: {exePath}");
                }
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
                Console.WriteLine("Removed from Windows startup");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to set Windows startup: {ex.Message}");
        }
    }

    private static void SetMacOSStartup(bool enable)
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var launchAgentsDir = Path.Combine(home, "Library", "LaunchAgents");
            var plistPath = Path.Combine(launchAgentsDir, "com.whisper-keyboard.plist");

            if (enable)
            {
                // Ensure LaunchAgents directory exists
                if (!Directory.Exists(launchAgentsDir))
                {
                    Directory.CreateDirectory(launchAgentsDir);
                }

                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    Console.WriteLine("Could not determine executable path");
                    return;
                }

                // Create the Launch Agent plist
                var plistContent = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
    <key>Label</key>
    <string>com.whisper-keyboard</string>
    <key>ProgramArguments</key>
    <array>
        <string>{exePath}</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <false/>
</dict>
</plist>
";
                File.WriteAllText(plistPath, plistContent);
                Console.WriteLine($"Created macOS Launch Agent: {plistPath}");
            }
            else
            {
                if (File.Exists(plistPath))
                {
                    File.Delete(plistPath);
                    Console.WriteLine($"Removed macOS Launch Agent: {plistPath}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to set macOS startup: {ex.Message}");
        }
    }
}
