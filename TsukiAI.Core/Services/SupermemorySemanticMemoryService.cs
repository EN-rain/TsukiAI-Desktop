using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TsukiAI.Core.Services;

/// <summary>
/// Supermemory REST adapter for .NET clients. It uses the v4 direct-memory and
/// hybrid-search endpoints, keeps the current singular container tag explicit,
/// and fails soft so a memory outage never breaks a chat turn.
/// </summary>
public sealed class SupermemorySemanticMemoryService : ISemanticMemoryService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly string _defaultContainerTag;
    private readonly int _maxAttempts;
    private readonly TimeSpan _requestTimeout;
    private readonly int _failureThreshold;
    private readonly TimeSpan _circuitCooldown;
    private int _consecutiveFailures;
    private DateTimeOffset? _circuitOpenUntilUtc;
    private bool _disposed;

    public SupermemorySemanticMemoryService(
        string apiKey,
        string baseUrl = "https://api.supermemory.ai",
        string defaultContainerTag = ConversationMemoryIdentity.DesktopDefaultContainer,
        HttpMessageHandler? handler = null)
    {
        var normalizedKey = NormalizeApiKey(apiKey);
        if (string.IsNullOrWhiteSpace(normalizedKey))
            throw new ArgumentException("Supermemory API key is required.", nameof(apiKey));

        if (!Uri.TryCreate(baseUrl?.TrimEnd('/'), UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Supermemory base URL must be an absolute HTTP(S) URL.", nameof(baseUrl));
        }

        _defaultContainerTag = ConversationMemoryIdentity.SanitizeContainerTag(
            defaultContainerTag,
            ConversationMemoryIdentity.DesktopDefaultContainer);
        _maxAttempts = Math.Clamp(ReadIntEnv("TSUKI_SUPERMEMORY_MAX_ATTEMPTS", 3), 1, 5);
        _requestTimeout = TimeSpan.FromMilliseconds(Math.Max(1000, ReadIntEnv("TSUKI_SUPERMEMORY_REQUEST_TIMEOUT_MS", 8000)));
        _failureThreshold = Math.Max(1, ReadIntEnv("TSUKI_SUPERMEMORY_CB_FAILURES", 5));
        _circuitCooldown = TimeSpan.FromMilliseconds(Math.Max(1000, ReadIntEnv("TSUKI_SUPERMEMORY_CB_COOLDOWN_MS", 30000)));

        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = baseUri;
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", normalizedKey);

        DevLog.WriteLine(
            "SemanticMemory(Supermemory): init url={0}, default_container={1}, timeout_ms={2}",
            baseUri,
            _defaultContainerTag,
            (int)_requestTimeout.TotalMilliseconds);
    }

    public async Task<bool> EnsureReadyAsync(CancellationToken ct = default)
    {
        // Supermemory has no provider-neutral health endpoint in its documented
        // API. Configuration is validated here; requests remain fail-soft.
        await Task.CompletedTask;
        return !_disposed && !IsCircuitOpen() && !ct.IsCancellationRequested;
    }

    public async Task AddMemoryAsync(
        string text,
        string source = "voicechat",
        string? userId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) || IsCircuitOpen())
            return;

        var content = text.Trim();
        if (content.Length > 10000)
            content = content[..10000];

        var body = new
        {
            containerTag = ResolveContainerTag(userId),
            memories = new[]
            {
                new
                {
                    content,
                    isStatic = false,
                    metadata = new
                    {
                        source = string.IsNullOrWhiteSpace(source) ? "chat" : source.Trim(),
                        timestamp = DateTimeOffset.UtcNow.ToString("O")
                    }
                }
            }
        };

        var response = await SendJsonAsync(HttpMethod.Post, "/v4/memories", body, ct);
        if (response?.IsSuccess == true)
        {
            RecordSuccess();
            return;
        }

        RecordFailure("add", response?.Error ?? "no response");
    }

    public async Task<IReadOnlyList<SemanticMemoryHit>> SearchAsync(
        string query,
        int topK = 5,
        string? userId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || IsCircuitOpen())
            return [];

        var body = new
        {
            q = query.Trim(),
            containerTag = ResolveContainerTag(userId),
            searchMode = "hybrid",
            limit = Math.Clamp(topK, 2, 100)
        };

        var response = await SendJsonAsync(HttpMethod.Post, "/v4/search", body, ct);
        if (response?.IsSuccess != true)
        {
            RecordFailure("search", response?.Error ?? "no response");
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                RecordSuccess();
                return [];
            }

            var hits = new List<SemanticMemoryHit>();
            foreach (var item in results.EnumerateArray().Take(Math.Clamp(topK, 1, 20)))
            {
                var text = FirstString(item, "content", "memory", "chunk");
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var similarity = FirstDouble(item, "similarity", "score");
                var distance = FirstDouble(item, "distance") ?? (similarity is null ? 1.0 : 1.0 - similarity.Value);
                var id = FirstString(item, "id", "docId", "chunkId") ?? Guid.NewGuid().ToString("N");
                var source = "supermemory";
                if (item.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
                    source = FirstString(metadata, "source") ?? source;

                hits.Add(new SemanticMemoryHit(id, text, source, distance));
            }

            RecordSuccess();
            return hits;
        }
        catch (JsonException ex)
        {
            RecordFailure("search(parse)", ex.Message);
            return [];
        }
    }

    public Task DeleteOlderThanAsync(TimeSpan age, CancellationToken ct = default)
    {
        // The documented direct-memory API does not expose a date-filtered
        // delete endpoint. Supermemory owns memory extraction/forgetting; do not
        // issue an undocumented destructive request from the retention worker.
        return Task.CompletedTask;
    }

    private string ResolveContainerTag(string? userId) =>
        ConversationMemoryIdentity.SanitizeContainerTag(userId, _defaultContainerTag);

    private async Task<ResponseData?> SendJsonAsync(
        HttpMethod method,
        string path,
        object body,
        CancellationToken ct)
    {
        if (_disposed || IsCircuitOpen())
            return null;

        var json = JsonSerializer.Serialize(body, JsonOptions);
        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(method, path)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(_requestTimeout);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
                var responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);

                if (response.IsSuccessStatusCode)
                    return new ResponseData(true, responseBody, null);

                var error = $"status={(int)response.StatusCode}";
                if (responseBody.Length > 220)
                    responseBody = responseBody[..220] + "...";
                if (!string.IsNullOrWhiteSpace(responseBody))
                    error += $": {responseBody}";

                if (attempt < _maxAttempts && ShouldRetry(response.StatusCode))
                {
                    await Task.Delay(GetRetryDelay(response, attempt), ct);
                    continue;
                }

                return new ResponseData(false, string.Empty, error);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < _maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex) when (attempt < _maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct);
                if (attempt == _maxAttempts)
                    return new ResponseData(false, string.Empty, ex.Message);
            }
            catch (Exception ex)
            {
                return new ResponseData(false, string.Empty, ex.Message);
            }
        }

        return new ResponseData(false, string.Empty, "request attempts exhausted");
    }

    private static bool ShouldRetry(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.Conflict or HttpStatusCode.TooManyRequests ||
        (int)status >= 500;

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } retryAfter)
            return retryAfter < TimeSpan.FromSeconds(10) ? retryAfter : TimeSpan.FromSeconds(10);

        return TimeSpan.FromMilliseconds(250 * attempt);
    }

    private void RecordSuccess()
    {
        Volatile.Write(ref _consecutiveFailures, 0);
        _circuitOpenUntilUtc = null;
    }

    private void RecordFailure(string operation, string message)
    {
        var failures = Interlocked.Increment(ref _consecutiveFailures);
        DevLog.WriteLine("SemanticMemory(Supermemory): {0} failed (count={1}): {2}", operation, failures, message);
        if (failures >= _failureThreshold)
        {
            _circuitOpenUntilUtc = DateTimeOffset.UtcNow.Add(_circuitCooldown);
            Volatile.Write(ref _consecutiveFailures, 0);
        }
    }

    private bool IsCircuitOpen()
    {
        var openUntil = _circuitOpenUntilUtc;
        if (openUntil is null)
            return false;
        if (DateTimeOffset.UtcNow < openUntil.Value)
            return true;

        _circuitOpenUntilUtc = null;
        return false;
    }

    private static string? FirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
                return property.GetString();
        }

        return null;
    }

    private static double? FirstDouble(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property) && property.TryGetDouble(out var value))
                return value;
        }

        return null;
    }

    private static string NormalizeApiKey(string? value)
    {
        var key = (value ?? string.Empty).Trim().Trim('"', '\'');
        return key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? key[7..].Trim()
            : key;
    }

    private static int ReadIntEnv(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _http.Dispose();
    }

    private sealed record ResponseData(bool IsSuccess, string Body, string? Error);
}
