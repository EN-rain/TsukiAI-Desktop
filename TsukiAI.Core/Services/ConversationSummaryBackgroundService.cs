namespace TsukiAI.Core.Services;

public sealed class ConversationSummaryBackgroundService
{
    private readonly IInferenceClient _inferenceClient;
    private readonly IConversationSummaryStore _summaryStore;
    private readonly ISemanticMemoryService _semanticMemory;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public ConversationSummaryBackgroundService(
        IInferenceClient inferenceClient,
        IConversationSummaryStore summaryStore,
        ISemanticMemoryService semanticMemory)
    {
        _inferenceClient = inferenceClient;
        _summaryStore = summaryStore;
        _semanticMemory = semanticMemory;
    }

    public Task TriggerVoiceSummaryIfNeededAsync(string reason, CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            if (!await _runLock.WaitAsync(0, ct))
            {
                DevLog.WriteLine("SummaryJob: skip (already running) reason={0}", reason);
                return;
            }

            try
            {
                var history = ConversationHistoryService.LoadVoiceChatHistory();
                if (history?.Messages == null)
                {
                    DevLog.WriteLine("SummaryJob: skip (no history) reason={0}", reason);
                    return;
                }

                var fullHistory = history.Messages
                    .Select(m => (m.Role, m.Content))
                    .Where(x => !string.IsNullOrWhiteSpace(x.Content))
                    .ToList();

                var estimatedTokens = EstimateTokens(fullHistory);
                var shouldSummarize = fullHistory.Count >= 100 || estimatedTokens >= 8000;
                if (!shouldSummarize)
                {
                    DevLog.WriteLine(
                        "SummaryJob: skip (messages={0}, est_tokens={1}) reason={2}",
                        fullHistory.Count,
                        estimatedTokens,
                        reason);
                    return;
                }

                DevLog.WriteLine(
                    "SummaryJob: start reason={0}, messages={1}, est_tokens={2}",
                    reason,
                    fullHistory.Count,
                    estimatedTokens);
                var summary = await _inferenceClient.SummarizeConversationAsync(fullHistory, ct);
                if (string.IsNullOrWhiteSpace(summary))
                {
                    DevLog.WriteLine("SummaryJob: empty summary");
                    return;
                }

                var sessionId = history.LastUpdated.ToString("yyyyMMddHHmmss");
                await _summaryStore.AddSummaryAsync(sessionId, "voicechat", summary, fullHistory.Count, ct);
                await _semanticMemory.AddMemoryAsync($"Conversation summary ({sessionId}): {summary}", "summary", ct);
                DevLog.WriteLine("SummaryJob: done session={0}", sessionId);
            }
            catch (OperationCanceledException)
            {
                DevLog.WriteLine("SummaryJob: cancelled");
            }
            catch (Exception ex)
            {
                DevLog.WriteLine("SummaryJob: failed: {0}", ex.Message);
            }
            finally
            {
                _runLock.Release();
            }
        }, ct);
    }

    private static int EstimateTokens(IReadOnlyList<(string Role, string Content)> history)
    {
        var chars = 0;
        foreach (var (_, content) in history)
        {
            chars += content.Length + 8;
        }

        return Math.Max(1, chars / 4);
    }
}
