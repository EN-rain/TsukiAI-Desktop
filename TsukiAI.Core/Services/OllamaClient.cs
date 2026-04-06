using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using System.IO;
using TsukiAI.Core.Models;

namespace TsukiAI.Core.Services;

public sealed class OllamaClient : IInferenceClient
{
    private readonly HttpClient _http;
    private readonly ResponseCache _cache;
    private readonly GenerationTuningSettings _tuning;
    private static readonly PromptBuilder PromptBuilder = new();
    public string Model { get; private set; }
    private DateTimeOffset _lastTagsAt = DateTimeOffset.MinValue;
    private HashSet<string> _lastTags = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastPsAt = DateTimeOffset.MinValue;
    private HashSet<string> _lastPs = new(StringComparer.OrdinalIgnoreCase);
    
    // Performance settings - optimized for speed
    private const int MAX_CONTEXT_MESSAGES = 4; // Only last 4 messages (was 6)
    private const int CONTEXT_WINDOW = 1024; // Smaller context (was 2048)

    /// <summary>
    /// True if the model has been warmed up and is ready for fast responses.
    /// </summary>
    public bool IsWarmedUp { get; private set; } = false;

    /// <summary>
    /// Gets whether the model is loaded and ready for inference.
    /// For Ollama, this is equivalent to IsWarmedUp.
    /// </summary>
    public bool IsLoaded => IsWarmedUp;

    public OllamaClient(string model = "qwen2.5:3b", string baseUrl = "http://localhost:11434", GenerationTuningSettings? tuning = null)
    {
        Model = model;
        _tuning = (tuning ?? GenerationTuningSettings.Default).Clamp();
        
        // Use SocketsHttpHandler for connection pooling and better performance
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 10,
            EnableMultipleHttp2Connections = true,
        };
        
        _http = new HttpClient(handler)
        { 
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(300) // 5 minutes for first load
        };
        
        _cache = new ResponseCache(maxEntries: 100, ttl: TimeSpan.FromMinutes(10));
        
        // Pre-warm common responses
        _cache.PreWarmCommonResponses("Tsuki");
    }

    /// <summary>
    /// Checks if Ollama server is reachable.
    /// </summary>
    public async Task<bool> IsServerReachableAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var response = await _http.GetAsync("/api/tags", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void SetModel(string model)
    {
        model = (model ?? "").Trim();
        if (model.Length == 0) return;
        Model = model;
        _cache.Clear(); // Clear cache when model changes
    }

    /// <summary>
    /// Warms up the model with a minimal request to reduce first-token latency.
    /// </summary>
    public async Task<bool> WarmupModelAsync(string? model = null, CancellationToken ct = default)
    {
        model = string.IsNullOrWhiteSpace(model) ? Model : model.Trim();
        try
        {
            var req = new
            {
                model,
                messages = new[]
                {
                    new { role = "system", content = "Warmup." },
                    new { role = "user", content = "ping" }
                },
                stream = false,
                options = new
                {
                    num_predict = 5,
                    temperature = 0.1
                },
                keep_alive = 86400
            };

            using var resp = await _http.PostAsJsonAsync("/api/chat", req, ct);
            var ok = resp.IsSuccessStatusCode;
            if (ok) 
            {
                DevLog.WriteLine("Ollama warmup OK: {0}", model ?? "(null)");
                IsWarmedUp = true;
            }
            else DevLog.WriteLine("Ollama warmup failed: {0} {1}", (int)resp.StatusCode, resp.ReasonPhrase ?? "");
            return ok;
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("Ollama warmup error: {0}", ex.Message ?? "(null)");
            return false;
        }
    }

    /// <summary>
    /// STREAMING chat with emotion. Returns final reply but streams partial responses.
    /// </summary>
    public async Task<AiReply> ChatWithEmotionStreamingAsync(
        string userText,
        string? personaName = null,
        string? preferredEmotion = null,
        IReadOnlyList<(string role, string content)>? history = null,
        Action<string>? onPartialReply = null,
        CancellationToken ct = default,
        string? systemInstructions = null
    )
    {
        // Check cache first (only for non-streaming fallback)
        var contextHash = ComputeContextHash(history);
        var cached = _cache.Get(contextHash, userText);
        if (cached != null && onPartialReply == null)
        {
            DevLog.WriteLine("OllamaClient: Cache hit for input");
            return new AiReply(cached.Reply, cached.Emotion);
        }

        personaName = string.IsNullOrWhiteSpace(personaName) ? "Tsuki" : personaName.Trim();
        preferredEmotion = (preferredEmotion ?? "").Trim();
        var intent = PromptBuilder.DetectIntent(userText);
        var runtimeTuning = PromptTuningProfiles.ForIntent(intent, _tuning);

        var system = PromptBuilder.BuildCompanionChatSystemPrompt(
            personaName,
            preferredEmotion,
            systemInstructions,
            intent: intent,
            requireJson: true,
            includeActivitySafetyRules: true,
            oneToTwoSentences: false);

        // Trim history to last N messages for speed
        var trimmedHistory = TrimHistory(history);

        // Stream the response
        var (reply, emotion) = await StreamChatAsync(
            system, 
            userText ?? "", 
            trimmedHistory, 
            onPartialReply ?? (_ => { }), 
            ct,
            runtimeTuning
        );

        // Cache the final result
        if (!string.IsNullOrWhiteSpace(reply))
        {
            _cache.Set(contextHash, userText ?? "", reply, emotion);
        }

        return ResponsePostProcessor.CleanAndValidate(new AiReply(reply, emotion), intent, _tuning);
    }

    /// <summary>
    /// Non-streaming fallback for simple queries.
    /// </summary>
    public async Task<AiReply> ChatWithEmotionAsync(
        string userText,
        string? personaName = null,
        string? preferredEmotion = null,
        IReadOnlyList<(string role, string content)>? history = null,
        CancellationToken ct = default,
        string? systemInstructions = null
    )
    {
        // Check cache first
        var contextHash = ComputeContextHash(history);
        var cached = _cache.Get(contextHash, userText);
        if (cached != null)
        {
            DevLog.WriteLine("OllamaClient: Cache hit");
            return new AiReply(cached.Reply, cached.Emotion);
        }

        personaName = string.IsNullOrWhiteSpace(personaName) ? "Tsuki" : personaName.Trim();
        preferredEmotion = (preferredEmotion ?? "").Trim();
        var intent = PromptBuilder.DetectIntent(userText);
        var runtimeTuning = PromptTuningProfiles.ForIntent(intent, _tuning);

        var system = PromptBuilder.BuildCompanionChatSystemPrompt(
            personaName,
            preferredEmotion,
            systemInstructions,
            intent: intent,
            requireJson: true,
            includeActivitySafetyRules: false,
            oneToTwoSentences: true);

        // Trim history for speed
        var trimmedHistory = TrimHistory(history);

        var content = await PostChatAsync(system, userText ?? "", trimmedHistory, ct, runtimeTuning);
        if (string.IsNullOrWhiteSpace(content))
        {
            await Task.Delay(150, ct);
            content = await PostChatAsync(system, userText ?? "", trimmedHistory, ct, runtimeTuning);
        }

        if (TryParseAiReply(content, out var parsed))
        {
            var cleaned = ResponsePostProcessor.CleanAndValidate(parsed, intent, _tuning);
            _cache.Set(contextHash, userText ?? "", cleaned.Reply, cleaned.Emotion);
            return cleaned;
        }

        // Fallback: treat as plain text
        if (LooksLikeJson(content))
        {
            return new AiReply("Hmm, give me a sec-try again?", "neutral");
        }

        var fallbackReply = string.IsNullOrWhiteSpace(content) ? "Hmm, I didn't catch that-try again?" : content;
        var cleanedFallback = ResponsePostProcessor.CleanAndValidate(new AiReply(fallbackReply, "neutral"), intent, _tuning);
        _cache.Set(contextHash, userText ?? "", cleanedFallback.Reply, cleanedFallback.Emotion);
        return cleanedFallback;
    }

    /// <summary>
    /// Trims history to last N messages to reduce token processing.
    /// </summary>
    private static List<(string role, string content)>? TrimHistory(IReadOnlyList<(string role, string content)>? history)
    {
        if (history == null || history.Count <= MAX_CONTEXT_MESSAGES)
            return history?.ToList();

        // Keep the most recent N messages
        return history.Skip(history.Count - MAX_CONTEXT_MESSAGES).ToList();
    }

    private static string ComputeContextHash(IReadOnlyList<(string role, string content)>? history)
    {
        if (history == null || history.Count == 0)
            return "";

        // Simple hash of recent message contents
        var recent = string.Join("|", history.TakeLast(3).Select(h => h.content[..Math.Min(20, h.content.Length)]));
        var bytes = System.Text.Encoding.UTF8.GetBytes(recent);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
    }

    #region Streaming Implementation

    /// <summary>
    /// Streams chat response token by token for immediate UI feedback.
    /// </summary>
    public async Task<(string Reply, string Emotion)> StreamChatAsync(
        string systemPrompt,
        string userText,
        List<(string role, string content)>? history,
        Action<string> onPartialReply,
        CancellationToken ct,
        GenerationTuningSettings? runtimeTuning = null
    )
    {
        var tuning = (runtimeTuning ?? _tuning).Clamp();
        var totalSw = Stopwatch.StartNew();
        DevLog.WriteLine("StreamChat: Starting request...");
        
        var messages = new List<OllamaMessage>
        {
            new() { role = "system", content = systemPrompt ?? "" },
        };

        if (history is not null)
        {
            foreach (var h in history)
            {
                if (string.IsNullOrWhiteSpace(h.content)) continue;
                messages.Add(new OllamaMessage { role = h.role, content = h.content });
            }
        }

        messages.Add(new OllamaMessage { role = "user", content = userText ?? "" });

        var req = new OllamaChatRequest
        {
            model = Model,
            stream = true,
            messages = messages,
            options = new OllamaOptions
            {
                num_predict = tuning.MaxTokens,
                num_ctx = CONTEXT_WINDOW,
                temperature = tuning.Temperature,
                top_p = tuning.TopP,
                top_k = tuning.TopK,
                repeat_penalty = tuning.RepeatPenalty,
                presence_penalty = tuning.PresencePenalty,
                frequency_penalty = tuning.FrequencyPenalty
            }
        };

        var reqSw = Stopwatch.StartNew();
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(req)
        };

        // Send request with response headers read to start getting data ASAP
        DevLog.WriteLine("StreamChat: Sending HTTP request...");
        using var resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
        reqSw.Stop();
        DevLog.WriteLine("StreamChat: HTTP headers received in {0}ms", reqSw.ElapsedMilliseconds);
        
        resp.EnsureSuccessStatusCode();

        var streamSw = Stopwatch.StartNew();
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8, bufferSize: 1024);
        streamSw.Stop();
        DevLog.WriteLine("StreamChat: Stream opened in {0}ms", streamSw.ElapsedMilliseconds);

        var buffer = new StringBuilder();
        var partialEmitted = false;
        var lastEmitTime = DateTimeOffset.Now;
        var firstTokenReceived = false;
        var tokenSw = Stopwatch.StartNew();

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                
                // Check for done signal
                if (doc.RootElement.TryGetProperty("done", out var doneProp) && doneProp.GetBoolean())
                    break;

                if (doc.RootElement.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var contentProp))
                {
                    var chunk = contentProp.GetString() ?? "";
                    buffer.Append(chunk);
                    
                    // Log first token timing
                    if (!firstTokenReceived && !string.IsNullOrWhiteSpace(chunk))
                    {
                        firstTokenReceived = true;
                        DevLog.WriteLine("StreamChat: First token received after {0}ms", tokenSw.ElapsedMilliseconds);
                    }
                    
                    // Emit partial reply every 100ms or on significant chunks
                    var now = DateTimeOffset.Now;
                    if ((now - lastEmitTime).TotalMilliseconds > 100 || chunk.Contains(' ') || chunk.Contains('\n'))
                    {
                        var partial = TryExtractPartialReply(buffer.ToString());
                        if (!string.IsNullOrWhiteSpace(partial))
                        {
                            onPartialReply(partial);
                            partialEmitted = true;
                            lastEmitTime = now;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Ignore malformed chunks and continue
            }
        }

        totalSw.Stop();
        DevLog.WriteLine("StreamChat: Total time {0}ms (first token: {1}ms)", 
            totalSw.ElapsedMilliseconds, 
            firstTokenReceived ? tokenSw.ElapsedMilliseconds.ToString() : "N/A");

        var full = buffer.ToString().Trim();
        
        // Parse final JSON response
        if (TryParseAiReply(full, out var parsed))
        {
            if (!partialEmitted)
                onPartialReply(parsed.Reply);
            return (parsed.Reply, parsed.Emotion);
        }

        // Fallback: treat as plain text
        if (!partialEmitted)
            onPartialReply(full);
        return (full, "neutral");
    }

    /// <summary>
    /// Attempts to extract a partial reply from incomplete JSON for streaming.
    /// Falls back to plain text if JSON is not found.
    /// </summary>
    private static string? TryExtractPartialReply(string text)
    {
        // Look for "reply":" and extract content up to the closing quote
        var startIdx = text.IndexOf("\"reply\":\"", StringComparison.OrdinalIgnoreCase);
        if (startIdx < 0) 
        {
            // Not JSON - return plain text directly (for LoRA models)
            return text.Trim();
        }

        startIdx += "\"reply\":\"".Length;
        
        // Find the end - look for unescaped closing quote
        var endIdx = startIdx;
        while (endIdx < text.Length)
        {
            if (text[endIdx] == '"' && (endIdx == 0 || text[endIdx - 1] != '\\'))
                break;
            endIdx++;
        }

        if (endIdx > startIdx)
        {
            return text[startIdx..endIdx].Replace("\\n", "\n").Replace("\\\"", "\"");
        }

        // If no closing quote yet, return what we have
        return text[startIdx..].Replace("\\n", "\n").Replace("\\\"", "\"");
    }

    #endregion

    #region Activity Summarization


    public async Task<string> SummarizeFiveMinuteAsync(
        ActivitySample sample,
        string systemPrompt,
        CancellationToken ct = default
    )
    {
        var activity = GuessActivity(sample.ProcessName, sample.WindowTitle);

        var user =
            $"Time: {sample.Timestamp:HH:mm}\n"
            + $"App: {Safe(sample.ProcessName)}\n"
            + $"WindowTitle: {Safe(sample.WindowTitle)}\n"
            + $"ActivityHint: {activity}\n"
            + $"IdleSeconds: {sample.IdleSeconds}\n";

        var text = await PostChatAsync(systemPrompt, user, null, ct);
        if (string.IsNullOrWhiteSpace(text))
        {
            await Task.Delay(150, ct);
            text = await PostChatAsync(systemPrompt, user, null, ct);
        }
        text = text.Trim();
        if (text.Length == 0) return "";

        // Ensure declarative output (no question marks)
        if (text.Contains('?'))
            text = text.Replace('?', '.');

        return text;
    }

    public async Task<string> ReactToHourlySummaryAsync(
        string hourlySummaryMarkdown,
        string systemPrompt,
        CancellationToken ct = default
    )
    {
        var user = "HourlySummary:\n" + (hourlySummaryMarkdown ?? "");
        var text = await PostChatAsync(systemPrompt, user, null, ct);
        if (string.IsNullOrWhiteSpace(text))
        {
            await Task.Delay(150, ct);
            text = await PostChatAsync(systemPrompt, user, null, ct);
        }
        return text.Trim();
    }

    public async Task<string> ReactToSummaryAsync(string summaryLine, string systemPrompt, CancellationToken ct = default)
    {
        var user = "Summary: " + (summaryLine ?? "");
        var text = await PostChatAsync(systemPrompt, user, null, ct);
        if (string.IsNullOrWhiteSpace(text))
        {
            await Task.Delay(150, ct);
            text = await PostChatAsync(systemPrompt, user, null, ct);
        }
        return text.Trim();
    }

    public async Task<string> SummarizeConversationAsync(IReadOnlyList<(string role, string content)> messages, CancellationToken ct = default)
    {
        if (messages == null || messages.Count == 0) return "";
        ct.ThrowIfCancellationRequested();
        DevLog.WriteLine("OllamaClient[Memory]: conversation summary using local memory/history only");
        return BuildLocalConversationSummary(messages);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Simple text-to-text request without history.
    /// </summary>
    public async Task<string> PostForTextAsync(string systemText, string userText, CancellationToken ct)
    {
        var messages = new List<OllamaMessage>
        {
            new() { role = "system", content = systemText ?? "" },
            new() { role = "user", content = userText ?? "" }
        };

        var req = new OllamaChatRequest
        {
            model = Model,
            stream = false,
            messages = messages,
            options = new OllamaOptions
            {
                num_predict = _tuning.MaxTokens,
                num_ctx = CONTEXT_WINDOW,
                temperature = _tuning.Temperature,
                top_p = _tuning.TopP,
                top_k = _tuning.TopK,
                repeat_penalty = _tuning.RepeatPenalty,
                presence_penalty = _tuning.PresencePenalty,
                frequency_penalty = _tuning.FrequencyPenalty
            }
        };

        using var resp = await _http.PostAsJsonAsync("/api/chat", req, cancellationToken: ct);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync(ct);
        return ExtractMessageContent(body);
    }

    private async Task<string> PostChatAsync(
        string systemText,
        string userText,
        List<(string role, string content)>? history,
        CancellationToken ct,
        GenerationTuningSettings? runtimeTuning = null
    )
    {
        var tuning = (runtimeTuning ?? _tuning).Clamp();
        var messages = new List<OllamaMessage>
        {
            new() { role = "system", content = systemText ?? "" },
        };

        if (history is not null)
        {
            foreach (var h in history)
            {
                if (string.IsNullOrWhiteSpace(h.content)) continue;
                messages.Add(new OllamaMessage { role = h.role, content = h.content });
            }
        }

        messages.Add(new OllamaMessage { role = "user", content = userText ?? "" });

        var req = new OllamaChatRequest
        {
            model = Model,
            stream = false,
            messages = messages,
            options = new OllamaOptions
            {
                num_predict = tuning.MaxTokens,
                num_ctx = CONTEXT_WINDOW,
                temperature = tuning.Temperature,
                top_p = tuning.TopP,
                top_k = tuning.TopK,
                repeat_penalty = tuning.RepeatPenalty,
                presence_penalty = tuning.PresencePenalty,
                frequency_penalty = tuning.FrequencyPenalty
            }
        };

        using var resp = await _http.PostAsJsonAsync("/api/chat", req, cancellationToken: ct);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync(ct);
        return ExtractMessageContent(body);
    }

    private static string GuessActivity(string? processName, string? windowTitle)
    {
        var p = (processName ?? "").ToLowerInvariant();
        var t = (windowTitle ?? "").ToLowerInvariant();

        if (p.Contains("code") || p.Contains("devenv") || p.Contains("rider") || p.Contains("idea") || t.Contains("visual studio"))
            return "coding / working in an IDE";
        if (p.Contains("chrome") || p.Contains("msedge") || p.Contains("firefox"))
            return t.Contains("youtube") ? "watching a video" : "browsing the web";
        if (p.Contains("discord") || p.Contains("slack") || p.Contains("teams"))
            return "chatting / communication";
        if (p.Contains("steam") || t.Contains("fps") || t.Contains("game"))
            return "gaming";
        if (p.Contains("notion") || p.Contains("obsidian") || p.Contains("onenote"))
            return "notes / planning";
        if (p.Contains("word") || p.Contains("excel") || p.Contains("powerpnt"))
            return "working on documents";

        return "general computer use";
    }

    private static bool TryParseAiReply(string content, out AiReply reply)
    {
        reply = new AiReply("", "neutral");
        if (string.IsNullOrWhiteSpace(content))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            var r = root.TryGetProperty("reply", out var replyProp) ? replyProp.GetString() ?? "" : "";
            var e = root.TryGetProperty("emotion", out var emoProp) ? emoProp.GetString() ?? "neutral" : "neutral";

            r = r.Trim();
            if (r.Length == 0)
                return false;

            reply = new AiReply(r, NormalizeEmotion(e));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeEmotion(string emotion)
    {
        emotion = (emotion ?? "").Trim();
        if (emotion.Length == 0) return "neutral";

        var lower = emotion.ToLowerInvariant();
        return lower switch
        {
            "happy" => "happy",
            "sad" => "sad",
            "angry" => "angry",
            "surprised" => "surprised",
            "playful" => "playful",
            "thinking" => "thinking",
            "neutral" => "neutral",
            _ => emotion.Length > 24 ? emotion[..24] : emotion,
        };
    }

    private static string ExtractMessageContent(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.Object &&
                message.TryGetProperty("content", out var content))
            {
                return (content.GetString() ?? "").Trim();
            }
        }
        catch
        {
            // ignore and fall back to raw body below
        }

        return body.Trim();
    }

    private static bool LooksLikeJson(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length < 2) return false;
        if (!text.StartsWith("{") || !text.EndsWith("}")) return false;
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Safe(string? s)
    {
        s ??= "";
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        if (s.Length > 140) s = s[..140] + "…";
        return s;
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

        var lines = new List<string>();
        if (userMessages.Count > 0)
            lines.Add("- user: " + string.Join(" | ", userMessages.Select(TrimForSummary)));
        if (assistantMessages.Count > 0)
            lines.Add("- assistant: " + string.Join(" | ", assistantMessages.Select(TrimForSummary)));

        return lines.Count == 0 ? "- no conversation content to summarize" : string.Join("\n", lines);
    }

    private static string TrimForSummary(string text)
    {
        const int max = 90;
        return text.Length <= max ? text : text[..max] + "...";
    }

    public async Task<bool> IsModelAvailableAsync(string model, CancellationToken ct = default)
    {
        model = (model ?? "").Trim();
        if (model.Length == 0) return false;

        try
        {
            if (DateTimeOffset.Now - _lastTagsAt > TimeSpan.FromSeconds(30))
            {
                using var resp = await _http.GetAsync("/api/tags", ct);
                if (!resp.IsSuccessStatusCode) return false;

                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                    return false;

                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in models.EnumerateArray())
                {
                    if (m.TryGetProperty("name", out var nameProp))
                    {
                        var name = (nameProp.GetString() ?? "").Trim();
                        if (name.Length > 0) set.Add(name);
                    }
                }

                _lastTags = set;
                _lastTagsAt = DateTimeOffset.Now;
            }

            return _lastTags.Contains(model);
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IsModelRunningAsync(string model, CancellationToken ct = default)
    {
        model = (model ?? "").Trim();
        if (model.Length == 0) return false;

        try
        {
            if (DateTimeOffset.Now - _lastPsAt > TimeSpan.FromSeconds(2))
            {
                using var resp = await _http.GetAsync("/api/ps", ct);
                if (!resp.IsSuccessStatusCode) return false;

                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                    return false;

                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in models.EnumerateArray())
                {
                    if (m.TryGetProperty("name", out var nameProp))
                    {
                        var name = (nameProp.GetString() ?? "").Trim();
                        if (name.Length > 0) set.Add(name);
                    }
                }

                _lastPs = set;
                _lastPsAt = DateTimeOffset.Now;
            }

            return _lastPs.Contains(model);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _http?.Dispose();
    }

    #endregion

    #region DTOs

    private sealed class OllamaChatRequest
    {
        public string model { get; set; } = "";
        public bool stream { get; set; }
        public List<OllamaMessage> messages { get; set; } = [];
        public OllamaOptions? options { get; set; }
    }

    private sealed class OllamaMessage
    {
        public string role { get; set; } = "";
        public string content { get; set; } = "";
    }

    private sealed class OllamaOptions
    {
        public int num_predict { get; set; }
        public int num_ctx { get; set; }
        public float temperature { get; set; }
        public float top_p { get; set; }
        public int top_k { get; set; }
        public float repeat_penalty { get; set; }
        public float presence_penalty { get; set; }
        public float frequency_penalty { get; set; }
    }

    #endregion
}
