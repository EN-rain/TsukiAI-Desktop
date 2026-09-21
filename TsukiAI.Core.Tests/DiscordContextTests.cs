using TsukiAI.VoiceChat.Services;
using Xunit;

namespace TsukiAI.Core.Tests;

public sealed class DiscordContextTests
{
    [Fact]
    public void Selected_context_is_bounded_quoted_and_speaker_attributed()
    {
        var turns = new[] {
            new DiscordContextTurn { Speaker = "current user", Text = "Help with quest" },
            new DiscordContextTurn { Speaker = "another speaker", Text = "I meant the north gate" },
        };
        Assert.True(DiscordContext.TryFormat(turns, out var prompt));
        Assert.Contains("untrusted conversation data", prompt);
        Assert.Contains("\"speaker\":\"current user\"", prompt);
        Assert.Contains("I meant the north gate", prompt);
        Assert.DoesNotContain("Discord ID", prompt);
    }

    [Theory]
    [InlineData("123456789012345678", "Hello")]
    [InlineData("system", "Hello")]
    [InlineData("current user", "")]
    [InlineData("current user", "\n")]
    public void Rejects_untrusted_speaker_metadata_or_noise(string speaker, string text)
    {
        Assert.False(DiscordContext.TryFormat(
            [new DiscordContextTurn { Speaker = speaker, Text = text }], out _));
    }

    [Fact]
    public void Rejects_oversized_turn_or_too_many_turns()
    {
        Assert.False(DiscordContext.TryFormat(
            [new DiscordContextTurn { Speaker = "current user", Text = new string('x', 501) }], out _));
        var many = Enumerable.Range(0, 5).Select(_ =>
            new DiscordContextTurn { Speaker = "current user", Text = "Hello" }).ToList();
        Assert.False(DiscordContext.TryFormat(many, out _));
    }

    [Fact]
    public void Empty_context_is_valid_and_injects_nothing()
    {
        Assert.True(DiscordContext.TryFormat(null, out var prompt));
        Assert.Equal(string.Empty, prompt);
    }
}
