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
    private readonly IVoiceConversationPipeline _pipeline;
    private readonly IWhisperService _whisperService;

    public VoiceApiController(
        AppSettings settings,
        IVoiceConversationPipeline pipeline,
        IWhisperService whisperService)
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
        var correlationId = Guid.NewGuid().ToString("N");
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
            if (_pipeline is VoiceConversationPipeline concretePipeline)
                concretePipeline.RecordSttLatency(sw.Elapsed, correlationId);
            DevLog.WriteLine("[VoiceAPI] correlation_id={0}, operation=stt, duration_ms={1}, status=ok", correlationId, sw.ElapsedMilliseconds);

            return Ok(new
            {
                correlation_id = correlationId,
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
            DevLog.WriteLine("[VoiceAPI] correlation_id={0}, operation=stt, status=error, error={1}", correlationId, ex);
            return StatusCode(500, new { error = ex.Message, correlation_id = correlationId });
        }
    }

    [HttpPost("process")]
    public async Task<IActionResult> Process([FromBody] ProcessRequest request, CancellationToken ct)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        if (!_settings.VoiceRuntimeV2Enabled || !_settings.VoiceApiControllerEnabled)
            return StatusCode(503, new { error = "Voice runtime API disabled by feature flag", correlation_id = correlationId });

        if (request is null || string.IsNullOrWhiteSpace(request.Text))
            return BadRequest(new { error = "text is required", correlation_id = correlationId });

        try
        {
            var totalSw = Stopwatch.StartNew();
            var result = await _pipeline.ProcessTextAsync(request.UserId ?? string.Empty, request.Text, correlationId, ct);
            totalSw.Stop();
            DevLog.WriteLine("[VoiceAPI] correlation_id={0}, operation=process, duration_ms={1}, status={2}",
                correlationId, totalSw.ElapsedMilliseconds, result.Success ? "ok" : "error");

            if (!result.Success)
                return StatusCode(500, new { text = result.ResponseText, audio = (string?)null, error = result.ErrorMessage, correlation_id = correlationId });

            return Ok(new
            {
                correlation_id = correlationId,
                text = result.ResponseText,
                audio = result.AudioPcm48kStereo.Length > 0 ? Convert.ToBase64String(result.AudioPcm48kStereo) : null
            });
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("[VoiceAPI] correlation_id={0}, operation=process, status=error, error={1}", correlationId, ex);
            return StatusCode(500, new { error = ex.Message, correlation_id = correlationId });
        }
    }

    [HttpPost("process-binary")]
    public async Task<IActionResult> ProcessBinary([FromBody] ProcessRequest request, CancellationToken ct)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        if (!_settings.VoiceRuntimeV2Enabled || !_settings.VoiceApiControllerEnabled)
            return StatusCode(503, new { error = "Voice runtime API disabled by feature flag", correlation_id = correlationId });

        if (request is null || string.IsNullOrWhiteSpace(request.Text))
            return BadRequest(new { error = "text is required", correlation_id = correlationId });

        var totalSw = Stopwatch.StartNew();
        var result = await _pipeline.ProcessTextAsync(request.UserId ?? string.Empty, request.Text, correlationId, ct);
        totalSw.Stop();
        DevLog.WriteLine("[VoiceAPI] correlation_id={0}, operation=process_binary, duration_ms={1}, status={2}",
            correlationId, totalSw.ElapsedMilliseconds, result.Success ? "ok" : "error");

        if (!result.Success || result.AudioPcm48kStereo.Length == 0)
            return StatusCode(204);

        Response.Headers["x-correlation-id"] = correlationId;
        Response.Headers["x-tsuki-text"] = result.ResponseText;
        return File(result.AudioPcm48kStereo, "application/octet-stream");
    }

    [HttpPost("test-tts")]
    public async Task<IActionResult> TestTts([FromBody] TestTtsRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Text))
            return BadRequest(new { error = "text is required" });

        var correlationId = Guid.NewGuid().ToString("N");
        var audio = await _pipeline.SynthesizeTextToPcmAsync(request.Text, correlationId, ct);
        return Ok(new
        {
            correlation_id = correlationId,
            text = request.Text,
            audio = audio.Length > 0 ? Convert.ToBase64String(audio) : null
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
