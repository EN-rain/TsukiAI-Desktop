using FluentAssertions;
using FsCheck.Xunit;
using TsukiAI.Core.Models;
using TsukiAI.Core.Services;
using Xunit;

namespace TsukiAI.Core.Tests.Services;

public class PromptBuilderTests
{
    private readonly PromptBuilder _promptBuilder = new();

    [Theory]
    [InlineData("What is the weather today?", PromptIntent.Question)]
    [InlineData("How do I fix this error?", PromptIntent.Question)]
    [InlineData("Why is the sky blue?", PromptIntent.Question)]
    [InlineData("open settings", PromptIntent.Command)]
    [InlineData("run a test", PromptIntent.Command)]
    [InlineData("I feel sad", PromptIntent.EmotionalSupport)]
    [InlineData("I'm anxious about this", PromptIntent.EmotionalSupport)]
    [InlineData("This is great!", PromptIntent.CasualChat)]
    [InlineData("Tell me a story", PromptIntent.CasualChat)]
    public void DetectIntent_VariousInputs_ReturnsCorrectIntent(string input, PromptIntent expected)
    {
        // Act
        var result = _promptBuilder.DetectIntent(input);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void DetectIntent_EmptyString_ReturnsCasualChat()
    {
        // Act
        var result = _promptBuilder.DetectIntent("");

        // Assert
        result.Should().Be(PromptIntent.CasualChat);
    }

    [Fact]
    public void DetectIntent_NullString_ReturnsCasualChat()
    {
        // Act
        var result = _promptBuilder.DetectIntent(null);

        // Assert
        result.Should().Be(PromptIntent.CasualChat);
    }

    [Theory]
    [InlineData("what", PromptIntent.Question)]
    [InlineData("how", PromptIntent.Question)]
    [InlineData("why", PromptIntent.Question)]
    public void DetectIntent_QuestionWords_ReturnsQuestion(string questionWord, PromptIntent expected)
    {
        // Arrange
        var input = $"{questionWord} is this happening?";

        // Act
        var result = _promptBuilder.DetectIntent(input);

        // Assert
        result.Should().Be(expected);
    }
    
    [Theory]
    [InlineData("open settings now")]
    [InlineData("set this to dark mode")]
    [InlineData("run this command")]
    public void DetectIntent_CommandDetection_ReturnsCommand(string input)
    {
        var result = _promptBuilder.DetectIntent(input);
        result.Should().Be(PromptIntent.Command);
    }

    [Theory]
    [InlineData("I feel sad", PromptIntent.EmotionalSupport)]
    [InlineData("I'm anxious", PromptIntent.EmotionalSupport)]
    [InlineData("I'm stressed", PromptIntent.EmotionalSupport)]
    [InlineData("I feel lonely", PromptIntent.EmotionalSupport)]
    public void DetectIntent_EmotionalKeywords_ReturnsEmotionalSupport(string input, PromptIntent expected)
    {
        // Act
        var result = _promptBuilder.DetectIntent(input);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void BuildActivitySummaryOneSentencePrompt_ReturnsValidPrompt()
    {
        // Act
        var result = _promptBuilder.BuildActivitySummaryOneSentencePrompt();

        // Assert
        result.Should().NotBeNullOrWhiteSpace();
        result.Should().Contain("1 sentence");
    }

    [Fact]
    public void BuildCompanionChatSystemPrompt_WithPersonaName_IncludesName()
    {
        // Act
        var result = _promptBuilder.BuildCompanionChatSystemPrompt("TestBot");

        // Assert
        result.Should().Contain("TestBot");
    }

    [Fact]
    public void BuildCompanionChatSystemPrompt_WithCustomInstructions_ReturnsCustomInstructions()
    {
        var customInstructions = "You are a helpful assistant.";

        var result = _promptBuilder.BuildCompanionChatSystemPrompt("Tsuki", customInstructions: customInstructions);

        result.Should().Be(customInstructions);
    }

    [Fact]
    public void BuildCompanionChatSystemPrompt_ContainsTemplateAndIntentGuidance()
    {
        var result = _promptBuilder.BuildCompanionChatSystemPrompt("Tsuki", intent: PromptIntent.Question);

        result.Should().Contain("You are Tsuki");
        result.Should().Contain("Intent guidance:");
        result.Should().Contain("Style variation:");
    }

    [Property]
    public void DetectIntent_AlwaysReturnsValidEnumValue(string input)
    {
        var result = _promptBuilder.DetectIntent(input);
        Enum.IsDefined(typeof(PromptIntent), result).Should().BeTrue();
    }

    [Property]
    public void BuildCompanionChatSystemPrompt_NeverReturnsEmpty_ForValidPersona(string persona)
    {
        var personaName = (persona ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(personaName))
        {
            personaName = "Tsuki";
        }

        var prompt = _promptBuilder.BuildCompanionChatSystemPrompt(personaName);
        prompt.Should().NotBeNullOrWhiteSpace();
    }
}
