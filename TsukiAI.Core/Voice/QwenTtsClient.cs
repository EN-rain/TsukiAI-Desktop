using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TsukiAI.Core.Services;

namespace TsukiAI.VoiceChat.Services;

/// <summary>
/// Client for the private Qwen3-TTS 0.6B CPU service. The service owns the
/// reference audio and cached full-ICL voice prompt; this client sends text
/// and an explicit target language only.
/// </summary>
public sealed class QwenTtsClient : ITtsClient
{
    private const int MaxWavBytes = 16 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly Func<(string BaseUrl, string ApiKey)> _configuration;
    private readonly int _maxAttempts;
    private readonly TimeSpan _healthCacheDuration;
    private readonly SemaphoreSlim _healthLock = new(1, 1);
    private DateTimeOffset _lastHealthyAt = DateTimeOffset.MinValue;
    private string? _lastHealthyBaseUrl;

    public string Name => "Qwen3-TTS 0.6B Base (full ICL)";

    public string BaseUrl => GetConfiguration().BaseUrl;

    public bool IsConfigured
    {
        get
        {
            try
            {
                var configuration = GetConfiguration();
                return !string.IsNullOrWhiteSpace(configuration.BaseUrl) &&
                       !string.IsNullOrWhiteSpace(configuration.ApiKey);
            }
            catch
            {
                return false;
            }
        }
    }

    public QwenTtsClient(
        string baseUrl,
        string? apiKey = null,
        HttpMessageHandler? handler = null,
        TimeSpan? timeout = null,
        int maxAttempts = 2,
        TimeSpan? healthCacheDuration = null)
        : this(
            () => (baseUrl, apiKey ?? string.Empty),
            handler,
            timeout,
            maxAttempts,
            healthCacheDuration)
    {
    }

    public QwenTtsClient(
        Func<(string BaseUrl, string ApiKey)> configuration,
        HttpMessageHandler? handler = null,
        TimeSpan? timeout = null,
        int maxAttempts = 2,
        TimeSpan? healthCacheDuration = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _maxAttempts = Math.Clamp(maxAttempts, 1, 3);
        _healthCacheDuration = healthCacheDuration ?? TimeSpan.FromSeconds(15);
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(180);
    }

    public async Task<byte[]> SynthesizeWavAsync(
        string text,
        string language,
        CancellationToken ct = default,
        string? correlationId = null)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0)
            return Array.Empty<byte>();

        var payload = new QwenTtsRequest(text, NormalizeLanguage(language));
        Exception? lastError = null;

        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var retryableFailure = true;
            try
            {
                await EnsureHealthyAsync(ct);
                var configuration = GetConfiguration();
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    new Uri($"{configuration.BaseUrl}/tts", UriKind.Absolute))
                {
                    Content = JsonContent.Create(payload, options: JsonOptions)
                };
                AddHeaders(request, correlationId, configuration.ApiKey);

                using var response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct);

                if (response.IsSuccessStatusCode)
                {
                    var wav = await ReadBytesWithLimitAsync(response.Content, MaxWavBytes, ct);
                    ValidateWav(wav);
                    _lastHealthyAt = DateTimeOffset.UtcNow;
                    _lastHealthyBaseUrl = configuration.BaseUrl;
                    return wav;
                }

                var body = await response.Content.ReadAsStringAsync(ct);
                lastError = new HttpRequestException(
                    $"Qwen TTS returned {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body)}");
                retryableFailure = IsRetryable(response.StatusCode);
                if (!retryableFailure || attempt == _maxAttempts)
                    throw lastError;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < _maxAttempts)
            {
                lastError = new TimeoutException("Qwen TTS request timed out.");
            }
            catch (HttpRequestException ex) when (attempt < _maxAttempts && retryableFailure)
            {
                lastError = ex;
            }

            var delay = TimeSpan.FromMilliseconds(350 * Math.Pow(2, attempt - 1));
            DevLog.WriteLine(
                "[QwenTTS][Retry] attempt={0}, delay_ms={1:F0}, error={2}",
                attempt,
                delay.TotalMilliseconds,
                lastError?.Message ?? "unknown");
            await Task.Delay(delay, ct);
        }

        throw lastError ?? new HttpRequestException("Qwen TTS request failed.");
    }

    public async Task<bool> IsAliveAsync(CancellationToken ct = default)
    {
        try
        {
            await EnsureHealthyAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureHealthyAsync(CancellationToken ct)
    {
        var configuration = GetConfiguration();
        if (string.Equals(_lastHealthyBaseUrl, configuration.BaseUrl, StringComparison.Ordinal) &&
            DateTimeOffset.UtcNow - _lastHealthyAt < _healthCacheDuration)
            return;

        await _healthLock.WaitAsync(ct);
        try
        {
            configuration = GetConfiguration();
            if (string.Equals(_lastHealthyBaseUrl, configuration.BaseUrl, StringComparison.Ordinal) &&
                DateTimeOffset.UtcNow - _lastHealthyAt < _healthCacheDuration)
                return;

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri($"{configuration.BaseUrl}/health", UriKind.Absolute));
            AddHeaders(request, null, configuration.ApiKey);
            using var healthTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            healthTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var response = await _http.SendAsync(request, healthTimeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException(
                    $"Qwen TTS health check returned {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body)}");
            }

            _lastHealthyAt = DateTimeOffset.UtcNow;
            _lastHealthyBaseUrl = configuration.BaseUrl;
        }
        finally
        {
            _healthLock.Release();
        }
    }

    private (string BaseUrl, string ApiKey) GetConfiguration()
    {
        var configuration = _configuration();
        return (
            NormalizeBaseUrl(configuration.BaseUrl),
            (configuration.ApiKey ?? string.Empty).Trim());
    }

    private static void AddHeaders(HttpRequestMessage request, string? correlationId, string apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        if (!string.IsNullOrWhiteSpace(correlationId))
            request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
    }

    private static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        statusCode == HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static void ValidateWav(byte[] wav)
    {
        if (wav.Length < 12 ||
            wav[0] != (byte)'R' || wav[1] != (byte)'I' ||
            wav[2] != (byte)'F' || wav[3] != (byte)'F' ||
            wav[8] != (byte)'W' || wav[9] != (byte)'A' ||
            wav[10] != (byte)'V' || wav[11] != (byte)'E')
        {
            throw new InvalidDataException("Qwen TTS returned a non-WAV response.");
        }
    }

    private static async Task<byte[]> ReadBytesWithLimitAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken ct)
    {
        if (content.Headers.ContentLength.HasValue && content.Headers.ContentLength.Value > maxBytes)
            throw new InvalidDataException($"Qwen TTS response exceeds the {maxBytes} byte limit.");

        await using var stream = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0)
                break;

            if (output.Length + read > maxBytes)
                throw new InvalidDataException($"Qwen TTS response exceeds the {maxBytes} byte limit.");

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static string NormalizeLanguage(string language) =>
        (language ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "JA" or "JP" or "JAPANESE" => "Japanese",
            "EN" or "ENGLISH" => "English",
            _ => throw new ArgumentException("Qwen TTS language must be EN or JA.", nameof(language))
        };

    private static string NormalizeBaseUrl(string baseUrl)
    {
        var value = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (value.Length == 0)
            value = "http://127.0.0.1:8100";
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = "http://" + value;
        }

        return value;
    }

    private static string Truncate(string value) =>
        value.Length <= 240 ? value : value[..240];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public void Dispose()
    {
        _http.Dispose();
        _healthLock.Dispose();
    }
}

public sealed record QwenTtsRequest(string Text, string Language);

public static class TtsLanguageDetector
{
    public static string Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "EN";

        var relevant = text.Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)).ToArray();
        if (relevant.Length == 0)
            return "EN";

        var japanese = relevant.Count(c =>
            (c >= 0x3040 && c <= 0x30FF) ||
            (c >= 0x4E00 && c <= 0x9FFF));

        return japanese >= relevant.Length * 0.4 ? "JA" : "EN";
    }
}
