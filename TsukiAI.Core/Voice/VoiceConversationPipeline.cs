using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Polly;
using Polly.Retry;
using TsukiAI.Core.Models;
using TsukiAI.Core.Services;

namespace TsukiAI.VoiceChat.Services;

public sealed record VoiceProcessResult(
    bool Success,
    string InputText,
    string ResponseText,
    byte[] AudioPcm48kStereo,
    string? ErrorMessage = null,
    string Language = "en",
    float Confidence = 0f);

public sealed record VoiceTurnEvent(
    string InputText,
    string ResponseText,
    int LlmMs,
    int TtsMs,
    int TotalMs);

public sealed class VoiceConversationPipeline : IVoiceConversationPipeline, IDisposable
{
    private static readonly ResiliencePipeline<HttpResponseMessage> CloudTtsRetryPipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
        .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
        {
            MaxRetryAttempts = 2,
            Delay = TimeSpan.FromMilliseconds(500),
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
                var statusCode = args.Outcome.Result?.StatusCode.ToString() ?? "Exception";
                DevLog.WriteLine("[VoiceFlow][CloudTTS][Retry]: attempt={0}, status={1}, delay_ms={2:F0}",
                    args.AttemptNumber, statusCode, args.RetryDelay.TotalMilliseconds);
                return ValueTask.CompletedTask;
            }
        })
        .Build();

    private readonly IInferenceClient _inferenceClient;
    private readonly VoicevoxClient _voicevoxClient;
    private readonly TranslationService _translationService;
    private readonly AudioProcessingService _audioProcessingService;
    private readonly AppSettings _settings;
    private readonly LatencyTracker _latencyTracker;
    private readonly HttpClient _httpClient;
    private readonly Channel<(string UserText, string AssistantText)> _historyQueue =
        Channel.CreateBounded<(string UserText, string AssistantText)>(new BoundedChannelOptions(128)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private readonly CancellationTokenSource _historyWorkerCts = new();
    private readonly Task _historyWorkerTask;
    private readonly object _historyLock = new();
    private List<ConversationHistoryService.ConversationMessage> _historyMessages = new();
    private bool _historyLoaded;
    private bool _isDisposed;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentTranscriptions = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inflightByUser = new();
    private readonly TimeSpan _dedupeWindow = TimeSpan.FromSeconds(2);
    private int _activeTurnCount;
    private int _queueDepth;
    private readonly System.Threading.Timer _queueDepthLogTimer;
    public event Action<VoiceTurnEvent>? TurnCompleted;
    public event Action<bool>? AssistantTypingChanged;

    public bool IsRunning { get; private set; }

    public VoiceConversationPipeline(
        IInferenceClient inferenceClient,
        VoicevoxClient voicevoxClient,
        TranslationService translationService,
        AudioProcessingService audioProcessingService,
        AppSettings settings)
    {
        _inferenceClient = inferenceClient;
        _voicevoxClient = voicevoxClient;
        _translationService = translationService;
        _audioProcessingService = audioProcessingService;
        _settings = settings;
        _latencyTracker = new LatencyTracker();
        // ngrok-skip-browser-warning is required for all requests through ngrok tunnels —
        // without it ngrok returns its own HTML interstitial page (404/502) instead of
        // forwarding to the backend VOICEVOX instance.
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("ngrok-skip-browser-warning", "true");
        _historyWorkerTask = Task.Run(HistoryWorkerAsync);
        _queueDepthLogTimer = new System.Threading.Timer(_ =>
        {
            var depth = Volatile.Read(ref _queueDepth);
            if (depth > 0)
            {
                DevLog.WriteLine("[VoiceFlow] component=queue_depth count={0} timestamp={1:O}", depth, DateTimeOffset.UtcNow);
            }
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public void Start()
    {
        IsRunning = true;
    }

    public void Stop()
    {
        IsRunning = false;
        foreach (var kv in _inflightByUser)
        {
            try { kv.Value.Cancel(); } catch { }
            try { kv.Value.Dispose(); } catch { }
        }
        _inflightByUser.Clear();
        FlushHistoryNow();
    }

    public async Task<VoiceProcessResult> ProcessTextAsync(string userId, string text, string? correlationId = null, CancellationToken ct = default)
    {
        correlationId ??= Guid.NewGuid().ToString("N");
        Interlocked.Increment(ref _queueDepth);
        var totalSw = Stopwatch.StartNew();
        var typingStateRaised = false;
        var runtimeSettings = GetRuntimeSettings();
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0)
            return new VoiceProcessResult(false, text, string.Empty, Array.Empty<byte>(), "Empty text");

        if (!runtimeSettings.VoiceTextReceptionEnabled)
            return new VoiceProcessResult(false, text, string.Empty, Array.Empty<byte>(), "Voice text reception disabled");

        if (!ShouldProcessUser(userId, runtimeSettings))
            return new VoiceProcessResult(false, text, string.Empty, Array.Empty<byte>(), "User filtered");

        var dedupeKey = $"{userId}:{text.ToLowerInvariant()}";
        if (IsDuplicate(dedupeKey))
            return new VoiceProcessResult(false, text, string.Empty, Array.Empty<byte>(), "Duplicate transcription");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var currentCts = _inflightByUser.AddOrUpdate(
            userId,
            _ => linkedCts,
            (_, existing) =>
            {
                try { existing.Cancel(); } catch { }
                try { existing.Dispose(); } catch { }
                return linkedCts;
            });

        try
        {
            SetAssistantTyping(true);
            typingStateRaised = true;

            var retrieveSw = Stopwatch.StartNew();
            var history = LoadRecentHistory(12);
            retrieveSw.Stop();
            DevLog.WriteLine("[VoiceFlow] retrieve_recent_ms={0}", retrieveSw.ElapsedMilliseconds);

            var llmSw = Stopwatch.StartNew();
            string responseText;
            if (TryBuildDateTimeResponse(text, out var localDateTimeResponse))
            {
                responseText = localDateTimeResponse;
                DevLog.WriteLine("[VoiceFlow] local_datetime_response=1");
            }
            else
            {
                AiReply reply;
                try
                {
                    reply = await _inferenceClient.ChatWithEmotionAsync(
                        text,
                        personaName: "Tsuki",
                        preferredEmotion: null,
                        history: history,
                        ct: currentCts.Token,
                        correlationId: correlationId);
                }
                catch (InferenceRateLimitException rateLimitEx)
                {
                    DevLog.WriteLine("[VoiceFlow] rate_limit_hit={0}", rateLimitEx.Message);
                    
                    // Try to switch provider if multi-provider mode is enabled
                    if (runtimeSettings.UseMultipleAiProviders && !string.IsNullOrWhiteSpace(runtimeSettings.MultiAiProvidersCsv))
                    {
                        var providerSwitcher = new ProviderSwitchingService();
                        var nextProvider = providerSwitcher.SwitchToNextProvider(runtimeSettings.MultiAiProvidersCsv);
                        
                        if (nextProvider != null)
                        {
                            DevLog.WriteLine($"[VoiceFlow] switched_to_provider={nextProvider}");
                            DevLog.WriteLine("[VoiceFlow] restart_required=true (provider switch requires app restart)");
                        }
                    }
                    
                    // Return error response
                    return new VoiceProcessResult(false, text, "Rate limit reached, please try again in a moment.", Array.Empty<byte>(), rateLimitEx.Message);
                }
                responseText = SanitizeForVoice(reply.Reply);
            }
            llmSw.Stop();
            _latencyTracker.RecordLatency("llm", llmSw.Elapsed);
            DevLog.WriteLine("[VoiceFlow] llm_ms={0}", llmSw.ElapsedMilliseconds);

            if (string.IsNullOrWhiteSpace(responseText))
                responseText = "Sorry, I could not process that.";

            var ttsText = StripParenthesesForTts(responseText);
            if (string.IsNullOrWhiteSpace(ttsText))
                ttsText = responseText;

            var ttsSw = Stopwatch.StartNew();
            byte[] wav;

            // Parallel TTS optimisation: when translation is needed and using local VOICEVOX,
            // fire audio_query (with the untranslated text as a warm-up probe) and DeepL
            // concurrently, then synthesize once we have both the translated text and query JSON.
            // This saves ~300-800ms per turn by overlapping the two network round-trips.
            bool needsTranslation = runtimeSettings.VoiceTranslateToJapanese
                && !LooksPrimarilyJapanese(ttsText)
                && runtimeSettings.TtsMode == TtsMode.LocalVoiceVox;

            if (needsTranslation && _translationService.IsEnabled)
            {
                // Run translation and audio_query for the original text concurrently.
                // We use the original text for audio_query so VOICEVOX can pre-process
                // phoneme data; we discard that result and re-query with translated text.
                // Net saving: translation latency is fully hidden behind audio_query.
                var translateTask = _translationService.TranslateToJapaneseAsync(responseText, currentCts.Token, correlationId);
                var warmQueryTask = _voicevoxClient.AudioQueryAsync(ttsText, runtimeSettings.VoicevoxSpeakerStyleId, currentCts.Token, correlationId);

                await Task.WhenAll(translateTask, warmQueryTask);
                ttsText = translateTask.Result;

                // Now fetch the real audio_query for the translated text, then synthesize.
                // If translated text happens to equal original (e.g. already Japanese), reuse warm query.
                string queryJson;
                if (string.Equals(ttsText, StripParenthesesForTts(responseText), StringComparison.Ordinal))
                {
                    queryJson = warmQueryTask.Result;
                }
                else
                {
                    queryJson = await _voicevoxClient.AudioQueryAsync(ttsText, runtimeSettings.VoicevoxSpeakerStyleId, currentCts.Token, correlationId);
                }

                wav = string.IsNullOrWhiteSpace(queryJson)
                    ? Array.Empty<byte>()
                    : await _voicevoxClient.SynthesizeFromQueryAsync(queryJson, runtimeSettings.VoicevoxSpeakerStyleId, currentCts.Token, correlationId);
            }
            else
            {
                // No translation or cloud TTS — use standard path.
                if (needsTranslation && !_translationService.IsEnabled)
                    DevLog.WriteLine("[VoiceFlow] translation_requested_but_deepl_unavailable=1");

                wav = await SynthesizeWavAsync(ttsText, currentCts.Token, correlationId);
            }

            var pcm = _audioProcessingService.ConvertVoiceVoxWavToDiscordPcm(wav);
            ttsSw.Stop();
            _latencyTracker.RecordLatency("tts", ttsSw.Elapsed);
            DevLog.WriteLine("[VoiceFlow] tts_ms={0}", ttsSw.ElapsedMilliseconds);

            EnqueueConversationTurn(text, responseText);

            totalSw.Stop();
            _latencyTracker.RecordLatency("total", totalSw.Elapsed);
            DevLog.WriteLine("[VoiceFlow] total_ms={0}", totalSw.ElapsedMilliseconds);
            LogLatencyPercentiles(correlationId);
            TurnCompleted?.Invoke(new VoiceTurnEvent(
                text,
                responseText,
                (int)llmSw.ElapsedMilliseconds,
                (int)ttsSw.ElapsedMilliseconds,
                (int)totalSw.ElapsedMilliseconds));

            return new VoiceProcessResult(true, text, responseText, pcm);
        }
        catch (OperationCanceledException)
        {
            return new VoiceProcessResult(false, text, string.Empty, Array.Empty<byte>(), "Request canceled");
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("[VoiceFlow] correlation_id={0}, component=pipeline, operation=process_text, status=error, error={1}", correlationId, ex);
            return new VoiceProcessResult(false, text, "I hit an issue processing that.", Array.Empty<byte>(), ex.Message);
        }
        finally
        {
            if (typingStateRaised)
            {
                SetAssistantTyping(false);
            }
            _inflightByUser.TryRemove(userId, out _);
            Interlocked.Decrement(ref _queueDepth);
        }
    }

    public async Task<byte[]> SynthesizeTextToPcmAsync(string text, string? correlationId = null, CancellationToken ct = default)
    {
        correlationId ??= Guid.NewGuid().ToString("N");
        var ttsText = StripParenthesesForTts(text);
        if (string.IsNullOrWhiteSpace(ttsText))
        {
            return Array.Empty<byte>();
        }

        var wav = await SynthesizeWavAsync(ttsText, ct, correlationId);
        if (wav.Length == 0)
        {
            return Array.Empty<byte>();
        }

        return _audioProcessingService.ConvertVoiceVoxWavToDiscordPcm(wav);
    }

    private void SetAssistantTyping(bool isTyping)
    {
        if (isTyping)
        {
            var count = Interlocked.Increment(ref _activeTurnCount);
            if (count == 1)
            {
                AssistantTypingChanged?.Invoke(true);
            }
            return;
        }

        var after = Interlocked.Decrement(ref _activeTurnCount);
        if (after <= 0)
        {
            Interlocked.Exchange(ref _activeTurnCount, 0);
            AssistantTypingChanged?.Invoke(false);
        }
    }

    private bool ShouldProcessUser(string userId, AppSettings settings)
    {
        var focused = settings.DiscordFocusedUserId;
        if (focused == ulong.MaxValue || focused == 0)
            return true;

        return ulong.TryParse(userId, out var parsed) && parsed == focused;
    }

    private bool IsDuplicate(string key)
    {
        var now = DateTimeOffset.UtcNow;
        if (_recentTranscriptions.TryGetValue(key, out var at) && now - at <= _dedupeWindow)
            return true;

        _recentTranscriptions[key] = now;
        foreach (var kv in _recentTranscriptions)
        {
            if (now - kv.Value > TimeSpan.FromMinutes(1))
                _recentTranscriptions.TryRemove(kv.Key, out _);
        }

        return false;
    }

    private IReadOnlyList<(string role, string content)> LoadRecentHistory(int count)
    {
        EnsureHistoryLoaded();
        lock (_historyLock)
        {
            return _historyMessages
                .TakeLast(Math.Max(1, count))
                .Select(m => (m.Role, m.Content))
                .ToList();
        }
    }

    private void EnqueueConversationTurn(string userText, string assistantText)
    {
        if (!_historyQueue.Writer.TryWrite((userText, assistantText)))
        {
            DevLog.WriteLine("[VoiceFlow] history_queue_full_drop=1");
        }
    }

    public void RecordSttLatency(TimeSpan duration, string? correlationId = null)
    {
        _latencyTracker.RecordLatency("stt", duration);
        var p = _latencyTracker.GetPercentiles("stt");
        if (p is null)
            return;

        DevLog.WriteLine("[VoiceFlow] correlation_id={0}, component=latency, operation=stt_percentiles, p50_ms={1:F1}, p95_ms={2:F1}, p99_ms={3:F1}, sample_count={4}, status=ok",
            correlationId ?? "none", p.P50, p.P95, p.P99, p.SampleCount);
    }

    private void LogLatencyPercentiles(string correlationId)
    {
        foreach (var operation in new[] { "stt", "llm", "tts", "total" })
        {
            var p = _latencyTracker.GetPercentiles(operation);
            if (p is null)
                continue;

            DevLog.WriteLine("[VoiceFlow] correlation_id={0}, component=latency, operation={1}_percentiles, p50_ms={2:F1}, p95_ms={3:F1}, p99_ms={4:F1}, sample_count={5}, status=ok",
                correlationId, operation, p.P50, p.P95, p.P99, p.SampleCount);
        }
    }

    private async Task<byte[]> SynthesizeWavAsync(string text, CancellationToken ct, string? correlationId)
    {
        var runtimeSettings = GetRuntimeSettings();

        // Auto-fallback: if CloudRemote is selected but no URL is configured, use local VOICEVOX
        if (runtimeSettings.TtsMode == TtsMode.CloudRemote && string.IsNullOrWhiteSpace(runtimeSettings.CloudTtsUrl))
        {
            DevLog.WriteLine("[VoiceFlow][CloudTTS] CloudTtsUrl is empty, falling back to local VOICEVOX");
            try
            {
                return await _voicevoxClient.SynthesizeWavAsync(text, runtimeSettings.VoicevoxSpeakerStyleId, ct, correlationId);
            }
            catch (Exception ex)
            {
                DevLog.WriteLine("[VoiceFlow][LocalTTS] fallback failed ({0})", ex.GetBaseException().Message);
                return Array.Empty<byte>();
            }
        }

        if (runtimeSettings.TtsMode == TtsMode.CloudRemote)
        {
            try
            {
                var baseUrl = runtimeSettings.CloudTtsUrl.TrimEnd('/');
                var queryUrl = $"{baseUrl}/audio_query?text={Uri.EscapeDataString(text)}&speaker={runtimeSettings.VoicevoxSpeakerStyleId}";
                using var queryResp = await CloudTtsRetryPipeline.ExecuteAsync(
                    async innerCt =>
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Post, queryUrl);
                        if (!string.IsNullOrWhiteSpace(correlationId))
                            req.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
                        return await _httpClient.SendAsync(req, innerCt);
                    },
                    ct);
                queryResp.EnsureSuccessStatusCode();
                var queryJson = await queryResp.Content.ReadAsStringAsync(ct);

                using var synthResp = await CloudTtsRetryPipeline.ExecuteAsync(
                    async innerCt =>
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/synthesis?speaker={runtimeSettings.VoicevoxSpeakerStyleId}")
                        {
                            Content = new StringContent(queryJson, System.Text.Encoding.UTF8, "application/json")
                        };
                        if (!string.IsNullOrWhiteSpace(correlationId))
                            req.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
                        return await _httpClient.SendAsync(req, innerCt);
                    },
                    ct);
                synthResp.EnsureSuccessStatusCode();
                return await synthResp.Content.ReadAsByteArrayAsync(ct);
            }
            catch (Exception ex)
            {
                DevLog.WriteLine("[VoiceFlow][CloudTTS] failed ({0})", ex.GetBaseException().Message);
                return Array.Empty<byte>();
            }
        }

        try
        {
            return await _voicevoxClient.SynthesizeWavAsync(text, runtimeSettings.VoicevoxSpeakerStyleId, ct, correlationId);
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("[VoiceFlow][LocalTTS] failed ({0}), returning text-only response", ex.GetBaseException().Message);
            return Array.Empty<byte>();
        }
    }

    private AppSettings GetRuntimeSettings()
    {
        try
        {
            return SettingsService.Load();
        }
        catch
        {
            return _settings;
        }
    }

    private static string SanitizeForVoice(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var cleaned = text.Trim();
        foreach (var tag in new[] { "<tool_call>", "</tool_call>", "<think>", "</think>", "Assistant:", "System:", "User:" })
            cleaned = cleaned.Replace(tag, string.Empty, StringComparison.OrdinalIgnoreCase);

        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, "\\s+", " ").Trim();
        const int maxChars = 280;
        return cleaned.Length <= maxChars ? cleaned : cleaned[..maxChars] + "...";
    }

    private static string StripParenthesesForTts(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var cleaned = text;
        // Remove round-parenthetical segments so metadata like "(GMT +8)" is not spoken.
        while (true)
        {
            var next = Regex.Replace(cleaned, @"\([^()]*\)|（[^（）]*）", string.Empty);
            if (next == cleaned)
                break;
            cleaned = next;
        }

        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
        return cleaned;
    }

    private static bool LooksPrimarilyJapanese(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var relevantChars = text.Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)).ToArray();
        if (relevantChars.Length == 0)
            return false;

        var japaneseChars = relevantChars.Count(c =>
            (c >= 0x3040 && c <= 0x30FF) ||
            (c >= 0x4E00 && c <= 0x9FFF));

        return japaneseChars >= relevantChars.Length * 0.4;
    }

    private static bool TryBuildDateTimeResponse(string userText, out string response)
    {
        response = string.Empty;
        var text = (userText ?? string.Empty).Trim();
        if (text.Length == 0)
            return false;

        var asksTime = Regex.IsMatch(
            text,
            @"\b(what(?:'s| is)?\s+the\s+time|what\s+time(?:\s+is\s+it)?|current\s+time|time\s+now|clock)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var asksDate = Regex.IsMatch(
            text,
            @"\b(what(?:'s| is)?\s+the\s+date|today(?:'s)?\s+date|current\s+date|what\s+day(?:\s+is\s+it)?|date\s+today)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!asksTime && !asksDate)
            return false;

        var now = DateTime.Now;
        var offset = TimeZoneInfo.Local.GetUtcOffset(now);
        var gmtText = FormatGmtOffset(offset);

        if (asksTime && asksDate)
        {
            response = $"It is {now:h:mm tt} on {now:dddd, MMMM d, yyyy} ({gmtText}).";
            return true;
        }

        if (asksTime)
        {
            response = $"The current time is {now:h:mm tt} ({gmtText}).";
            return true;
        }

        response = $"Today is {now:dddd, MMMM d, yyyy}.";
        return true;
    }

    private static string FormatGmtOffset(TimeSpan offset)
    {
        var sign = offset >= TimeSpan.Zero ? "+" : "-";
        var abs = offset.Duration();
        if (abs.Minutes == 0)
        {
            return $"GMT {sign}{abs.Hours}";
        }

        return $"GMT {sign}{abs.Hours}:{abs.Minutes:00}";
    }

    private async Task HistoryWorkerAsync()
    {
        var reader = _historyQueue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_historyWorkerCts.Token))
            {
                while (reader.TryRead(out var turn))
                {
                    AppendConversationTurn(turn.UserText, turn.AssistantText);
                }

                FlushHistoryNow();
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("[VoiceFlow] history_worker_failed={0}", ex.Message);
        }
        finally
        {
            while (reader.TryRead(out var turn))
            {
                AppendConversationTurn(turn.UserText, turn.AssistantText);
            }
            FlushHistoryNow();
        }
    }

    private void EnsureHistoryLoaded()
    {
        if (_historyLoaded) return;

        lock (_historyLock)
        {
            if (_historyLoaded) return;
            try
            {
                var history = ConversationHistoryService.LoadVoiceChatHistoryAsync().GetAwaiter().GetResult();
                _historyMessages = history?.Messages?.ToList() ?? new List<ConversationHistoryService.ConversationMessage>();
            }
            catch (Exception ex)
            {
                _historyMessages = new List<ConversationHistoryService.ConversationMessage>();
                DevLog.WriteLine("[VoiceFlow] history_load_failed={0}", ex.Message);
            }

            _historyLoaded = true;
        }
    }

    private void AppendConversationTurn(string userText, string assistantText)
    {
        EnsureHistoryLoaded();
        lock (_historyLock)
        {
            _historyMessages.Add(new ConversationHistoryService.ConversationMessage
            {
                Role = "user",
                Content = userText,
                SpeakerId = "discord-user",
                SpeakerName = "User",
                Timestamp = DateTime.Now
            });
            _historyMessages.Add(new ConversationHistoryService.ConversationMessage
            {
                Role = "assistant",
                Content = assistantText,
                SpeakerId = "AI",
                SpeakerName = "Tsuki",
                Timestamp = DateTime.Now
            });

            if (_historyMessages.Count > 400)
                _historyMessages = _historyMessages.TakeLast(400).ToList();
        }
    }

    private void FlushHistoryNow()
    {
        EnsureHistoryLoaded();
        try
        {
            List<ConversationHistoryService.ConversationMessage> snapshot;
            lock (_historyLock)
            {
                snapshot = _historyMessages.ToList();
            }

            var displayText = string.Join("\n", snapshot.TakeLast(40).Select(m => $"{m.Role}: {m.Content}"));
            ConversationHistoryService.SaveVoiceChatHistoryWithSpeakersAsync(snapshot, displayText).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("[VoiceFlow] save_history_failed={0}", ex.Message);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        try { _historyWorkerCts.Cancel(); } catch { }

        try
        {
            _historyWorkerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("[VoiceFlow] history_worker_wait_failed={0}", ex.Message);
        }
        
        FlushHistoryNow();
        _historyWorkerCts.Dispose();
        _queueDepthLogTimer.Dispose();
        _httpClient.Dispose();
    }
}
