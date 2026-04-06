using TsukiAI.Core.Models;

namespace TsukiAI.Core.Services;

public interface IConversationSummaryStore
{
    Task<bool> EnsureReadyAsync(CancellationToken ct = default);

    Task AddSummaryAsync(
        string sessionId,
        string source,
        string summary,
        int messageCount,
        CancellationToken ct = default);

    Task<IReadOnlyList<ConversationSummaryMemory>> SearchSummariesAsync(
        string query,
        int limit = 3,
        CancellationToken ct = default);
}
