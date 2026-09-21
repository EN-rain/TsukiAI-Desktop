using System.Text.RegularExpressions;
using TsukiAI.VoiceChat.Services;
using Xunit;

namespace TsukiAI.Core.Tests;

public sealed class TtsTextChunkerTests
{
    [Fact]
    public void Keeps_all_words_in_order_without_silently_truncating_long_replies()
    {
        var input = string.Join(" ", Enumerable.Range(0, 165)
            .Select(index => $"word{index}")) + ". A final sentence must also be spoken!";
        var parts = TtsTextChunker.Split(input);

        Assert.True(parts.Count > 2);
        Assert.All(parts, part => Assert.InRange(part.Length, 1, 140));
        Assert.Equal(input, string.Join(" ", parts));
        Assert.EndsWith("spoken!", parts[^1]);
    }

    [Fact]
    public void Retains_parentheticals_unicode_and_long_unbroken_words()
    {
        var text = "She said (do not skip these words). " +
            new string('あ', 185) + "。 Final line.";
        var parts = TtsTextChunker.Split(text);
        Assert.All(parts, part => Assert.InRange(part.Length, 1, 140));
        Assert.Contains("(do not skip these words)", string.Join(" ", parts));
        Assert.Equal(
            Regex.Replace(text, @"\s+", ""),
            Regex.Replace(string.Join("", parts), @"\s+", ""));
    }

    [Fact]
    public void Empty_text_stays_empty_and_invalid_limit_is_rejected()
    {
        Assert.Empty(TtsTextChunker.Split(" \t "));
        Assert.Throws<ArgumentOutOfRangeException>(() => TtsTextChunker.Split("hello", 0));
    }
}
