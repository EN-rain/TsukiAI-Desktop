using System.Text.Json;
using TsukiAI.Core.Models;

namespace TsukiAI.Core.Services;

/// <summary>
/// Manages provider switching on rate limit (429 errors).
/// Persists the current active provider across app restarts.
/// </summary>
public sealed class ProviderSwitchingService
{
    private readonly string _statePath;
    private ProviderState _state;
    private readonly object _lock = new();

    public ProviderSwitchingService()
    {
        var baseDir = SettingsService.GetBaseDir();
        _statePath = Path.Combine(baseDir, "provider-state.json");
        _state = LoadState();
    }

    /// <summary>
    /// Gets the current active provider from the ordered list.
    /// </summary>
    public string GetCurrentProvider(string providersCsv)
    {
        lock (_lock)
        {
            var providers = ParseProviders(providersCsv);
            if (providers.Count == 0)
                return "groq"; // Default fallback

            // If current provider is still in the list, use it
            if (!string.IsNullOrEmpty(_state.CurrentProvider) && 
                providers.Contains(_state.CurrentProvider, StringComparer.OrdinalIgnoreCase))
            {
                return _state.CurrentProvider;
            }

            // Otherwise use first provider in list
            var firstProvider = providers[0];
            _state.CurrentProvider = firstProvider;
            SaveState();
            return firstProvider;
        }
    }

    /// <summary>
    /// Switches to the next available provider when rate limit is hit.
    /// Returns the new provider name, or null if no more providers available.
    /// </summary>
    public string? SwitchToNextProvider(string providersCsv)
    {
        lock (_lock)
        {
            var providers = ParseProviders(providersCsv);
            if (providers.Count == 0)
                return null;

            var currentIndex = providers.FindIndex(p => 
                p.Equals(_state.CurrentProvider, StringComparison.OrdinalIgnoreCase));

            // Move to next provider
            var nextIndex = (currentIndex + 1) % providers.Count;
            var nextProvider = providers[nextIndex];

            DevLog.WriteLine($"ProviderSwitching: {_state.CurrentProvider} -> {nextProvider} (rate limit)");
            
            _state.CurrentProvider = nextProvider;
            _state.LastSwitchTime = DateTimeOffset.UtcNow;
            SaveState();

            return nextProvider;
        }
    }

    /// <summary>
    /// Gets the URL for a given provider name.
    /// </summary>
    public static string GetProviderUrl(string providerName)
    {
        return providerName.ToLowerInvariant() switch
        {
            "groq" => "https://api.groq.com/openai/v1",
            "cerebras" => "https://api.cerebras.ai/v1",
            "gemini" => "https://generativelanguage.googleapis.com/v1beta",
            "github" => "https://models.github.ai",
            "mistral" => "https://api.mistral.ai/v1",
            _ => "https://api.groq.com/openai/v1"
        };
    }

    /// <summary>
    /// Gets the API key for a given provider from settings.
    /// </summary>
    public static string GetProviderApiKey(string providerName, AppSettings settings)
    {
        return providerName.ToLowerInvariant() switch
        {
            "groq" => settings.GroqApiKey,
            "cerebras" => settings.CerebrasApiKey,
            "gemini" => settings.GeminiApiKey,
            "github" => settings.GitHubApiKey,
            "mistral" => settings.MistralApiKey,
            _ => settings.RemoteInferenceApiKey
        };
    }

    /// <summary>
    /// Gets the model name for a given provider.
    /// </summary>
    public static string GetProviderModel(string providerName)
    {
        return providerName.ToLowerInvariant() switch
        {
            "groq" => "llama-3.3-70b-versatile",
            "cerebras" => "llama3.1-8b",
            "gemini" => "gemini-1.5-flash",
            "github" => "gpt-4o-mini",
            "mistral" => "mistral-small-latest",
            _ => "llama-3.3-70b-versatile"
        };
    }

    private List<string> ParseProviders(string csv)
    {
        return (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private ProviderState LoadState()
    {
        try
        {
            if (!File.Exists(_statePath))
                return new ProviderState { CurrentProvider = "groq" };

            var json = File.ReadAllText(_statePath);
            return JsonSerializer.Deserialize<ProviderState>(json) ?? new ProviderState { CurrentProvider = "groq" };
        }
        catch
        {
            return new ProviderState { CurrentProvider = "groq" };
        }
    }

    private void SaveState()
    {
        try
        {
            var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_statePath, json);
        }
        catch (Exception ex)
        {
            DevLog.WriteLine($"ProviderSwitching: Failed to save state: {ex.Message}");
        }
    }

    private sealed class ProviderState
    {
        public string CurrentProvider { get; set; } = "groq";
        public DateTimeOffset LastSwitchTime { get; set; }
    }
}
