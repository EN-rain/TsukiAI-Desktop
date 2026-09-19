using System.Net;
using System.Text;
using System.Text.Json;
using TsukiAI.VoiceChat.Services;
using Xunit;

namespace TsukiAI.Core.Tests;

public sealed class QwenTtsClientTests
{
    [Fact]
    public async Task SynthesizeWavAsync_checks_health_sends_qwen_payload_and_validates_wav()
    {
        var wav = MinimalWav();
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ProductionHealthJson(), Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(wav)
            });
        using var client = new QwenTtsClient(
            "http://127.0.0.1:8100",
            "test-api-key",
            handler: handler,
            maxAttempts: 1);

        var result = await client.SynthesizeWavAsync("Hello there", "Auto", correlationId: "corr-1");

        Assert.Equal(wav, result);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("http://127.0.0.1:8100/health", handler.Requests[0].Request.RequestUri?.ToString());
        Assert.Equal("test-api-key", handler.Requests[0].Request.Headers.GetValues("X-Api-Key").Single());
        Assert.Equal("http://127.0.0.1:8100/tts", handler.Requests[1].Request.RequestUri?.ToString());
        Assert.Equal("corr-1", handler.Requests[1].Request.Headers.GetValues("X-Correlation-ID").Single());

        using var body = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal("Hello there", body.RootElement.GetProperty("text").GetString());
        Assert.Equal("Auto", body.RootElement.GetProperty("language").GetString());
    }

    [Fact]
    public void Client_name_identifies_the_trained_custom_voice()
    {
        using var client = new QwenTtsClient("http://127.0.0.1:8100", "test-api-key");

        Assert.Equal("Qwen3-TTS 0.6B trained Tsukihime custom voice", client.Name);
    }

    [Fact]
    public void LanguageDetector_routes_japanese_text_to_ja()
    {
        Assert.Equal("JA", TtsLanguageDetector.Detect("やっと来た！"));
        Assert.Equal("EN", TtsLanguageDetector.Detect("Hello there."));
    }

    [Fact]
    public void PronunciationPolicy_keeps_script_detection_separate_from_english_policy()
    {
        Assert.Equal("Japanese", TsukiPronunciationPolicy.Resolve("やっと来た！"));
        Assert.Equal("English", TsukiPronunciationPolicy.Resolve("Hello there."));
        Assert.Equal("Auto", TsukiPronunciationPolicy.Resolve("Hello there.", "Auto"));
        Assert.Equal("Japanese", TsukiPronunciationPolicy.Resolve("Hello there.", "Japanese"));
        Assert.Throws<ArgumentException>(() => TsukiPronunciationPolicy.Resolve("Hello", "invalid"));
    }

    [Fact]
    public async Task SynthesizeWavAsync_rejects_base_or_clone_health_identity()
    {
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    ProductionHealthJson(mode: "full-icl", model: "Qwen/Qwen3-TTS-12Hz-0.6B-Base"),
                    Encoding.UTF8,
                    "application/json")
            });
        using var client = new QwenTtsClient(
            "http://127.0.0.1:8100",
            "test-api-key",
            handler: handler,
            maxAttempts: 1);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.SynthesizeWavAsync("Hello", "English"));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Zero_shot_mode_accepts_only_the_explicit_full_icl_contract()
    {
        var wav = MinimalWav();
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ZeroShotHealthJson(), Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(wav)
            });
        using var client = new QwenTtsClient(
            "http://127.0.0.1:8102",
            "test-api-key",
            handler: handler,
            maxAttempts: 1,
            expectedMode: "zero-shot");

        var result = await client.SynthesizeWavAsync("Hello there", "English");

        Assert.Equal(wav, result);
        Assert.Equal("Qwen3-TTS 0.6B zero-shot tsuki_v1 (full ICL)", client.Name);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Trained_base_full_icl_mode_accepts_the_epoch_zero_contract()
    {
        var wav = MinimalWav();
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    TrainedBaseFullIclHealthJson(),
                    Encoding.UTF8,
                    "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(wav)
            });
        using var client = new QwenTtsClient(
            "http://127.0.0.1:8100",
            "test-api-key",
            handler: handler,
            maxAttempts: 1,
            expectedMode: "trained-base-full-icl");

        var result = await client.SynthesizeWavAsync("Hello there", "English");

        Assert.Equal(wav, result);
        Assert.Equal("Qwen3-TTS 0.6B trained Base/full-ICL Tsukihime voice", client.Name);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task SynthesizeWavAsync_rejects_non_wav_response()
    {
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ProductionHealthJson(), Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not-a-wav")
            });
        using var client = new QwenTtsClient(
            "http://127.0.0.1:8100",
            "test-api-key",
            handler: handler,
            maxAttempts: 1);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.SynthesizeWavAsync("Hello", "EN"));
    }

    [Fact]
    public async Task SynthesizeWavAsync_rejects_unknown_language_before_network_call()
    {
        using var handler = new RecordingHandler();
        using var client = new QwenTtsClient(
            "http://127.0.0.1:8100",
            "test-api-key",
            handler: handler,
            maxAttempts: 1);

        await Assert.ThrowsAsync<ArgumentException>(() => client.SynthesizeWavAsync("Hello", "French"));
        Assert.Empty(handler.Requests);
    }

    private static byte[] MinimalWav() =>
    [
        (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0,
        (byte)'W', (byte)'A', (byte)'V', (byte)'E'
    ];

    private static string ProductionHealthJson(
        string mode = "trained-custom",
        string model = "/opt/tsuki-qwen3-tts/checkpoints/tsukihime/checkpoint-epoch-1") =>
        $$"""
        {
          "status": "ready",
          "mode": "{{mode}}",
          "speaker": "tsukihime",
          "model": "{{model}}",
          "checkpoint_id": "tsuki-test-checkpoint",
          "deployment_stage": "production",
          "approval_status": "APPROVED_FOR_PRODUCTION",
          "reference_required": false,
          "runtime_lora_loaded": false,
          "artifact_tree_sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "manifest_sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
          "conditioning": {
            "reference_required": false,
            "reference_sha256": null,
            "x_vector_only_mode": null
          }
        }
        """;

    private static string ZeroShotHealthJson() =>
        """
        {
          "status": "ready",
          "mode": "zero-shot",
          "profile": "tsuki_v1",
          "model": "Qwen/Qwen3-TTS-12Hz-0.6B-Base",
          "deployment_stage": "isolated",
          "approval_status": "APPROVED_FOR_ISOLATED_ZERO_SHOT",
          "reference_required": true,
          "runtime_lora_loaded": false,
          "conditioning": {
            "reference_required": true,
            "reference_sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "reference_transcript_sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "x_vector_only_mode": false
          }
        }
        """;

    private static string TrainedBaseFullIclHealthJson() =>
        """
        {
          "status": "ready",
          "mode": "trained-base-full-icl",
          "profile": "epoch-0",
          "model": "tsukihime-exe/tsuki-qwen3-tts-models/epoch-0",
          "base_model": "Qwen/Qwen3-TTS-12Hz-0.6B-Base",
          "checkpoint_id": "epoch-0",
          "deployment_stage": "isolated",
          "approval_status": "APPROVED_FOR_ISOLATED_BASE_FULL_ICL",
          "reference_required": true,
          "runtime_lora_loaded": false,
          "conditioning": {
            "reference_required": true,
            "reference_sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "reference_transcript_sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "x_vector_only_mode": false
          }
        }
        """;

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public List<RecordedRequest> Requests { get; } = [];

        public RecordingHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request, body));
            return _responses.Dequeue();
        }
    }

    private sealed record RecordedRequest(HttpRequestMessage Request, string Body);
}
