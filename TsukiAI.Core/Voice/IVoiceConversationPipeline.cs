namespace TsukiAI.VoiceChat.Services;

public interface IVoiceConversationPipeline
{
    Task<VoiceProcessResult> ProcessTextAsync(
        string userId,
        string text,
        string? correlationId = null,
        CancellationToken ct = default);

    Task<byte[]> SynthesizeTextToPcmAsync(
        string text,
        string? correlationId = null,
        CancellationToken ct = default);
}
