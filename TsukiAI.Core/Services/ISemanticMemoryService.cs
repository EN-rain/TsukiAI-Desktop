namespace TsukiAI.Core.Services;

public interface ISemanticMemoryService
{
    Task<bool> EnsureReadyAsync(CancellationToken ct = default);
    Task AddMemoryAsync(string text, string source = "voicechat", string? userId = null, CancellationToken ct = default);
    Task<IReadOnlyList<SemanticMemoryHit>> SearchAsync(string query, int topK = 5, string? userId = null, CancellationToken ct = default);

    /// <summary>Deletes memories older than the given age (retention policy).</summary>
    Task DeleteOlderThanAsync(TimeSpan age, CancellationToken ct = default);
}

public sealed record SemanticMemoryHit(
    string Id,
    string Text,
    string Source,
    double Distance
);
