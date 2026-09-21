using System.Text.RegularExpressions;

namespace TsukiAI.VoiceChat.Services;

/// <summary>
/// Plan every part of an utterance for a small TTS model. A per-request limit
/// is not permission to truncate the last words or an entire sentence.
/// </summary>
public static class TtsTextChunker
{
    public const int DefaultMaxChars = 140;

    public static IReadOnlyList<string> Split(string? text, int maxChars = DefaultMaxChars)
    {
        if (maxChars < 4)
            throw new ArgumentOutOfRangeException(nameof(maxChars));

        var value = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        if (value.Length == 0)
            return [];

        var chunks = new List<string>();
        foreach (var sentence in TtsSentenceSplitter.Split(value))
        {
            var remaining = sentence;
            while (remaining.Length > maxChars)
            {
                var cut = remaining.LastIndexOf(' ', maxChars - 1);
                if (cut < maxChars / 3)
                    cut = maxChars;
                if (cut < remaining.Length && char.IsHighSurrogate(remaining[cut - 1]))
                    cut--;
                if (cut < 1)
                    throw new InvalidDataException("Cannot split this TTS sentence safely.");

                var part = remaining[..cut].Trim();
                if (part.Length > 0)
                    chunks.Add(part);
                remaining = remaining[cut..].TrimStart();
            }
            if (remaining.Length > 0)
                chunks.Add(remaining);
        }
        return chunks;
    }
}
