using TsukiAI.Core.Models;
using TsukiAI.Core.Services;
using Xunit;

namespace TsukiAI.Core.Tests;

[CollectionDefinition("Groq model environment", DisableParallelization = true)]
public sealed class GroqModelEnvironmentCollection;

[Collection("Groq model environment")]
public sealed class InferenceClientFactoryGroqTests
{
    [Fact]
    public void Groq_model_control_overrides_single_and_multi_provider_paths()
    {
        var previous = Environment.GetEnvironmentVariable("TSUKI_GROQ_MODEL");
        try
        {
            Environment.SetEnvironmentVariable("TSUKI_GROQ_MODEL", "synthetic-selected-groq-model");
            Assert.Equal("synthetic-selected-groq-model", ProviderSwitchingService.GetProviderModel("groq"));

            var settings = new AppSettings(
                InferenceMode: InferenceMode.RemoteColab,
                RemoteInferenceUrl: "https://api.groq.com/openai/v1",
                GroqApiKey: "synthetic-test-key",
                ModelName: "original-single-provider-model");
            using var client = InferenceClientFactory.CreateForProvider(settings, "api", null);
            Assert.IsType<GroqRotatingInferenceClient>(client);
            Assert.Equal("synthetic-selected-groq-model", client.Model);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TSUKI_GROQ_MODEL", previous);
        }
    }

    [Fact]
    public void Groq_model_default_retains_existing_multi_and_single_provider_behavior()
    {
        var previous = Environment.GetEnvironmentVariable("TSUKI_GROQ_MODEL");
        try
        {
            Environment.SetEnvironmentVariable("TSUKI_GROQ_MODEL", null);
            // A developer .env can also configure the model; exercise the
            // no-override behavior only when it is actually absent.
            if (EnvConfiguration.Get("TSUKI_GROQ_MODEL").Length > 0)
                return;

            Assert.Equal("openai/gpt-oss-120b", ProviderSwitchingService.GetProviderModel("groq"));
            var settings = new AppSettings(
                InferenceMode: InferenceMode.RemoteColab,
                RemoteInferenceUrl: "https://api.groq.com/openai/v1",
                GroqApiKey: "synthetic-test-key",
                ModelName: "original-single-provider-model");
            using var client = InferenceClientFactory.CreateForProvider(settings, "api", null);
            Assert.Equal("original-single-provider-model", client.Model);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TSUKI_GROQ_MODEL", previous);
        }
    }

    [Fact]
    public void Single_provider_groq_uses_rotating_pool_even_without_multi_provider_switching()
    {
        var settings = new AppSettings(
            InferenceMode: InferenceMode.RemoteColab,
            RemoteInferenceUrl: "https://api.groq.com/openai/v1",
            GroqApiKey: "synthetic-key-not-a-real-secret",
            ModelName: "openai/gpt-oss-20b",
            UseMultipleAiProviders: false);
        var client = InferenceClientFactory.Create(settings);
        try
        {
            Assert.IsType<GroqRotatingInferenceClient>(client);
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public void Non_groq_single_provider_does_not_use_groq_rotation()
    {
        var settings = new AppSettings(
            InferenceMode: InferenceMode.RemoteColab,
            RemoteInferenceUrl: "https://example.invalid/openai/v1",
            RemoteInferenceApiKey: "synthetic-key-not-a-real-secret",
            UseMultipleAiProviders: false);
        var client = InferenceClientFactory.Create(settings);
        try
        {
            Assert.IsType<RemoteInferenceClient>(client);
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }
}
