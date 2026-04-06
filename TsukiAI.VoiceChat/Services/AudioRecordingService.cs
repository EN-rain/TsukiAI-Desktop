namespace TsukiAI.VoiceChat.Services;

public sealed class AudioRecordingService : IDisposable
{
    public bool IsRecording { get; private set; }

    public void Start() => IsRecording = true;
    public void Stop() => IsRecording = false;
    public void Dispose() => Stop();
}
