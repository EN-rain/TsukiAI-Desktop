using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TsukiAI.Core.Models;

namespace TsukiAI.Core.Services;

/// <summary>
/// Remote inference client for vLLM OpenAI-compatible API.
/// Supports both streaming and non-streaming inference with dual output (UI + TTS).
/// </summary>
public sealed class RemoteInferenceClient : IInferenceClient
{
    private readonly HttpClient _httpClient;
    private bool _disposed = false;
    private System.Threading.Timer? _keepAliveTimer;
    private readonly ISemanticMemoryService? _semanticMemory;
    private readonly IConversationSummaryStore? _summaryStore;
    private readonly GenerationTuningSettings _tuning;
    private static readonly PromptBuilder PromptBuilder = new();
    private const int RemoteHistoryWindowDefault = 6;
    private const int RemoteHistoryWindowDetailed = 8;
    
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
        IConversationSummaryStore? summaryStore = null,
        GenerationTuningSettings? tuning = null)
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
        _summaryStore = summaryStore;
        _tuning = (tuning ?? GenerationTuningSettings.Default).Clamp();
        
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
        if (_keepAliveTimer != null) return;
        
        _keepAliveTimer = new System.Threading.Timer(async _ =>
        {
            try
            {
                var testRequest = new { prompt = "ping", system_prompt = "Reply with 'pong'" };
                var json = JsonSerializer.Serialize(testRequest);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _httpClient.PostAsync($"{BaseUrl}/generate", content, cts.Token);
                DevLog.WriteLine("RemoteInferenceClient: Keep-alive ping sent");
            }
            catch (Exception ex)
            {
                DevLog.WriteLine($"RemoteInferenceClient: Keep-alive failed: {ex.Message}");
            }
        }, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
        
        DevLog.WriteLine("RemoteInferenceClient: Keep-alive started (2 min interval)");
    }

    /// <summary>
    /// Stop keep-alive timer.
    /// </summary>
    private void StopKeepAlive()
    {
        _keepAliveTimer?.Dispose();
        _keepAliveTimer = null;
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
        string? systemInstructions = null)
    {
        try
        {
            personaName = string.IsNullOrWhiteSpace(personaName) ? "Tsuki" : personaName.Trim();
            
            var intent = PromptBuilder.DetectIntent(userText);
            var runtimeTuning = PromptTuningProfiles.ForIntent(intent, _tuning);
            var includeFewShotExamples = intent is PromptIntent.Question or PromptIntent.EmotionalSupport;
            var historyWindow = includeFewShotExamples ? RemoteHistoryWindowDetailed : RemoteHistoryWindowDefault;
            var systemPrompt = BuildSystemPrompt(
                personaName,
                preferredEmotion,
                systemInstructions,
                intent,
                includeFewShotExamples);

            // Hybrid memory context:
            // 1) Always include last 10 messages.
            // 2) Semantic search only when user likely references past context.
            // 3) Build context in parallel.
            var shouldSearchPast = ShouldSearchPastReference(userText);
            DevLog.WriteLine(
                "RemoteInferenceClient[Memory]: trigger={0}, service={1}",
                shouldSearchPast ? "on" : "off",
                _semanticMemory is null ? "missing" : "ready");

            var recentSw = System.Diagnostics.Stopwatch.StartNew();
            var recentHistoryTask = Task.Run(() =>
            {
                var value = (history ?? []).TakeLast(historyWindow).ToList();
                recentSw.Stop();
                DevLog.WriteLine("RemoteInferenceClient[Memory]: retrieve_recent_ms={0}", recentSw.ElapsedMilliseconds);
                return value;
            }, ct);

            var chromaSw = System.Diagnostics.Stopwatch.StartNew();
            var semanticSearchTask = shouldSearchPast && _semanticMemory is not null
                ? Task.Run(async () =>
                {
                    var hits = await _semanticMemory.SearchAsync(userText, 5, ct);
                    chromaSw.Stop();
                    DevLog.WriteLine("RemoteInferenceClient[Memory]: retrieve_chroma_ms={0}", chromaSw.ElapsedMilliseconds);
                    return hits;
                }, ct)
                : Task.Run<IReadOnlyList<SemanticMemoryHit>>(() =>
                {
                    chromaSw.Stop();
                    DevLog.WriteLine("RemoteInferenceClient[Memory]: retrieve_chroma_ms={0}", chromaSw.ElapsedMilliseconds);
                    return [];
                }, ct);

            var sqliteSw = System.Diagnostics.Stopwatch.StartNew();
            var summarySearchTask = shouldSearchPast && _summaryStore is not null
                ? Task.Run(async () =>
                {
                    var hits = await _summaryStore.SearchSummariesAsync(userText, 3, ct);
                    sqliteSw.Stop();
                    DevLog.WriteLine("RemoteInferenceClient[Memory]: retrieve_sqlite_ms={0}", sqliteSw.ElapsedMilliseconds);
                    return hits;
                }, ct)
                : Task.Run<IReadOnlyList<ConversationSummaryMemory>>(() =>
                {
                    sqliteSw.Stop();
                    DevLog.WriteLine("RemoteInferenceClient[Memory]: retrieve_sqlite_ms={0}", sqliteSw.ElapsedMilliseconds);
                    return [];
                }, ct);

            await Task.WhenAll(recentHistoryTask, semanticSearchTask, summarySearchTask);
            var recentHistory = await recentHistoryTask;
            var memoryHits = await semanticSearchTask;
            var summaryHits = await summarySearchTask;
            DevLog.WriteLine(
                "RemoteInferenceClient[Memory]: recent={0}, semantic_hits={1}, summary_hits={2}",
                recentHistory.Count,
                memoryHits.Count,
                summaryHits.Count);

            var fullPrompt = BuildPromptWithHistory(userText, recentHistory, memoryHits, summaryHits, historyWindow);
            if (memoryHits.Count > 0 || summaryHits.Count > 0)
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
            var response = await PostGenerateWithFallbackAsync(request, fullPrompt, systemPrompt, runtimeTuning, ct);
            
            DevLog.WriteLine($"RemoteInferenceClient: Response status: {response.StatusCode}");
            response.EnsureSuccessStatusCode();
            
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            sw.Stop();
            DevLog.WriteLine($"RemoteInferenceClient: Response received in {sw.ElapsedMilliseconds}ms");
            
            using var doc = JsonDocument.Parse(responseBody);
            var englishText = ExtractAssistantText(doc.RootElement);
            
            if (string.IsNullOrEmpty(englishText))
            {
                return new AiReply("Sorry, I couldn't process that.", "sad");
            }
            
            // Return English text only (DeepL will translate for TTS)
            var replyText = englishText.Trim();

            if (_semanticMemory is not null && !string.IsNullOrWhiteSpace(userText) && !string.IsNullOrWhiteSpace(replyText))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _semanticMemory.AddMemoryAsync($"User: {userText}\nAssistant: {replyText}", "voicechat");
                        DevLog.WriteLine("RemoteInferenceClient[Memory]: write-back saved");
                    }
                    catch (Exception memoryEx)
                    {
                        DevLog.WriteLine($"RemoteInferenceClient[Memory]: write-back failed: {memoryEx.Message}");
                    }
                });
            }

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
        string? systemInstructions = null)
    {
        try
        {
            personaName = string.IsNullOrWhiteSpace(personaName) ? "Tsuki" : personaName.Trim();

            var intent = PromptBuilder.DetectIntent(userText);
            var runtimeTuning = PromptTuningProfiles.ForIntent(intent, _tuning);
            var includeFewShotExamples = intent is PromptIntent.Question or PromptIntent.EmotionalSupport;
            var historyWindow = includeFewShotExamples ? RemoteHistoryWindowDetailed : RemoteHistoryWindowDefault;
            var systemPrompt = BuildSystemPrompt(
                personaName,
                preferredEmotion,
                systemInstructions,
                intent,
                includeFewShotExamples);

            var shouldSearchPast = ShouldSearchPastReference(userText);
            var recentHistoryTask = Task.Run(() => (history ?? []).TakeLast(historyWindow).ToList(), ct);

            var semanticSearchTask = shouldSearchPast && _semanticMemory is not null
                ? Task.Run(() => _semanticMemory.SearchAsync(userText, 5, ct), ct)
                : Task.FromResult<IReadOnlyList<SemanticMemoryHit>>([]);

            var summarySearchTask = shouldSearchPast && _summaryStore is not null
                ? Task.Run(() => _summaryStore.SearchSummariesAsync(userText, 3, ct), ct)
                : Task.FromResult<IReadOnlyList<ConversationSummaryMemory>>([]);

            await Task.WhenAll(recentHistoryTask, semanticSearchTask, summarySearchTask);
            var recentHistory = await recentHistoryTask;
            var memoryHits = await semanticSearchTask;
            var summaryHits = await summarySearchTask;
            var fullPrompt = BuildPromptWithHistory(userText, recentHistory, memoryHits, summaryHits, historyWindow);

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

            var streamResponse = await StartStreamingWithFallbackAsync(request, fullPrompt, systemPrompt, ct);
            if (streamResponse is null)
            {
                DevLog.WriteLine("RemoteInferenceClient: /chat_stream unavailable, using non-streaming fallback");
                return await ChatWithEmotionAsync(userText, personaName, preferredEmotion, history, ct, systemInstructions);
            }

            using (streamResponse)
            {
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
                    return await ChatWithEmotionAsync(userText, personaName, preferredEmotion, history, ct, systemInstructions);
                }

                if (_semanticMemory is not null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _semanticMemory.AddMemoryAsync($"User: {userText}\nAssistant: {replyText}", "voicechat");
                        }
                        catch
                        {
                        }
                    });
                }

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
            return await ChatWithEmotionAsync(userText, personaName, preferredEmotion, history, ct, systemInstructions);
        }
    }

    private async Task<HttpResponseMessage?> StartStreamingWithFallbackAsync(
        object streamRequest,
        string fullPrompt,
        string systemPrompt,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(streamRequest);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/chat_stream")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

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
    /// Summarize conversation history using custom API.
    /// </summary>
    public async Task<string> SummarizeConversationAsync(
        IReadOnlyList<(string role, string content)> history,
        CancellationToken ct = default)
    {
        if (history == null || history.Count == 0)
            return "";

        try
        {
            ct.ThrowIfCancellationRequested();
            DevLog.WriteLine("RemoteInferenceClient[Memory]: conversation summary using local memory/history only");
            return BuildLocalConversationSummary(history);
        }
        catch (Exception ex)
        {
            DevLog.WriteLine($"RemoteInferenceClient: Summarization failed: {ex.Message}");
            return "";
        }
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
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(enrichedRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        DevLog.WriteLine($"RemoteInferenceClient: Sending request to {BaseUrl}/generate");
        DevLog.WriteLine($"RemoteInferenceClient: Request payload: {json.Substring(0, Math.Min(200, json.Length))}...");

        var response = await _httpClient.PostAsync($"{BaseUrl}/generate", content, ct);
        if ((int)response.StatusCode is 400 or 404 or 422)
        {
            DevLog.WriteLine("RemoteInferenceClient: /generate rejected, trying OpenAI-compatible chat/completions");
            response.Dispose();

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
            response = await _httpClient.PostAsync(chatCompletionsUrl, openAiContent, ct);
            if (!response.IsSuccessStatusCode && (int)response.StatusCode is 400 or 404 or 405)
            {
                response.Dispose();
                // Some providers expect /v1/chat/completions even when base URL does not end with /v1.
                if (!BaseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                {
                    var v1Url = $"{BaseUrl}/v1/chat/completions";
                    DevLog.WriteLine($"RemoteInferenceClient: Trying {v1Url}");
                    response = await _httpClient.PostAsync(v1Url, new StringContent(openAiJson, Encoding.UTF8, "application/json"), ct);
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
                response = await _httpClient.PostAsync($"{BaseUrl}/generate", legacyContent, ct);
            }
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
            includeFewShotExamples: includeFewShotExamples);
    }

    private string BuildPromptWithHistory(
        string userText,
        IReadOnlyList<(string role, string content)>? history,
        IReadOnlyList<SemanticMemoryHit>? memories = null,
        IReadOnlyList<ConversationSummaryMemory>? summaries = null,
        int maxHistoryMessages = RemoteHistoryWindowDefault)
    {
        var prompt = new StringBuilder();

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

        if (summaries != null && summaries.Count > 0)
        {
            prompt.AppendLine("Past conversation summaries:");
            foreach (var s in summaries.Take(3))
            {
                prompt.AppendLine($"- {s.CreatedAtUtc:yyyy-MM-dd}: {s.Summary}");
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
        if (text.Length < 8)
            return false;

        return Regex.IsMatch(
            text,
            @"\b(remember|earlier|before|previous|last time|we talked|you said|as i said|again|continue|same as|mentioned)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string BuildLocalConversationSummary(IReadOnlyList<(string role, string content)> history)
    {
        var userMessages = history.Where(h => string.Equals(h.role, "user", StringComparison.OrdinalIgnoreCase))
            .Select(h => h.content.Trim())
            .Where(x => x.Length > 0)
            .TakeLast(3)
            .ToList();

        var assistantMessages = history.Where(h => string.Equals(h.role, "assistant", StringComparison.OrdinalIgnoreCase))
            .Select(h => h.content.Trim())
            .Where(x => x.Length > 0)
            .TakeLast(2)
            .ToList();

        var parts = new List<string>();
        if (userMessages.Count > 0)
            parts.Add("Recent user topics: " + string.Join(" | ", userMessages.Select(TrimForSummary)));
        if (assistantMessages.Count > 0)
            parts.Add("Recent assistant replies: " + string.Join(" | ", assistantMessages.Select(TrimForSummary)));

        return parts.Count == 0
            ? "No conversation content to summarize."
            : string.Join("\n", parts);
    }

    private static string TrimForSummary(string text)
    {
        const int max = 90;
        return text.Length <= max ? text : text[..max] + "...";
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

    public void Dispose()
    {
        if (_disposed) return;
        StopKeepAlive();
        _httpClient?.Dispose();
        _disposed = true;
    }
}
