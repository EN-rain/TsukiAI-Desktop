namespace TsukiAI.VoiceChat.Services;

public interface IVoiceConversationPipeline
{
    Task<VoiceProcessResult> ProcessTextAsync(
        string userId,
        string text,
        string? correlationId = null,
        CancellationToken ct = default,
        bool synthesizeAudio = true,
        string? memoryScope = null,
        bool molabVoiceReply = false,
        IReadOnlyList<DiscordContextTurn>? contextTurns = null);

    Task<byte[]> SynthesizeTextToPcmAsync(
        string text,
        string? correlationId = null,
        CancellationToken ct = default);
}
