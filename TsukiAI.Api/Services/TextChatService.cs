using System.Collections.Concurrent;
using System.Text.Json;
using TsukiAI.Core.Models;
using TsukiAI.Core.Services;

namespace TsukiAI.Api.Services;

/// <summary>
/// Discord text chat brain: fully per-user memory.
/// - Own conversation history per Discord user id, persisted to the data dir.
/// - Semantic recall scoped to that user only (Chroma user_id filter) — user 1's
///   memories are never visible in user 2's conversations.
/// - The speaker's display name is injected into the prompt and stored with the
///   memory, so Tsuki can address people by name.
/// - Retention: history and memories older than RetentionDays are pruned.
/// Deliberately independent of the voice pipeline (no TTS, no shared history).
/// </summary>
public sealed class TextChatService
{
    private sealed record Turn(string Name, string User, string Assistant, DateTimeOffset At);

    private class UserHistory
    {
        public List<Turn> Turns { get; set; } = [];
    }

    private static readonly JsonSerializerOptions FileJsonOptions = new() { WriteIndented = false };

    private readonly IInferenceClient _llm;
    private readonly ISemanticMemoryService _memory;
    private readonly AppSettings _settings;
    private readonly PromptBuilder _promptBuilder = new();
    private readonly ConcurrentDictionary<string, UserHistory> _histories = new();
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly int _retentionDays;
    private readonly int _maxTurns = 60;

    public int RetentionDays => _retentionDays;

    public TextChatService(IInferenceClient llm, ISemanticMemoryService memory, AppSettings settings)
    {
        _llm = llm;
        _memory = memory;
        _settings = settings;
        _retentionDays = Math.Max(1, GetIntEnv("TSUKI_MEMORY_RETENTION_DAYS", 30));
    }

    public async Task<string> ReplyAsync(string userId, string displayName, string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var name = string.IsNullOrWhiteSpace(displayName) ? "someone" : displayName.Trim();

        await _turnGate.WaitAsync(ct);
        try
        {
            var history = LoadHistory(userId);

            // Semantic recall: always on for text chat, scoped to this user only.
            List<SemanticMemoryHit> memories = [];
            try
            {
                memories = (await _memory.SearchAsync(text, topK: 5, userId, ct)).ToList();
            }
            catch (Exception ex)
            {
                DevLog.WriteLine("TextChat[{0}]: memory search failed: {1}", userId, ex.Message);
            }

            var memoryContext = memories.Count > 0
                ? $"\nThings you remember about {name} from past conversations:\n" +
                  string.Join("\n", memories.Select(m => $"- {m.Text}"))
                : string.Empty;

            var systemInstructions =
                $"\nThe person you are talking to right now is {name}. Address them by name when it feels natural." +
                memoryContext;

            var llmHistory = history.Turns
                .TakeLast(30)
                .SelectMany(t => new (string role, string content)[]
                {
                    ("user", $"{t.Name}: {t.User}"),
                    ("assistant", t.Assistant)
                })
                .ToList();

            var reply = await _llm.ChatWithEmotionAsync(
                $"{name}: {text}",
                personaName: "Tsuki",
                history: llmHistory,
                ct: ct,
                systemInstructions: systemInstructions);

            var replyText = reply.Reply.Trim();
            if (string.IsNullOrWhiteSpace(replyText))
                replyText = "...";

            var now = DateTimeOffset.UtcNow;
            history.Turns.RemoveAll(t => t.At < now.AddDays(-_retentionDays));
            history.Turns.Add(new Turn(name, text, replyText, now));
            while (history.Turns.Count > _maxTurns)
                history.Turns.RemoveAt(0);
            SaveHistory(userId, history);

            // Long-term memory write, scoped to this user and tagged with their name.
            try
            {
                await _memory.AddMemoryAsync(
                    $"{name} said: \"{text}\" — Tsuki replied: \"{replyText}\"",
                    source: "discord-text",
                    userId,
                    ct);
            }
            catch (Exception ex)
            {
                DevLog.WriteLine("TextChat[{0}]: memory write failed: {1}", userId, ex.Message);
            }

            DevLog.WriteLine("TextChat[{0}] ({1}): turn ok, memories={2}, history={3}",
                userId, name, memories.Count, history.Turns.Count);
            return replyText;
        }
        finally
        {
            _turnGate.Release();
        }
    }

    private UserHistory LoadHistory(string userId)
    {
        var history = _histories.GetOrAdd(userId, _ =>
        {
            try
            {
                var path = HistoryPath(userId);
                if (File.Exists(path))
                {
                    var loaded = JsonSerializer.Deserialize<UserHistory>(File.ReadAllText(path), FileJsonOptions);
                    if (loaded is not null)
                    {
                        loaded.Turns.RemoveAll(t => t.At < DateTimeOffset.UtcNow.AddDays(-_retentionDays));
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                DevLog.WriteLine("TextChat[{0}]: history load failed: {1}", userId, ex.Message);
            }
            return new UserHistory();
        });

        // Drop memories past the retention window even if the file was cached.
        history.Turns.RemoveAll(t => t.At < DateTimeOffset.UtcNow.AddDays(-_retentionDays));
        return history;
    }

    private void SaveHistory(string userId, UserHistory history)
    {
        try
        {
            var path = HistoryPath(userId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(history, FileJsonOptions));
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("TextChat[{0}]: history save failed: {1}", userId, ex.Message);
        }
    }

    private static string HistoryPath(string userId)
    {
        var safe = new string(userId.Where(char.IsLetterOrDigit).ToArray());
        return Path.Combine(SettingsService.GetBaseDir(), $"text_history_{safe}.json");
    }

    private static int GetIntEnv(string key, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        return int.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }
}
