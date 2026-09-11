namespace TsukiAI.Core.Services;

/// <summary>Memory-disabled fallback used by clients that still require the service contract.</summary>
public sealed class NoopSemanticMemoryService : ISemanticMemoryService
{
    public Task<bool> EnsureReadyAsync(CancellationToken ct = default) => Task.FromResult(false);

    public Task AddMemoryAsync(string text, string source = "voicechat", string? userId = null, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<SemanticMemoryHit>> SearchAsync(string query, int topK = 5, string? userId = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SemanticMemoryHit>>([]);

    public Task DeleteOlderThanAsync(TimeSpan age, CancellationToken ct = default)
        => Task.CompletedTask;
}
