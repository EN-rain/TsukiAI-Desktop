using System.Net.Http;
using TsukiAI.Core.Models;

namespace TsukiAI.Core.Services;

/// <summary>
/// OpenAI-compatible Groq inference with per-request API-key rotation.
///
/// The existing Groq key pool is shared by STT and LLM configuration, but the
/// LLM client needs its own authenticated HTTP client for each key. A failed
/// 401/403/429 marks only that key as cooling down and retries the same request
/// with the next available key. Key values are never written to logs.
/// </summary>
public sealed class GroqRotatingInferenceClient : IInferenceClient
{
    private readonly string _baseUrl;
    private readonly string _fallbackApiKey;
    private readonly ISemanticMemoryService? _semanticMemory;
    private readonly GenerationTuningSettings _tuning;
    private readonly string _replyTonePreset;
    private readonly GroqApiKeyPool _keyPool;
    private readonly object _gate = new();
    private readonly Dictionary<string, RemoteInferenceClient> _clients = new(StringComparer.Ordinal);
    private string _model;
    private bool _disposed;

    public GroqRotatingInferenceClient(
        string baseUrl,
        string modelName,
        GroqApiKeyPool keyPool,
        string fallbackApiKey = "",
        ISemanticMemoryService? semanticMemory = null,
        GenerationTuningSettings? tuning = null,
        string replyTonePreset = "natural")
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("Base URL cannot be empty.", nameof(baseUrl));

        _baseUrl = baseUrl.TrimEnd('/');
        _model = string.IsNullOrWhiteSpace(modelName) ? "openai/gpt-oss-120b" : modelName.Trim();
        _keyPool = keyPool ?? throw new ArgumentNullException(nameof(keyPool));
        _fallbackApiKey = NormalizeKey(fallbackApiKey);
        _semanticMemory = semanticMemory;
        _tuning = (tuning ?? GenerationTuningSettings.Default).Clamp();
        _replyTonePreset = string.IsNullOrWhiteSpace(replyTonePreset) ? "natural" : replyTonePreset.Trim().ToLowerInvariant();
    }

    public string Model => GetCurrentClient().Model;

    public bool IsLoaded => GetCurrentClient().IsLoaded;

    public bool IsWarmedUp => GetCurrentClient().IsWarmedUp;

    public Task<bool> IsServerReachableAsync(CancellationToken ct = default) =>
        GetCurrentClient().IsServerReachableAsync(ct);

    public Task<bool> WarmupModelAsync(string? model = null, CancellationToken ct = default) =>
        GetCurrentClient().WarmupModelAsync(model, ct);

    public Task<AiReply> ChatWithEmotionAsync(
        string userText,
        string? personaName = null,
        string? preferredEmotion = null,
        IReadOnlyList<(string role, string content)>? history = null,
        CancellationToken ct = default,
        string? systemInstructions = null,
        string? correlationId = null) =>
        ExecuteWithRotationAsync(client => client.ChatWithEmotionAsync(
            userText,
            personaName,
            preferredEmotion,
            history,
            ct,
            systemInstructions,
            correlationId), ct);

    public Task<AiReply> ChatWithEmotionStreamingAsync(
        string userText,
        string? personaName = null,
        string? preferredEmotion = null,
        IReadOnlyList<(string role, string content)>? history = null,
        Action<string>? onPartialReply = null,
        CancellationToken ct = default,
        string? systemInstructions = null,
        string? correlationId = null) =>
        ExecuteWithRotationAsync(client => client.ChatWithEmotionStreamingAsync(
            userText,
            personaName,
            preferredEmotion,
            history,
            onPartialReply,
            ct,
            systemInstructions,
            correlationId), ct);

    public void SetModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return;

        lock (_gate)
        {
            _model = model.Trim();
            foreach (var client in _clients.Values)
                client.SetModel(_model);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            foreach (var client in _clients.Values)
            {
                try { client.Dispose(); }
                catch { /* best effort during process shutdown */ }
            }

            _clients.Clear();
        }
    }

    private RemoteInferenceClient GetCurrentClient()
    {
        var key = GetCandidates().First();
        return GetClient(key);
    }

    private IReadOnlyList<string> GetCandidates()
    {
        var candidates = _keyPool.GetCandidates().ToList();
        if (candidates.Count == 0 && _fallbackApiKey.Length > 0)
            candidates.Add(_fallbackApiKey);
        if (candidates.Count == 0)
            candidates.Add(string.Empty);
        return candidates;
    }

    private RemoteInferenceClient GetClient(string apiKey)
    {
        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(GroqRotatingInferenceClient));

            if (_clients.TryGetValue(apiKey, out var existing))
                return existing;

            var client = new RemoteInferenceClient(
                _baseUrl,
                apiKey,
                _model,
                _semanticMemory,
                _tuning,
                _replyTonePreset);
            _clients[apiKey] = client;
            return client;
        }
    }

    private async Task<T> ExecuteWithRotationAsync<T>(
        Func<IInferenceClient, Task<T>> operation,
        CancellationToken ct)
    {
        Exception? lastError = null;

        foreach (var apiKey in GetCandidates())
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var result = await operation(GetClient(apiKey));
                _keyPool.MarkSuccess(apiKey);
                return result;
            }
            catch (Exception ex) when (TryGetRotationStatus(ex, out var statusCode))
            {
                _keyPool.MarkFailure(apiKey, statusCode);
                lastError = ex;
                DevLog.WriteLine(
                    "Groq LLM key failed; rotating (status={0}, available_keys={1})",
                    statusCode,
                    _keyPool.GetCandidates().Count);
            }
        }

        if (lastError is not null)
            throw lastError;

        throw new InferenceException("Groq inference key pool is empty.");
    }

    private static bool TryGetRotationStatus(Exception exception, out int statusCode)
    {
        if (exception is InferenceRateLimitException)
        {
            statusCode = 429;
            return true;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException http && http.StatusCode is { } status)
            {
                statusCode = (int)status;
                return statusCode is 401 or 403 or 429;
            }
        }

        statusCode = 0;
        return false;
    }

    private static string NormalizeKey(string value) =>
        (value ?? string.Empty).Trim().Trim('"', '\'');
}
