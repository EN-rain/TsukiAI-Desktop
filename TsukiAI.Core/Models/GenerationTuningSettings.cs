namespace TsukiAI.Core.Models;

public enum PromptIntent
{
    CasualChat = 0,
    Question = 1,
    EmotionalSupport = 2,
    Command = 3
}

public sealed record GenerationTuningSettings(
    int MaxTokens = 80,
    float Temperature = 0.7f,
    float TopP = 0.9f,
    int TopK = 40,
    float RepeatPenalty = 1.08f,
    float PresencePenalty = 0.0f,
    float FrequencyPenalty = 0.0f,
    int MaxReplyChars = 360
)
{
    public static GenerationTuningSettings Default => new();

    public GenerationTuningSettings Clamp()
    {
        return this with
        {
            MaxTokens = Math.Clamp(MaxTokens, 16, 1024),
            Temperature = Math.Clamp(Temperature, 0.0f, 2.0f),
            TopP = Math.Clamp(TopP, 0.1f, 1.0f),
            TopK = Math.Clamp(TopK, 1, 200),
            RepeatPenalty = Math.Clamp(RepeatPenalty, 0.8f, 2.0f),
            PresencePenalty = Math.Clamp(PresencePenalty, -2.0f, 2.0f),
            FrequencyPenalty = Math.Clamp(FrequencyPenalty, -2.0f, 2.0f),
            MaxReplyChars = Math.Clamp(MaxReplyChars, 80, 2000)
        };
    }
}
