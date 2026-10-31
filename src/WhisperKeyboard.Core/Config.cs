using System.Text.Json;
using WhisperKeyboard.Core.Transcription;

namespace WhisperKeyboard.Core;

public class Config
{
    private static string GetConfigDirectory()
    {
        // Cross-platform: use ApplicationData on Windows, ~/.config on Unix
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData))
        {
            // Fallback for Unix systems where ApplicationData might be empty
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            appData = Path.Combine(home, ".config");
        }
        return Path.Combine(appData, "WhisperKeyboard");
    }

    private static string ConfigDirectory => GetConfigDirectory();
    private static string ConfigFilePath => Path.Combine(ConfigDirectory, "config.json");

    // Audio Settings
    public int SampleRate { get; set; } = 16000;
    public int ChunkSize { get; set; } = 1024;
    public int Channels { get; set; } = 1;
    public int DeviceIndex { get; set; } = -1; // -1 = default device
    public int VadThreshold { get; set; } = 500;
    public double SilenceThreshold { get; set; } = 0.5;
    public double MinSpeechDuration { get; set; } = 0.5;
    public double MaxSilenceDuration { get; set; } = 1.0;
    public double MinAudioDuration { get; set; } = 0.5;

    // Pre-roll / Post-roll padding (extra audio captured before/after a recording)
    // PreRollEnabled keeps the mic running while idle so audio from just before
    // a keypress / speech start is available. Disabling it lets the mic shut off.
    public bool PreRollEnabled { get; set; } = true;
    public double PreRollSeconds { get; set; } = 1.0;
    public double PostRollSeconds { get; set; } = 0.3;

    // OpenAI Settings
    public string ApiKey { get; set; } = "";
    public string ApiBaseUrl { get; set; } = "https://stt.etdofresh.com";
    public string Model { get; set; } = "whisper-1";
    public string Language { get; set; } = "en";

    // Speech-to-text backends, tried in order until one returns text:
    // ChatGPT (via the Codex sign-in), then the server above, then Windows/macOS speech.
    public TranscriptionSettings Transcription { get; set; } = new();

    // Typing Settings
    public double TypingSpeed { get; set; } = 0.001;
    public int TypingDelayMs { get; set; } = 0; // Delay between characters in typing mode (0 = no delay, batch send)
    public bool UseVirtualSpaceKey { get; set; } = false; // Send space via VK_SPACE keystroke instead of Unicode (more reliable in terminals)
    public int VirtualSpaceHoldMs { get; set; } = 5; // Hold time between VK_SPACE down and up (only used when UseVirtualSpaceKey is on)
    public bool AddPunctuation { get; set; } = true;
    public bool CapitalizeSentences { get; set; } = true;
    public bool PasteMode { get; set; } = true; // Default to paste mode (more portable)
    public bool AutoEnter { get; set; } = false;
    // After pasting or typing, read the focused field back (UI Automation on Windows,
    // Accessibility on macOS) and retry when the field is provably unchanged.
    public bool VerifyTextInsertion { get; set; } = true;
    public int InsertionRetryCount { get; set; } = 2; // Extra attempts after the first: paste again, then type
    public int InsertionVerifyTimeoutMs { get; set; } = 1500; // How long to wait for text to show up before retrying
    public bool ExitWordsEnabled { get; set; } = true;
    public List<string> ExitWords { get; set; } = new List<string> { "over", "enter", "submit" };

    // Wake/Pause Word Settings
    public bool WakeWordsEnabled { get; set; } = false;
    public List<string> WakeWords { get; set; } = new List<string> { "start listening" };
    public List<string> PauseWords { get; set; } = new List<string> { "stop listening" };

    // Hotkey Settings (empty string = disabled)
    public string ToggleRecordingHotkey { get; set; } = "";
    public string PauseResumeHotkey { get; set; } = "";
    public string OpenSettingsHotkey { get; set; } = "";
    public string LongRecordHotkey { get; set; } = "";

    // Long Recording Settings
    public bool ShowLongRecordButton { get; set; } = true;
    public int MaxLongRecordMinutes { get; set; } = 30;

    // Push-to-Talk Settings
    public bool PushToTalkEnabled { get; set; } = true;
    // Which keys must be held for push-to-talk. A "+"-joined list of modifier
    // tokens (e.g. "RightOption+RightShift", "RightControl+RightShift", "Fn").
    // macOS: any combination of Left/Right Control/Shift/Option/Command and Fn.
    // Windows: Left/Right Control/Shift/Alt/Win (Fn unsupported).
    public string PushToTalkKeys { get; set; } = "RightOption+RightShift";
    // Releasing the keys sooner than this discards the clip (an accidental tap).
    public double PushToTalkMinHoldSeconds { get; set; } = 0.3;

    // General Settings
    public bool ShowNotifications { get; set; } = true;
    public bool StartOnLogin { get; set; } = false;
    public bool StartListeningOnLaunch { get; set; } = true;
    // When listening starts (launch, tray menu, or hotkey), begin paused so the
    // user must Resume to start VAD instead of it listening immediately.
    public bool StartListeningPaused { get; set; } = false;
    public bool IsCalibrated { get; set; } = false;

    public static Config Load()
    {
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                var json = File.ReadAllText(ConfigFilePath);
                var config = JsonSerializer.Deserialize<Config>(json);
                if (config != null)
                {
                    // Override API key from environment if present
                    var envApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                    if (!string.IsNullOrEmpty(envApiKey))
                    {
                        config.ApiKey = envApiKey;
                    }
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading config: {ex.Message}");
        }

        var defaultConfig = new Config();

        // Try to get API key from environment
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrEmpty(apiKey))
        {
            defaultConfig.ApiKey = apiKey;
        }

        defaultConfig.Save();
        return defaultConfig;
    }

    public void Save()
    {
        try
        {
            if (!Directory.Exists(ConfigDirectory))
            {
                Directory.CreateDirectory(ConfigDirectory);
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            var json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(ConfigFilePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving config: {ex.Message}");
        }
    }

    public static string GetConfigPath() => ConfigFilePath;
}
