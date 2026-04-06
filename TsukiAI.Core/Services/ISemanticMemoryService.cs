namespace TsukiAI.Core.Services;

public interface ISemanticMemoryService
{
    Task<bool> EnsureReadyAsync(CancellationToken ct = default);
    Task AddMemoryAsync(string text, string source = "voicechat", CancellationToken ct = default);
    Task<IReadOnlyList<SemanticMemoryHit>> SearchAsync(string query, int topK = 5, CancellationToken ct = default);
}

public sealed record SemanticMemoryHit(
    string Id,
    string Text,
    string Source,
    double Distance
);
