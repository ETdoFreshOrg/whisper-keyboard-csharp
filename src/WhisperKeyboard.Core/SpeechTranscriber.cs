using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using NAudio.Lame;
using NAudio.Wave;
using WhisperKeyboard.Core.Transcription;

namespace WhisperKeyboard.Core;

public class SpeechTranscriber : IDisposable
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly HttpClient _chainClient; // no timeout of its own: the chain owns timeouts
    private bool _disposed;

    public SpeechTranscriber(Config config)
    {
        _config = config;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _chainClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private string TranscriptionsUrl
    {
        get
        {
            var baseUrl = string.IsNullOrWhiteSpace(_config.ApiBaseUrl)
                ? "https://api.openai.com"
                : _config.ApiBaseUrl.TrimEnd('/');
            return $"{baseUrl}/v1/audio/transcriptions";
        }
    }

    // Don't hard-block on a missing key for non-OpenAI endpoints: some self-hosted /
    // OpenAI-compatible Whisper servers (e.g. whisper.cpp, faster-whisper) accept
    // unauthenticated requests. If the server DOES need a key it returns 401, which the
    // Settings "Test endpoint" button surfaces clearly.
    // NOTE: the default stt.etdofresh.com DOES require a bearer token — set an API key.
    private bool RequiresApiKey =>
        string.IsNullOrWhiteSpace(_config.ApiBaseUrl) ||
        _config.ApiBaseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Test whether a transcription endpoint is reachable and the credentials/model are accepted.
    /// Sends a short silent clip to {baseUrl}/v1/audio/transcriptions and reports the outcome.
    /// Static so the Settings window can validate in-progress values before they are saved.
    /// </summary>
    public static async Task<(bool Ok, string Message)> TestEndpointAsync(
        string baseUrl, string apiKey, string model, CancellationToken cancellationToken = default)
    {
        var resolvedBase = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com" : baseUrl.TrimEnd('/');
        var url = $"{resolvedBase}/v1/audio/transcriptions";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrEmpty(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            // ~0.3s of silence at 16kHz mono 16-bit PCM
            var silence = new byte[16000 * 2 * 3 / 10];
            var wav = ConvertToWav(silence, 16000, 1);

            using var content = new MultipartFormDataContent();
            var audioContent = new ByteArrayContent(wav);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(audioContent, "file", "test.wav");
            content.Add(new StringContent(string.IsNullOrWhiteSpace(model) ? "whisper-1" : model), "model");
            content.Add(new StringContent("json"), "response_format");
            request.Content = content;

            var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
                return (true, $"OK — endpoint reachable, credentials accepted (HTTP {(int)response.StatusCode}).");

            var snippet = body.Length > 200 ? body.Substring(0, 200) + "…" : body;
            var hint = response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "Check your API key.",
                System.Net.HttpStatusCode.Forbidden => "Key rejected or no access to this model.",
                System.Net.HttpStatusCode.NotFound => "Endpoint path not found — check the base URL.",
                _ => "See server response."
            };
            return (false, $"HTTP {(int)response.StatusCode} {response.StatusCode}: {hint} {snippet}".Trim());
        }
        catch (TaskCanceledException)
        {
            return (false, "Timed out — endpoint did not respond within 15s. Check the URL/network.");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"Error: {ex.Message}");
        }
    }

    public async Task<TranscriptionResult?> TranscribeAsync(byte[] audioData, CancellationToken cancellationToken = default)
    {
        if (RequiresApiKey && string.IsNullOrEmpty(_config.ApiKey))
        {
            Console.WriteLine("Error: OpenAI API key not configured");
            return null;
        }

        try
        {
            // Convert raw PCM to WAV format
            byte[] wavData = ConvertToWav(audioData, _config.SampleRate, _config.Channels);

            using var request = new HttpRequestMessage(HttpMethod.Post, TranscriptionsUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);

            using var content = new MultipartFormDataContent();

            // Add the audio file
            var audioContent = new ByteArrayContent(wavData);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(audioContent, "file", "audio.wav");

            // Add the model parameter
            content.Add(new StringContent(_config.Model), "model");

            // Add language if specified (not "auto")
            if (!string.IsNullOrEmpty(_config.Language) && _config.Language != "auto")
            {
                content.Add(new StringContent(_config.Language), "language");
            }

            // Add response format
            content.Add(new StringContent("verbose_json"), "response_format");

            request.Content = content;

            var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"API Error: {response.StatusCode} - {responseBody}");
                return null;
            }

            var result = JsonSerializer.Deserialize<WhisperApiResponse>(responseBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (result == null)
            {
                return null;
            }

            return BuildResult(result);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Transcription error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Transcribe a clip for the fallback chain ("lan" backend). Unlike TranscribeAsync this
    /// throws a <see cref="TranscriberException"/> with a one-line reason on any failure, so the
    /// chain can fall through. Short clips go up as 16 kHz WAV; clips over a minute as MP3
    /// (smaller), falling back to WAV where LAME is unavailable.
    /// </summary>
    public async Task<TranscriptionResult> TranscribeRecordingAsync(Recording recording, string language, CancellationToken cancellationToken)
    {
        if (RequiresApiKey && string.IsNullOrEmpty(_config.ApiKey))
            throw new TranscriberException("no API key set");

        byte[] encodedData = recording.Wav;
        string contentType = "audio/wav";
        string fileName = "audio.wav";
        if (recording.Duration > TimeSpan.FromMinutes(1) &&
            TryConvertToMp3(recording.Pcm, recording.SampleRate, recording.Channels) is { } mp3)
        {
            encodedData = mp3;
            contentType = "audio/mpeg";
            fileName = "audio.mp3";
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, TranscriptionsUrl);
        if (!string.IsNullOrEmpty(_config.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);

        using var content = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(encodedData);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(audioContent, "file", fileName);
        content.Add(new StringContent(string.IsNullOrWhiteSpace(_config.Model) ? "whisper-1" : _config.Model), "model");
        if (!string.IsNullOrEmpty(language) && language != "auto")
            content.Add(new StringContent(language), "language");
        content.Add(new StringContent("verbose_json"), "response_format");
        request.Content = content;

        HttpResponseMessage response;
        try
        {
            response = await _chainClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new TranscriberException($"can't reach {ServerName}: {ex.Message}", ex);
        }

        using (response)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"API Error: {response.StatusCode} - {responseBody}");
                throw new TranscriberException($"{ServerName} returned HTTP {(int)response.StatusCode}");
            }

            WhisperApiResponse? result;
            try
            {
                result = JsonSerializer.Deserialize<WhisperApiResponse>(responseBody, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (JsonException)
            {
                result = null;
            }

            if (result?.Text == null)
                throw new TranscriberException($"{ServerName} reply had no text");

            return BuildResult(result);
        }
    }

    /// <summary>Host name of the configured server, for messages.</summary>
    public string ServerName =>
        Uri.TryCreate(TranscriptionsUrl, UriKind.Absolute, out var uri) ? uri.Host : "server";

    /// <summary>
    /// Cheap availability check: the base URL parses and a TCP connection to it opens
    /// within the timeout. Returns null when usable, otherwise a one-line reason.
    /// </summary>
    public async Task<string?> CheckReachableAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (RequiresApiKey && string.IsNullOrEmpty(_config.ApiKey))
            return "no API key set";
        if (!Uri.TryCreate(TranscriptionsUrl, UriKind.Absolute, out var uri))
            return "server URL is not valid";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            await socket.ConnectAsync(uri.Host, uri.Port, cts.Token);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"{uri.Host} did not answer within {timeout.TotalSeconds:0} s";
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            return $"can't reach {uri.Host}: {ex.Message}";
        }
    }

    private static byte[] ConvertToWav(byte[] pcmData, int sampleRate, int channels) =>
        Recording.BuildWav(pcmData, sampleRate, channels);

    private static byte[] ConvertToMp3(byte[] pcmData, int sampleRate, int channels)
    {
        using var outputStream = new MemoryStream();
        var waveFormat = new WaveFormat(sampleRate, 16, channels);

        using (var writer = new LameMP3FileWriter(outputStream, waveFormat, 64)) // 64kbps
        {
            writer.Write(pcmData, 0, pcmData.Length);
        }

        return outputStream.ToArray();
    }

    /// <summary>
    /// Try to convert PCM to MP3. Returns null if LAME is not available (e.g., on macOS).
    /// </summary>
    private static byte[]? TryConvertToMp3(byte[] pcmData, int sampleRate, int channels)
    {
        try
        {
            return ConvertToMp3(pcmData, sampleRate, channels);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] MP3 encoding not available: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Transcribe long recordings using MP3 encoding to reduce file size.
    /// Falls back to WAV if MP3 encoding is not available (e.g., on macOS without LAME).
    /// </summary>
    public async Task<TranscriptionResult?> TranscribeLongRecordingAsync(byte[] audioData, CancellationToken cancellationToken = default)
    {
        if (RequiresApiKey && string.IsNullOrEmpty(_config.ApiKey))
        {
            Console.WriteLine("Error: OpenAI API key not configured");
            return null;
        }

        try
        {
            // Try to convert to MP3 (smaller), fall back to WAV if LAME is not available
            byte[] encodedData;
            string contentType;
            string fileName;

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Converting {audioData.Length / 1024.0 / 1024.0:F2} MB PCM...");

            var mp3Data = TryConvertToMp3(audioData, _config.SampleRate, _config.Channels);
            if (mp3Data != null)
            {
                encodedData = mp3Data;
                contentType = "audio/mpeg";
                fileName = "audio.mp3";
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] MP3 size: {encodedData.Length / 1024.0 / 1024.0:F2} MB");
            }
            else
            {
                // Fallback to WAV format
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Falling back to WAV format...");
                encodedData = ConvertToWav(audioData, _config.SampleRate, _config.Channels);
                contentType = "audio/wav";
                fileName = "audio.wav";
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] WAV size: {encodedData.Length / 1024.0 / 1024.0:F2} MB");
            }

            // Use a longer timeout for long recordings (5 minutes)
            using var longTimeoutClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

            using var request = new HttpRequestMessage(HttpMethod.Post, TranscriptionsUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);

            using var content = new MultipartFormDataContent();

            // Add the audio file
            var audioContent = new ByteArrayContent(encodedData);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            content.Add(audioContent, "file", fileName);

            // Add the model parameter
            content.Add(new StringContent(_config.Model), "model");

            // Add language if specified (not "auto")
            if (!string.IsNullOrEmpty(_config.Language) && _config.Language != "auto")
            {
                content.Add(new StringContent(_config.Language), "language");
            }

            // Add response format
            content.Add(new StringContent("verbose_json"), "response_format");

            request.Content = content;

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Sending long recording to Whisper API...");
            var response = await longTimeoutClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"API Error: {response.StatusCode} - {responseBody}");
                return null;
            }

            var result = JsonSerializer.Deserialize<WhisperApiResponse>(responseBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (result == null)
            {
                return null;
            }

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Long recording transcription complete");
            return BuildResult(result);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Long recording transcription error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Transcribe an audio/video file from disk. The file should already be in a Whisper-compatible format.
    /// </summary>
    public async Task<TranscriptionResult?> TranscribeFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (RequiresApiKey && string.IsNullOrEmpty(_config.ApiKey))
        {
            Console.WriteLine("Error: OpenAI API key not configured");
            return null;
        }

        try
        {
            var fileBytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var contentType = ext switch
            {
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                ".ogg" => "audio/ogg",
                ".m4a" => "audio/mp4",
                ".flac" => "audio/flac",
                ".webm" => "audio/webm",
                _ => "application/octet-stream"
            };

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Transcribing file: {filePath} ({fileBytes.Length / 1024.0 / 1024.0:F2} MB)");

            using var longTimeoutClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var request = new HttpRequestMessage(HttpMethod.Post, TranscriptionsUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);

            using var content = new MultipartFormDataContent();
            var audioContent = new ByteArrayContent(fileBytes);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            content.Add(audioContent, "file", Path.GetFileName(filePath));
            content.Add(new StringContent(_config.Model), "model");

            if (!string.IsNullOrEmpty(_config.Language) && _config.Language != "auto")
                content.Add(new StringContent(_config.Language), "language");

            content.Add(new StringContent("verbose_json"), "response_format");
            request.Content = content;

            var response = await longTimeoutClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"API Error: {response.StatusCode} - {responseBody}");
                throw new Exception($"API Error: {response.StatusCode} - {responseBody}");
            }

            var result = JsonSerializer.Deserialize<WhisperApiResponse>(responseBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return result == null ? null : BuildResult(result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex.Message.StartsWith("API Error:")) { throw; }
        catch (Exception ex)
        {
            Console.WriteLine($"File transcription error: {ex.Message}");
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _httpClient.Dispose();
        _chainClient.Dispose();
        _disposed = true;
    }

    private static TranscriptionResult BuildResult(WhisperApiResponse response)
    {
        var result = new TranscriptionResult
        {
            Text = response.Text ?? "",
            Language = response.Language ?? ""
        };

        if (response.Segments != null && response.Segments.Count > 0)
        {
            result.AvgLogProb = response.Segments.Average(s => s.AvgLogprob);
            result.NoSpeechProb = response.Segments.Max(s => s.NoSpeechProb);
            result.CompressionRatio = response.Segments.Average(s => s.CompressionRatio);

            // Convert avg_logprob to a 0-1 confidence score
            // Typical range: -1.0 (low confidence) to 0.0 (high confidence)
            result.Confidence = Math.Clamp(1.0 + result.AvgLogProb, 0.0, 1.0);

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Whisper confidence: avg_logprob={result.AvgLogProb:F3}, no_speech_prob={result.NoSpeechProb:F3}, compression_ratio={result.CompressionRatio:F2}, words={result.WordCount}");
        }
        else
        {
            result.Confidence = 1.0;
        }

        return result;
    }

    private class WhisperApiResponse
    {
        public string? Text { get; set; }
        public string? Language { get; set; }
        public double? Duration { get; set; }
        public List<WhisperSegment>? Segments { get; set; }
    }

    private class WhisperSegment
    {
        public int Id { get; set; }
        public string Text { get; set; } = "";
        public double Start { get; set; }
        public double End { get; set; }
        [JsonPropertyName("avg_logprob")]
        public double AvgLogprob { get; set; }
        [JsonPropertyName("no_speech_prob")]
        public double NoSpeechProb { get; set; }
        [JsonPropertyName("compression_ratio")]
        public double CompressionRatio { get; set; } = 1.0;
    }
}
