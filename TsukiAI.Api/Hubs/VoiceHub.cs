using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using TsukiAI.VoiceChat.Services;

namespace TsukiAI.Api.Hubs;

/// <summary>
/// Real-time voice/chat hub. Phase 1 is turn-based (matching the desktop pipeline);
/// token-level streaming can be layered on later using
/// IInferenceClient.ChatWithEmotionStreamingAsync.
/// </summary>
[Authorize]
public sealed class VoiceHub : Hub
{
    private readonly IVoiceConversationPipeline _pipeline;

    public VoiceHub(IVoiceConversationPipeline pipeline)
    {
        _pipeline = pipeline;
    }

    public async Task ProcessText(string text)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        await Clients.Caller.SendAsync("TurnStarted", correlationId);

        var result = await _pipeline.ProcessTextAsync(Context.ConnectionId, text ?? string.Empty, correlationId);
        if (result.Success)
        {
            await Clients.Caller.SendAsync("TurnCompleted", new
            {
                correlation_id = correlationId,
                input = result.InputText,
                text = result.ResponseText,
                audio = result.AudioPcm48kStereo.Length > 0 ? Convert.ToBase64String(result.AudioPcm48kStereo) : null
            });
        }
        else
        {
            await Clients.Caller.SendAsync("TurnFailed", new
            {
                correlation_id = correlationId,
                error = result.ErrorMessage ?? "processing failed"
            });
        }
    }
}
