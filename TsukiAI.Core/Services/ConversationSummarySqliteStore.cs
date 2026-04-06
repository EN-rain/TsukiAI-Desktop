using Microsoft.Data.Sqlite;
using TsukiAI.Core.Models;

namespace TsukiAI.Core.Services;

public sealed class ConversationSummarySqliteStore : IConversationSummaryStore
{
    private readonly string _dbPath;

    public ConversationSummarySqliteStore(string baseDir)
    {
        Directory.CreateDirectory(baseDir);
        _dbPath = Path.Combine(baseDir, "memory_summaries.db");
    }

    public async Task<bool> EnsureReadyAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection($"Data Source={_dbPath}");
            await conn.OpenAsync(ct);
            var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                CREATE TABLE IF NOT EXISTS conversation_summaries (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id TEXT NOT NULL,
                    source TEXT NOT NULL,
                    summary TEXT NOT NULL,
                    message_count INTEGER NOT NULL,
                    created_at_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_summaries_created ON conversation_summaries(created_at_utc DESC);
                """;
            await cmd.ExecuteNonQueryAsync(ct);
            DevLog.WriteLine("SummaryStore: ready sqlite={0}", _dbPath);
            return true;
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("SummaryStore: ensure failed: {0}", ex.Message);
            return false;
        }
    }

    public async Task AddSummaryAsync(
        string sessionId,
        string source,
        string summary,
        int messageCount,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return;

        sessionId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId.Trim();
        source = string.IsNullOrWhiteSpace(source) ? "voicechat" : source.Trim();

        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(ct);

        var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO conversation_summaries (session_id, source, summary, message_count, created_at_utc)
            VALUES ($session_id, $source, $summary, $message_count, $created_at_utc);
            """;
        cmd.Parameters.AddWithValue("$session_id", sessionId);
        cmd.Parameters.AddWithValue("$source", source);
        cmd.Parameters.AddWithValue("$summary", summary.Trim());
        cmd.Parameters.AddWithValue("$message_count", messageCount);
        cmd.Parameters.AddWithValue("$created_at_utc", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);

        DevLog.WriteLine("SummaryStore: add ok source={0}, messages={1}", source, messageCount);
    }

    public async Task<IReadOnlyList<ConversationSummaryMemory>> SearchSummariesAsync(
        string query,
        int limit = 3,
        CancellationToken ct = default)
    {
        var results = new List<ConversationSummaryMemory>();
        if (string.IsNullOrWhiteSpace(query))
            return results;

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();

        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(ct);

        var cmd = conn.CreateCommand();
        var lim = Math.Clamp(limit, 1, 10);
        if (terms.Length == 0)
        {
            cmd.CommandText =
                """
                SELECT id, session_id, source, summary, message_count, created_at_utc
                FROM conversation_summaries
                ORDER BY datetime(created_at_utc) DESC
                LIMIT $lim;
                """;
            cmd.Parameters.AddWithValue("$lim", lim);
        }
        else
        {
            var likeClauses = new List<string>();
            for (var i = 0; i < terms.Length; i++)
            {
                var p = $"$q{i}";
                likeClauses.Add($"summary LIKE {p}");
                cmd.Parameters.AddWithValue(p, $"%{terms[i]}%");
            }

            cmd.CommandText =
                $"""
                SELECT id, session_id, source, summary, message_count, created_at_utc
                FROM conversation_summaries
                WHERE {string.Join(" OR ", likeClauses)}
                ORDER BY datetime(created_at_utc) DESC
                LIMIT $lim;
                """;
            cmd.Parameters.AddWithValue("$lim", lim);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt64(0);
            var sessionId = reader.GetString(1);
            var source = reader.GetString(2);
            var summary = reader.GetString(3);
            var messageCount = reader.GetInt32(4);
            var createdAt = DateTime.TryParse(reader.GetString(5), out var dt) ? dt : DateTime.UtcNow;
            results.Add(new ConversationSummaryMemory(id, sessionId, source, summary, messageCount, createdAt));
        }

        DevLog.WriteLine("SummaryStore: search query='{0}' hits={1}", query, results.Count);
        return results;
    }
}
