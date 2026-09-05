namespace TsukiAI.Core.Models;

public sealed record MemoryEntry(
    string Id,
    string Type,
    string Content,
    string[] Tags,
    int Importance,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    double DecayScore,
    string Source
)
{
    /// <summary>Only use in GetRelevant when >= 0.8. Seen 2–3 times + confidence => save.</summary>
    public double Confidence { get; init; } = 1.0;
    public int SeenCount { get; init; } = 1;
}

