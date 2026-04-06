using System.Collections;
using System.Globalization;
using System.IO;
using TsukiAI.Core.Models;

namespace TsukiAI.VoiceChat.Infrastructure;

public sealed record EnvVarStatus(string Key, bool IsSet, string DisplayValue);

public static class EnvConfiguration
{
    private static readonly string[] KnownKeys =
    {
        "TSUKI_DISCORD_BOT_TOKEN",
        "TSUKI_DISCORD_GUILD_ID",
        "TSUKI_DISCORD_CHANNEL_ID",
        "TSUKI_DISCORD_FOCUSED_USER_ID",
        "TSUKI_ASSEMBLYAI_API_KEY",
        "TSUKI_DEEPL_API_KEY",
        "TSUKI_REMOTE_INFERENCE_URL",
        "TSUKI_REMOTE_INFERENCE_API_KEY",
        "TSUKI_CLOUD_TTS_URL",
        "TSUKI_VOICEVOX_BASE_URL",
        "TSUKI_MODEL_NAME",
        "TSUKI_USE_DEEPL_TRANSLATE",
        "TSUKI_USE_DEEPL_FREE_API",
        "TSUKI_VOICE_RUNTIME_V2",
        "TSUKI_VOICE_API_CONTROLLER",
        "TSUKI_VOICE_BARGE_IN"
    };

    public static AppSettings ApplyToSettings(AppSettings settings)
    {
        var env = LoadEnv();

        var discordGuildId = ParseULong(env, "TSUKI_DISCORD_GUILD_ID", settings.DiscordDefaultGuildId);
        var discordChannelId = ParseULong(env, "TSUKI_DISCORD_CHANNEL_ID", settings.DiscordDefaultChannelId);
        var discordFocusedUserId = ParseULong(env, "TSUKI_DISCORD_FOCUSED_USER_ID", settings.DiscordFocusedUserId);
        var useDeepLTranslate = ParseBool(env, "TSUKI_USE_DEEPL_TRANSLATE", settings.UseDeepLTranslate);
        var useDeepLFreeApi = ParseBool(env, "TSUKI_USE_DEEPL_FREE_API", settings.UseDeepLFreeApi);
        var voiceRuntimeV2Enabled = ParseBool(env, "TSUKI_VOICE_RUNTIME_V2", settings.VoiceRuntimeV2Enabled);
        var voiceApiControllerEnabled = ParseBool(env, "TSUKI_VOICE_API_CONTROLLER", settings.VoiceApiControllerEnabled);
        var voiceBargeInEnabled = ParseBool(env, "TSUKI_VOICE_BARGE_IN", settings.VoiceBargeInEnabled);

        return settings with
        {
            DiscordBotToken = ReadString(env, "TSUKI_DISCORD_BOT_TOKEN", settings.DiscordBotToken),
            DiscordDefaultGuildId = discordGuildId,
            DiscordDefaultChannelId = discordChannelId,
            DiscordFocusedUserId = discordFocusedUserId,
            AssemblyAIApiKey = ReadString(env, "TSUKI_ASSEMBLYAI_API_KEY", settings.AssemblyAIApiKey),
            DeepLApiKey = ReadString(env, "TSUKI_DEEPL_API_KEY", settings.DeepLApiKey),
            RemoteInferenceUrl = ReadString(env, "TSUKI_REMOTE_INFERENCE_URL", settings.RemoteInferenceUrl),
            RemoteInferenceApiKey = ReadString(env, "TSUKI_REMOTE_INFERENCE_API_KEY", settings.RemoteInferenceApiKey),
            CloudTtsUrl = ReadString(env, "TSUKI_CLOUD_TTS_URL", settings.CloudTtsUrl),
            VoicevoxBaseUrl = ReadString(env, "TSUKI_VOICEVOX_BASE_URL", settings.VoicevoxBaseUrl),
            ModelName = ReadString(env, "TSUKI_MODEL_NAME", settings.ModelName),
            UseDeepLTranslate = useDeepLTranslate,
            UseDeepLFreeApi = useDeepLFreeApi,
            VoiceRuntimeV2Enabled = voiceRuntimeV2Enabled,
            VoiceApiControllerEnabled = voiceApiControllerEnabled,
            VoiceBargeInEnabled = voiceBargeInEnabled
        };
    }

    public static IReadOnlyList<EnvVarStatus> GetStatus()
    {
        var env = LoadEnv();
        return KnownKeys
            .Select(key =>
            {
                var value = ReadString(env, key, string.Empty);
                var isSet = !string.IsNullOrWhiteSpace(value);
                return new EnvVarStatus(key, isSet, MaskValue(key, value));
            })
            .ToList();
    }

    private static Dictionary<string, string> LoadEnv()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
        {
            var key = item.Key?.ToString();
            var value = item.Value?.ToString();
            if (!string.IsNullOrWhiteSpace(key) && value is not null)
            {
                map[key] = value;
            }
        }

        var envPath = FindEnvFile();
        if (envPath is null)
        {
            return map;
        }

        foreach (var rawLine in File.ReadAllLines(envPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var idx = line.IndexOf('=');
            if (idx <= 0)
            {
                continue;
            }

            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
            {
                value = value[1..^1];
            }

            if (key.Length > 0)
            {
                map[key] = value;
            }
        }

        return map;
    }

    private static string? FindEnvFile()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.CurrentDirectory, ".env"),
            Path.Combine(AppContext.BaseDirectory, ".env")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, ".env");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string ReadString(IReadOnlyDictionary<string, string> env, string key, string fallback)
    {
        if (env.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        return fallback;
    }

    private static bool ParseBool(IReadOnlyDictionary<string, string> env, string key, bool fallback)
    {
        if (!env.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "y" => true,
            "0" or "false" or "no" or "n" => false,
            _ => fallback
        };
    }

    private static ulong ParseULong(IReadOnlyDictionary<string, string> env, string key, ulong fallback)
    {
        if (!env.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return ulong.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static string MaskValue(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Not set";
        }

        var sensitive = key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
            || key.Contains("KEY", StringComparison.OrdinalIgnoreCase);

        if (!sensitive)
        {
            return value.Length > 64 ? value[..64] + "..." : value;
        }

        if (value.Length <= 8)
        {
            return "********";
        }

        return $"{value[..4]}...{value[^4..]}";
    }
}
