using System.Text.Json;

namespace TsukiAI.Core.Services;

/// <summary>
/// Manages persistent conversation history for Chat and Voice Chat modes with debounced saves.
/// Async methods are canonical. Sync methods are retained as temporary wrappers for compatibility.
/// </summary>
public static class ConversationHistoryService
{
    private static readonly string BaseDir = SettingsService.GetBaseDir();
    private static readonly string ChatHistoryPath = Path.Combine(BaseDir, "chat_history.json");
    private static readonly string VoiceChatHistoryPath = Path.Combine(BaseDir, "voice_chat_history.json");
    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };
    private static readonly TimeSpan SaveDebounceDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SyncWrapperTimeout = TimeSpan.FromSeconds(3);
    private const int MaxHistoryMessages = 1000; // Prevent unbounded growth

    private static Timer? _chatSaveTimer;
    private static Timer? _voiceSaveTimer;
    private static ConversationHistory? _pendingChatHistory;
    private static ConversationHistory? _pendingVoiceHistory;
    private static readonly object _chatLock = new();
    private static readonly object _voiceLock = new();
    private static int _chatFlushInProgress;
    private static int _voiceFlushInProgress;

    public class ConversationHistory
    {
        public List<ConversationMessage> Messages { get; set; } = new();
        public string DisplayText { get; set; } = string.Empty;
        public DateTime LastUpdated { get; set; } = DateTime.Now;
    }

    public class ConversationMessage
    {
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string? SpeakerId { get; set; }
        public string? SpeakerName { get; set; }
    }

    public static Task SaveChatHistoryAsync(IEnumerable<(string role, string content)> context, string displayText)
    {
        var messages = context.Select(c => new ConversationMessage
        {
            Role = c.role,
            Content = c.content,
            Timestamp = DateTime.Now
        }).TakeLast(MaxHistoryMessages).ToList(); // Limit to prevent unbounded growth

        QueueFlushChat(new ConversationHistory
        {
            Messages = messages,
            DisplayText = displayText,
            LastUpdated = DateTime.Now
        });

        return Task.CompletedTask;
    }

    public static Task SaveVoiceChatHistoryAsync(IEnumerable<(string role, string content)> context, string displayText)
    {
        var messages = context.Select(c => new ConversationMessage
        {
            Role = c.role,
            Content = c.content,
            Timestamp = DateTime.Now
        }).TakeLast(MaxHistoryMessages).ToList(); // Limit to prevent unbounded growth

        QueueFlushVoice(new ConversationHistory
        {
            Messages = messages,
            DisplayText = displayText,
            LastUpdated = DateTime.Now
        });

        return Task.CompletedTask;
    }

    public static Task SaveVoiceChatHistoryWithSpeakersAsync(IEnumerable<ConversationMessage> messages, string displayText)
    {
        var limitedMessages = messages.TakeLast(MaxHistoryMessages).ToList(); // Limit to prevent unbounded growth

        QueueFlushVoice(new ConversationHistory
        {
            Messages = limitedMessages,
            DisplayText = displayText,
            LastUpdated = DateTime.Now
        });

        return Task.CompletedTask;
    }

    public static async Task<ConversationHistory?> LoadChatHistoryAsync()
    {
        try
        {
            if (!File.Exists(ChatHistoryPath))
                return null;

            var json = await File.ReadAllTextAsync(ChatHistoryPath).ConfigureAwait(false);
            var history = JsonSerializer.Deserialize<ConversationHistory>(json);
            if (history is not null)
            {
                DevLog.WriteLine("[History] Loaded chat history: {0} messages from {1}", history.Messages.Count, history.LastUpdated);
            }

            return history;
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("[History] Failed to load chat history: {0}", ex.Message);
            return null;
        }
    }

    public static async Task<ConversationHistory?> LoadVoiceChatHistoryAsync()
    {
        try
        {
            if (!File.Exists(VoiceChatHistoryPath))
                return null;

            var json = await File.ReadAllTextAsync(VoiceChatHistoryPath).ConfigureAwait(false);
            var history = JsonSerializer.Deserialize<ConversationHistory>(json);
            if (history is not null)
            {
                DevLog.WriteLine("[History] Loaded voice chat history: {0} messages from {1}", history.Messages.Count, history.LastUpdated);
            }

            return history;
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("[History] Failed to load voice chat history: {0}", ex.Message);
            return null;
        }
    }

    [Obsolete("Use SaveChatHistoryAsync instead.")]
    public static void SaveChatHistory(IEnumerable<(string role, string content)> context, string displayText)
    {
        WaitSync(SaveChatHistoryAsync(context, displayText), nameof(SaveChatHistoryAsync));
    }

    [Obsolete("Use SaveVoiceChatHistoryAsync instead.")]
    public static void SaveVoiceChatHistory(IEnumerable<(string role, string content)> context, string displayText)
    {
        WaitSync(SaveVoiceChatHistoryAsync(context, displayText), nameof(SaveVoiceChatHistoryAsync));
    }

    [Obsolete("Use SaveVoiceChatHistoryWithSpeakersAsync instead.")]
    public static void SaveVoiceChatHistoryWithSpeakers(IEnumerable<ConversationMessage> messages, string displayText)
    {
        WaitSync(SaveVoiceChatHistoryWithSpeakersAsync(messages, displayText), nameof(SaveVoiceChatHistoryWithSpeakersAsync));
    }

    [Obsolete("Use LoadChatHistoryAsync instead.")]
    public static ConversationHistory? LoadChatHistory()
    {
        var task = LoadChatHistoryAsync();
        return WaitSync(task, nameof(LoadChatHistoryAsync)) ? task.GetAwaiter().GetResult() : null;
    }

    [Obsolete("Use LoadVoiceChatHistoryAsync instead.")]
    public static ConversationHistory? LoadVoiceChatHistory()
    {
        var task = LoadVoiceChatHistoryAsync();
        return WaitSync(task, nameof(LoadVoiceChatHistoryAsync)) ? task.GetAwaiter().GetResult() : null;
    }

    public static void ClearChatHistory()
    {
        try
        {
            if (File.Exists(ChatHistoryPath))
            {
                File.Delete(ChatHistoryPath);
                DevLog.WriteLine("[History] Cleared chat history");
            }
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("[History] Failed to clear chat history: {0}", ex.Message);
        }
    }

    public static void ClearVoiceChatHistory()
    {
        try
        {
            if (File.Exists(VoiceChatHistoryPath))
            {
                File.Delete(VoiceChatHistoryPath);
                DevLog.WriteLine("[History] Cleared voice chat history");
            }
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("[History] Failed to clear voice chat history: {0}", ex.Message);
        }
    }

    public static void FlushAllPending()
    {
        lock (_chatLock)
        {
            _chatSaveTimer?.Dispose();
            _chatSaveTimer = null;
        }

        lock (_voiceLock)
        {
            _voiceSaveTimer?.Dispose();
            _voiceSaveTimer = null;
        }

        FlushPendingSync(ref _pendingChatHistory, ChatHistoryPath, _chatLock, "chat");
        FlushPendingSync(ref _pendingVoiceHistory, VoiceChatHistoryPath, _voiceLock, "voice");
    }

    private static void QueueFlushChat(ConversationHistory history)
    {
        lock (_chatLock)
        {
            _pendingChatHistory = history;
            _chatSaveTimer?.Dispose();
            _chatSaveTimer = new Timer(_ => QueueFlushChatWorker(), null, SaveDebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private static void QueueFlushVoice(ConversationHistory history)
    {
        lock (_voiceLock)
        {
            _pendingVoiceHistory = history;
            _voiceSaveTimer?.Dispose();
            _voiceSaveTimer = new Timer(_ => QueueFlushVoiceWorker(), null, SaveDebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private static void QueueFlushChatWorker()
    {
        _ = Task.Run(FlushChatHistoryAsync);
    }

    private static void QueueFlushVoiceWorker()
    {
        _ = Task.Run(FlushVoiceHistoryAsync);
    }

    private static async Task FlushChatHistoryAsync()
    {
        if (Interlocked.Exchange(ref _chatFlushInProgress, 1) == 1)
            return;

        try
        {
            ConversationHistory? history;
            lock (_chatLock)
            {
                history = _pendingChatHistory;
                _pendingChatHistory = null;
            }

            if (history is null)
                return;

            var json = JsonSerializer.Serialize(history, CompactJson);
            await File.WriteAllTextAsync(ChatHistoryPath, json).ConfigureAwait(false);
            DevLog.WriteLine("[History] Flushed chat history: {0} messages", history.Messages.Count);
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("[History] Failed to flush chat history: {0}", ex.Message);
        }
        finally
        {
            Volatile.Write(ref _chatFlushInProgress, 0);
        }
    }

    private static async Task FlushVoiceHistoryAsync()
    {
        if (Interlocked.Exchange(ref _voiceFlushInProgress, 1) == 1)
            return;

        try
        {
            ConversationHistory? history;
            lock (_voiceLock)
            {
                history = _pendingVoiceHistory;
                _pendingVoiceHistory = null;
            }

            if (history is null)
                return;

            var json = JsonSerializer.Serialize(history, CompactJson);
            await File.WriteAllTextAsync(VoiceChatHistoryPath, json).ConfigureAwait(false);
            DevLog.WriteLine("[History] Flushed voice chat history: {0} messages", history.Messages.Count);
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("[History] Failed to flush voice chat history: {0}", ex.Message);
        }
        finally
        {
            Volatile.Write(ref _voiceFlushInProgress, 0);
        }
    }

    private static void FlushPendingSync(ref ConversationHistory? pending, string path, object syncLock, string kind)
    {
        lock (syncLock)
        {
            if (pending is null)
                return;

            try
            {
                var json = JsonSerializer.Serialize(pending, CompactJson);
                File.WriteAllText(path, json);
                DevLog.WriteLine("[History] Emergency flush {0} history: {1} messages", kind, pending.Messages.Count);
            }
            catch (Exception ex)
            {
                DevLog.WriteLine("[History] Emergency flush {0} failed: {1}", kind, ex.Message);
            }
            finally
            {
                pending = null;
            }
        }
    }

    private static bool WaitSync(Task task, string operation)
    {
        try
        {
            if (!task.Wait(SyncWrapperTimeout))
            {
                DevLog.WriteLine("[History] Sync wrapper timeout in {0}", operation);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("[History] Sync wrapper failed in {0}: {1}", operation, ex.GetBaseException().Message);
            return false;
        }
    }
}
