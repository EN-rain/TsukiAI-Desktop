using TsukiAI.Core.Models;
using TsukiAI.Core.Services;
using Xunit;

namespace TsukiAI.Core.Tests;

public sealed class LiteRouterInferenceClientTests
{
    [Fact]
    public void LiteRouter_has_exact_OpenAI_compatible_v1_endpoint()
    {
        Assert.Equal("https://api.literouter.com/v1", ProviderSwitchingService.GetProviderUrl("literouter"));
    }

    [Fact]
    public void Single_remote_LiteRouter_uses_independent_rotating_client_without_network()
    {
        var settings = new AppSettings(
            InferenceMode: InferenceMode.RemoteColab,
            RemoteInferenceUrl: "https://api.literouter.com/v1",
            ModelName: "synthetic-model-for-test",
            UseMultipleAiProviders: false);

        using var client = InferenceClientFactory.Create(settings);
        Assert.IsType<LiteRouterRotatingInferenceClient>(client);
    }

    [Fact]
    public void LiteRouter_round_robin_avoids_cooling_key_and_respects_long_retry_after()
    {
        var pool = new GroqApiKeyPool(["test-key-a", "test-key-b"], rotateOnSuccess: true);
        Assert.Equal(["test-key-a", "test-key-b"], pool.GetAvailableCandidates());
        pool.MarkSuccess("test-key-a");
        Assert.Equal(["test-key-b", "test-key-a"], pool.GetAvailableCandidates());

        pool.MarkFailure("test-key-b", 429, TimeSpan.FromHours(2));
        Assert.Equal(["test-key-a"], pool.GetAvailableCandidates());
        Assert.Equal(["test-key-a"], pool.GetAvailableCandidates(DateTimeOffset.UtcNow.AddHours(1)));
        Assert.Equal(2, pool.GetAvailableCandidates(DateTimeOffset.UtcNow.AddHours(3)).Count);
    }

    [Fact]
    public void Provider_change_from_other_instance_is_observed_without_restarting_live_switcher()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tsuki-provider-test-{Guid.NewGuid():N}.json");
        try
        {
            var liveClient = new ProviderSwitchingService(path);
            var voicePipeline = new ProviderSwitchingService(path);
            Assert.Equal("groq", liveClient.GetCurrentProvider("groq,literouter"));
            Assert.Equal("literouter", voicePipeline.SwitchToNextProvider("groq,literouter"));
            Assert.Equal("literouter", liveClient.GetCurrentProvider("groq,literouter"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
