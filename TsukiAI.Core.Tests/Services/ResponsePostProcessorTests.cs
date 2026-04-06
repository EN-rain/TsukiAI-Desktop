using FluentAssertions;
using FsCheck;
using FsCheck.Xunit;
using TsukiAI.Core.Models;
using TsukiAI.Core.Services;
using Xunit;

namespace TsukiAI.Core.Tests.Services;

public class ResponsePostProcessorTests
{
    [Fact]
    public void CleanAndValidate_ValidReply_ReturnsCleanedReply()
    {
        var reply = new AiReply("Hello! How are you?", "happy");
        var tuning = GenerationTuningSettings.Default;

        var result = ResponsePostProcessor.CleanAndValidate(reply, PromptIntent.CasualChat, tuning);

        result.Reply.Should().Be("Hello! How are you?");
        result.Emotion.Should().Be("happy");
    }

    [Fact]
    public void CleanAndValidate_EmptyReply_ReturnsFallback()
    {
        var reply = new AiReply("", "neutral");
        var tuning = GenerationTuningSettings.Default;

        var result = ResponsePostProcessor.CleanAndValidate(reply, PromptIntent.CasualChat, tuning);

        result.Reply.Should().NotBeNullOrWhiteSpace();
        result.Reply.Should().Be("Okay, let's try that again.");
    }

    [Fact]
    public void CleanAndValidate_WhitespaceReply_ReturnsFallback()
    {
        var reply = new AiReply("   \n\t  ", "neutral");
        var tuning = GenerationTuningSettings.Default;

        var result = ResponsePostProcessor.CleanAndValidate(reply, PromptIntent.CasualChat, tuning);

        result.Reply.Should().NotBeNullOrWhiteSpace();
        result.Reply.Should().NotContain("\n");
        result.Reply.Should().NotContain("\t");
    }

    [Theory]
    [InlineData("Hello! This is a test.", "Hello! This is a test.")]
    [InlineData("  Hello!  ", "Hello!")]
    [InlineData("Hello!\n\nExtra newlines", "Hello! Extra newlines")]
    [InlineData("Hello!   Multiple   spaces", "Hello! Multiple spaces")]
    public void CleanAndValidate_VariousInputs_CleansCorrectly(string input, string expected)
    {
        var reply = new AiReply(input, "neutral");
        var tuning = GenerationTuningSettings.Default;

        var result = ResponsePostProcessor.CleanAndValidate(reply, PromptIntent.CasualChat, tuning);

        result.Reply.Should().Be(expected);
    }

    [Fact]
    public void CleanAndValidate_TooLongReply_TruncatesCorrectly()
    {
        var longText = new string('a', 500); // 500 characters
        var reply = new AiReply(longText, "neutral");
        var tuning = GenerationTuningSettings.Default with { MaxReplyChars = 100 };

        var result = ResponsePostProcessor.CleanAndValidate(reply, PromptIntent.CasualChat, tuning);

        result.Reply.Length.Should().BeLessThanOrEqualTo(103);
    }

    [Theory]
    [InlineData("happy")]
    [InlineData("sad")]
    [InlineData("neutral")]
    [InlineData("excited")]
    [InlineData("angry")]
    public void CleanAndValidate_ValidEmotions_PreservesEmotion(string emotion)
    {
        var reply = new AiReply("Test text", emotion);
        var tuning = GenerationTuningSettings.Default;

        var result = ResponsePostProcessor.CleanAndValidate(reply, PromptIntent.CasualChat, tuning);

        result.Emotion.Should().Be(emotion);
    }
    
    [Property]
    public void CleanupText_RemovesMarkdownArtifacts_ForAnyInput(string input)
    {
        var cleaned = ResponsePostProcessor.CleanupText(input);
        cleaned.Should().NotContain("```");
        cleaned.StartsWith("assistant:", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Property]
    public void CleanAndValidate_EnforcesMaxLength_ForAnyInput(NonNull<string> input)
    {
        var tuning = GenerationTuningSettings.Default with { MaxReplyChars = 120 };
        var result = ResponsePostProcessor.CleanAndValidate(
            new AiReply(input.Get, "neutral"),
            PromptIntent.Command,
            tuning);

        result.Reply.Length.Should().BeLessThanOrEqualTo(123);
    }

    [Property]
    public void CleanAndValidate_FallbackIsNonEmpty_ForInvalidInput(string input)
    {
        var invalid = string.IsNullOrWhiteSpace(input) ? input : "   ";
        var result = ResponsePostProcessor.CleanAndValidate(
            new AiReply(invalid, "neutral"),
            PromptIntent.CasualChat,
            GenerationTuningSettings.Default);

        result.Reply.Should().NotBeNullOrWhiteSpace();
    }
}
