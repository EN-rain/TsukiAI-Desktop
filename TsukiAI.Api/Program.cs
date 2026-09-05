using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using TsukiAI.Api.Hubs;
using TsukiAI.Api.Services;
using TsukiAI.Core.Models;
using TsukiAI.Core.Services;
using TsukiAI.VoiceChat.Services;

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

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
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
    });

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

app.UseAuthentication();
app.UseAuthorization();

// ---------------------------------------------------------------------------
// Auth endpoints
// ---------------------------------------------------------------------------
app.MapPost("/auth/login", async (HttpContext ctx, JsonElement body) =>
{
    if (!publicMode)
        return Results.Ok(new { status = "ok", mode = "local" });

    var password = body.ValueKind == JsonValueKind.Object &&
                   body.TryGetProperty("password", out var p) && p.ValueKind == JsonValueKind.String
        ? p.GetString()
        : null;

    if (string.IsNullOrWhiteSpace(password) || password != webPassword)
    {
        DevLog.WriteLine("Auth: failed login attempt from {0}", ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        return Results.Unauthorized();
    }

    var claimsPrincipal = new System.Security.Claims.ClaimsPrincipal(
        new System.Security.Claims.ClaimsIdentity("tsuki_web"));
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, claimsPrincipal);
    return Results.Ok(new { status = "ok" });
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

    var payload = JsonSerializer.Deserialize<AddMemoryRequest>(body);
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
        : JsonSerializer.Deserialize<ChatRequest>(body);

    if (payload is null || string.IsNullOrWhiteSpace(payload.Text))
        return Results.BadRequest(new { error = "text is required" });

    var result = await pipeline.ProcessTextAsync("web", payload.Text, ct: ctx.RequestAborted);
    if (!result.Success)
        return Results.Json(new { error = result.ErrorMessage }, statusCode: 500);

    return Results.Ok(new { text = result.ResponseText });
});

// Health lives outside the auth fallback via [AllowAnonymous] on the controller action.

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

/// <summary>Falls back to a no-op memory service when semantic memory is disabled.</summary>
sealed class NullSemanticMemoryService : ISemanticMemoryService
{
    public static readonly NullSemanticMemoryService Instance = new();

    public Task<bool> EnsureReadyAsync(CancellationToken ct = default) => Task.FromResult(false);
    public Task AddMemoryAsync(string text, string source = "voicechat", CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<SemanticMemoryHit>> SearchAsync(string query, int topK = 5, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SemanticMemoryHit>>([]);
}
