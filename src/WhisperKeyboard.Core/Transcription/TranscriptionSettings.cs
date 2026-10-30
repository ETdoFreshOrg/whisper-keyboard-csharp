namespace WhisperKeyboard.Core.Transcription;

/// <summary>
/// The "Transcription" block of config.json: which backends to try and in what order.
/// Language comes from <see cref="Config.Language"/>; the "lan" backend uses the
/// existing ApiBaseUrl / ApiKey / Model server settings.
/// </summary>
public class TranscriptionSettings
{
    public const string ChatGpt = "chatgpt";
    public const string Lan = "lan";
    public const string System = "system";

    public static readonly string[] AllBackendIds = { ChatGpt, Lan, System };

    public List<string> Order { get; set; } = new() { ChatGpt, Lan, System };
    public int TotalTimeoutSeconds { get; set; } = 120;
    public int FailureCooldownSeconds { get; set; } = 120;

    public ChatGptBackendSettings ChatGptBackend { get; set; } = new();
    public BackendSettings LanBackend { get; set; } = new() { TimeoutSeconds = 30 };
    public BackendSettings SystemBackend { get; set; } = new() { TimeoutSeconds = 60 };

    public BackendSettings For(string id) => id switch
    {
        ChatGpt => ChatGptBackend,
        Lan => LanBackend,
        System => SystemBackend,
        _ => throw new ArgumentException($"Unknown transcription backend '{id}'", nameof(id))
    };

    /// <summary>
    /// The order with unknown ids dropped, duplicates removed and any missing
    /// backend appended, so a hand-edited config can't lose a backend.
    /// </summary>
    public List<string> NormalizedOrder()
    {
        var result = new List<string>();
        foreach (var id in Order ?? new List<string>())
        {
            var key = id?.Trim().ToLowerInvariant() ?? "";
            if (AllBackendIds.Contains(key) && !result.Contains(key))
                result.Add(key);
        }
        foreach (var id in AllBackendIds)
        {
            if (!result.Contains(id))
                result.Add(id);
        }
        return result;
    }
}

public class BackendSettings
{
    public bool Enabled { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 30;
}

public class ChatGptBackendSettings : BackendSettings
{
    /// <summary>Path to Codex's auth.json. Empty = $CODEX_HOME/auth.json or ~/.codex/auth.json.</summary>
    public string CodexAuthPath { get; set; } = "";
}
