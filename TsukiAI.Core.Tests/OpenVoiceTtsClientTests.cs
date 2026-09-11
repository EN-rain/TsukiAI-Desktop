using System.Net;
using System.Text;
using System.Text.Json;
using TsukiAI.VoiceChat.Services;
using Xunit;

namespace TsukiAI.Core.Tests;

public sealed class OpenVoiceTtsClientTests
{
    [Fact]
    public async Task SynthesizeWavAsync_checks_health_sends_openvoice_payload_and_validates_wav()
    {
        var wav = MinimalWav();
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"engine\":\"openvoice-v2\"}", Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(wav)
            });
        using var client = new OpenVoiceTtsClient(
            "http://127.0.0.1:8000",
            "test-api-key",
            handler: handler,
            maxAttempts: 1);

        var result = await client.SynthesizeWavAsync("Hello there", "EN", correlationId: "corr-1");

        Assert.Equal(wav, result);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("http://127.0.0.1:8000/health", handler.Requests[0].Request.RequestUri?.ToString());
        Assert.Equal("test-api-key", handler.Requests[0].Request.Headers.GetValues("X-Api-Key").Single());
        Assert.Equal("http://127.0.0.1:8000/tts", handler.Requests[1].Request.RequestUri?.ToString());
        Assert.Equal("corr-1", handler.Requests[1].Request.Headers.GetValues("X-Correlation-ID").Single());

        using var body = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal("Hello there", body.RootElement.GetProperty("text").GetString());
        Assert.Equal("EN", body.RootElement.GetProperty("language").GetString());
    }

    [Fact]
    public void LanguageDetector_routes_japanese_text_to_ja()
    {
        Assert.Equal("JA", TtsLanguageDetector.Detect("やっと来た！"));
        Assert.Equal("EN", TtsLanguageDetector.Detect("Hello there."));
    }

    [Fact]
    public async Task SynthesizeWavAsync_rejects_non_wav_response()
    {
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not-a-wav")
            });
        using var client = new OpenVoiceTtsClient(
            "http://127.0.0.1:8000",
            "test-api-key",
            handler: handler,
            maxAttempts: 1);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.SynthesizeWavAsync("Hello", "EN"));
    }

    private static byte[] MinimalWav() =>
    [
        (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0,
        (byte)'W', (byte)'A', (byte)'V', (byte)'E'
    ];

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
