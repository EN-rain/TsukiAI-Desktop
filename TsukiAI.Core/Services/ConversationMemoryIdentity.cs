using System.Security.Cryptography;
using System.Text;

namespace TsukiAI.Core.Services;

/// <summary>
/// Stable, provider-safe memory containers. Discord users are isolated from
/// one another, while the single desktop user gets one private desktop store.
/// </summary>
public static class ConversationMemoryIdentity
{
    public const string DiscordDefaultContainer = "discord-default";
    public const string DesktopDefaultContainer = "desktop-user-default";

    public static string ForDiscordUser(string? userId)
    {
        var normalized = (userId ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? DiscordDefaultContainer
            : $"discord-user-{ShortHash(normalized)}";
    }

    public static string ForDesktopUser(string? _)
    {
        return DesktopDefaultContainer;
    }

    public static string SanitizeContainerTag(string? value, string fallback)
    {
        var source = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var safe = new string(source
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            .ToArray());

        if (safe.Length == 0)
            safe = fallback;

        return safe.Length <= 100 ? safe : safe[..100];
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..24].ToLowerInvariant();
    }
}
