using System.Net;
using System.Text;
using System.Text.Json;
using TsukiAI.Core.Services;
using Xunit;

namespace TsukiAI.Core.Tests;

public sealed class SupermemorySemanticMemoryServiceTests
{
    [Fact]
    public async Task SearchAsync_uses_scoped_hybrid_search_and_maps_results()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"results\":[{\"memory\":\"User likes quiet mornings\",\"similarity\":0.9,\"id\":\"mem-1\"}]}" ,
                Encoding.UTF8,
                "application/json")
        });
        using var service = new SupermemorySemanticMemoryService(
            "test-key",
            "https://memory.test",
            "discord-default",
            handler);

        var results = await service.SearchAsync("morning routine", 3, "discord-user-abc");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://memory.test/v4/search", request.RequestUri?.ToString());
        Assert.Equal("Bearer test-key", request.Authorization);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("morning routine", body.RootElement.GetProperty("q").GetString());
        Assert.Equal("hybrid", body.RootElement.GetProperty("searchMode").GetString());
        Assert.Equal("discord-user-abc", body.RootElement.GetProperty("containerTag").GetString());
        Assert.Equal(3, body.RootElement.GetProperty("limit").GetInt32());
        Assert.Equal("User likes quiet mornings", Assert.Single(results).Text);
        Assert.Equal(0.1, Assert.Single(results).Distance, precision: 6);
    }

    [Fact]
    public async Task AddMemoryAsync_uses_direct_memory_endpoint_and_source_metadata()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });
        using var service = new SupermemorySemanticMemoryService(
            "test-key",
            "https://memory.test/",
            "desktop-default",
            handler);

        await service.AddMemoryAsync("User prefers Japanese replies", "desktop-chat", "desktop-user-default");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://memory.test/v4/memories", request.RequestUri?.ToString());
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("desktop-user-default", body.RootElement.GetProperty("containerTag").GetString());
        var memory = body.RootElement.GetProperty("memories")[0];
        Assert.Equal("User prefers Japanese replies", memory.GetProperty("content").GetString());
        Assert.Equal("desktop-chat", memory.GetProperty("metadata").GetProperty("source").GetString());
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.RequestUri,
                request.Headers.Authorization?.ToString() ?? string.Empty,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return responder(request);
        }
    }

    private sealed record RecordedRequest(Uri? RequestUri, string Authorization, string Body);
}
