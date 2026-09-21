using TsukiAI.Core.Models;

namespace TsukiAI.Core.Services;

/// <summary>
/// Resolves the effective provider (URL / API key / model) and builds the matching
/// IInferenceClient. Shared by the desktop app and the web API so both honor
/// multi-provider selection, per-provider keys, and .env overrides identically.
/// </summary>
public static class InferenceClientFactory
{
    public const string QwenAbliteratedModel = "lukey03/Qwen3.5-9B-abliterated";
    private const string MolabLlmUrl = "https://molab-tts.tsukihime.dpdns.org/v1";
    private const string ColabLlmUrl = "https://colab-tts.tsukihime.dpdns.org/v1";

    // This is independent of the selected speech synthesizer. Existing API
    // credentials/models remain untouched when TSUKI_LLM_PROVIDER=api.
    public static IInferenceClient CreateForProvider(
        AppSettings settings, string provider, string? llmApiKey,
        ISemanticMemoryService? semanticMemory = null)
    {
        var normalized = (provider ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized == "api") return CreateApiProvider(settings, semanticMemory);
        if (normalized is not ("molab" or "colab"))
            throw new ArgumentException("LLM provider must be api, molab or colab", nameof(provider));
        if (string.IsNullOrWhiteSpace(llmApiKey))
            throw new InvalidOperationException("TSUKI_LLM_API_KEY must be configured for notebook LLM serving");
        return new RemoteInferenceClient(
            normalized == "molab" ? MolabLlmUrl : ColabLlmUrl,
            llmApiKey.Trim(), QwenAbliteratedModel, semanticMemory,
            settings.GetGenerationTuning(), settings.ReplyTonePreset);
    }

    public static IInferenceClient Create(AppSettings settings, ISemanticMemoryService? semanticMemory = null)
        => CreateForProvider(settings,
            EnvConfiguration.Get("TSUKI_LLM_PROVIDER", "api"),
            EnvConfiguration.Get("TSUKI_LLM_API_KEY"), semanticMemory);

    private static IInferenceClient CreateApiProvider(AppSettings settings, ISemanticMemoryService? semanticMemory)
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

        // A single Groq inference provider still needs key rotation: multi-
        // provider switching must not be required for 401/403/429 failover.
        if (settings.InferenceMode == InferenceMode.RemoteColab &&
            effectiveRemoteUrl.Contains("api.groq.com", StringComparison.OrdinalIgnoreCase))
        {
            var groqKeys = GroqApiKeyPool.LoadFromFileOrValues(
                EnvConfiguration.Get("TSUKI_GROQ_API_KEYS_FILE"),
                EnvConfiguration.Get("GROQ_API_KEYS_FILE"),
                EnvConfiguration.Get("GROQ_KEYS_HOST_PATH"),
                EnvConfiguration.Get("TSUKI_GROQ_API_KEYS"),
                effectiveRemoteApiKey);
            DevLog.WriteLine("InferenceClientFactory: Groq inference rotation loaded ({0} keys)", groqKeys.Count);
            return new GroqRotatingInferenceClient(
                effectiveRemoteUrl, EnvConfiguration.Get("TSUKI_GROQ_MODEL", effectiveModelName), groqKeys,
                effectiveRemoteApiKey, semanticMemory,
                generationTuning, settings.ReplyTonePreset);
        }

        if (settings.InferenceMode == InferenceMode.RemoteColab &&
            IsLiteRouterUrl(effectiveRemoteUrl))
        {
            var keys = GroqApiKeyPool.LoadFromFileOrValues(
                EnvConfiguration.Get("TSUKI_LITEROUTER_API_KEYS_FILE"),
                EnvConfiguration.Get("TSUKI_LITEROUTER_API_KEYS"),
                EnvConfiguration.Get("TSUKI_LITEROUTER_API_KEY"),
                effectiveRemoteApiKey).WithRoundRobin();
            return new LiteRouterRotatingInferenceClient(
                "https://api.literouter.com/v1",
                ProviderSwitchingService.ResolveLiteRouterModel(effectiveModelName),
                keys, semanticMemory: semanticMemory,
                tuning: generationTuning, replyTonePreset: settings.ReplyTonePreset);
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
        // Never reuse another provider's generic remote credential for LiteRouter.
        // Its secrets are exclusively TSUKI_LITEROUTER_API_KEY / _API_KEYS(_FILE).
        if (IsLiteRouterUrl(remoteUrl))
            return NormalizeApiKey(EnvConfiguration.Get("TSUKI_LITEROUTER_API_KEY"));
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

    private static bool IsLiteRouterUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Host.Equals("api.literouter.com", StringComparison.OrdinalIgnoreCase);

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
