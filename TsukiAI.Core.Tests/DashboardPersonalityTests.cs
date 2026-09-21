using System;
using System.Text;
using TsukiAI.Core.Services;
using Xunit;

namespace TsukiAI.Core.Tests;

[CollectionDefinition("dashboard-personality-environment", DisableParallelization = true)]
public sealed class DashboardPersonalityEnvironmentCollection;

[Collection("dashboard-personality-environment")]
public sealed class DashboardPersonalityTests
{
    [Fact]
    public void Dashboard_personality_applies_to_system_prompt_and_reset_preserves_base_persona()
    {
        var previous = Environment.GetEnvironmentVariable("TSUKI_PERSONALITY_INJECTION_B64");
        try
        {
            var instructions = "Use a quiet voice.\nRemember this is user-authored operator style: 月.";
            Environment.SetEnvironmentVariable("TSUKI_PERSONALITY_INJECTION_B64",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(instructions)));
            var prompt = new PromptBuilder().BuildCompanionChatSystemPrompt("Tsuki");
            Assert.Contains("You are Tsuki", prompt);
            Assert.Contains("Modelfile style anchor:", prompt);
            Assert.Contains("Dashboard personality instructions", prompt);
            Assert.Contains(instructions, prompt);
            Environment.SetEnvironmentVariable("TSUKI_PERSONALITY_INJECTION_B64", "");
            var defaultPrompt = new PromptBuilder().BuildCompanionChatSystemPrompt("Tsuki");
            Assert.Contains("You are Tsuki", defaultPrompt);
            Assert.DoesNotContain(instructions, defaultPrompt);
            Assert.DoesNotContain("Dashboard personality instructions", defaultPrompt);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TSUKI_PERSONALITY_INJECTION_B64", previous);
        }
    }

    [Fact]
    public void Invalid_configured_personality_does_not_replace_the_base_prompt()
    {
        var previous = Environment.GetEnvironmentVariable("TSUKI_PERSONALITY_INJECTION_B64");
        try
        {
            foreach (var invalid in new[] {
                "not base64!",
                Convert.ToBase64String(new byte[] { 0xff, 0xfe }),
                Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('x', 4001))),
                Convert.ToBase64String(Encoding.UTF8.GetBytes("bad\0data")),
            })
            {
                Environment.SetEnvironmentVariable("TSUKI_PERSONALITY_INJECTION_B64", invalid);
                Assert.Equal("", PromptBuilder.ReadDashboardPersonality());
                var prompt = new PromptBuilder().BuildCompanionChatSystemPrompt("Tsuki");
                Assert.Contains("You are Tsuki", prompt);
                Assert.DoesNotContain("Dashboard personality instructions", prompt);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("TSUKI_PERSONALITY_INJECTION_B64", previous);
        }
    }
}
