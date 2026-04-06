using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Polly;
using Polly.Retry;
using Polly.CircuitBreaker;
using TsukiAI.Core.Models;

namespace TsukiAI.Core.Services;

/// <summary>
/// Remote inference client for vLLM OpenAI-compatible API.
/// Supports both streaming and non-streaming inference with dual output (UI + TTS).
/// Includes resilience policies: retry with exponential backoff and jitter.
/// </summary>
public sealed class RemoteInferenceClient : IInferenceClient
{
    private readonly HttpClient _httpClient;
    private bool _disposed = false;
    private CancellationTokenSource? _keepAliveCts;
    private Task? _keepAliveTask;
    private int _keepAliveInFlight;
    private readonly ISemanticMemoryService? _semanticMemory;
    private readonly GenerationTuningSettings _tuning;
    private readonly string _replyTonePreset;
    private readonly TimeSpan _semanticSearchBudget;
    private static readonly PromptBuilder PromptBuilder = new();
    private const int RemoteHistoryWindowDefault = 6;
    private const int RemoteHistoryWindowDetailed = 8;
    private bool _skipGenerateEndpoint = false; // Skip /generate and use /chat/completions directly
    private readonly Channel<string>? _memoryWriteQueue;
    private readonly CancellationTokenSource? _memoryWriteCts;
    private readonly Task? _memoryWriteWorkerTask;
    
    // Resilience policy: retry with exponential backoff + jitter
    private static readonly ResiliencePipeline<HttpResponseMessage> HttpRetryPipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
        .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromSeconds(1),
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
                var statusCode = args.Outcome.Result?.StatusCode.ToString() ?? "Exception";
                DevLog.WriteLine($"RemoteInferenceClient[Retry]: attempt={args.AttemptNumber}, status={statusCode}, delay={args.RetryDelay.TotalSeconds:F1}s");
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
                .Handle<HttpRequestException>()
                .Handle<TaskCanceledException>(ex => !ex.CancellationToken.IsCancellationRequested),
            OnOpened = args =>
            {
                DevLog.WriteLine("RemoteInferenceClient[CircuitBreaker]: opened for {0}ms", args.BreakDuration.TotalMilliseconds);
                return ValueTask.CompletedTask;
            },
            OnClosed = _ =>
            {
                DevLog.WriteLine("RemoteInferenceClient[CircuitBreaker]: closed");
                return ValueTask.CompletedTask;
            }
        })
        .Build();
    
    public string BaseUrl { get; private set; }
    public string ApiKey { get; private set; }
    public string Model { get; private set; }
    public bool IsLoaded => !string.IsNullOrEmpty(BaseUrl);
    public bool IsWarmedUp { get; private set; } = false;

    public RemoteInferenceClient(
        string baseUrl,
        string apiKey = "",
        string modelName = "Qwen/Qwen2.5-3B-Instruct",
        ISemanticMemoryService? semanticMemory = null,
        GenerationTuningSettings? tuning = null,
        string replyTonePreset = "natural")
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("Base URL cannot be empty. Please configure the Remote Inference URL in settings.", nameof(baseUrl));
        }
        
        BaseUrl = baseUrl.TrimEnd('/');
        
        if (!BaseUrl.StartsWith("http://") && !BaseUrl.StartsWith("https://"))
        {
            throw new ArgumentException($"Base URL must start with http:// or https://. Got: {BaseUrl}", nameof(baseUrl));
        }
        
        ApiKey = apiKey ?? "";
        Model = NormalizeModelForBaseUrl(baseUrl, modelName);
        _semanticMemory = semanticMemory;
        _tuning = (tuning ?? GenerationTuningSettings.Default).Clamp();
        _replyTonePreset = string.IsNullOrWhiteSpace(replyTonePreset) ? "natural" : replyTonePreset.Trim().ToLowerInvariant();
        _semanticSearchBudget = TimeSpan.FromMilliseconds(Math.Max(50, ReadIntEnv("TSUKI_SEMANTIC_SEARCH_BUDGET_MS", 150)));
        if (_semanticMemory is not null)
        {
            _memoryWriteQueue = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
            _memoryWriteCts = new CancellationTokenSource();
            _memoryWriteWorkerTask = Task.Run(MemoryWriteWorkerAsync);
        }
        
        _httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan // Streaming needs infinite timeout
        };
        
        // Add ngrok header to skip browser warning
        _httpClient.DefaultRequestHeaders.Add("ngrok-skip-browser-warning", "true");
        
        // vLLM uses standard Authorization header if API key is set
        if (!string.IsNullOrEmpty(ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization = 
                new AuthenticationHeaderValue("Bearer", ApiKey);
            DevLog.WriteLine($"RemoteInferenceClient: API key set (length: {ApiKey.Length})");
        }
        else
        {
            DevLog.WriteLine("RemoteInferenceClient: No API key (tunnel is private)");
        }
        
        DevLog.WriteLine($"RemoteInferenceClient: Initialized with remote server at: {BaseUrl}");
        DevLog.WriteLine($"RemoteInferenceClient: Model: {Model}");
        DevLog.WriteLine($"RemoteInferenceClient: Semantic search budget ms: {(int)_semanticSearchBudget.TotalMilliseconds}");
    }

    /// <summary>
    /// Check if the server is reachable by making a test request.
    /// </summary>
    public async Task<bool> IsServerReachableAsync(CancellationToken ct = default)
    {
        try
        {
            // Test with a simple generate request
            var testRequest = new
            {
                prompt = "hi",
                system_prompt = "You are a helpful assistant."
            };
            
            var json = JsonSerializer.Serialize(testRequest);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            
            var response = await _httpClient.PostAsync($"{BaseUrl}/generate", content, cts.Token);
            IsWarmedUp = response.IsSuccessStatusCode;
            
            if (response.IsSuccessStatusCode)
            {
                DevLog.WriteLine("RemoteInferenceClient: Server health check passed");
            }
            
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            DevLog.WriteLine($"RemoteInferenceClient: Health check failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Warmup the model (just checks connectivity).
    /// </summary>
    public async Task<bool> WarmupModelAsync(string? model = null, CancellationToken ct = default)
    {
        var reachable = await IsServerReachableAsync(ct);
        if (reachable)
        {
            DevLog.WriteLine("RemoteInferenceClient: Server is ready");
            StartKeepAlive();
        }
        return reachable;
    }

    /// <summary>
    /// Start periodic keep-alive pings to prevent tunnel from going idle.
    /// Pings every 2 minutes.
    /// </summary>
    private void StartKeepAlive()
    {
        if (_keepAliveTask is not null) return;

        _keepAliveCts = new CancellationTokenSource();
        _keepAliveTask = Task.Run(() => KeepAliveLoopAsync(_keepAliveCts.Token));

        DevLog.WriteLine("RemoteInferenceClient: Keep-alive started (2 min interval)");
    }

    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await SendKeepAliveAsync();
                }
                catch (Exception ex)
                {
                    DevLog.WriteLine($"RemoteInferenceClient: Keep-alive loop error: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            DevLog.WriteLine($"RemoteInferenceClient: Keep-alive loop fatal error: {ex.Message}");
        }
    }

    private async Task SendKeepAliveAsync()
    {
        if (Interlocked.Exchange(ref _keepAliveInFlight, 1) == 1)
            return;

        try
        {
            var testRequest = new { prompt = "ping", system_prompt = "Reply with 'pong'" };
            var json = JsonSerializer.Serialize(testRequest);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _httpClient.PostAsync($"{BaseUrl}/generate", content, cts.Token);
            DevLog.WriteLine("RemoteInferenceClient: Keep-alive ping sent");
        }
        catch (Exception ex)
        {
            DevLog.WriteLine($"RemoteInferenceClient: Keep-alive failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _keepAliveInFlight, 0);
        }
    }

    /// <summary>
    /// Stop keep-alive timer.
    /// </summary>
    private void StopKeepAlive()
    {
        var cts = _keepAliveCts;
        var task = _keepAliveTask;
        _keepAliveCts = null;
        _keepAliveTask = null;

        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        if (task is not null)
        {
            try { task.Wait(TimeSpan.FromSeconds(1)); } catch { }
        }
    }

    /// <summary>
    /// Chat with emotion - non-streaming version using custom FastAPI endpoint.
    /// </summary>
    public async Task<AiReply> ChatWithEmotionAsync(
        string userText,
        string? personaName = null,
        string? preferredEmotion = null,
        IReadOnlyList<(string role, string content)>? history = null,
        CancellationToken ct = default,
        string? systemInstructions = null,
        string? correlationId = null)
    {
        try
        {
            personaName = string.IsNullOrWhiteSpace(personaName) ? "Tsuki" : personaName.Trim();
            
            var intent = PromptBuilder.DetectIntent(userText);
            var runtimeTuning = PromptTuningProfiles.ForIntent(intent, _tuning);
            const bool includeFewShotExamples = false; // Keep prompt lightweight at runtime.
            var historyWindow = RemoteHistoryWindowDefault;
            var systemPrompt = BuildSystemPrompt(
                personaName,
                preferredEmotion,
                systemInstructions,
                _replyTonePreset,
                intent,
                includeFewShotExamples);

            // Hybrid memory context:
            // 1) Always include last 10 messages.
            // 2) Semantic search only when user likely references past context.
            // 3) Build context in parallel (only semantic search is async).
            var shouldSearchPast = ShouldSearchPastReference(userText);
            DevLog.WriteLine(
                "RemoteInferenceClient[Memory]: trigger={0}, service={1}",
                shouldSearchPast ? "on" : "off",
                _semanticMemory is null ? "missing" : "ready");

            // Direct execution - not CPU-bound, just LINQ
            var recentSw = System.Diagnostics.Stopwatch.StartNew();
            var recentHistory = (history ?? []).TakeLast(historyWindow).ToList();
            recentSw.Stop();
            DevLog.WriteLine("RemoteInferenceClient[Memory]: retrieve_recent_ms={0}", recentSw.ElapsedMilliseconds);

            // Semantic search is already async, no need for Task.Run wrapper
            var chromaSw = System.Diagnostics.Stopwatch.StartNew();
            var memoryHits = shouldSearchPast && _semanticMemory is not null
                ? await SearchSemanticMemoryWithBudgetAsync(userText, ct)
                : Array.Empty<SemanticMemoryHit>();
            chromaSw.Stop();
            DevLog.WriteLine("RemoteInferenceClient[Memory]: retrieve_chroma_ms={0}", chromaSw.ElapsedMilliseconds);
            
            DevLog.WriteLine(
                "RemoteInferenceClient[Memory]: recent={0}, semantic_hits={1}",
                recentHistory.Count,
                memoryHits.Count);

            var fullPrompt = BuildPromptWithHistory(userText, recentHistory, memoryHits, historyWindow);
            if (memoryHits.Count > 0)
            {
                DevLog.WriteLine("RemoteInferenceClient[Memory]: memory context added to prompt");
            }
            
            var request = new
            {
                prompt = fullPrompt,
                system_prompt = systemPrompt,
                model = Model,
                max_new_tokens = runtimeTuning.MaxTokens,
                max_tokens = runtimeTuning.MaxTokens,
                temperature = runtimeTuning.Temperature,
                top_p = runtimeTuning.TopP,
                top_k = runtimeTuning.TopK,
                repetition_penalty = runtimeTuning.RepeatPenalty,
                presence_penalty = runtimeTuning.PresencePenalty,
                frequency_penalty = runtimeTuning.FrequencyPenalty
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await HttpRetryPipeline.ExecuteAsync(
                async ct => await PostGenerateWithFallbackAsync(request, fullPrompt, systemPrompt, runtimeTuning, ct, correlationId),
                ct);
            
            DevLog.WriteLine($"RemoteInferenceClient: Response status: {response.StatusCode}");
            
            // Check for rate limit (429) - retry policy will have already attempted retries
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                DevLog.WriteLine("RemoteInferenceClient: Rate limit (429) detected");
                throw new InferenceRateLimitException("Rate limit exceeded (429)");
            }
            
            response.EnsureSuccessStatusCode();
            
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            sw.Stop();
            DevLog.WriteLine("RemoteInferenceClient: correlation_id={0}, operation=chat_with_emotion_complete, duration_ms={1}, status={2}",
                correlationId ?? "none",
                sw.ElapsedMilliseconds,
                (int)response.StatusCode);
            
            using var doc = JsonDocument.Parse(responseBody);
            var englishText = ExtractAssistantText(doc.RootElement);
            
            if (string.IsNullOrEmpty(englishText))
            {
                return new AiReply("Sorry, I couldn't process that.", "sad");
            }
            
            // Return English text only (DeepL will translate for TTS)
            var replyText = englishText.Trim();

            EnqueueMemoryWrite(userText, replyText);

            return ResponsePostProcessor.CleanAndValidate(new AiReply(replyText, "neutral"), intent, runtimeTuning);
        }
        catch (HttpRequestException ex)
        {
            DevLog.WriteLine($"RemoteInferenceClient: Network error: {ex.Message}");
            DevLog.WriteLine($"RemoteInferenceClient: BaseUrl: {BaseUrl}");
            DevLog.WriteLine($"RemoteInferenceClient: Full exception: {ex}");
            throw new InferenceNetworkException("Failed to connect to remote server", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            DevLog.WriteLine($"RemoteInferenceClient: Request timed out: {ex.Message}");
            throw new InferenceTimeoutException(TimeSpan.FromSeconds(30), "Remote inference request");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            DevLog.WriteLine("RemoteInferenceClient: Request cancelled by user");
            throw;
        }
        catch (Exception ex)
        {
            DevLog.WriteLine($"RemoteInferenceClient: Chat failed: {ex.Message}");
            throw new InferenceException($"Remote inference failed: {ex.Message}", ex);
        }
    }

    private async Task<IReadOnlyList<SemanticMemoryHit>> SearchSemanticMemoryWithBudgetAsync(string userText, CancellationToken ct)
    {
        if (_semanticMemory is null)
            return Array.Empty<SemanticMemoryHit>();

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(_semanticSearchBudget);

        try
        {
            return await _semanticMemory.SearchAsync(userText, 3, budgetCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            DevLog.WriteLine("RemoteInferenceClient[Memory]: semantic_search_budget_exceeded_ms={0}", (int)_semanticSearchBudget.TotalMilliseconds);
            return Array.Empty<SemanticMemoryHit>();
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("RemoteInferenceClient[Memory]: semantic_search_failed={0}", ex.GetBaseException().Message);
            return Array.Empty<SemanticMemoryHit>();
        }
    }

    private static int ReadIntEnv(string key, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        return int.TryParse(raw, out var parsed) ? parsed : fallback;
    }

    /// <summary>
    /// Chat with emotion - streaming version (fallback to non-streaming for custom API).
    /// </summary>
    public async Task<AiReply> ChatWithEmotionStreamingAsync(
        string userText,
        string? personaName = null,
        string? preferredEmotion = null,
        IReadOnlyList<(string role, string content)>? history = null,
        Action<string>? onPartialReply = null,
        CancellationToken ct = default,
        string? systemInstructions = null,
        string? correlationId = null)
    {
        try
        {
            personaName = string.IsNullOrWhiteSpace(personaName) ? "Tsuki" : personaName.Trim();

            var intent = PromptBuilder.DetectIntent(userText);
            var runtimeTuning = PromptTuningProfiles.ForIntent(intent, _tuning);
            const bool includeFewShotExamples = false; // Keep prompt lightweight at runtime.
            var historyWindow = RemoteHistoryWindowDefault;
            var systemPrompt = BuildSystemPrompt(
                personaName,
                preferredEmotion,
                systemInstructions,
                _replyTonePreset,
                intent,
                includeFewShotExamples);

            var shouldSearchPast = ShouldSearchPastReference(userText);
            
            // Direct execution - not CPU-bound
            var recentHistory = (history ?? []).TakeLast(historyWindow).ToList();

            // Already async, no Task.Run wrapper needed
            var memoryHits = shouldSearchPast && _semanticMemory is not null
                ? await _semanticMemory.SearchAsync(userText, 5, ct)
                : Array.Empty<SemanticMemoryHit>();
                
            var fullPrompt = BuildPromptWithHistory(userText, recentHistory, memoryHits, historyWindow);

            var request = new
            {
                prompt = fullPrompt,
                system_prompt = systemPrompt,
                model = Model,
                max_new_tokens = runtimeTuning.MaxTokens,
                max_tokens = runtimeTuning.MaxTokens,
                temperature = runtimeTuning.Temperature,
                top_p = runtimeTuning.TopP,
                top_k = runtimeTuning.TopK,
                repetition_penalty = runtimeTuning.RepeatPenalty,
                presence_penalty = runtimeTuning.PresencePenalty,
                frequency_penalty = runtimeTuning.FrequencyPenalty,
                stream = true
            };

            var streamResponse = await StartStreamingWithFallbackAsync(request, fullPrompt, systemPrompt, ct, correlationId);
            if (streamResponse is null)
            {
                DevLog.WriteLine("RemoteInferenceClient: /chat_stream unavailable, using non-streaming fallback");
                return await ChatWithEmotionAsync(userText, personaName, preferredEmotion, history, ct, systemInstructions, correlationId);
            }

            using (streamResponse)
            {
                // Check for rate limit (429) before throwing
                if (streamResponse.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    DevLog.WriteLine("RemoteInferenceClient: Rate limit (429) detected in streaming");
                    throw new InferenceRateLimitException("Rate limit exceeded (429)");
                }
                
                streamResponse.EnsureSuccessStatusCode();
                using var stream = await streamResponse.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream, Encoding.UTF8, bufferSize: 1024);

                var fullBuffer = new StringBuilder();
                var dataBuffer = new StringBuilder();
                string? currentEvent = null;

                while (!reader.EndOfStream && !ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    if (line is null)
                        break;

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        if (TryConsumeSseEvent(currentEvent, dataBuffer.ToString(), fullBuffer, onPartialReply))
                            break;

                        currentEvent = null;
                        dataBuffer.Clear();
                        continue;
                    }

                    if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                    {
                        currentEvent = line["event:".Length..].Trim();
                        continue;
                    }

                    if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        if (dataBuffer.Length > 0)
                            dataBuffer.Append('\n');
                        dataBuffer.Append(line["data:".Length..].TrimStart());
                        continue;
                    }
                }

                if (dataBuffer.Length > 0)
                {
                    _ = TryConsumeSseEvent(currentEvent, dataBuffer.ToString(), fullBuffer, onPartialReply);
                }

                var replyText = fullBuffer.ToString().Trim();
                if (string.IsNullOrWhiteSpace(replyText))
                {
                    DevLog.WriteLine("RemoteInferenceClient: Streaming produced no tokens, using non-streaming fallback");
                    return await ChatWithEmotionAsync(userText, personaName, preferredEmotion, history, ct, systemInstructions, correlationId);
                }

                EnqueueMemoryWrite(userText, replyText);

                return ResponsePostProcessor.CleanAndValidate(new AiReply(replyText, "neutral"), intent, runtimeTuning);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            DevLog.WriteLine($"RemoteInferenceClient: Streaming failed ({ex.Message}), using non-streaming fallback");
            return await ChatWithEmotionAsync(userText, personaName, preferredEmotion, history, ct, systemInstructions, correlationId);
        }
    }

    private async Task<HttpResponseMessage?> StartStreamingWithFallbackAsync(
        object streamRequest,
        string fullPrompt,
        string systemPrompt,
        CancellationToken ct,
        string? correlationId)
    {
        var json = JsonSerializer.Serialize(streamRequest);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/chat_stream")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(correlationId))
            request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.IsSuccessStatusCode)
            return response;

        if ((int)response.StatusCode is 400 or 404 or 405 or 422)
        {
            response.Dispose();
            var legacy = new
            {
                prompt = fullPrompt,
                system_prompt = systemPrompt,
                stream = true
            };

            using var legacyRequest = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/chat_stream")
            {
                Content = new StringContent(JsonSerializer.Serialize(legacy), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(correlationId))
                legacyRequest.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);

            var legacyResponse = await _httpClient.SendAsync(legacyRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            if (legacyResponse.IsSuccessStatusCode)
                return legacyResponse;

            if ((int)legacyResponse.StatusCode is 400 or 404 or 405 or 422)
            {
                legacyResponse.Dispose();
                return null;
            }

            return legacyResponse;
        }

        return response;
    }

    private static bool TryConsumeSseEvent(
        string? eventName,
        string data,
        StringBuilder fullBuffer,
        Action<string>? onPartialReply)
    {
        var name = (eventName ?? "token").Trim().ToLowerInvariant();
        if (name is "done" or "end" or "complete")
            return true;

        if (string.Equals(data.Trim(), "[DONE]", StringComparison.OrdinalIgnoreCase))
            return true;

        var token = ExtractTokenFromSseData(data);
        if (!string.IsNullOrEmpty(token))
        {
            fullBuffer.Append(token);
            onPartialReply?.Invoke(fullBuffer.ToString());
        }

        return false;
    }

    private static string ExtractTokenFromSseData(string data)
    {
        if (string.IsNullOrWhiteSpace(data))
            return string.Empty;

        var trimmed = data.Trim();
        if ((trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal)))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.TryGetProperty("token", out var tokenProp))
                    return tokenProp.GetString() ?? string.Empty;
                if (doc.RootElement.TryGetProperty("text", out var textProp))
                    return textProp.GetString() ?? string.Empty;
                if (doc.RootElement.TryGetProperty("delta", out var deltaProp))
                    return deltaProp.GetString() ?? string.Empty;
                if (doc.RootElement.TryGetProperty("response", out var respProp))
                    return respProp.GetString() ?? string.Empty;
            }
            catch (JsonException)
            {
            }
        }

        return data;
    }

    /// <summary>
    /// Summarize activity logs using custom API.
    /// </summary>
    public async Task<string> SummarizeActivityAsync(
        string activityText,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(activityText))
            return "";

        try
        {
            var request = new
            {
                prompt = activityText,
                system_prompt = PromptBuilder.BuildActivitySummaryOneSentencePrompt()
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync($"{BaseUrl}/generate", content, ct);
            response.EnsureSuccessStatusCode();
            
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseBody);
            var summary = doc.RootElement.GetProperty("response").GetString();
            
            return summary ?? "";
        }
        catch (Exception ex)
        {
            DevLog.WriteLine($"RemoteInferenceClient: Activity summary failed: {ex.Message}");
            return "";
        }
    }

    public void SetModel(string model)
    {
        Model = NormalizeModelForBaseUrl(BaseUrl, model);
    }

    public void SetBaseUrl(string baseUrl)
    {
        BaseUrl = baseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(baseUrl));
        Model = NormalizeModelForBaseUrl(BaseUrl, Model);
        DevLog.WriteLine($"RemoteInferenceClient: Base URL updated to: {BaseUrl}");
    }

    public void SetApiKey(string apiKey)
    {
        ApiKey = apiKey ?? "";
        _httpClient.DefaultRequestHeaders.Authorization = null;
        if (!string.IsNullOrEmpty(ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization = 
                new AuthenticationHeaderValue("Bearer", ApiKey);
        }
    }

    private async Task<HttpResponseMessage> PostGenerateWithFallbackAsync(
        object enrichedRequest,
        string fullPrompt,
        string systemPrompt,
        GenerationTuningSettings runtimeTuning,
        CancellationToken ct,
        string? correlationId)
    {
        // Skip /generate endpoint if we know it doesn't work (saves ~300ms per request)
        if (_skipGenerateEndpoint)
        {
            return await PostChatCompletionsAsync(fullPrompt, systemPrompt, runtimeTuning, ct, correlationId);
        }
        
        var json = JsonSerializer.Serialize(enrichedRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        DevLog.WriteLine($"RemoteInferenceClient: Sending request to {BaseUrl}/generate");
        DevLog.WriteLine($"RemoteInferenceClient: Request payload: {json.Substring(0, Math.Min(200, json.Length))}...");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/generate") { Content = content };
        if (!string.IsNullOrWhiteSpace(correlationId))
            request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
        var response = await _httpClient.SendAsync(request, ct);
        if ((int)response.StatusCode is 400 or 404 or 422)
        {
            DevLog.WriteLine("RemoteInferenceClient: /generate rejected, trying OpenAI-compatible chat/completions");
            response.Dispose();
            
            // Remember to skip /generate next time
            _skipGenerateEndpoint = true;
            
            return await PostChatCompletionsAsync(fullPrompt, systemPrompt, runtimeTuning, ct, correlationId);
        }

        return response;
    }
    
    private async Task<HttpResponseMessage> PostChatCompletionsAsync(
        string fullPrompt,
        string systemPrompt,
        GenerationTuningSettings runtimeTuning,
        CancellationToken ct,
        string? correlationId)
    {
        var openAiRequest = new
        {
            model = Model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = fullPrompt }
            },
            max_tokens = runtimeTuning.MaxTokens,
            temperature = runtimeTuning.Temperature,
            top_p = runtimeTuning.TopP,
            presence_penalty = runtimeTuning.PresencePenalty,
            frequency_penalty = runtimeTuning.FrequencyPenalty,
            stream = false
        };
        var openAiJson = JsonSerializer.Serialize(openAiRequest);
        var openAiContent = new StringContent(openAiJson, Encoding.UTF8, "application/json");

        var chatCompletionsUrl = $"{BaseUrl}/chat/completions";
        using var chatRequest = new HttpRequestMessage(HttpMethod.Post, chatCompletionsUrl) { Content = openAiContent };
        if (!string.IsNullOrWhiteSpace(correlationId))
            chatRequest.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
        var response = await _httpClient.SendAsync(chatRequest, ct);
        if (!response.IsSuccessStatusCode && (int)response.StatusCode is 400 or 404 or 405)
        {
            response.Dispose();
            // Some providers expect /v1/chat/completions even when base URL does not end with /v1.
            if (!BaseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                var v1Url = $"{BaseUrl}/v1/chat/completions";
                DevLog.WriteLine($"RemoteInferenceClient: Trying {v1Url}");
                using var v1Req = new HttpRequestMessage(HttpMethod.Post, v1Url)
                {
                    Content = new StringContent(openAiJson, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrWhiteSpace(correlationId))
                    v1Req.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
                response = await _httpClient.SendAsync(v1Req, ct);
            }
        }

        if (!response.IsSuccessStatusCode && (int)response.StatusCode is 400 or 404 or 422)
        {
            DevLog.WriteLine("RemoteInferenceClient: Falling back to legacy payload format");
            response.Dispose();

            var legacyRequest = new
            {
                prompt = fullPrompt,
                system_prompt = systemPrompt
            };

            var legacyJson = JsonSerializer.Serialize(legacyRequest);
            var legacyContent = new StringContent(legacyJson, Encoding.UTF8, "application/json");
            using var legacyReq = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/generate") { Content = legacyContent };
            if (!string.IsNullOrWhiteSpace(correlationId))
                legacyReq.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
            response = await _httpClient.SendAsync(legacyReq, ct);
        }

        return response;
    }

    private static string? ExtractAssistantText(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("response", out var responseProp) && responseProp.ValueKind == JsonValueKind.String)
                return responseProp.GetString();

            if (root.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.Object &&
                    message.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String)
                {
                    return content.GetString();
                }

                if (first.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    return text.GetString();
            }

            if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
                return outputText.GetString();
        }

        return null;
    }

    private static string BuildSystemPrompt(
        string personaName,
        string? preferredEmotion,
        string? customInstructions,
        string tonePreset,
        PromptIntent intent,
        bool includeFewShotExamples = true)
    {
        return PromptBuilder.BuildCompanionChatSystemPrompt(
            personaName,
            preferredEmotion,
            customInstructions,
            intent: intent,
            requireJson: false,
            includeActivitySafetyRules: false,
            oneToTwoSentences: true,
            includeFewShotExamples: includeFewShotExamples,
            tonePreset: tonePreset);
    }

    private string BuildPromptWithHistory(
        string userText,
        IReadOnlyList<(string role, string content)>? history,
        IReadOnlyList<SemanticMemoryHit>? memories = null,
        int maxHistoryMessages = RemoteHistoryWindowDefault)
    {
        // Pre-allocate StringBuilder for better performance
        var prompt = new StringBuilder(capacity: 2048);

        // Always include recent message window.
        if (history != null && history.Count > 0)
        {
            foreach (var (role, content) in history.TakeLast(Math.Max(1, maxHistoryMessages)))
            {
                prompt.AppendLine($"{role}: {content}");
            }
        }

        // Add semantic memories only when available.
        if (memories != null && memories.Count > 0)
        {
            prompt.AppendLine();
            prompt.AppendLine("Relevant past context:");
            foreach (var m in memories.Take(5))
            {
                prompt.AppendLine($"- {m.Text}");
            }
            prompt.AppendLine();
        }

        // Current user message
        prompt.Append($"user: {userText}");
        
        return prompt.ToString();
    }

    private static bool ShouldSearchPastReference(string? userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return false;

        var text = userText.Trim().ToLowerInvariant();
        
        // Increased threshold - only search for explicit references (saves 100-300ms per request)
        if (text.Length < 15)
            return false;

        // More restrictive pattern - only explicit past references
        return Regex.IsMatch(
            text,
            @"\b(remember|earlier|before|previous|last time|you said|as i said)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string NormalizeModelForBaseUrl(string? baseUrl, string? modelName)
    {
        var model = (modelName ?? string.Empty).Trim();
        if (!string.Equals(model, "tsuki-lora", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(model))
        {
            return model;
        }

        var url = (baseUrl ?? string.Empty).Trim().ToLowerInvariant();
        if (url.Contains("api.moonshot.ai") || url.Contains("api.moonshot.cn")) return "moonshot-v1-8k";
        if (url.Contains("cerebras.ai")) return "llama3.1-8b";
        if (url.Contains("api.groq.com")) return "llama-3.1-8b-instant";
        if (url.Contains("generativelanguage.googleapis.com")) return "gemini-1.5-flash";
        if (url.Contains("api.openai.com")) return "gpt-4o-mini";
        if (url.Contains("openrouter.ai")) return "openai/gpt-4o-mini";
        if (url.Contains("api.cohere.com")) return "command-r-plus";
        if (url.Contains("api.mistral.ai")) return "mistral-small-latest";
        if (url.Contains("api.deepseek.com")) return "deepseek-chat";
        if (url.Contains("anthropic.com")) return "claude-3-5-haiku-latest";
        if (url.Contains("models.github.ai")) return "gpt-4o-mini";

        return string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model;
    }

    // REMOVED: SegmentConcatenatedWords - was breaking text by inserting spaces inside words
    // REMOVED: ParseAiReply - no longer needed with structured JSON output

    private void EnqueueMemoryWrite(string userText, string replyText)
    {
        if (_semanticMemory is null || _memoryWriteQueue is null)
            return;

        if (string.IsNullOrWhiteSpace(userText) || string.IsNullOrWhiteSpace(replyText))
            return;

        var payload = $"User: {userText}\nAssistant: {replyText}";
        if (!_memoryWriteQueue.Writer.TryWrite(payload))
        {
            DevLog.WriteLine("RemoteInferenceClient[Memory]: write queue full, dropping oldest");
        }
    }

    private async Task MemoryWriteWorkerAsync()
    {
        if (_semanticMemory is null || _memoryWriteQueue is null || _memoryWriteCts is null)
            return;

        try
        {
            while (await _memoryWriteQueue.Reader.WaitToReadAsync(_memoryWriteCts.Token))
            {
                while (_memoryWriteQueue.Reader.TryRead(out var payload))
                {
                    try
                    {
                        await _semanticMemory.AddMemoryAsync(payload, "voicechat", _memoryWriteCts.Token);
                    }
                    catch (OperationCanceledException) when (_memoryWriteCts.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        DevLog.WriteLine($"RemoteInferenceClient[Memory]: write-back failed: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_memoryWriteCts.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopKeepAlive();
        if (_memoryWriteCts is not null)
        {
            try { _memoryWriteCts.Cancel(); } catch { }
        }
        if (_memoryWriteWorkerTask is not null)
        {
            try { _memoryWriteWorkerTask.Wait(TimeSpan.FromSeconds(1)); } catch { }
        }
        _memoryWriteCts?.Dispose();
        _httpClient?.Dispose();
        _disposed = true;
    }
}
