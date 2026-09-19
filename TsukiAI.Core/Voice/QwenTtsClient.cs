using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using TsukiAI.Core.Services;

namespace TsukiAI.VoiceChat.Services;

/// <summary>
/// Client for a private Qwen3-TTS 0.6B service. The default contract is the
/// approved trained-custom Tsukihime service. An explicitly configured
/// zero-shot contract is also supported for the isolated IBM Base-model
/// deployment; it requires the service to attest to the reference profile and
/// full ICL conditioning.
/// </summary>
public sealed class QwenTtsClient : ITtsClient
{
    private const int MaxWavBytes = 16 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly Func<(string BaseUrl, string ApiKey)> _configuration;
    private readonly int _maxAttempts;
    private readonly TimeSpan _healthCacheDuration;
    private readonly string _expectedMode;
    private readonly SemaphoreSlim _healthLock = new(1, 1);
    private DateTimeOffset _lastHealthyAt = DateTimeOffset.MinValue;
    private string? _lastHealthyBaseUrl;

    public string Name => _expectedMode switch
    {
        "zero-shot" => "Qwen3-TTS 0.6B zero-shot tsuki_v1 (full ICL)",
        "trained-base-full-icl" => "Qwen3-TTS 0.6B trained Base/full-ICL Tsukihime voice",
        _ => "Qwen3-TTS 0.6B trained Tsukihime custom voice"
    };

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
        TimeSpan? healthCacheDuration = null,
        string expectedMode = "trained-custom")
        : this(
            () => (baseUrl, apiKey ?? string.Empty),
            handler,
            timeout,
            maxAttempts,
            healthCacheDuration,
            expectedMode)
    {
    }

    public QwenTtsClient(
        Func<(string BaseUrl, string ApiKey)> configuration,
        HttpMessageHandler? handler = null,
        TimeSpan? timeout = null,
        int maxAttempts = 2,
        TimeSpan? healthCacheDuration = null,
        string expectedMode = "trained-custom")
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _maxAttempts = Math.Clamp(maxAttempts, 1, 3);
        _healthCacheDuration = healthCacheDuration ?? TimeSpan.FromSeconds(15);
        _expectedMode = NormalizeExpectedMode(expectedMode);
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

            var healthBody = await response.Content.ReadAsStringAsync(healthTimeout.Token);
            ValidateHealth(healthBody, _expectedMode);
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
            "AUTO" => "Auto",
            _ => throw new ArgumentException("Qwen TTS language must be Auto, English, or Japanese.", nameof(language))
        };

    private static void ValidateHealth(string body, string expectedMode)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Qwen TTS health returned invalid JSON.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            var problems = new List<string>();
            var mode = StringProperty(root, "mode");
            var model = StringProperty(root, "model");
            var stage = StringProperty(root, "deployment_stage");
            var approval = StringProperty(root, "approval_status");

            if (!string.Equals(StringProperty(root, "status"), "ready", StringComparison.OrdinalIgnoreCase))
                problems.Add("status must be ready");
            if (expectedMode == "zero-shot")
                ValidateZeroShotHealth(root, mode, model, stage, approval, problems);
            else if (expectedMode == "trained-base-full-icl")
                ValidateTrainedBaseFullIclHealth(root, mode, model, stage, approval, problems);
            else
                ValidateTrainedCustomHealth(root, mode, model, stage, approval, problems);

            if (problems.Count > 0)
                throw new InvalidDataException(
                    $"Qwen TTS health is not an approved {expectedMode} runtime: " +
                    string.Join("; ", problems));
        }
    }

    private static void ValidateTrainedCustomHealth(
        JsonElement root,
        string mode,
        string model,
        string stage,
        string approval,
        List<string> problems)
    {
        if (!string.Equals(mode, "trained-custom", StringComparison.OrdinalIgnoreCase))
            problems.Add("mode must be trained-custom");
        if (!string.Equals(StringProperty(root, "speaker"), "tsukihime", StringComparison.OrdinalIgnoreCase))
            problems.Add("speaker must be tsukihime");
        if (string.IsNullOrWhiteSpace(model) || model.Contains("qwen3-tts-12hz-0.6b-base", StringComparison.OrdinalIgnoreCase))
            problems.Add("model must be a trained checkpoint, not the official Base model");
        if (string.IsNullOrWhiteSpace(StringProperty(root, "checkpoint_id")))
            problems.Add("checkpoint_id must be recorded");
        if (!string.Equals(stage, "production", StringComparison.OrdinalIgnoreCase))
            problems.Add("deployment_stage must be production");
        if (!string.Equals(approval, "APPROVED_FOR_PRODUCTION", StringComparison.Ordinal))
            problems.Add("approval_status must be APPROVED_FOR_PRODUCTION");
        if (!BooleanPropertyIsFalse(root, "reference_required"))
            problems.Add("reference_required must be false");
        if (!BooleanPropertyIsFalse(root, "runtime_lora_loaded"))
            problems.Add("runtime_lora_loaded must be false");
        if (!Sha256Property(root, "artifact_tree_sha256") || !Sha256Property(root, "manifest_sha256"))
            problems.Add("artifact and manifest SHA-256 values must be recorded");

        if (!root.TryGetProperty("conditioning", out var conditioning) ||
            conditioning.ValueKind != JsonValueKind.Object)
        {
            problems.Add("conditioning health must be present");
            return;
        }

        if (!BooleanPropertyIsFalse(conditioning, "reference_required"))
            problems.Add("conditioning.reference_required must be false");
        if (!NullProperty(conditioning, "reference_sha256"))
            problems.Add("conditioning.reference_sha256 must be null");
        if (conditioning.TryGetProperty("x_vector_only_mode", out var xVector) &&
            xVector.ValueKind != JsonValueKind.Null &&
            xVector.ValueKind != JsonValueKind.False)
            problems.Add("conditioning.x_vector_only_mode must not be enabled");
    }

    private static void ValidateZeroShotHealth(
        JsonElement root,
        string mode,
        string model,
        string stage,
        string approval,
        List<string> problems)
    {
        const string expectedModel = "Qwen/Qwen3-TTS-12Hz-0.6B-Base";
        if (!string.Equals(mode, "zero-shot", StringComparison.OrdinalIgnoreCase))
            problems.Add("mode must be zero-shot");
        if (!string.Equals(StringProperty(root, "profile"), "tsuki_v1", StringComparison.Ordinal))
            problems.Add("profile must be tsuki_v1");
        if (!string.Equals(model, expectedModel, StringComparison.Ordinal))
            problems.Add($"model must be {expectedModel}");
        if (!string.Equals(stage, "isolated", StringComparison.OrdinalIgnoreCase))
            problems.Add("deployment_stage must be isolated");
        if (!string.Equals(approval, "APPROVED_FOR_ISOLATED_ZERO_SHOT", StringComparison.Ordinal))
            problems.Add("approval_status must be APPROVED_FOR_ISOLATED_ZERO_SHOT");
        if (!BooleanPropertyIsTrue(root, "reference_required"))
            problems.Add("reference_required must be true");
        if (!BooleanPropertyIsFalse(root, "runtime_lora_loaded"))
            problems.Add("runtime_lora_loaded must be false");

        if (!root.TryGetProperty("conditioning", out var conditioning) ||
            conditioning.ValueKind != JsonValueKind.Object)
        {
            problems.Add("conditioning health must be present");
            return;
        }

        if (!BooleanPropertyIsTrue(conditioning, "reference_required"))
            problems.Add("conditioning.reference_required must be true");
        if (!Sha256Property(conditioning, "reference_sha256") ||
            !Sha256Property(conditioning, "reference_transcript_sha256"))
            problems.Add("reference and transcript SHA-256 values must be recorded");
        if (!BooleanPropertyIsFalse(conditioning, "x_vector_only_mode"))
            problems.Add("conditioning.x_vector_only_mode must be false for full ICL");
    }

    private static void ValidateTrainedBaseFullIclHealth(
        JsonElement root,
        string mode,
        string model,
        string stage,
        string approval,
        List<string> problems)
    {
        const string expectedBaseModel = "Qwen/Qwen3-TTS-12Hz-0.6B-Base";
        if (!string.Equals(mode, "trained-base-full-icl", StringComparison.OrdinalIgnoreCase))
            problems.Add("mode must be trained-base-full-icl");
        if (string.IsNullOrWhiteSpace(StringProperty(root, "profile")))
            problems.Add("profile must be recorded");
        if (!string.Equals(StringProperty(root, "base_model"), expectedBaseModel, StringComparison.Ordinal))
            problems.Add($"base_model must be {expectedBaseModel}");
        if (string.IsNullOrWhiteSpace(model) ||
            model.Contains("qwen3-tts-12hz-0.6b-base", StringComparison.OrdinalIgnoreCase))
            problems.Add("model must identify the trained Base/full-ICL checkpoint");
        if (string.IsNullOrWhiteSpace(StringProperty(root, "checkpoint_id")))
            problems.Add("checkpoint_id must be recorded");
        if (!string.Equals(stage, "isolated", StringComparison.OrdinalIgnoreCase))
            problems.Add("deployment_stage must be isolated");
        if (!string.Equals(approval, "APPROVED_FOR_ISOLATED_BASE_FULL_ICL", StringComparison.Ordinal))
            problems.Add("approval_status must be APPROVED_FOR_ISOLATED_BASE_FULL_ICL");
        if (!BooleanPropertyIsTrue(root, "reference_required"))
            problems.Add("reference_required must be true");
        if (!BooleanPropertyIsFalse(root, "runtime_lora_loaded"))
            problems.Add("runtime_lora_loaded must be false");

        if (!root.TryGetProperty("conditioning", out var conditioning) ||
            conditioning.ValueKind != JsonValueKind.Object)
        {
            problems.Add("conditioning health must be present");
            return;
        }

        if (!BooleanPropertyIsTrue(conditioning, "reference_required"))
            problems.Add("conditioning.reference_required must be true");
        if (!Sha256Property(conditioning, "reference_sha256") ||
            !Sha256Property(conditioning, "reference_transcript_sha256"))
            problems.Add("conditioning reference and transcript SHA-256 values must be recorded");
        if (!BooleanPropertyIsFalse(conditioning, "x_vector_only_mode"))
            problems.Add("conditioning.x_vector_only_mode must be false for full ICL");
    }

    private static string StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static bool BooleanPropertyIsFalse(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.False;

    private static bool BooleanPropertyIsTrue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool NullProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Null;

    private static bool Sha256Property(JsonElement element, string name) =>
        Regex.IsMatch(StringProperty(element, name), "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);

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

    private static string NormalizeExpectedMode(string expectedMode)
    {
        var value = (expectedMode ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "trained-custom" or "zero-shot" or "trained-base-full-icl" => value,
            _ => throw new ArgumentException(
                "Qwen TTS expected mode must be trained-custom, zero-shot, or trained-base-full-icl.",
                nameof(expectedMode))
        };
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

/// <summary>
/// Separates script detection from the Qwen pronunciation mode. Japanese text
/// is always sent as Japanese. English text uses an explicit, reviewable policy
/// rather than silently inheriting the detector's generic EN result.
/// </summary>
public static class TsukiPronunciationPolicy
{
    public static string Resolve(string text, string? englishLanguage = null)
    {
        if (TtsLanguageDetector.Detect(text) == "JA")
            return "Japanese";

        var policy = (englishLanguage ?? "English").Trim().ToUpperInvariant();
        return policy switch
        {
            "EN" or "ENGLISH" => "English",
            "AUTO" => "Auto",
            "JA" or "JP" or "JAPANESE" => "Japanese",
            _ => throw new ArgumentException(
                "Tsuki English pronunciation policy must be English, Auto, or Japanese.",
                nameof(englishLanguage))
        };
    }
}
