namespace TsukiAI.VoiceChat.Services;

public sealed class DiscordVoiceService : IDisposable
{
    private readonly string _token;
    public bool IsConnected { get; private set; }

    public DiscordVoiceService(TsukiAI.Core.Models.AppSettings settings)
    {
        _token = settings.DiscordBotToken ?? string.Empty;
    }

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        await Task.Yield();
        if (string.IsNullOrWhiteSpace(_token))
        {
            TsukiAI.Core.Services.DevLog.WriteLine("[DiscordVoiceService] Bot token is empty; skipping connect.");
            IsConnected = false;
            return false;
        }

        IsConnected = true;
        TsukiAI.Core.Services.DevLog.WriteLine("[DiscordVoiceService] Connection placeholder active.");
        return true;
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        IsConnected = false;
        TsukiAI.Core.Services.DevLog.WriteLine("[DiscordVoiceService] Disconnected.");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        IsConnected = false;
    }
}
