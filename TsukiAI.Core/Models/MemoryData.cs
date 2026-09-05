namespace TsukiAI.Core.Models;

public sealed class MemoryData
{
    public int Version { get; set; } = 1;
    public MemoryMeta Meta { get; set; } = new();
    public List<MemoryEntry> Entries { get; set; } = [];
}

public sealed class MemoryMeta
{
    public DateTimeOffset? LastLearningAt { get; set; }
    public DateTimeOffset? LastWeeklyLearningAt { get; set; }
    public DateTimeOffset? LastDecayAt { get; set; }
}

