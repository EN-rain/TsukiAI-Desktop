using System.Text.RegularExpressions;
using TsukiAI.Core.Services;
using Xunit;

namespace TsukiAI.Core.Tests;

public sealed class ConversationMemoryIdentityTests
{
    [Fact]
    public void DiscordUser_is_stable_and_isolated_per_user()
    {
        var first = ConversationMemoryIdentity.ForDiscordUser("1234567890");
        var firstAgain = ConversationMemoryIdentity.ForDiscordUser("1234567890");
        var second = ConversationMemoryIdentity.ForDiscordUser("9876543210");

        Assert.Equal(first, firstAgain);
        Assert.NotEqual(first, second);
        Assert.Matches(new Regex(@"^discord-user-[a-f0-9]{24}$"), first);
    }

    [Fact]
    public void DesktopUser_uses_one_private_desktop_container()
    {
        Assert.Equal(
            ConversationMemoryIdentity.ForDesktopUser("web"),
            ConversationMemoryIdentity.ForDesktopUser("local-mic"));
        Assert.Equal("desktop-user-default", ConversationMemoryIdentity.ForDesktopUser("web"));
    }
}
