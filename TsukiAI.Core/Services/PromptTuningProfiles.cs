using TsukiAI.Core.Models;

namespace TsukiAI.Core.Services;

public static class PromptTuningProfiles
{
    public static GenerationTuningSettings ForIntent(PromptIntent intent, GenerationTuningSettings baseSettings)
    {
        var b = (baseSettings ?? GenerationTuningSettings.Default).Clamp();
        return intent switch
        {
            PromptIntent.Question => b with
            {
                Temperature = Math.Clamp(b.Temperature - 0.15f, 0.15f, 1.2f),
                MaxTokens = Math.Clamp(Math.Max(b.MaxTokens, 120), 64, 256)
            },
            PromptIntent.EmotionalSupport => b with
            {
                Temperature = Math.Clamp(b.Temperature + 0.05f, 0.2f, 1.25f),
                PresencePenalty = Math.Clamp(b.PresencePenalty + 0.05f, -2f, 2f),
                MaxTokens = Math.Clamp(Math.Max(b.MaxTokens, 100), 64, 220)
            },
            PromptIntent.Command => b with
            {
                Temperature = Math.Clamp(b.Temperature - 0.2f, 0.1f, 1.0f),
                MaxTokens = Math.Clamp(Math.Min(b.MaxTokens, 80), 32, 120)
            },
            _ => b with
            {
                MaxTokens = Math.Clamp(b.MaxTokens, 48, 160)
            }
        };
    }
}
