using System.Net;
using System.Net.Sockets;
using System.Text;
using TsukiAI.Core.Services;
using Xunit;

namespace TsukiAI.Core.Tests;

public sealed class GroqRotatingInferenceClientTests
{
    [Fact]
    public async Task Exhausted_rejected_keys_fail_fast_without_more_network_requests()
    {
        var pool = new GroqApiKeyPool(["fake-first", "fake-second"]);
        pool.MarkFailure("fake-first", 403);
        pool.MarkFailure("fake-second", 403);
        using var client = new GroqRotatingInferenceClient(
            "http://127.0.0.1:1", "fake-model", pool);
        var failure = await Assert.ThrowsAsync<InferenceException>(
            () => client.ChatWithEmotionAsync("hello"));
        Assert.Contains("cooling down", failure.Message);
    }

    [Fact]
    public async Task A_403_from_first_key_retries_same_prompt_with_second_key()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var observed = new List<string>();

        var server = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
                using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var request = await reader.ReadLineAsync(deadline.Token);
                Assert.Contains("/openai/v1/chat/completions", request);
                Assert.DoesNotContain("/generate", request);
                string? authorization = null;
                var contentLength = 0;
                while (true)
                {
                    var header = await reader.ReadLineAsync(deadline.Token);
                    if (string.IsNullOrEmpty(header))
                        break;
                    if (header.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                        authorization = header["Authorization:".Length..].Trim();
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(header["Content-Length:".Length..].Trim());
                }
                var remaining = contentLength;
                var scratch = new char[4096];
                while (remaining > 0)
                {
                    var read = await reader.ReadAsync(scratch.AsMemory(0, Math.Min(remaining, scratch.Length)), deadline.Token);
                    if (read == 0) throw new IOException("Request body truncated");
                    remaining -= read;
                }

                observed.Add(authorization ?? "missing");
                var body = attempt == 0
                    ? "{\"error\":\"invalid_api_key\"}"
                    : "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Hello.\"}}]}";
                var bytes = Encoding.UTF8.GetBytes(body);
                var status = attempt == 0 ? "403 Forbidden" : "200 OK";
                var headers = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, deadline.Token);
                await stream.WriteAsync(bytes, deadline.Token);
                await stream.FlushAsync(deadline.Token);
            }
        }, deadline.Token);

        var keys = new GroqApiKeyPool(["fake-bad-key", "fake-working-key"]);
        using var client = new GroqRotatingInferenceClient(
            $"http://127.0.0.1:{port}/openai/v1", "fake-model", keys);
        var result = await client.ChatWithEmotionAsync("Say hello.", ct: deadline.Token);
        await server.WaitAsync(deadline.Token);
        Assert.False(string.IsNullOrWhiteSpace(result.Reply));
        Assert.Contains("Hello", result.Reply);
        Assert.Equal(["Bearer fake-bad-key", "Bearer fake-working-key"], observed);
        Assert.Equal(["fake-working-key"], keys.GetCandidates());
    }
}
