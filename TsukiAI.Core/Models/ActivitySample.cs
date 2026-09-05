namespace TsukiAI.Core.Models;

// Dummy class for backward compatibility - not used in voice chat
public sealed record ActivitySample(
    DateTimeOffset Timestamp,
    string ProcessName,
    string WindowTitle,
    int IdleSeconds,
    string? ScreenshotPath
);
