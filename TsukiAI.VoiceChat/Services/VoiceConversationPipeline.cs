using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Channels;
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

public sealed class VoiceConversationPipeline : IDisposable
{
    private readonly IInferenceClient _inferenceClient;
    private readonly VoicevoxClient _voicevoxClient;
    private readonly TranslationService _translationService;
    private readonly AudioProcessingService _audioProcessingService;
    private readonly AppSettings _settings;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(45) };
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
        _historyWorkerTask = Task.Run(HistoryWorkerAsync);
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

    public async Task<VoiceProcessResult> ProcessTextAsync(string userId, string text, CancellationToken ct = default)
    {
        var totalSw = Stopwatch.StartNew();
        var typingStateRaised = false;
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0)
            return new VoiceProcessResult(false, text, string.Empty, Array.Empty<byte>(), "Empty text");

        if (!_settings.VoiceTextReceptionEnabled)
            return new VoiceProcessResult(false, text, string.Empty, Array.Empty<byte>(), "Voice text reception disabled");

        if (!ShouldProcessUser(userId))
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
            var history = LoadRecentHistory(20);
            retrieveSw.Stop();
            DevLog.WriteLine("[VoiceFlow] retrieve_recent_ms={0}", retrieveSw.ElapsedMilliseconds);

            var llmSw = Stopwatch.StartNew();
            var reply = await _inferenceClient.ChatWithEmotionAsync(
                text,
                personaName: "Tsuki",
                preferredEmotion: null,
                history: history,
                ct: currentCts.Token);
            llmSw.Stop();
            DevLog.WriteLine("[VoiceFlow] llm_ms={0}", llmSw.ElapsedMilliseconds);

            var responseText = SanitizeForVoice(reply.Reply);
            if (string.IsNullOrWhiteSpace(responseText))
                responseText = "Sorry, I could not process that.";

            var ttsText = responseText;
            if (_settings.VoiceTranslateToJapanese)
            {
                if (_translationService.IsEnabled)
                {
                    ttsText = await _translationService.TranslateToJapaneseAsync(responseText, currentCts.Token);
                }
                else
                {
                    DevLog.WriteLine("[VoiceFlow] translation_requested_but_deepl_unavailable=1");
                }
            }

            var ttsSw = Stopwatch.StartNew();
            var wav = await SynthesizeWavAsync(ttsText, currentCts.Token);
            var pcm = _audioProcessingService.ConvertVoiceVoxWavToDiscordPcm(wav);
            ttsSw.Stop();
            DevLog.WriteLine("[VoiceFlow] tts_ms={0}", ttsSw.ElapsedMilliseconds);

            EnqueueConversationTurn(text, responseText);

            totalSw.Stop();
            DevLog.WriteLine("[VoiceFlow] total_ms={0}", totalSw.ElapsedMilliseconds);
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
            DevLog.WriteLine("[VoiceFlow] error={0}", ex.Message);
            return new VoiceProcessResult(false, text, "I hit an issue processing that.", Array.Empty<byte>(), ex.Message);
        }
        finally
        {
            if (typingStateRaised)
            {
                SetAssistantTyping(false);
            }
            _inflightByUser.TryRemove(userId, out _);
        }
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

    private bool ShouldProcessUser(string userId)
    {
        var focused = _settings.DiscordFocusedUserId;
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

    private async Task<byte[]> SynthesizeWavAsync(string text, CancellationToken ct)
    {
        if (_settings.TtsMode == TtsMode.CloudRemote && !string.IsNullOrWhiteSpace(_settings.CloudTtsUrl))
        {
            var baseUrl = _settings.CloudTtsUrl.TrimEnd('/');
            var queryUrl = $"{baseUrl}/audio_query?text={Uri.EscapeDataString(text)}&speaker={_settings.VoicevoxSpeakerStyleId}";
            using var queryResp = await _httpClient.PostAsync(queryUrl, null, ct);
            queryResp.EnsureSuccessStatusCode();
            var queryJson = await queryResp.Content.ReadAsStringAsync(ct);

            using var content = new StringContent(queryJson, System.Text.Encoding.UTF8, "application/json");
            using var synthResp = await _httpClient.PostAsync($"{baseUrl}/synthesis?speaker={_settings.VoicevoxSpeakerStyleId}", content, ct);
            synthResp.EnsureSuccessStatusCode();
            return await synthResp.Content.ReadAsByteArrayAsync(ct);
        }

        return await _voicevoxClient.SynthesizeWavAsync(text, _settings.VoicevoxSpeakerStyleId, ct);
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
                var history = ConversationHistoryService.LoadVoiceChatHistory();
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
            ConversationHistoryService.SaveVoiceChatHistoryWithSpeakers(snapshot, displayText);
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
        try { _historyWorkerTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        FlushHistoryNow();
        _historyWorkerCts.Dispose();
        _httpClient.Dispose();
    }
}
