namespace TsukiAI.VoiceChat.Services;

/// <summary>
/// Common synthesis contract used by the API, Discord bridge path, and desktop
/// voice pipeline. Implementations return a validated WAV file.
/// </summary>
public interface ITtsClient : IDisposable
{
    string Name { get; }

    bool IsConfigured { get; }

    Task<byte[]> SynthesizeWavAsync(
        string text,
        string language,
        CancellationToken ct = default,
        string? correlationId = null);
}
