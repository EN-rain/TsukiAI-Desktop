namespace TsukiAI.VoiceChat.Services;

public interface IWhisperService
{
    Task<TranscriptionResult> TranscribeDiscordPcmAsync(byte[] pcm48kStereo, CancellationToken ct = default);
}

