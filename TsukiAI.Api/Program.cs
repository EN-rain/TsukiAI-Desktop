using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using TsukiAI.Api.Hubs;
using TsukiAI.Api.Infrastructure;
using TsukiAI.Api.Services;
using TsukiAI.Core.Models;
using TsukiAI.Core.Services;
using TsukiAI.VoiceChat.Services;

// Body JSON uses camelCase/snake_case from web clients; default deserialization is case-sensitive.
var bodyJsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Settings: settings.json (optional, via TSUKI_DATA_DIR) + env/.env overrides.
// API keys must come from the environment in web deployments; a settings.json
// file is only a convenience for local development.
// ---------------------------------------------------------------------------
var settings = EnvConfiguration.ApplyToSettings(SettingsService.Load() with
{
    EnabledMode = InteractionMode.VoiceChat,
    // The web API is the voice runtime — always on.
    VoiceRuntimeV2Enabled = true,
    VoiceApiControllerEnabled = true
});

var webPassword = Environment.GetEnvironmentVariable("TSUKI_WEB_PASSWORD")?.Trim();
var publicMode = !string.IsNullOrWhiteSpace(webPassword);

// Without a password the API refuses to bind publicly (single-user deployment guard).
if (!publicMode)
{
    builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(5000));
}

builder.Services.AddControllers();
builder.Services.AddSignalR();

// Machine clients (discord-voice-bridge) authenticate with a shared secret in
// the X-Api-Key header; browsers use the session cookie.
var apiKey = Environment.GetEnvironmentVariable("TSUKI_API_KEY")?.Trim();

builder.Services
    .AddAuthentication("Smart")
    .AddPolicyScheme("Smart", "Cookie or API key", options =>
    {
        // UseAuthentication runs exactly one default scheme; route each request
        // to the handler that can actually evaluate it.
        options.ForwardDefaultSelector = ctx =>
            ctx.Request.Headers.ContainsKey(ApiKeyAuthenticationHandler.HeaderName)
                ? ApiKeyAuthenticationHandler.SchemeName
                : CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.Cookie.Name = "tsuki_web";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = publicMode ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
    })
    .AddScheme<ApiKeyOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName,
        options => options.ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey);

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// ---------------------------------------------------------------------------
// Core services (same wiring shape as the desktop app, minus WPF-only pieces)
// ---------------------------------------------------------------------------
builder.Services.AddSingleton(settings);

var chromaUrl = Environment.GetEnvironmentVariable("TSUKI_CHROMA_URL")?.Trim();
if (settings.SemanticMemoryEnabled && !string.IsNullOrWhiteSpace(chromaUrl))
{
    var semanticMemory = new ChromaHttpSemanticMemoryService(chromaUrl);
    builder.Services.AddSingleton<ISemanticMemoryService>(semanticMemory);
    builder.Services.AddSingleton<IInferenceClient>(sp =>
        InferenceClientFactory.Create(sp.GetRequiredService<AppSettings>(), semanticMemory));
    DevLog.WriteLine("Api: semantic memory enabled via ChromaDB at {0}", chromaUrl);
}
else
{
    builder.Services.AddSingleton<ISemanticMemoryService>(NullSemanticMemoryService.Instance);
    builder.Services.AddSingleton<IInferenceClient>(sp =>
        InferenceClientFactory.Create(sp.GetRequiredService<AppSettings>(), null));
    DevLog.WriteLine("Api: semantic memory disabled (SemanticMemoryEnabled={0}, TSUKI_CHROMA_URL set={1})",
        settings.SemanticMemoryEnabled, !string.IsNullOrWhiteSpace(chromaUrl));
}

builder.Services.AddSingleton(sp => new VoicevoxClient(sp.GetRequiredService<AppSettings>().VoicevoxBaseUrl));
builder.Services.AddSingleton<TranslationService>();
builder.Services.AddSingleton<AudioProcessingService>();

builder.Services.AddSingleton(sp =>
{
    var sttKey = Environment.GetEnvironmentVariable("TSUKI_GROQ_STT_API_KEY")?.Trim();
    if (string.IsNullOrWhiteSpace(sttKey))
        sttKey = sp.GetRequiredService<AppSettings>().GroqApiKey;
    var model = Environment.GetEnvironmentVariable("TSUKI_GROQ_STT_MODEL")?.Trim();
    return new GroqWhisperService(sttKey ?? string.Empty, model, sp.GetRequiredService<AudioProcessingService>());
});
builder.Services.AddSingleton<IWhisperService>(sp => sp.GetRequiredService<GroqWhisperService>());

builder.Services.AddSingleton<VoiceConversationPipeline>();
builder.Services.AddSingleton<IVoiceConversationPipeline>(sp => sp.GetRequiredService<VoiceConversationPipeline>());

var app = builder.Build();

// Static SPA assets first: they short-circuit before auth/routing. Serving them
// later lets requests with file extensions fall through routing (MapFallbackToFile
// uses a :nonfile pattern, so .js/.css never reach it) and end up 401'd by the
// authorization fallback policy instead of being served.
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

// ---------------------------------------------------------------------------
// Auth endpoints
// ---------------------------------------------------------------------------
app.MapPost("/auth/login", async (HttpContext ctx, JsonElement body) =>
{
    if (publicMode)
    {
        var password = body.ValueKind == JsonValueKind.Object &&
                       body.TryGetProperty("password", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(password) || password != webPassword)
        {
            DevLog.WriteLine("Auth: failed login attempt from {0}", ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            return Results.Unauthorized();
        }
    }

    var claimsPrincipal = new System.Security.Claims.ClaimsPrincipal(
        new System.Security.Claims.ClaimsIdentity("tsuki_web"));
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, claimsPrincipal);
    return Results.Ok(new { status = "ok", mode = publicMode ? "public" : "local" });
}).AllowAnonymous();

app.MapPost("/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { status = "ok" });
}).AllowAnonymous();

app.MapGet("/auth/status", (HttpContext ctx) =>
    Results.Ok(new { authenticated = ctx.User.Identity?.IsAuthenticated == true, public_mode = publicMode }))
    .AllowAnonymous();

// ---------------------------------------------------------------------------
// Voice + memory API (route contract kept from the desktop app)
// ---------------------------------------------------------------------------
app.MapControllers();
app.MapHub<VoiceHub>("/hubs/voice");

app.MapPost("/api/memory/add", async (HttpContext ctx, ISemanticMemoryService memory) =>
{
    using var sr = new StreamReader(ctx.Request.Body);
    var body = await sr.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(body))
        return Results.BadRequest(new { error = "Empty body" });

    var payload = JsonSerializer.Deserialize<AddMemoryRequest>(body, bodyJsonOptions);
    if (payload is null || string.IsNullOrWhiteSpace(payload.Text))
        return Results.BadRequest(new { error = "text is required" });

    await memory.AddMemoryAsync(payload.Text, payload.Source ?? "web", ctx.RequestAborted);
    return Results.Ok(new { status = "ok" });
});

app.MapGet("/api/memory/search", async (HttpContext ctx, string q, int? k, ISemanticMemoryService memory) =>
{
    if (string.IsNullOrWhiteSpace(q))
        return Results.BadRequest(new { error = "q is required" });

    var hits = await memory.SearchAsync(q, k ?? 5, ctx.RequestAborted);
    return Results.Ok(hits);
});

// Text chat convenience endpoint for the web UI (same pipeline as voice, minus STT).
app.MapPost("/api/chat", async (HttpContext ctx, IVoiceConversationPipeline pipeline) =>
{
    using var sr = new StreamReader(ctx.Request.Body);
    var body = await sr.ReadToEndAsync();
    var payload = string.IsNullOrWhiteSpace(body)
        ? null
        : JsonSerializer.Deserialize<ChatRequest>(body, bodyJsonOptions);

    if (payload is null || string.IsNullOrWhiteSpace(payload.Text))
        return Results.BadRequest(new { error = "text is required" });

    var result = await pipeline.ProcessTextAsync("web", payload.Text, ct: ctx.RequestAborted);
    if (!result.Success)
        return Results.Json(new { error = result.ErrorMessage }, statusCode: 500);

    return Results.Ok(new { text = result.ResponseText });
});

// ---------------------------------------------------------------------------
// Settings (non-secret subset only — API keys are never readable via the API)
// ---------------------------------------------------------------------------
app.MapGet("/api/settings", (AppSettings s) =>
{
    var activeProvider = default(string);
    if (s.UseMultipleAiProviders && !string.IsNullOrWhiteSpace(s.MultiAiProvidersCsv))
    {
        activeProvider = new ProviderSwitchingService().GetCurrentProvider(s.MultiAiProvidersCsv);
    }

    return Results.Ok(new
    {
        model_name = s.ModelName,
        inference_mode = s.InferenceMode.ToString(),
        use_multiple_providers = s.UseMultipleAiProviders,
        multi_providers_csv = s.MultiAiProvidersCsv,
        active_provider = activeProvider,
        reply_tone_preset = s.ReplyTonePreset,
        generation = new
        {
            max_tokens = s.GenerationMaxTokens,
            temperature = s.GenerationTemperature,
            top_p = s.GenerationTopP,
            top_k = s.GenerationTopK,
            repeat_penalty = s.GenerationRepeatPenalty,
            max_reply_chars = s.GenerationMaxReplyChars
        },
        tts = new
        {
            mode = s.TtsMode.ToString(),
            voicevox_base_url = s.VoicevoxBaseUrl,
            speaker_style_id = s.VoicevoxSpeakerStyleId
        },
        translation = new
        {
            voice_translate_to_japanese = s.VoiceTranslateToJapanese,
            use_deepl = s.UseDeepLTranslate,
            use_deepl_free_api = s.UseDeepLFreeApi
        },
        memory = new { semantic_memory_enabled = s.SemanticMemoryEnabled },
        stt = new { mode = s.SttMode.ToString(), language_code = s.SttLanguageCode }
    });
});

app.MapPut("/api/settings", async (HttpContext ctx, AppSettings current) =>
{
    using var sr = new StreamReader(ctx.Request.Body);
    var body = await sr.ReadToEndAsync();
    var patch = string.IsNullOrWhiteSpace(body)
        ? null
        : JsonSerializer.Deserialize<SettingsPatch>(body, bodyJsonOptions);

    if (patch is null)
        return Results.BadRequest(new { error = "empty body" });

    var updated = current;
    if (!string.IsNullOrWhiteSpace(patch.ModelName)) updated = updated with { ModelName = patch.ModelName };
    if (!string.IsNullOrWhiteSpace(patch.ReplyTonePreset)) updated = updated with { ReplyTonePreset = patch.ReplyTonePreset };
    if (patch.Generation is not null)
    {
        var g = patch.Generation;
        updated = updated with
        {
            GenerationMaxTokens = g.MaxTokens ?? updated.GenerationMaxTokens,
            GenerationTemperature = g.Temperature ?? updated.GenerationTemperature,
            GenerationTopP = g.TopP ?? updated.GenerationTopP,
            GenerationTopK = g.TopK ?? updated.GenerationTopK,
            GenerationRepeatPenalty = g.RepeatPenalty ?? updated.GenerationRepeatPenalty,
            GenerationMaxReplyChars = g.MaxReplyChars ?? updated.GenerationMaxReplyChars
        };
    }
    if (patch.Tts is not null)
    {
        var t = patch.Tts;
        updated = updated with
        {
            VoicevoxBaseUrl = t.VoicevoxBaseUrl ?? updated.VoicevoxBaseUrl,
            VoicevoxSpeakerStyleId = t.SpeakerStyleId ?? updated.VoicevoxSpeakerStyleId
        };
        if (!string.IsNullOrWhiteSpace(t.Mode) &&
            Enum.TryParse<TtsMode>(t.Mode, ignoreCase: true, out var ttsMode))
        {
            updated = updated with { TtsMode = ttsMode };
        }
    }
    if (patch.Translation is not null)
    {
        var tr = patch.Translation;
        updated = updated with
        {
            VoiceTranslateToJapanese = tr.VoiceTranslateToJapanese ?? updated.VoiceTranslateToJapanese,
            UseDeepLTranslate = tr.UseDeepl ?? updated.UseDeepLTranslate,
            UseDeepLFreeApi = tr.UseDeeplFreeApi ?? updated.UseDeepLFreeApi
        };
    }
    if (patch.Memory is not null && patch.Memory.SemanticMemoryEnabled is { } memEnabled)
        updated = updated with { SemanticMemoryEnabled = memEnabled };
    if (patch.Stt is not null)
    {
        var stt = patch.Stt;
        updated = updated with { SttLanguageCode = stt.LanguageCode ?? updated.SttLanguageCode };
        if (!string.IsNullOrWhiteSpace(stt.Mode) &&
            Enum.TryParse<SttMode>(stt.Mode, ignoreCase: true, out var sttMode))
        {
            updated = updated with { SttMode = sttMode };
        }
    }

    await SettingsService.SaveAsync(updated);
    DevLog.WriteLine("Api: settings updated via web UI");
    return Results.Ok(new { status = "ok" });
});

app.MapGet("/api/history", async () =>
{
    var history = await ConversationHistoryService.LoadVoiceChatHistoryAsync();
    var messages = history?.Messages
        .Select(m => new { role = m.Role, content = m.Content, timestamp = m.Timestamp, speaker_id = m.SpeakerId })
        .ToList() ?? [];
    return Results.Ok(new { messages, last_updated = history?.LastUpdated });
});

app.MapDelete("/api/history", () =>
{
    ConversationHistoryService.ClearVoiceChatHistory();
    return Results.Ok(new { status = "ok" });
});

// Health lives outside the auth fallback via [AllowAnonymous] on the controller action.

// SPA fallback: any unmatched GET serves the web app shell (auth policy does NOT
// apply to it, or the login page could never load).
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

sealed class AddMemoryRequest
{
    public string Text { get; set; } = string.Empty;
    public string? Source { get; set; }
}

sealed class ChatRequest
{
    public string Text { get; set; } = string.Empty;
}

sealed class SettingsPatch
{
    public string? ModelName { get; set; }
    public string? ReplyTonePreset { get; set; }
    public GenerationPatch? Generation { get; set; }
    public TtsPatch? Tts { get; set; }
    public TranslationPatch? Translation { get; set; }
    public MemoryPatch? Memory { get; set; }
    public SttPatch? Stt { get; set; }
}

sealed class GenerationPatch
{
    public int? MaxTokens { get; set; }
    public float? Temperature { get; set; }
    public float? TopP { get; set; }
    public int? TopK { get; set; }
    public float? RepeatPenalty { get; set; }
    public int? MaxReplyChars { get; set; }
}

sealed class TtsPatch
{
    public string? Mode { get; set; }
    public string? VoicevoxBaseUrl { get; set; }
    public int? SpeakerStyleId { get; set; }
}

sealed class TranslationPatch
{
    public bool? VoiceTranslateToJapanese { get; set; }
    public bool? UseDeepl { get; set; }
    public bool? UseDeeplFreeApi { get; set; }
}

sealed class MemoryPatch
{
    public bool? SemanticMemoryEnabled { get; set; }
}

sealed class SttPatch
{
    public string? Mode { get; set; }
    public string? LanguageCode { get; set; }
}

/// <summary>Falls back to a no-op memory service when semantic memory is disabled.</summary>
sealed class NullSemanticMemoryService : ISemanticMemoryService
{
    public static readonly NullSemanticMemoryService Instance = new();

    public Task<bool> EnsureReadyAsync(CancellationToken ct = default) => Task.FromResult(false);
    public Task AddMemoryAsync(string text, string source = "voicechat", CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<SemanticMemoryHit>> SearchAsync(string query, int topK = 5, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SemanticMemoryHit>>([]);
}
