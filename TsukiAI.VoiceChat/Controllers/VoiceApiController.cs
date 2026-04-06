using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using TsukiAI.Core.Models;
using TsukiAI.Core.Services;
using TsukiAI.VoiceChat.Services;

namespace TsukiAI.VoiceChat.Controllers;

[ApiController]
[Route("api/voice")]
public sealed class VoiceApiController : ControllerBase
{
    private readonly AppSettings _settings;
    private readonly VoiceConversationPipeline _pipeline;
    private readonly WhisperService _whisperService;

    public VoiceApiController(
        AppSettings settings,
        VoiceConversationPipeline pipeline,
        WhisperService whisperService)
    {
        _settings = settings;
        _pipeline = pipeline;
        _whisperService = whisperService;
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new
        {
            status = "ok",
            runtime_v2 = _settings.VoiceRuntimeV2Enabled,
            api_enabled = _settings.VoiceApiControllerEnabled,
            text_reception = _settings.VoiceTextReceptionEnabled
        });
    }

    [HttpPost("stt")]
    public async Task<IActionResult> Stt([FromBody] SttRequest request, CancellationToken ct)
    {
        if (!_settings.VoiceRuntimeV2Enabled || !_settings.VoiceApiControllerEnabled)
            return StatusCode(503, new { error = "Voice runtime API disabled by feature flag" });

        if (request is null || string.IsNullOrWhiteSpace(request.AudioData))
            return BadRequest(new { error = "audioData is required" });

        try
        {
            var sw = Stopwatch.StartNew();
            var pcm = Convert.FromBase64String(request.AudioData);
            var result = await _whisperService.TranscribeDiscordPcmAsync(pcm, ct);
            sw.Stop();
            DevLog.WriteLine("[VoiceAPI] stt_ms={0}", sw.ElapsedMilliseconds);

            return Ok(new
            {
                text = result.Text,
                language = result.Language,
                confidence = result.Confidence
            });
        }
        catch (FormatException)
        {
            return BadRequest(new { error = "audioData is not valid base64" });
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("[VoiceAPI] stt_error={0}", ex.Message);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("process")]
    public async Task<IActionResult> Process([FromBody] ProcessRequest request, CancellationToken ct)
    {
        if (!_settings.VoiceRuntimeV2Enabled || !_settings.VoiceApiControllerEnabled)
            return StatusCode(503, new { error = "Voice runtime API disabled by feature flag" });

        if (request is null || string.IsNullOrWhiteSpace(request.Text))
            return BadRequest(new { error = "text is required" });

        var totalSw = Stopwatch.StartNew();
        var result = await _pipeline.ProcessTextAsync(request.UserId ?? string.Empty, request.Text, ct);
        totalSw.Stop();
        DevLog.WriteLine("[VoiceAPI] process_total_ms={0}", totalSw.ElapsedMilliseconds);

        if (!result.Success)
            return Ok(new { text = result.ResponseText, audio = (string?)null, error = result.ErrorMessage });

        return Ok(new
        {
            text = result.ResponseText,
            audio = result.AudioPcm48kStereo.Length > 0 ? Convert.ToBase64String(result.AudioPcm48kStereo) : null
        });
    }

    [HttpPost("process-binary")]
    public async Task<IActionResult> ProcessBinary([FromBody] ProcessRequest request, CancellationToken ct)
    {
        if (!_settings.VoiceRuntimeV2Enabled || !_settings.VoiceApiControllerEnabled)
            return StatusCode(503, new { error = "Voice runtime API disabled by feature flag" });

        if (request is null || string.IsNullOrWhiteSpace(request.Text))
            return BadRequest(new { error = "text is required" });

        var totalSw = Stopwatch.StartNew();
        var result = await _pipeline.ProcessTextAsync(request.UserId ?? string.Empty, request.Text, ct);
        totalSw.Stop();
        DevLog.WriteLine("[VoiceAPI] process_binary_total_ms={0}", totalSw.ElapsedMilliseconds);

        if (!result.Success || result.AudioPcm48kStereo.Length == 0)
            return StatusCode(204);

        Response.Headers["x-tsuki-text"] = result.ResponseText;
        return File(result.AudioPcm48kStereo, "application/octet-stream");
    }

    [HttpPost("test-tts")]
    public async Task<IActionResult> TestTts([FromBody] TestTtsRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Text))
            return BadRequest(new { error = "text is required" });

        var result = await _pipeline.ProcessTextAsync("tts-test", request.Text, ct);
        return Ok(new
        {
            text = result.ResponseText,
            audio = result.AudioPcm48kStereo.Length > 0 ? Convert.ToBase64String(result.AudioPcm48kStereo) : null,
            error = result.ErrorMessage
        });
    }
}

public sealed class SttRequest
{
    public string? UserId { get; set; }
    public string AudioData { get; set; } = string.Empty;
}

public sealed class ProcessRequest
{
    public string? UserId { get; set; }
    public string Text { get; set; } = string.Empty;
}

public sealed class TestTtsRequest
{
    public string Text { get; set; } = string.Empty;
}
