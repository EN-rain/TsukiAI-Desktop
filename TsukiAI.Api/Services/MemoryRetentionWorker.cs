using TsukiAI.Core.Services;

namespace TsukiAI.Api.Services;

/// <summary>
/// Daily retention purge: deletes semantic memories older than the configured
/// window (TSUKI_MEMORY_RETENTION_DAYS, default 30) so memory "resets" on a
/// rolling basis instead of growing forever.
/// </summary>
public sealed class MemoryRetentionWorker(
    TextChatService textChat,
    ISemanticMemoryService memory,
    ILogger<MemoryRetentionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Memory retention worker started ({Days} days)", textChat.RetentionDays);

        // Run once shortly after startup, then daily.
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await memory.DeleteOlderThanAsync(TimeSpan.FromDays(textChat.RetentionDays), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Memory retention purge failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
