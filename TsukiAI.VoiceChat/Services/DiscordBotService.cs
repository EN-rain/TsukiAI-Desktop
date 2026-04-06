namespace TsukiAI.VoiceChat.Services;

public sealed class DiscordBotService
{
    private readonly DiscordVoiceService _discordVoiceService;

    public DiscordBotService(DiscordVoiceService discordVoiceService)
    {
        _discordVoiceService = discordVoiceService;
    }

    public Task<bool> StartAsync(CancellationToken ct = default) => _discordVoiceService.ConnectAsync(ct);
    public Task StopAsync(CancellationToken ct = default) => _discordVoiceService.DisconnectAsync(ct);
}
