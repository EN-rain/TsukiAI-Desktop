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
    private readonly List<Task> _backgroundTasks = new();
    public static IReadOnlyList<EnvVarStatus> EnvStatus { get; private set; } = Array.Empty<EnvVarStatus>();

    private void RunBackground(string operation, Func<Task> work)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                DevLog.WriteLine("App: background task '{0}' failed: {1}", operation, ex.GetBaseException().Message);
            }
        });

        lock (_backgroundTasks)
        {
            _backgroundTasks.Add(task);
        }
    }

    private Task[] SnapshotBackgroundTasks()
    {
        lock (_backgroundTasks)
        {
            _backgroundTasks.RemoveAll(t => t.IsCompleted);
            return _backgroundTasks.ToArray();
        }
    }

    public static void ConfigureServices(IServiceCollection services, AppSettings settings)
    {
        var scriptPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "scripts", "semantic_memory_chroma.py"));
        var semanticMemory = new ChromaSqliteSemanticMemoryService(SettingsService.GetBaseDir(), scriptPath);
        var generationTuning = settings.GetGenerationTuning();
        
        // Multi-provider support: resolve current provider from state
        string effectiveRemoteUrl;
        string effectiveRemoteApiKey;
        string effectiveModelName;
        
        if (settings.UseMultipleAiProviders && !string.IsNullOrWhiteSpace(settings.MultiAiProvidersCsv))
        {
            var providerSwitcher = new ProviderSwitchingService();
            var currentProvider = providerSwitcher.GetCurrentProvider(settings.MultiAiProvidersCsv);
            effectiveRemoteUrl = ProviderSwitchingService.GetProviderUrl(currentProvider);
            effectiveRemoteApiKey = ProviderSwitchingService.GetProviderApiKey(currentProvider, settings);
            effectiveModelName = ProviderSwitchingService.GetProviderModel(currentProvider);
            
            DevLog.WriteLine($"App: Multi-provider mode enabled, using provider: {currentProvider}");
            DevLog.WriteLine($"App: Provider URL: {effectiveRemoteUrl}");
            DevLog.WriteLine($"App: Provider model: {effectiveModelName}");
        }
        else
        {
            effectiveRemoteUrl = ResolveStartupRemoteUrl(settings.RemoteInferenceUrl);
            effectiveRemoteApiKey = ResolveEffectiveRemoteApiKey(settings, effectiveRemoteUrl);
            effectiveModelName = settings.ModelName;
        }

        // Semantic memory: pass null when disabled so RemoteInferenceClient skips all memory ops
        ISemanticMemoryService? activeSemanticMemory = settings.SemanticMemoryEnabled ? semanticMemory : null;
        DevLog.WriteLine("App: SemanticMemory enabled={0}", settings.SemanticMemoryEnabled);

        IInferenceClient inferenceClient = settings.InferenceMode switch
        {
            InferenceMode.RemoteColab => new RemoteInferenceClient(
                effectiveRemoteUrl,
                effectiveRemoteApiKey,
                effectiveModelName,
                activeSemanticMemory,
                generationTuning,
                settings.ReplyTonePreset),
            _ => new OllamaClient(settings.ModelName, tuning: generationTuning, replyTonePreset: settings.ReplyTonePreset)
        };

        services.AddSingleton(settings);
        services.AddSingleton<ISemanticMemoryService>(semanticMemory);
        services.AddSingleton<IInferenceClient>(inferenceClient);
        services.AddSingleton(new ConversationFormattingService("Tsuki", "User"));
        services.AddSingleton<ConversationViewModel>();

        // Voice-only services
        services.AddSingleton<VoiceConversationPipeline>();
        services.AddSingleton<IVoiceConversationPipeline>(sp => sp.GetRequiredService<VoiceConversationPipeline>());
        services.AddSingleton(sp => new VoicevoxClient(sp.GetRequiredService<AppSettings>().VoicevoxBaseUrl));
        services.AddSingleton(sp => new VoicevoxEngineService(sp.GetRequiredService<AppSettings>().VoicevoxEnginePath));
        services.AddSingleton<TtsPlaybackService>();
        services.AddSingleton<WhisperService>();
        services.AddSingleton<IWhisperService>(sp => sp.GetRequiredService<WhisperService>());
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
            if (settings.TtsMode == TtsMode.LocalVoiceVox)
            {
                RunBackground("voicevox_engine_start", async () =>
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
            else
            {
                DevLog.WriteLine("App: Skipping local VoiceVox engine startup because remote TTS mode is selected.");
            }
        }

        if (settings.SemanticMemoryEnabled)
        {
            RunBackground("semantic_memory_ready", async () =>
            {
                try
                {
                    var ready = await _serviceProvider.GetRequiredService<ISemanticMemoryService>().EnsureReadyAsync();
                    DevLog.WriteLine("App: Semantic memory ready={0}", ready);
                }
                catch (Exception ex)
                {
                    DevLog.WriteLine($"App: Semantic memory startup failed: {ex.Message}");
                }
            });
        }
        else
        {
            DevLog.WriteLine("App: Semantic memory disabled by settings, skipping init.");
        }

        RunBackground("http_api_server", StartHttpApiServerAsync);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DevLog.WriteLine("App: OnExit called, shutting down...");
        
        // Flush any pending history saves immediately
        ConversationHistoryService.FlushAllPending();
        
        // Ensure Discord bridge is terminated even if MainWindow cleanup was skipped.
        TsukiAI.VoiceChat.Views.MainWindow.StopBridgeProcessesOnShutdown();

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
        
        // Stop the web server with async pattern
        try
        {
            _webAppCts?.Cancel();
            if (_webApp != null)
            {
                using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var shutdownTasks = new List<Task>
                {
                    _webApp.StopAsync(shutdownCts.Token),
                    _webApp.DisposeAsync().AsTask()
                };
                
                if (!Task.WaitAll(shutdownTasks.ToArray(), TimeSpan.FromSeconds(5)))
                {
                    DevLog.WriteLine("App: web server shutdown timed out.");
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

        // Give background startup tasks a short window to settle before disposing dependencies.
        var backgroundTasks = SnapshotBackgroundTasks();
        if (backgroundTasks.Length > 0)
        {
            try
            {
                if (!Task.WaitAll(backgroundTasks, TimeSpan.FromSeconds(2)))
                {
                    DevLog.WriteLine("App: background tasks did not finish before shutdown.");
                }
            }
            catch (Exception ex)
            {
                DevLog.WriteLine("App: background task wait failed: {0}", ex.GetBaseException().Message);
            }
        }
        
        try
        {
            DevLog.FlushAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        _serviceProvider?.Dispose();
        lock (_backgroundTasks)
        {
            _backgroundTasks.Clear();
        }
        base.OnExit(e);
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
            builder.Services.AddSingleton(_serviceProvider.GetRequiredService<IVoiceConversationPipeline>());
            builder.Services.AddSingleton(_serviceProvider.GetRequiredService<IWhisperService>());
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
