using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using TsukiAI.Core.Models;
using TsukiAI.VoiceChat.Controllers;
using TsukiAI.VoiceChat.Services;

namespace TsukiAI.VoiceChat.Tests.Controllers;

public sealed class VoiceApiControllerTests
{
    [Fact]
    public void Health_ReturnsOkWithStatus()
    {
        var controller = CreateController();

        var result = controller.Health();

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = ToJson(ok.Value);
        json.GetProperty("status").GetString().Should().Be("ok");
    }

    [Fact]
    public async Task Stt_EmptyAudio_ReturnsBadRequest()
    {
        var controller = CreateController();
        var response = await controller.Stt(new SttRequest { AudioData = "" }, CancellationToken.None);
        response.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Stt_ValidAudio_ReturnsTranscription()
    {
        var whisper = new Mock<IWhisperService>();
        whisper.Setup(x => x.TranscribeDiscordPcmAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranscriptionResult("hello there", "en", 0.93f));
        var controller = CreateController(whisperService: whisper.Object);

        var audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("pcm"));
        var response = await controller.Stt(new SttRequest { AudioData = audio }, CancellationToken.None);

        var ok = response.Should().BeOfType<OkObjectResult>().Subject;
        var json = ToJson(ok.Value);
        json.GetProperty("text").GetString().Should().Be("hello there");
        json.GetProperty("language").GetString().Should().Be("en");
        json.GetProperty("correlation_id").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Process_ValidRequest_ReturnsTextAndAudio()
    {
        var pipeline = new Mock<IVoiceConversationPipeline>();
        pipeline.Setup(x => x.ProcessTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VoiceProcessResult(true, "hi", "reply", [1, 2, 3]));
        var controller = CreateController(pipeline.Object);

        var response = await controller.Process(new ProcessRequest { UserId = "u1", Text = "hello" }, CancellationToken.None);

        var ok = response.Should().BeOfType<OkObjectResult>().Subject;
        var json = ToJson(ok.Value);
        json.GetProperty("text").GetString().Should().Be("reply");
        json.GetProperty("audio").GetString().Should().NotBeNullOrWhiteSpace();
        json.GetProperty("correlation_id").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Process_PropagatesCorrelationId_ToPipeline()
    {
        string? capturedCorrelationId = null;
        var pipeline = new Mock<IVoiceConversationPipeline>();
        pipeline.Setup(x => x.ProcessTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, CancellationToken>((_, _, id, _) => capturedCorrelationId = id)
            .ReturnsAsync(new VoiceProcessResult(true, "hi", "reply", Array.Empty<byte>()));

        var controller = CreateController(pipeline.Object);
        var response = await controller.Process(new ProcessRequest { UserId = "u1", Text = "hello" }, CancellationToken.None);
        var ok = response.Should().BeOfType<OkObjectResult>().Subject;
        var json = ToJson(ok.Value);

        capturedCorrelationId.Should().NotBeNullOrWhiteSpace();
        json.GetProperty("correlation_id").GetString().Should().Be(capturedCorrelationId);
    }

    [Fact]
    public async Task Process_InvalidInput_ReturnsBadRequest()
    {
        var controller = CreateController();
        var response = await controller.Process(new ProcessRequest { Text = "" }, CancellationToken.None);
        response.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Process_PipelineFailure_ReturnsInternalServerError_WithCorrelationId()
    {
        var pipeline = new Mock<IVoiceConversationPipeline>();
        pipeline.Setup(x => x.ProcessTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("pipeline failed"));
        var controller = CreateController(pipeline.Object);

        var response = await controller.Process(new ProcessRequest { UserId = "u1", Text = "hello" }, CancellationToken.None);

        var objectResult = response.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(500);
        var json = ToJson(objectResult.Value);
        json.GetProperty("correlation_id").GetString().Should().NotBeNullOrWhiteSpace();
    }

    private static VoiceApiController CreateController(
        IVoiceConversationPipeline? pipeline = null,
        IWhisperService? whisperService = null)
    {
        if (pipeline is null)
        {
            var pipelineMock = new Mock<IVoiceConversationPipeline>();
            pipelineMock.Setup(x => x.ProcessTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new VoiceProcessResult(true, "in", "ok", Array.Empty<byte>()));
            pipeline = pipelineMock.Object;
        }

        if (whisperService is null)
        {
            var whisperMock = new Mock<IWhisperService>();
            whisperMock.Setup(x => x.TranscribeDiscordPcmAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TranscriptionResult("text", "en", 1f));
            whisperService = whisperMock.Object;
        }

        var settings = AppSettings.Default with
        {
            VoiceRuntimeV2Enabled = true,
            VoiceApiControllerEnabled = true
        };

        return new VoiceApiController(settings, pipeline, whisperService);
    }

    private static JsonElement ToJson(object? value)
    {
        return JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
