using TsukiAI.Core.Models;

namespace TsukiAI.Core.Services;

/// <summary>
/// Resolves the effective provider (URL / API key / model) and builds the matching
/// IInferenceClient. Shared by the desktop app and the web API so both honor
/// multi-provider selection, per-provider keys, and .env overrides identically.
/// </summary>
public static class InferenceClientFactory
{
    public static IInferenceClient Create(AppSettings settings, ISemanticMemoryService? semanticMemory = null)
    {
        var generationTuning = settings.GetGenerationTuning();

        string effectiveRemoteUrl;
        string effectiveRemoteApiKey;
        string effectiveModelName;

        if (settings.UseMultipleAiProviders && !string.IsNullOrWhiteSpace(settings.MultiAiProvidersCsv))
        {
            // Hot-switching wrapper: resolves the active provider from state on
            // every call, so the pipeline's 429 failover takes effect immediately.
            DevLog.WriteLine("InferenceClientFactory: multi-provider hot-switching enabled ({0})",
                settings.MultiAiProvidersCsv);
            return new SwitchingInferenceClient(settings, semanticMemory);
        }

        {
            effectiveRemoteUrl = ResolveStartupRemoteUrl(settings.RemoteInferenceUrl);
            effectiveRemoteApiKey = ResolveEffectiveRemoteApiKey(settings, effectiveRemoteUrl);
            effectiveModelName = settings.ModelName;
        }

        return settings.InferenceMode switch
        {
            InferenceMode.RemoteColab => new RemoteInferenceClient(
                effectiveRemoteUrl,
                effectiveRemoteApiKey,
                effectiveModelName,
                semanticMemory,
                generationTuning,
                settings.ReplyTonePreset),
            _ => new OllamaClient(settings.ModelName, tuning: generationTuning, replyTonePreset: settings.ReplyTonePreset)
        };
    }

    private static string ResolveStartupRemoteUrl(string? configuredUrl)
    {
        var url = (configuredUrl ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        // Safe startup fallback to avoid hard crash on empty URL.
        return "https://api.moonshot.ai/v1";
    }

    private static string ResolveEffectiveRemoteApiKey(AppSettings settings, string remoteUrl)
    {
        var url = (remoteUrl ?? string.Empty).Trim().ToLowerInvariant();
        string providerKey = string.Empty;

        if (url.Contains("cerebras.ai"))
        {
            providerKey = settings.CerebrasApiKey;
        }
        else if (url.Contains("api.groq.com"))
        {
            providerKey = settings.GroqApiKey;
        }
        else if (url.Contains("generativelanguage.googleapis.com"))
        {
            providerKey = settings.GeminiApiKey;
        }
        else if (url.Contains("models.github.ai"))
        {
            providerKey = settings.GitHubApiKey;
        }
        else if (url.Contains("api.mistral.ai"))
        {
            providerKey = settings.MistralApiKey;
        }

        var selected = string.IsNullOrWhiteSpace(providerKey) ? settings.RemoteInferenceApiKey : providerKey;
        return NormalizeApiKey(selected);
    }

    private static string NormalizeApiKey(string? value)
    {
        var key = (value ?? string.Empty).Trim().Trim('"', '\'');
        const string bearerPrefix = "bearer ";
        if (key.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            key = key[bearerPrefix.Length..].Trim();
        }

        return key;
    }
}
