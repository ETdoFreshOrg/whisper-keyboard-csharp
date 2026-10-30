using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WhisperKeyboard.Core.Transcription;

/// <summary>
/// Transcribes through ChatGPT's own dictation endpoint, signed in with the Codex
/// app's ChatGPT login (the same path ETcode uses). The endpoint is undocumented,
/// so any non-2xx reply falls through to the next backend.
/// The access token is read from Codex's auth.json on each use and never logged or stored.
/// </summary>
public sealed class ChatGptTranscriber : ITranscriber, IDisposable
{
    public const string Endpoint = "https://chatgpt.com/backend-api/transcribe";
    public const string ClientName = "Codex Desktop";
    public const int MaxUploadBytes = 10 * 1024 * 1024;

    private readonly Func<string> _authPath;
    private readonly HttpClient _http;

    public string Id => TranscriptionSettings.ChatGpt;
    public string DisplayName => "ChatGPT";

    public ChatGptTranscriber(Func<string> authPath, HttpMessageHandler? handler = null)
    {
        _authPath = authPath;
        _http = new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan // the chain owns timeouts
        };
    }

    /// <summary>Resolve Codex's auth.json: explicit path, else $CODEX_HOME/auth.json, else ~/.codex/auth.json.</summary>
    public static string ResolveAuthPath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return Environment.ExpandEnvironmentVariables(configured.Trim());

        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(codexHome))
            return Path.Combine(codexHome, "auth.json");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".codex", "auth.json");
    }

    /// <summary>Read tokens.access_token from a Codex auth.json, or null if absent.</summary>
    public static string? ReadAccessToken(string authPath)
    {
        try
        {
            if (!File.Exists(authPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(authPath));
            if (doc.RootElement.TryGetProperty("tokens", out var tokens) &&
                tokens.ValueKind == JsonValueKind.Object &&
                tokens.TryGetProperty("access_token", out var token) &&
                token.ValueKind == JsonValueKind.String)
            {
                var value = token.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch (Exception)
        {
            // Unreadable or half-written file: treat as signed out.
        }
        return null;
    }

    public Task<string?> CheckAsync(CancellationToken cancellationToken)
    {
        var path = _authPath();
        if (!File.Exists(path))
            return Task.FromResult<string?>("not signed in to Codex (no auth.json)");
        return Task.FromResult(ReadAccessToken(path) == null
            ? "Codex auth.json has no ChatGPT access token"
            : null);
    }

    public async Task<TranscriptionResult> TranscribeAsync(Recording recording, string language, CancellationToken cancellationToken)
    {
        // Opus is ~10x smaller than WAV; fall back to WAV if encoding failed.
        var ogg = recording.OggOpus;
        var (audio, contentType, fileName) = ogg != null
            ? (ogg, "audio/ogg", "codex.ogg")
            : (recording.Wav, "audio/wav", "codex.wav");

        if (audio.Length > MaxUploadBytes)
            throw new TranscriberException($"clip is {audio.Length / 1024 / 1024} MB, over the 10 MB limit");

        var token = ReadAccessToken(_authPath())
            ?? throw new TranscriberException("not signed in to Codex");

        var response = await SendAsync(audio, contentType, fileName, language, token, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Codex may have refreshed the token since we read it: re-read once and retry.
            response.Dispose();
            var refreshed = ReadAccessToken(_authPath())
                ?? throw new TranscriberException("ChatGPT sign-in expired (HTTP 401)");
            response = await SendAsync(audio, contentType, fileName, language, refreshed, cancellationToken);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new TranscriberException(DescribeStatus(response.StatusCode));

            string? text = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("text", out var t) &&
                    t.ValueKind == JsonValueKind.String)
                {
                    text = t.GetString();
                }
            }
            catch (JsonException) { }

            if (text == null)
                throw new TranscriberException("reply had no text");

            return new TranscriptionResult { Text = text, Language = language, Confidence = 1.0 };
        }
    }

    private async Task<HttpResponseMessage> SendAsync(byte[] audio, string contentType, string fileName,
        string language, string token, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            // Cloudflare in front of chatgpt.com rejects HTTP/2 from non-browser clients.
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("User-Agent", ClientName);
        request.Headers.TryAddWithoutValidation("originator", ClientName);

        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, "file", fileName);
        if (!string.IsNullOrWhiteSpace(language) && language != "auto")
            content.Add(new StringContent(language), "language");
        request.Content = content;

        try
        {
            return await _http.SendAsync(request, cancellationToken);
        }
        finally
        {
            request.Dispose();
        }
    }

    internal static string DescribeStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "ChatGPT sign-in expired (HTTP 401)",
        HttpStatusCode.Forbidden => "ChatGPT refused the request (HTTP 403)",
        HttpStatusCode.RequestEntityTooLarge => "clip too large for ChatGPT (HTTP 413)",
        HttpStatusCode.TooManyRequests => "ChatGPT rate limit (HTTP 429)",
        _ => $"ChatGPT returned HTTP {(int)status}"
    };

    public void Dispose() => _http.Dispose();
}
