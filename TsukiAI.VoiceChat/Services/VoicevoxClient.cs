using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using Polly;
using Polly.Retry;
using TsukiAI.Core.Services;

namespace TsukiAI.VoiceChat.Services;

public sealed class VoicevoxClient : IDisposable
{
    private HttpClient _http;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, byte[]> _wavCache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _wavCacheLru = new();
    private const int WavCacheMaxEntries = 50;
    
    // Resilience policy for TTS synthesis
    private static readonly ResiliencePipeline<HttpResponseMessage> TtsRetryPipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
        .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
        {
            MaxRetryAttempts = 2,
            Delay = TimeSpan.FromMilliseconds(500),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                .HandleResult(r => 
                    r.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                    r.StatusCode == System.Net.HttpStatusCode.RequestTimeout ||
                    (int)r.StatusCode >= 500)
                .Handle<HttpRequestException>()
                .Handle<TaskCanceledException>(ex => !ex.CancellationToken.IsCancellationRequested),
            OnRetry = args =>
            {
                DevLog.WriteLine($"VoicevoxClient[Retry]: attempt={args.AttemptNumber}, delay={args.RetryDelay.TotalMilliseconds:F0}ms");
                return ValueTask.CompletedTask;
            }
        })
        .Build();

    public string BaseUrl { get; private set; }

    public VoicevoxClient(string baseUrl = "http://127.0.0.1:50021")
    {
        BaseUrl = NormalizeBaseUrl(baseUrl);
        _http = CreateClient(BaseUrl);
    }

    public void SetBaseUrl(string baseUrl)
    {
        BaseUrl = NormalizeBaseUrl(baseUrl);
        var old = _http;
        _http = CreateClient(BaseUrl);
        try { old.Dispose(); } catch { }
    }

    public async Task<byte[]> SynthesizeWavAsync(string text, int speakerStyleId, CancellationToken ct, string? correlationId = null)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return Array.Empty<byte>();

        var cacheKey = $"{speakerStyleId}:{text}";
        if (TryGetCachedWav(cacheKey, out var cached))
            return cached;

        var queryUrl = $"/audio_query?text={Uri.EscapeDataString(text)}&speaker={speakerStyleId}";
        
        // Wrap query call with retry policy
        using var queryResp = await TtsRetryPipeline.ExecuteAsync(
            async ct =>
            {
                using var queryRequest = new HttpRequestMessage(HttpMethod.Post, queryUrl);
                if (!string.IsNullOrWhiteSpace(correlationId))
                    queryRequest.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
                return await _http.SendAsync(queryRequest, ct);
            },
            ct);
        queryResp.EnsureSuccessStatusCode();

        var queryJson = await queryResp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(queryJson))
            return Array.Empty<byte>();

        using var synthReq = new HttpRequestMessage(HttpMethod.Post, $"/synthesis?speaker={speakerStyleId}")
        {
            Content = new StringContent(queryJson, Encoding.UTF8, "application/json")
        };
        synthReq.Headers.TryAddWithoutValidation("accept", "audio/wav");
        if (!string.IsNullOrWhiteSpace(correlationId))
            synthReq.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);

        // Wrap synthesis call with retry policy
        using var synthResp = await TtsRetryPipeline.ExecuteAsync(
            async ct => await _http.SendAsync(synthReq, HttpCompletionOption.ResponseHeadersRead, ct),
            ct);
        synthResp.EnsureSuccessStatusCode();

        var wav = await synthResp.Content.ReadAsByteArrayAsync(ct);
        if (wav.Length > 0)
            StoreCachedWav(cacheKey, wav);
        return wav;
    }

    public async Task<bool> IsAliveAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync("/speakers", ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<VoicevoxSpeaker>> GetSpeakersAsync(CancellationToken ct)
    {
        try
        {
            var speakers = await _http.GetFromJsonAsync<List<VoicevoxSpeaker>>("/speakers", ct);
            return speakers ?? new List<VoicevoxSpeaker>();
        }
        catch
        {
            return new List<VoicevoxSpeaker>();
        }
    }

    public static async Task<string?> AutoDetectBaseUrlAsync(CancellationToken ct)
    {
        var candidates = new[]
        {
            "http://127.0.0.1:50021",
            "http://localhost:50021",
            "http://127.0.0.1:50022",
            "http://localhost:50022"
        };

        foreach (var url in candidates)
        {
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(1.5) };
                using var resp = await http.GetAsync("/speakers", ct);
                if (resp.IsSuccessStatusCode)
                    return url;
            }
            catch { }
        }

        return null;
    }

    private static HttpClient CreateClient(string baseUrl)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(60)
        };
        client.DefaultRequestHeaders.Add("ngrok-skip-browser-warning", "true");
        return client;
    }

    private bool TryGetCachedWav(string cacheKey, out byte[] wav)
    {
        lock (_cacheLock)
        {
            if (_wavCache.TryGetValue(cacheKey, out var existing))
            {
                _wavCacheLru.Remove(cacheKey);
                _wavCacheLru.AddFirst(cacheKey);
                wav = existing;
                return true;
            }
        }

        wav = Array.Empty<byte>();
        return false;
    }

    private void StoreCachedWav(string cacheKey, byte[] wav)
    {
        lock (_cacheLock)
        {
            _wavCache[cacheKey] = wav;
            _wavCacheLru.Remove(cacheKey);
            _wavCacheLru.AddFirst(cacheKey);

            while (_wavCacheLru.Count > WavCacheMaxEntries)
            {
                var last = _wavCacheLru.Last?.Value;
                if (last is null) break;
                _wavCacheLru.RemoveLast();
                _wavCache.Remove(last);
            }
        }
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        baseUrl = (baseUrl ?? "").Trim();
        if (baseUrl.Length == 0) return "http://127.0.0.1:50021";
        if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            baseUrl = "http://" + baseUrl;
        return baseUrl.TrimEnd('/');
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}

public sealed class VoicevoxSpeaker
{
    public string name { get; set; } = "";
    public string speaker_uuid { get; set; } = "";
    public List<VoicevoxStyle> styles { get; set; } = new();
    public string version { get; set; } = "";
}

public sealed class VoicevoxStyle
{
    public string name { get; set; } = "";
    public int id { get; set; }
}
