using System.Text.RegularExpressions;
using TsukiAI.Core.Models;

namespace TsukiAI.Core.Services;

public static class ResponsePostProcessor
{
    private static readonly Regex MultiWhitespace = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly string[] BlockedPatterns =
    [
        "as an ai",
        "i cannot",
        "i can't assist with that request",
        "language model"
    ];

    public static AiReply CleanAndValidate(
        AiReply reply,
        PromptIntent intent,
        GenerationTuningSettings tuning)
    {
        var clean = CleanupText(reply.Reply);
        clean = FilterPatterns(clean);
        clean = EnforceLength(clean, intent, tuning.MaxReplyChars);

        if (string.IsNullOrWhiteSpace(clean))
            clean = "Okay, let's try that again.";

        return reply with { Reply = clean };
    }

    public static string CleanupText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var clean = text.Trim();
        clean = clean.Replace("```", string.Empty, StringComparison.Ordinal);
        if (clean.StartsWith("assistant:", StringComparison.OrdinalIgnoreCase))
            clean = clean[10..].Trim();

        clean = MultiWhitespace.Replace(clean, " ");
        return clean;
    }

    private static string FilterPatterns(string text)
    {
        var lower = text.ToLowerInvariant();
        foreach (var pattern in BlockedPatterns)
        {
            if (lower.Contains(pattern, StringComparison.Ordinal))
                return "Fair point. Let me keep it simple - tell me what you want to do next.";
        }
        return text;
    }

    private static string EnforceLength(string text, PromptIntent intent, int maxChars)
    {
        var hardMax = Math.Clamp(maxChars, 80, 2000);
        if (text.Length <= hardMax)
            return text;

        if (intent is PromptIntent.Question or PromptIntent.EmotionalSupport)
        {
            var cut = text[..hardMax];
            var dot = cut.LastIndexOfAny(['.', '!', '?']);
            if (dot > 40)
                return cut[..(dot + 1)].Trim();
        }

        return text[..hardMax].TrimEnd() + "...";
    }
}
