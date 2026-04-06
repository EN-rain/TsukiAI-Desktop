namespace TsukiAI.VoiceChat.Services;

public sealed class WhisperService : IWhisperService
{
    private readonly AudioProcessingService _audioProcessingService;

    public WhisperService(AudioProcessingService audioProcessingService)
    {
        _audioProcessingService = audioProcessingService;
    }

    public Task<TranscriptionResult> TranscribeDiscordPcmAsync(byte[] pcm48kStereo, CancellationToken ct = default)
    {
        // AssemblyAI-first runtime: local Whisper endpoint is optional.
        // We still convert format for future local STT integration.
        _ = _audioProcessingService.ConvertDiscordToWhisperFormat(pcm48kStereo);
        TsukiAI.Core.Services.DevLog.WriteLine("[STT] Local whisper not configured; returning empty transcription.");
        return Task.FromResult(new TranscriptionResult(string.Empty, "en", 0.0f));
    }
}

public sealed record TranscriptionResult(string Text, string Language, float Confidence);
