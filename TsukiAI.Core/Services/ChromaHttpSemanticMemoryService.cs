using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TsukiAI.Core.Services;

/// <summary>
/// Semantic memory backed by a ChromaDB server (REST v2) instead of the desktop
/// Python worker. The chromadb container auto-embeds documents with its default
/// embedding function, so no client-side embedding is needed.
/// Keeps the same circuit-breaker/fail-soft behavior as the desktop service so a
/// memory outage degrades to "no semantic recall" instead of failing chat turns.
/// </summary>
public sealed class ChromaHttpSemanticMemoryService : ISemanticMemoryService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _collectionName;
    private readonly string _routePrefix; // /api/v2/tenants/{tenant}/databases/{db}
    private readonly int _failureThreshold;
    private readonly TimeSpan _circuitCooldown;
    private readonly TimeSpan _requestTimeout;

    private readonly SemaphoreSlim _collectionGate = new(1, 1);
    private string? _collectionId;

    private int _consecutiveFailures;
    private DateTimeOffset? _circuitOpenUntilUtc;
    private bool _disposed;

    public ChromaHttpSemanticMemoryService(string baseUrl, string? collectionName = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _collectionName = string.IsNullOrWhiteSpace(collectionName)
            ? GetEnv("TSUKI_CHROMA_COLLECTION", "tsuki_memory")
            : collectionName!;
        var tenant = GetEnv("TSUKI_CHROMA_TENANT", "default_tenant");
        var database = GetEnv("TSUKI_CHROMA_DATABASE", "default_database");
        _routePrefix = $"/api/v2/tenants/{Uri.EscapeDataString(tenant)}/databases/{Uri.EscapeDataString(database)}";
        _failureThreshold = Math.Max(1, GetIntEnv("TSUKI_SEMANTIC_CB_FAILURES", 5));
        _circuitCooldown = TimeSpan.FromMilliseconds(Math.Max(1000, GetIntEnv("TSUKI_SEMANTIC_CB_COOLDOWN_MS", 30000)));
        _requestTimeout = TimeSpan.FromMilliseconds(Math.Max(1000, GetIntEnv("TSUKI_SEMANTIC_REQUEST_TIMEOUT_MS", 8000)));

        _http = new HttpClient { BaseAddress = new Uri(_baseUrl), Timeout = _requestTimeout };

        DevLog.WriteLine(
            "SemanticMemory(ChromaHttp): init url={0}, collection={1}, cb_threshold={2}, cb_cooldown_ms={3}, request_timeout_ms={4}",
            _baseUrl, _collectionName, _failureThreshold, (int)_circuitCooldown.TotalMilliseconds, (int)_requestTimeout.TotalMilliseconds);
    }

    public async Task<bool> EnsureReadyAsync(CancellationToken ct = default)
    {
        if (IsCircuitOpen())
            return false;

        try
        {
            using var resp = await SendAsync(HttpMethod.Get, "/api/v2/heartbeat", null, ct);
            if (resp is { IsSuccessStatusCode: true })
            {
                var ok = await EnsureCollectionAsync(ct);
                if (ok)
                {
                    RecordSuccess();
                    DevLog.WriteLine("SemanticMemory(ChromaHttp): ready");
                }
                return ok;
            }

            RecordFailure("heartbeat", $"status={resp?.StatusCode}");
            return false;
        }
        catch (Exception ex)
        {
            RecordFailure("heartbeat(exception)", ex.Message);
            return false;
        }
    }

    public async Task AddMemoryAsync(string text, string source = "voicechat", string? userId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) || IsCircuitOpen())
            return;

        try
        {
            var collectionId = await GetCollectionIdAsync(ct);
            if (collectionId is null)
            {
                RecordFailure("add", "collection unavailable");
                return;
            }

            var body = new
            {
                ids = new[] { Guid.NewGuid().ToString("N") },
                documents = new[] { text },
                metadatas = new[] { new
                {
                    source,
                    user_id = userId ?? string.Empty,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                } }
            };
            using var resp = await SendAsync(
                HttpMethod.Post, $"{_routePrefix}/collections/{collectionId}/add", body, ct);

            if (resp is { IsSuccessStatusCode: true })
            {
                RecordSuccess();
                return;
            }

            // A duplicate id would 4xx, but ids are GUIDs; anything else is a real failure.
            RecordFailure("add", $"status={resp?.StatusCode}: {await ReadErrorAsync(resp, ct)}");
        }
        catch (Exception ex)
        {
            RecordFailure("add(exception)", ex.Message);
        }
    }

    public async Task<IReadOnlyList<SemanticMemoryHit>> SearchAsync(string query, int topK = 5, string? userId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || IsCircuitOpen())
            return [];

        try
        {
            var k = Math.Max(1, Math.Min(20, topK));
            var collectionId = await GetCollectionIdAsync(ct);
            if (collectionId is null)
            {
                RecordFailure("search", "collection unavailable");
                return [];
            }

            // Scope to the user when provided: one user's memories never leak
            // into another user's recall.
            var body = userId is null
                ? (object)new { query_texts = new[] { query }, n_results = k }
                : new { query_texts = new[] { query }, n_results = k, where = new { user_id = userId } };
            using var resp = await SendAsync(
                HttpMethod.Post, $"{_routePrefix}/collections/{collectionId}/query", body, ct);

            if (resp is null || !resp.IsSuccessStatusCode)
            {
                RecordFailure("search", $"status={resp?.StatusCode}: {await ReadErrorAsync(resp, ct)}");
                return [];
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            var texts = root.GetProperty("documents").EnumerateArray().FirstOrDefault();
            var metas = root.TryGetProperty("metadatas", out var m) ? m.EnumerateArray().FirstOrDefault() : default;
            var distances = root.TryGetProperty("distances", out var d) ? d.EnumerateArray().FirstOrDefault() : default;
            var ids = root.TryGetProperty("ids", out var i) ? i.EnumerateArray().FirstOrDefault() : default;

            var hits = new List<SemanticMemoryHit>();
            var count = texts.ValueKind == JsonValueKind.Array ? texts.GetArrayLength() : 0;
            for (var idx = 0; idx < count; idx++)
            {
                var text = texts[idx].GetString() ?? string.Empty;
                var source = "voicechat";
                if (metas.ValueKind == JsonValueKind.Array && idx < metas.GetArrayLength())
                {
                    var meta = metas[idx];
                    if (meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("source", out var s))
                        source = s.GetString() ?? source;
                }

                var distance = 1.0;
                if (distances.ValueKind == JsonValueKind.Array && idx < distances.GetArrayLength()
                    && distances[idx].TryGetDouble(out var dist))
                {
                    distance = dist;
                }

                var id = ids.ValueKind == JsonValueKind.Array && idx < ids.GetArrayLength()
                    ? ids[idx].GetString() ?? $"{idx}"
                    : $"{idx}";

                hits.Add(new SemanticMemoryHit(id, text, source, distance));
            }

            RecordSuccess();
            return hits;
        }
        catch (Exception ex)
        {
            RecordFailure("search(exception)", ex.Message);
            return [];
        }
    }

    public async Task DeleteOlderThanAsync(TimeSpan age, CancellationToken ct = default)
    {
        if (IsCircuitOpen())
            return;

        try
        {
            var collectionId = await GetCollectionIdAsync(ct);
            if (collectionId is null)
                return;

            var cutoff = DateTimeOffset.UtcNow.Subtract(age).ToUnixTimeSeconds();
            var body = new
            {
                where = new Dictionary<string, object>
                {
                    ["timestamp"] = new Dictionary<string, object> { ["$lt"] = cutoff }
                }
            };
            using var resp = await SendAsync(
                HttpMethod.Post, $"{_routePrefix}/collections/{collectionId}/delete", body, ct);

            if (resp is { IsSuccessStatusCode: true })
            {
                RecordSuccess();
                DevLog.WriteLine("SemanticMemory(ChromaHttp): purged memories older than {0} days", (int)age.TotalDays);
                return;
            }

            RecordFailure("delete-old", $"status={resp?.StatusCode}: {await ReadErrorAsync(resp, ct)}");
        }
        catch (Exception ex)
        {
            RecordFailure("delete-old(exception)", ex.Message);
        }
    }

    private async Task<HttpResponseMessage?> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_requestTimeout);

        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        }

        return await _http.SendAsync(request, timeoutCts.Token);
    }

    private async Task<string?> GetCollectionIdAsync(CancellationToken ct)
    {
        if (_collectionId is not null)
            return _collectionId;

        await _collectionGate.WaitAsync(ct);
        try
        {
            if (_collectionId is not null)
                return _collectionId;

            var body = new { name = _collectionName, get_or_create = true, metadata = (object?)null };
            using var resp = await SendAsync(HttpMethod.Post, $"{_routePrefix}/collections", body, ct);
            if (resp is null || !resp.IsSuccessStatusCode)
            {
                DevLog.WriteLine("SemanticMemory(ChromaHttp): create/get collection failed: {0}", await ReadErrorAsync(resp, ct));
                return null;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("id", out var idEl))
            {
                _collectionId = idEl.GetString();
                DevLog.WriteLine("SemanticMemory(ChromaHttp): collection '{0}' id={1}", _collectionName, _collectionId);
            }

            return _collectionId;
        }
        finally
        {
            _collectionGate.Release();
        }
    }

    private async Task<bool> EnsureCollectionAsync(CancellationToken ct) => await GetCollectionIdAsync(ct) is not null;

    private static async Task<string> ReadErrorAsync(HttpResponseMessage? resp, CancellationToken ct)
    {
        if (resp is null)
            return "no response";
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            return body.Length > 220 ? body[..220] + "..." : body;
        }
        catch
        {
            return $"status={(int)resp.StatusCode}";
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

    private void RecordSuccess()
    {
        Volatile.Write(ref _consecutiveFailures, 0);
        _circuitOpenUntilUtc = null;
    }

    private void RecordFailure(string operation, string? message)
    {
        var failures = Interlocked.Increment(ref _consecutiveFailures);
        DevLog.WriteLine("SemanticMemory(ChromaHttp): {0} failed (count={1}): {2}", operation, failures, message ?? string.Empty);
        if (failures >= _failureThreshold)
        {
            _circuitOpenUntilUtc = DateTimeOffset.UtcNow.Add(_circuitCooldown);
            DevLog.WriteLine("SemanticMemory(ChromaHttp): circuit opened for {0}ms", (int)_circuitCooldown.TotalMilliseconds);
            Volatile.Write(ref _consecutiveFailures, 0);
        }
    }

    private static string GetEnv(string key, string defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(raw) ? defaultValue : raw.Trim();
    }

    private static int GetIntEnv(string key, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        return int.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _http.Dispose();
        _collectionGate.Dispose();
    }
}
