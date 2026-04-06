using System.Net.Http;
using System.Text.Json;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace TsukiAI.VoiceChat.Services;

public sealed class TranslationService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly bool _enabled;
    private readonly Dictionary<string, (string Text, DateTimeOffset ExpiresAt)> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _cacheLru = new();
    private readonly object _cacheLock = new();
    private const int CacheCapacity = 256;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private static readonly ResiliencePipeline<HttpResponseMessage> HttpRetryPipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
        .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromSeconds(1),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                .HandleResult(r => r.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                                   r.StatusCode == System.Net.HttpStatusCode.RequestTimeout ||
                                   (int)r.StatusCode >= 500)
                .Handle<HttpRequestException>()
                .Handle<TaskCanceledException>(ex => !ex.CancellationToken.IsCancellationRequested),
            OnRetry = args =>
            {
                var code = args.Outcome.Result?.StatusCode.ToString() ?? "Exception";
                TsukiAI.Core.Services.DevLog.WriteLine("[Translation][Retry]: attempt={0}, status={1}, delay_ms={2}",
                    args.AttemptNumber, code, args.RetryDelay.TotalMilliseconds);
                return ValueTask.CompletedTask;
            }
        })
        .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
        {
            FailureRatio = 1.0,
            MinimumThroughput = 5,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(30),
            ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                .HandleResult(r => (int)r.StatusCode >= 500 || r.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                .Handle<HttpRequestException>(),
        })
        .Build();

    public TranslationService(TsukiAI.Core.Models.AppSettings settings)
    {
        _apiKey = settings.DeepLApiKey?.Trim() ?? "";
        _enabled = settings.UseDeepLTranslate && !string.IsNullOrWhiteSpace(_apiKey);
        _baseUrl = settings.UseDeepLFreeApi ? "https://api-free.deepl.com/v2" : "https://api.deepl.com/v2";

        // 5s timeout — DeepL is fast; 15s was causing full-pipeline stalls on network issues
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        if (_enabled)
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"DeepL-Auth-Key {_apiKey}");
    }

    public bool IsEnabled => _enabled;

    public async Task<string> TranslateToJapaneseAsync(string text, CancellationToken ct = default, string? correlationId = null)
    {
        return await TranslateAsync(text, "JA", "EN", ct, correlationId);
    }

    public async Task<string> TranslateToEnglishAsync(string text, CancellationToken ct = default, string? correlationId = null)
    {
        return await TranslateAsync(text, "EN", null, ct, correlationId);
    }

    public async Task<string> TranslateAsync(string text, string targetLang, string? sourceLang = null, CancellationToken ct = default, string? correlationId = null)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return string.Empty;
        if (!_enabled) return text;
        var cacheKey = $"{sourceLang ?? "auto"}->{targetLang}:{text}";
        if (TryGetCached(cacheKey, out var cached))
            return cached;

        try
        {
            var form = new Dictionary<string, string>
            {
                ["text"] = text,
                ["target_lang"] = targetLang
            };
            if (!string.IsNullOrWhiteSpace(sourceLang))
                form["source_lang"] = sourceLang;

            using var response = await HttpRetryPipeline.ExecuteAsync(
                async innerCt =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/translate")
                    {
                        Content = new FormUrlEncodedContent(form)
                    };
                    if (!string.IsNullOrWhiteSpace(correlationId))
                        request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
                    return await _httpClient.SendAsync(request, innerCt);
                },
                ct);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                TsukiAI.Core.Services.DevLog.WriteLine($"[Translation] DeepL failed: {(int)response.StatusCode} {err}");
                return text;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<DeepLResponse>(body);
            var translated = result?.translations?.FirstOrDefault()?.text?.Trim() ?? text;
            StoreCached(cacheKey, translated);
            return translated;
        }
        catch (Exception ex)
        {
            TsukiAI.Core.Services.DevLog.WriteLine($"[Translation] DeepL exception: {ex.Message}");
            return text;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private bool TryGetCached(string cacheKey, out string text)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(cacheKey, out var entry))
            {
                if (entry.ExpiresAt > DateTimeOffset.UtcNow)
                {
                    _cacheLru.Remove(cacheKey);
                    _cacheLru.AddFirst(cacheKey);
                    text = entry.Text;
                    return true;
                }

                _cache.Remove(cacheKey);
                _cacheLru.Remove(cacheKey);
            }
        }

        text = string.Empty;
        return false;
    }

    private void StoreCached(string cacheKey, string text)
    {
        lock (_cacheLock)
        {
            _cache[cacheKey] = (text, DateTimeOffset.UtcNow.Add(CacheTtl));
            _cacheLru.Remove(cacheKey);
            _cacheLru.AddFirst(cacheKey);

            while (_cacheLru.Count > CacheCapacity)
            {
                var last = _cacheLru.Last?.Value;
                if (last is null)
                    break;
                _cacheLru.RemoveLast();
                _cache.Remove(last);
            }
        }
    }

    private sealed class DeepLResponse
    {
        public DeepLTranslation[]? translations { get; set; }
    }

    private sealed class DeepLTranslation
    {
        public string? text { get; set; }
    }
}
