using TsukiAI.Core.Models;

namespace TsukiAI.Core.Services;

/// <summary>
/// LiteRouter's OpenAI-compatible chat API with independent key rotation.
/// Its model is intentionally supplied by configuration: available model IDs
/// vary by account and must be checked using GET /v1/models.
/// </summary>
public sealed class LiteRouterRotatingInferenceClient : GroqRotatingInferenceClient
{
    public LiteRouterRotatingInferenceClient(
        string baseUrl,
        string modelName,
        GroqApiKeyPool keyPool,
        ISemanticMemoryService? semanticMemory = null,
        GenerationTuningSettings? tuning = null,
        string replyTonePreset = "natural")
        : base(baseUrl, modelName, keyPool, semanticMemory: semanticMemory,
            tuning: tuning, replyTonePreset: replyTonePreset, providerName: "LiteRouter")
    {
    }
}
