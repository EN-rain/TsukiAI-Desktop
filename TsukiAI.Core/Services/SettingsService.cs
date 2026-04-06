using System.IO;
using System.Text.Json;
using TsukiAI.Core.Models;

namespace TsukiAI.Core.Services;

public static class SettingsService
{
    private const string FileName = "settings.json";

    public static async Task<string> GetBaseDirAsync()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TsukiAI"
        );
        // Directory.CreateDirectory is fast and synchronous - no need for Task.Run
        Directory.CreateDirectory(baseDir);
        return await Task.FromResult(baseDir);
    }
    
    public static string GetBaseDir()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TsukiAI"
        );
        Directory.CreateDirectory(baseDir);
        return baseDir;
    }

    public static string GetSettingsPath()
    {
        return Path.Combine(GetBaseDir(), FileName);
    }

    public static AppSettings Load()
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path))
                return AppSettings.Default;

            var json = File.ReadAllText(path);
            var loadedSettings = JsonSerializer.Deserialize<AppSettings>(json);
            
            // If deserialization failed, return defaults
            if (loadedSettings == null)
                return AppSettings.Default;
            
            // Merge with defaults to ensure new properties have correct default values
            // This handles the case where old settings files don't have new properties
            var mergedSettings = loadedSettings with
            {
                InferenceTimeoutSeconds = loadedSettings.InferenceTimeoutSeconds == 0 ? 60 : loadedSettings.InferenceTimeoutSeconds,
                ModelLoadTimeoutSeconds = loadedSettings.ModelLoadTimeoutSeconds == 0 ? 120 : loadedSettings.ModelLoadTimeoutSeconds,
                HealthCheckTimeoutSeconds = loadedSettings.HealthCheckTimeoutSeconds == 0 ? 10 : loadedSettings.HealthCheckTimeoutSeconds,
                MaxInferenceRetries = loadedSettings.MaxInferenceRetries == 0 ? 3 : loadedSettings.MaxInferenceRetries,
                GenerationMaxTokens = loadedSettings.GenerationMaxTokens == 0 ? 80 : loadedSettings.GenerationMaxTokens,
                GenerationTopK = loadedSettings.GenerationTopK == 0 ? 40 : loadedSettings.GenerationTopK,
                GenerationMaxReplyChars = loadedSettings.GenerationMaxReplyChars == 0 ? 360 : loadedSettings.GenerationMaxReplyChars,
                // Migrate old "All Users" (0) default to new "Auto Focus" (ulong.MaxValue) default
                DiscordFocusedUserId = loadedSettings.DiscordFocusedUserId == 0 ? ulong.MaxValue : loadedSettings.DiscordFocusedUserId,
                VrChatOscHost = string.IsNullOrWhiteSpace(loadedSettings.VrChatOscHost) ? "127.0.0.1" : loadedSettings.VrChatOscHost,
                VrChatOscInputPort = loadedSettings.VrChatOscInputPort == 0 ? 9000 : loadedSettings.VrChatOscInputPort,
                VrChatOscOutputPort = loadedSettings.VrChatOscOutputPort == 0 ? 9001 : loadedSettings.VrChatOscOutputPort
            };
            
            // Migrate old single API key to provider-specific keys
            return MigrateApiKeys(mergedSettings);
        }
        catch
        {
            return AppSettings.Default;
        }
    }

    public static async Task<AppSettings> LoadAsync()
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path))
                return AppSettings.Default;

            var json = await File.ReadAllTextAsync(path);
            var loadedSettings = JsonSerializer.Deserialize<AppSettings>(json);
            
            // If deserialization failed, return defaults
            if (loadedSettings == null)
                return AppSettings.Default;
            
            // Merge with defaults to ensure new properties have correct default values
            // This handles the case where old settings files don't have new properties
            return loadedSettings with
            {
                InferenceTimeoutSeconds = loadedSettings.InferenceTimeoutSeconds == 0 ? 60 : loadedSettings.InferenceTimeoutSeconds,
                ModelLoadTimeoutSeconds = loadedSettings.ModelLoadTimeoutSeconds == 0 ? 120 : loadedSettings.ModelLoadTimeoutSeconds,
                HealthCheckTimeoutSeconds = loadedSettings.HealthCheckTimeoutSeconds == 0 ? 10 : loadedSettings.HealthCheckTimeoutSeconds,
                MaxInferenceRetries = loadedSettings.MaxInferenceRetries == 0 ? 3 : loadedSettings.MaxInferenceRetries,
                GenerationMaxTokens = loadedSettings.GenerationMaxTokens == 0 ? 80 : loadedSettings.GenerationMaxTokens,
                GenerationTopK = loadedSettings.GenerationTopK == 0 ? 40 : loadedSettings.GenerationTopK,
                GenerationMaxReplyChars = loadedSettings.GenerationMaxReplyChars == 0 ? 360 : loadedSettings.GenerationMaxReplyChars,
                // Migrate old "All Users" (0) default to new "Auto Focus" (ulong.MaxValue) default
                DiscordFocusedUserId = loadedSettings.DiscordFocusedUserId == 0 ? ulong.MaxValue : loadedSettings.DiscordFocusedUserId,
                VrChatOscHost = string.IsNullOrWhiteSpace(loadedSettings.VrChatOscHost) ? "127.0.0.1" : loadedSettings.VrChatOscHost,
                VrChatOscInputPort = loadedSettings.VrChatOscInputPort == 0 ? 9000 : loadedSettings.VrChatOscInputPort,
                VrChatOscOutputPort = loadedSettings.VrChatOscOutputPort == 0 ? 9001 : loadedSettings.VrChatOscOutputPort
            };
        }
        catch
        {
            return AppSettings.Default;
        }
    }

    public static async Task SaveAsync(AppSettings settings)
    {
        settings ??= AppSettings.Default;
        var path = GetSettingsPath();
        
        // Create backup of existing settings before overwriting
        if (File.Exists(path))
        {
            var backupPath = path + ".bak";
            try
            {
                await Task.Run(() => File.Copy(path, backupPath, overwrite: true));
            }
            catch
            {
                // Ignore backup failures
            }
        }
        
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);
    }
    
    public static void Save(AppSettings settings)
    {
        settings ??= AppSettings.Default;
        var path = GetSettingsPath();
        
        // Create backup of existing settings before overwriting
        if (File.Exists(path))
        {
            var backupPath = path + ".bak";
            try
            {
                File.Copy(path, backupPath, overwrite: true);
            }
            catch
            {
                // Ignore backup failures
            }
        }
        
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private static AppSettings MigrateApiKeys(AppSettings settings)
    {
        // If we already have provider-specific keys, no migration needed
        if (!string.IsNullOrWhiteSpace(settings.CerebrasApiKey) ||
            !string.IsNullOrWhiteSpace(settings.GroqApiKey) ||
            !string.IsNullOrWhiteSpace(settings.GeminiApiKey))
        {
            return settings;
        }

        // If no old API key, nothing to migrate
        if (string.IsNullOrWhiteSpace(settings.RemoteInferenceApiKey))
        {
            return settings;
        }

        // Migrate the old single API key to all selected providers
        var providers = (settings.MultiAiProvidersCsv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var apiKey = settings.RemoteInferenceApiKey;

        return settings with
        {
            CerebrasApiKey = providers.Contains("cerebras", StringComparer.OrdinalIgnoreCase) ? apiKey : settings.CerebrasApiKey,
            GroqApiKey = providers.Contains("groq", StringComparer.OrdinalIgnoreCase) ? apiKey : settings.GroqApiKey,
            GeminiApiKey = providers.Contains("gemini", StringComparer.OrdinalIgnoreCase) ? apiKey : settings.GeminiApiKey,
            GitHubApiKey = providers.Contains("github", StringComparer.OrdinalIgnoreCase) ? apiKey : settings.GitHubApiKey,
            MistralApiKey = providers.Contains("mistral", StringComparer.OrdinalIgnoreCase) ? apiKey : settings.MistralApiKey
        };
    }
}
