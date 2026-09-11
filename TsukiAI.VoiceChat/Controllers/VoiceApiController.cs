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
    private const int MaxTextChars = 4000;
    private const int MaxTtsTextChars = 1200;
    private const int MaxAudioUploadBytes = 15 * 1024 * 1024;
    private const int MaxBase64AudioChars = ((MaxAudioUploadBytes + 2) / 3) * 4;

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
        if (request.AudioData.Length > MaxBase64AudioChars)
            return StatusCode(413, new { error = "audioData is too large", correlation_id = correlationId });

        try
        {
            var sw = Stopwatch.StartNew();
            var pcm = Convert.FromBase64String(request.AudioData);
            if (pcm.Length > MaxAudioUploadBytes)
                return StatusCode(413, new { error = "audioData is too large", correlation_id = correlationId });
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return StatusCode(499, new { error = "Request canceled", correlation_id = correlationId });
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("[VoiceAPI] correlation_id={0}, operation=stt, status=error, error={1}", correlationId, ex);
            return StatusCode(502, new { error = "Speech-to-text failed", correlation_id = correlationId });
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
        if (request.Text.Length > MaxTextChars || (request.UserId?.Length ?? 0) > 64)
            return StatusCode(413, new { error = "text or user ID is too long", correlation_id = correlationId });

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
            return StatusCode(502, new { error = "Voice processing failed", correlation_id = correlationId });
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
        if (request.Text.Length > MaxTextChars || (request.UserId?.Length ?? 0) > 64)
            return StatusCode(413, new { error = "text or user ID is too long", correlation_id = correlationId });

        try
        {
            var totalSw = Stopwatch.StartNew();
            var result = await _pipeline.ProcessTextAsync(request.UserId ?? string.Empty, request.Text, correlationId, ct);
            totalSw.Stop();
            DevLog.WriteLine("[VoiceAPI] correlation_id={0}, operation=process_binary, duration_ms={1}, status={2}",
                correlationId, totalSw.ElapsedMilliseconds, result.Success ? "ok" : "error");

            if (!result.Success)
                return StatusCode(502, new { error = "Voice processing failed", correlation_id = correlationId });
            if (result.AudioPcm48kStereo.Length == 0)
                return NoContent();

            Response.Headers["x-correlation-id"] = correlationId;
            Response.Headers["x-tsuki-text"] = result.ResponseText;
            return File(result.AudioPcm48kStereo, "application/octet-stream");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return StatusCode(499, new { error = "Request canceled", correlation_id = correlationId });
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("[VoiceAPI] correlation_id={0}, operation=process_binary, status=error, error={1}", correlationId, ex);
            return StatusCode(502, new { error = "Voice processing failed", correlation_id = correlationId });
        }
    }

    [HttpPost("test-tts")]
    public async Task<IActionResult> TestTts([FromBody] TestTtsRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Text))
            return BadRequest(new { error = "text is required" });
        if (request.Text.Length > MaxTtsTextChars)
            return StatusCode(413, new { error = "text is too long" });

        var correlationId = Guid.NewGuid().ToString("N");
        try
        {
            var audio = await _pipeline.SynthesizeTextToPcmAsync(request.Text, correlationId, ct);
            if (audio.Length == 0)
                return StatusCode(502, new { error = "Text-to-speech unavailable", correlation_id = correlationId });
            return Ok(new
            {
                correlation_id = correlationId,
                text = request.Text,
                audio = Convert.ToBase64String(audio)
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return StatusCode(499, new { error = "Request canceled", correlation_id = correlationId });
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("[VoiceAPI] correlation_id={0}, operation=test_tts, status=error, error={1}", correlationId, ex);
            return StatusCode(502, new { error = "Text-to-speech unavailable", correlation_id = correlationId });
        }
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
