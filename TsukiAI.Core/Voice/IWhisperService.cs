namespace TsukiAI.VoiceChat.Services;

public interface IWhisperService
{
    Task<TranscriptionResult> TranscribeDiscordPcmAsync(byte[] pcm48kStereo, CancellationToken ct = default);
}

public sealed record TranscriptionResult(string Text, string Language, float Confidence);

