using System.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using System.Text.Json;
using System.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TsukiAI.Core.Models;
using TsukiAI.Core.Services;
using TsukiAI.Core.ViewModels;
using TsukiAI.VoiceChat.Controllers;
using TsukiAI.VoiceChat.Infrastructure;
using TsukiAI.VoiceChat.Services;
using TsukiAI.VoiceChat.ViewModels;
using TsukiAI.VoiceChat.Views;

namespace TsukiAI.VoiceChat;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _serviceProvider;
    private WebApplication? _webApp;
    private CancellationTokenSource? _webAppCts;
    public static IReadOnlyList<EnvVarStatus> EnvStatus { get; private set; } = Array.Empty<EnvVarStatus>();

    public static void ConfigureServices(IServiceCollection services, AppSettings settings)
    {
        var scriptPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "scripts", "semantic_memory_chroma.py"));
        var semanticMemory = new ChromaSqliteSemanticMemoryService(SettingsService.GetBaseDir(), scriptPath);
        var summaryStore = new ConversationSummarySqliteStore(SettingsService.GetBaseDir());
        var generationTuning = settings.GetGenerationTuning();
        var effectiveRemoteUrl = ResolveStartupRemoteUrl(settings.RemoteInferenceUrl);
        var effectiveRemoteApiKey = ResolveEffectiveRemoteApiKey(settings, effectiveRemoteUrl);

        IInferenceClient inferenceClient = settings.InferenceMode switch
        {
            InferenceMode.RemoteColab => new RemoteInferenceClient(
                effectiveRemoteUrl,
                effectiveRemoteApiKey,
                settings.ModelName,
                semanticMemory,
                summaryStore,
                generationTuning),
            _ => new OllamaClient(settings.ModelName, tuning: generationTuning)
        };

        services.AddSingleton(settings);
        services.AddSingleton<ISemanticMemoryService>(semanticMemory);
        services.AddSingleton<IConversationSummaryStore>(summaryStore);
        services.AddSingleton<IInferenceClient>(inferenceClient);
        services.AddSingleton<ConversationSummaryBackgroundService>();
        services.AddSingleton(new ConversationFormattingService("Tsuki", "User"));
        services.AddSingleton<ConversationViewModel>();

        // Voice-only services
        services.AddSingleton<VoiceConversationPipeline>();
        services.AddSingleton(sp => new VoicevoxClient(sp.GetRequiredService<AppSettings>().VoicevoxBaseUrl));
        services.AddSingleton(sp => new VoicevoxEngineService(sp.GetRequiredService<AppSettings>().VoicevoxEnginePath));
        services.AddSingleton<TtsPlaybackService>();
        services.AddSingleton<WhisperService>();
        services.AddSingleton<AudioRecordingService>();
        services.AddSingleton<AudioProcessingService>();
        services.AddSingleton<DiscordVoiceService>();
        services.AddSingleton<DiscordBotService>();
        services.AddSingleton<TranslationService>();
        services.AddSingleton<VoiceApiController>();

        services.AddSingleton<VoiceChatViewModel>();
        services.AddSingleton<MainWindow>();
    }

    private static string ResolveStartupRemoteUrl(string? configuredUrl)
    {
        var url = (configuredUrl ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        // Safe startup fallback to avoid hard crash on empty URL.
        return "https://api.moonshot.ai/v1";
    }

    private static string ResolveEffectiveRemoteApiKey(AppSettings settings, string remoteUrl)
    {
        var url = (remoteUrl ?? string.Empty).Trim().ToLowerInvariant();
        string providerKey = string.Empty;

        if (url.Contains("cerebras.ai"))
        {
            providerKey = settings.CerebrasApiKey;
        }
        else if (url.Contains("api.groq.com"))
        {
            providerKey = settings.GroqApiKey;
        }
        else if (url.Contains("generativelanguage.googleapis.com"))
        {
            providerKey = settings.GeminiApiKey;
        }
        else if (url.Contains("models.github.ai"))
        {
            providerKey = settings.GitHubApiKey;
        }
        else if (url.Contains("api.mistral.ai"))
        {
            providerKey = settings.MistralApiKey;
        }

        var selected = string.IsNullOrWhiteSpace(providerKey) ? settings.RemoteInferenceApiKey : providerKey;
        var normalized = NormalizeApiKey(selected);
        DevLog.WriteLine("App: Remote API key source={0}, length={1}",
            string.IsNullOrWhiteSpace(providerKey) ? "generic" : "provider-specific",
            normalized.Length);
        return normalized;
    }

    private static string NormalizeApiKey(string? value)
    {
        var key = (value ?? string.Empty).Trim().Trim('"', '\'');
        const string bearerPrefix = "bearer ";
        if (key.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            key = key[bearerPrefix.Length..].Trim();
        }

        return key;
    }

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        EnvStatus = EnvConfiguration.GetStatus();
        var settings = EnvConfiguration.ApplyToSettings(SettingsService.Load() with
        {
            EnabledMode = InteractionMode.VoiceChat,
            // Voice chat mode requires the runtime/api controller path to be on.
            // Env vars can still explicitly override these.
            VoiceRuntimeV2Enabled = true,
            VoiceApiControllerEnabled = true
        });

        var services = new ServiceCollection();
        ConfigureServices(services, settings);
        _serviceProvider = services.BuildServiceProvider();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.DataContext = _serviceProvider.GetRequiredService<VoiceChatViewModel>();
        mainWindow.Show();

        if (settings.VoiceRuntimeV2Enabled)
        {
            _serviceProvider.GetRequiredService<VoiceConversationPipeline>().Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    var engine = _serviceProvider.GetRequiredService<VoicevoxEngineService>();
                    await engine.StartAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    DevLog.WriteLine("App: VoiceVox engine start failed: {0}", ex.Message);
                }
            });
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var ready = await _serviceProvider.GetRequiredService<ISemanticMemoryService>().EnsureReadyAsync();
                DevLog.WriteLine("App: Semantic memory ready={0}", ready);
                var summaryReady = await _serviceProvider.GetRequiredService<IConversationSummaryStore>().EnsureReadyAsync();
                DevLog.WriteLine("App: Summary store ready={0}", summaryReady);
            }
            catch (Exception ex)
            {
                DevLog.WriteLine($"App: Semantic memory startup failed: {ex.Message}");
            }
        });

        _ = Task.Run(StartHttpApiServerAsync);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DevLog.WriteLine("App: OnExit called, shutting down...");
        
        // Ensure Discord bridge is terminated even if MainWindow cleanup was skipped.
        TsukiAI.VoiceChat.Views.MainWindow.StopBridgeProcessesOnShutdown();

        try
        {
            var summarizer = _serviceProvider?.GetService<ConversationSummaryBackgroundService>();
            if (summarizer is not null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var summaryTask = summarizer.TriggerVoiceSummaryIfNeededAsync("app_exit", cts.Token);
                if (!summaryTask.Wait(TimeSpan.FromSeconds(5)))
                {
                    DevLog.WriteLine("App: exit summary job timed out.");
                }
            }
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("App: exit summary job failed: {0}", ex.Message);
        }

        (_serviceProvider?.GetService<IInferenceClient>())?.Dispose();
        try
        {
            _serviceProvider?.GetService<VoiceConversationPipeline>()?.Stop();
            _serviceProvider?.GetService<VoicevoxEngineService>()?.Stop();
        }
        catch
        {
            // best-effort shutdown
        }
        
        // Stop the web server
        try
        {
            _webAppCts?.Cancel();
            if (_webApp != null)
            {
                using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var stopTask = _webApp.StopAsync(shutdownCts.Token);
                if (!stopTask.Wait(TimeSpan.FromSeconds(4)))
                {
                    DevLog.WriteLine("App: web server stop timed out.");
                }

                var disposeTask = _webApp.DisposeAsync().AsTask();
                if (!disposeTask.Wait(TimeSpan.FromSeconds(2)))
                {
                    DevLog.WriteLine("App: web server dispose timed out.");
                }
            }
        }
        catch
        {
            // best-effort shutdown
        }
        finally
        {
            _webAppCts?.Dispose();
        }
        
        _serviceProvider?.Dispose();
        base.OnExit(e);

        // Ensure background workers cannot keep the hosting process alive.
        Environment.Exit(0);
    }

    private async Task StartHttpApiServerAsync()
    {
        _webAppCts = new CancellationTokenSource();
        
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(5000));
        builder.Services.AddControllers();
        if (_serviceProvider is not null)
        {
            builder.Services.AddSingleton(_serviceProvider.GetRequiredService<AppSettings>());
            builder.Services.AddSingleton(_serviceProvider.GetRequiredService<VoiceConversationPipeline>());
            builder.Services.AddSingleton(_serviceProvider.GetRequiredService<WhisperService>());
        }
        _webApp = builder.Build();
        _webApp.MapControllers();
        _webApp.MapPost("/api/memory/add", async (HttpContext ctx) =>
        {
            if (_serviceProvider is null)
                return Results.Problem("Service provider not available");

            var service = _serviceProvider.GetRequiredService<ISemanticMemoryService>();
            using var sr = new StreamReader(ctx.Request.Body);
            var body = await sr.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(body))
                return Results.BadRequest(new { error = "Empty body" });

            var payload = JsonSerializer.Deserialize<AddMemoryRequest>(body);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Text))
                return Results.BadRequest(new { error = "text is required" });

            await service.AddMemoryAsync(payload.Text, payload.Source ?? "voicechat", ctx.RequestAborted);
            return Results.Ok(new { status = "ok" });
        });

        _webApp.MapGet("/api/memory/search", async (string q, int? k, HttpContext ctx) =>
        {
            if (_serviceProvider is null)
                return Results.Problem("Service provider not available");

            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "q is required" });

            var service = _serviceProvider.GetRequiredService<ISemanticMemoryService>();
            var hits = await service.SearchAsync(q, k ?? 5, ctx.RequestAborted);
            return Results.Ok(hits);
        });
        
        try
        {
            await _webApp.RunAsync(_webAppCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected when shutting down
        }
    }

    private sealed record AddMemoryRequest(string Text, string? Source);
}
