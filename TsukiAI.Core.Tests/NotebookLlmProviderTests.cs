using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TsukiAI.Core.Models;
using TsukiAI.Core.Services;
using Xunit;

namespace TsukiAI.Core.Tests;

public sealed class NotebookLlmProviderTests
{
    private static AppSettings ApiSettings => new(
        InferenceMode: InferenceMode.RemoteColab,
        RemoteInferenceUrl: "https://api.groq.com/openai/v1",
        GroqApiKey: "synthetic-only-test-key",
        ModelName: "openai/gpt-oss-20b",
        UseMultipleAiProviders: false);

    [Fact]
    public void Companion_persona_is_in_the_system_prompt_regardless_of_provider()
    {
        var prompt = new PromptBuilder().BuildCompanionChatSystemPrompt("Tsuki");
        Assert.Contains("You are Tsuki", prompt);
        Assert.Contains("Modelfile style anchor:", prompt);
        Assert.Contains("casually witty", prompt);
        Assert.Contains("Start with filler words sometimes", prompt);
        Assert.Contains("Stay real. Stay Tsuki.", prompt);
    }

    [Fact]
    public async Task Openai_inference_sends_personality_as_system_message_not_user_context()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = socket.GetStream();
            var header = new List<byte>();
            var one = new byte[1];
            while (!header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 }))
            {
                if (await stream.ReadAsync(one, deadline.Token) != 1)
                    throw new IOException("HTTP headers truncated");
                header.Add(one[0]);
                if (header.Count > 16000) throw new IOException("Oversized HTTP headers");
            }
            var headers = Encoding.ASCII.GetString(header.ToArray());
            Assert.Contains("/openai/v1/chat/completions", headers);
            Assert.Contains("Authorization: Bearer synthetic-only-private-key", headers,
                StringComparison.OrdinalIgnoreCase);
            var contentLength = int.Parse(headers.Split("\r\n")
                .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                .Split(':', 2)[1].Trim());
            var body = new byte[contentLength];
            await stream.ReadExactlyAsync(body, deadline.Token);
            var result = Encoding.UTF8.GetString(body);
            var response = Encoding.UTF8.GetBytes(
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Hello.\"}}]}");
            var responseHeaders = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(responseHeaders, deadline.Token);
            await stream.WriteAsync(response, deadline.Token);
            await stream.FlushAsync(deadline.Token);
            return result;
        }, deadline.Token);
        using var client = new RemoteInferenceClient(
            $"http://127.0.0.1:{port}/openai/v1",
            "synthetic-only-private-key", InferenceClientFactory.QwenAbliteratedModel);
        var reply = await client.ChatWithEmotionAsync("And the reward?", ct: deadline.Token);
        var body = await server.WaitAsync(deadline.Token);
        using var json = JsonDocument.Parse(body);
        var messages = json.RootElement.GetProperty("messages");
        Assert.Equal(InferenceClientFactory.QwenAbliteratedModel,
            json.RootElement.GetProperty("model").GetString());
        Assert.InRange(json.RootElement.GetProperty("max_tokens").GetInt32(), 1, 512);
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Contains("You are Tsuki", messages[0].GetProperty("content").GetString());
        Assert.Contains("casually witty", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.DoesNotContain("Modelfile style anchor", messages[1].GetProperty("content").GetString());
        Assert.False(string.IsNullOrWhiteSpace(reply.Reply));
    }

    [Theory]
    [InlineData("molab", "https://molab-tts.tsukihime.dpdns.org/v1")]
    [InlineData("colab", "https://colab-tts.tsukihime.dpdns.org/v1")]
    public void Explicit_notebook_llm_keeps_exact_identity_and_independent_endpoint(
        string provider, string expectedUrl)
    {
        using var selected = Assert.IsType<RemoteInferenceClient>(
            InferenceClientFactory.CreateForProvider(
                ApiSettings, provider, "synthetic-only-notebook-key"));
        Assert.Equal(expectedUrl, selected.BaseUrl);
        Assert.Equal(InferenceClientFactory.QwenAbliteratedModel, selected.Model);
        Assert.Equal("synthetic-only-notebook-key", selected.ApiKey);
    }

    [Fact]
    public void Api_provider_remains_existing_rotation_without_notebook_key()
    {
        using var selected = Assert.IsType<GroqRotatingInferenceClient>(
            InferenceClientFactory.CreateForProvider(ApiSettings, "api", null));
        Assert.Equal("openai/gpt-oss-20b", selected.Model);
    }

    [Theory]
    [InlineData("molab")]
    [InlineData("colab")]
    public void Notebook_provider_without_api_key_fails_closed(string provider)
    {
        Assert.Throws<InvalidOperationException>(() =>
            InferenceClientFactory.CreateForProvider(ApiSettings, provider, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("molab;api")]
    public void Unrecognized_provider_is_rejected(string provider)
    {
        Assert.Throws<ArgumentException>(() =>
            InferenceClientFactory.CreateForProvider(ApiSettings, provider, "synthetic"));
    }
}
