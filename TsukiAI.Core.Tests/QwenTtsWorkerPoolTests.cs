using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using TsukiAI.VoiceChat.Services;
using Xunit;

namespace TsukiAI.Core.Tests;

public sealed class QwenTtsWorkerPoolTests
{
    [Fact]
    public void Worker_configuration_preserves_file_order_and_normalizes_urls()
    {
        var workers = QwenTtsWorkerConfiguration.Parse("""
            A|https://a.example/|key-a
            B|https://b.example/|key-b
            C|https://c.example/|key-c
            """);

        Assert.Equal(["A", "B", "C"], workers.Select(worker => worker.Name));
        Assert.Equal("https://a.example", workers[0].BaseUrl);
        Assert.Equal("key-c", workers[2].ApiKey);
    }

    [Fact]
    public void Worker_configuration_rejects_malformed_entries()
    {
        var error = Assert.Throws<FormatException>(() =>
            QwenTtsWorkerConfiguration.Parse("A|https://a.example"));

        Assert.Contains("name|baseUrl|apiKey", error.Message);
    }

    [Fact]
    public void Sentence_splitter_ignores_abbreviations_and_decimals()
    {
        var sentences = TtsSentenceSplitter.Split(
            "Dr. Tsuki measured 3.14 volts carefully. The test is complete! 日本語の文です。次です。");

        Assert.Equal(
            [
                "Dr. Tsuki measured 3.14 volts carefully.",
                "The test is complete!",
                "日本語の文です。",
                "次です。"
            ],
            sentences);
    }

    [Fact]
    public async Task Pool_routes_sentences_round_robin_and_reassembles_audio_in_order()
    {
        var calls = new ConcurrentBag<WorkerCall>();
        var workers = Configurations();
        using var pool = new RoundRobinQwenTtsClient(
            workers,
            worker => new QwenTtsClient(
                worker.BaseUrl,
                worker.ApiKey,
                handler: new RecordingHandler(worker.Name, calls),
                maxAttempts: 1));

        var result = await pool.SynthesizeWavAsync(
            "First sentence is ready. Second sentence is ready. Third sentence is ready. Fourth sentence is ready.",
            "EN");

        var ttsCalls = calls
            .Where(call => call.Path == "/tts")
            .ToList();

        Assert.Equal(4, ttsCalls.Count);
        Assert.Contains(ttsCalls, call => call.Worker == "A" && call.Text.StartsWith("First sentence"));
        Assert.Contains(ttsCalls, call => call.Worker == "B" && call.Text.StartsWith("Second sentence"));
        Assert.Contains(ttsCalls, call => call.Worker == "C" && call.Text.StartsWith("Third sentence"));
        Assert.Contains(ttsCalls, call => call.Worker == "A" && call.Text.StartsWith("Fourth sentence"));

        Assert.Equal([1, 2, 3, 1], ExtractMarkers(result));
    }

    [Fact]
    public async Task Pool_fails_over_a_failed_worker_without_losing_sentence_order()
    {
        var calls = new ConcurrentBag<WorkerCall>();
        var workers = Configurations();
        using var pool = new RoundRobinQwenTtsClient(
            workers,
            worker => new QwenTtsClient(
                worker.BaseUrl,
                worker.ApiKey,
                handler: new RecordingHandler(worker.Name, calls, failHealth: worker.Name == "B"),
                maxAttempts: 1));

        var result = await pool.SynthesizeWavAsync(
            "First sentence is ready. Second sentence is ready. Third sentence is ready.",
            "EN");

        var ttsCalls = calls.Where(call => call.Path == "/tts").ToList();
        Assert.DoesNotContain(ttsCalls, call => call.Worker == "B");
        Assert.Contains(ttsCalls, call => call.Worker == "A" && call.Text.StartsWith("First sentence"));
        Assert.Contains(ttsCalls, call => call.Worker == "C" && call.Text.StartsWith("Second sentence"));
        Assert.Contains(ttsCalls, call => call.Worker == "C" && call.Text.StartsWith("Third sentence"));
        Assert.Equal([1, 3, 3], ExtractMarkers(result));
    }

    private static IReadOnlyList<QwenTtsWorkerConfiguration> Configurations() =>
    [
        new("A", "http://worker-a", "key-a"),
        new("B", "http://worker-b", "key-b"),
        new("C", "http://worker-c", "key-c")
    ];

    private static IReadOnlyList<byte> ExtractMarkers(byte[] wav)
    {
        var dataOffset = FindChunk(wav, "data");
        var dataLength = BitConverter.ToInt32(wav, dataOffset + 4);
        return Enumerable.Range(0, dataLength / 2)
            .Select(index => wav[dataOffset + 8 + index * 2])
            .ToArray();
    }

    private static int FindChunk(byte[] wav, string wanted)
    {
        for (var position = 12; position + 8 <= wav.Length;)
        {
            var id = Encoding.ASCII.GetString(wav, position, 4);
            var size = BitConverter.ToInt32(wav, position + 4);
            if (id == wanted)
                return position;
            position += 8 + size + (size & 1);
        }

        throw new InvalidDataException($"Missing {wanted} chunk.");
    }

    private static byte[] MinimalWav(byte marker)
    {
        var wav = new byte[46];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BitConverter.GetBytes(38).CopyTo(wav, 4);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(wav, 12);
        BitConverter.GetBytes(16).CopyTo(wav, 16);
        BitConverter.GetBytes((short)1).CopyTo(wav, 20);
        BitConverter.GetBytes((short)1).CopyTo(wav, 22);
        BitConverter.GetBytes(24000).CopyTo(wav, 24);
        BitConverter.GetBytes(48000).CopyTo(wav, 28);
        BitConverter.GetBytes((short)2).CopyTo(wav, 32);
        BitConverter.GetBytes((short)16).CopyTo(wav, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BitConverter.GetBytes(2).CopyTo(wav, 40);
        wav[44] = marker;
        return wav;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _worker;
        private readonly ConcurrentBag<WorkerCall> _calls;
        private readonly bool _failHealth;

        public RecordingHandler(
            string worker,
            ConcurrentBag<WorkerCall> calls,
            bool failHealth = false)
        {
            _worker = worker;
            _calls = calls;
            _failHealth = failHealth;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/health")
            {
                _calls.Add(new WorkerCall(_worker, path, string.Empty));
                return _failHealth
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                            {
                              "status": "ready",
                              "mode": "trained-custom",
                              "speaker": "tsukihime",
                              "model": "/opt/tsuki/checkpoint-epoch-1",
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
                            """)
                    };
            }

            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            var text = json.RootElement.GetProperty("text").GetString() ?? string.Empty;
            _calls.Add(new WorkerCall(_worker, path, text));
            var marker = _worker switch
            {
                "A" => (byte)1,
                "B" => (byte)2,
                _ => (byte)3
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(MinimalWav(marker))
            };
        }
    }

    private sealed record WorkerCall(string Worker, string Path, string Text);
}
