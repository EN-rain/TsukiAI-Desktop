using System.Text.Json;
using System.Text.RegularExpressions;
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

// Keep public request bodies bounded. Audio uploads use the largest part of
// this budget; JSON/text endpoints apply their own smaller field limits below.
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 16 * 1024 * 1024;

    // Without a password the API refuses to bind publicly (single-user
    // deployment guard).
    if (!publicMode)
        options.ListenLocalhost(5000);
});

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

builder.Services.AddSingleton<ITtsClient>(sp => new OpenVoiceTtsClient(() =>
{
    var current = EnvConfiguration.ApplyToSettings(SettingsService.Load());
    return (current.OpenVoiceUrl, current.OpenVoiceApiKey);
}));
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

// Discord text chat brain: per-user memory, names, retention. Uses its own
// provider-switching client WITHOUT global semantic memory — TextChatService
// scopes all memory writes/recall per user itself.
builder.Services.AddSingleton<TextChatService>(sp => new TextChatService(
    new SwitchingInferenceClient(settings),
    sp.GetRequiredService<ISemanticMemoryService>(),
    settings));
builder.Services.AddHostedService<MemoryRetentionWorker>();

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

    AddMemoryRequest? payload;
    try
    {
        payload = JsonSerializer.Deserialize<AddMemoryRequest>(body, bodyJsonOptions);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "Invalid JSON" });
    }

    if (payload is null || string.IsNullOrWhiteSpace(payload.Text))
        return Results.BadRequest(new { error = "text is required" });
    if (payload.Text.Length > 4000 || (payload.Source?.Length ?? 0) > 128)
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

    await memory.AddMemoryAsync(payload.Text, payload.Source ?? "web", ct: ctx.RequestAborted);
    return Results.Ok(new { status = "ok" });
});

app.MapGet("/api/memory/search", async (HttpContext ctx, string q, int? k, ISemanticMemoryService memory) =>
{
    if (string.IsNullOrWhiteSpace(q))
        return Results.BadRequest(new { error = "q is required" });
    if (q.Length > 4000)
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

    var hits = await memory.SearchAsync(q, Math.Clamp(k ?? 5, 1, 20), ct: ctx.RequestAborted);
    return Results.Ok(hits);
});

// Text chat convenience endpoint for the web UI (same pipeline as voice, minus STT).
app.MapPost("/api/chat", async (HttpContext ctx, IVoiceConversationPipeline pipeline) =>
{
    using var sr = new StreamReader(ctx.Request.Body);
    var body = await sr.ReadToEndAsync();
    ChatRequest? payload;
    try
    {
        payload = string.IsNullOrWhiteSpace(body)
            ? null
            : JsonSerializer.Deserialize<ChatRequest>(body, bodyJsonOptions);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "Invalid JSON" });
    }

    if (payload is null || string.IsNullOrWhiteSpace(payload.Text))
        return Results.BadRequest(new { error = "text is required" });
    if (payload.Text.Length > 4000)
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

    try
    {
        var result = await pipeline.ProcessTextAsync("web", payload.Text, ct: ctx.RequestAborted, synthesizeAudio: false);
        if (!result.Success)
            return Results.Json(new { error = "Chat processing failed" }, statusCode: 502);

        return Results.Ok(new { text = result.ResponseText });
    }
    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
    {
        return Results.StatusCode(499);
    }
    catch (Exception ex)
    {
        DevLog.WriteLine("Api: web chat failed: {0}", ex);
        return Results.Json(new { error = "Chat provider unavailable" }, statusCode: 502);
    }
});

// ---------------------------------------------------------------------------
// Settings (non-secret subset only — API keys are never readable via the API)
// ---------------------------------------------------------------------------
app.MapGet("/api/settings", () =>
{
    var s = EnvConfiguration.ApplyToSettings(SettingsService.Load());
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
            mode = TtsMode.OpenVoice.ToString(),
            openvoice_url = s.OpenVoiceUrl,
            openvoice_configured = !string.IsNullOrWhiteSpace(s.OpenVoiceUrl) &&
                                   !string.IsNullOrWhiteSpace(s.OpenVoiceApiKey)
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

app.MapPut("/api/settings", async (HttpContext ctx) =>
{
    var current = EnvConfiguration.ApplyToSettings(SettingsService.Load());
    using var sr = new StreamReader(ctx.Request.Body);
    var body = await sr.ReadToEndAsync();
    SettingsPatch? patch;
    try
    {
        patch = string.IsNullOrWhiteSpace(body)
            ? null
            : JsonSerializer.Deserialize<SettingsPatch>(body, bodyJsonOptions);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "Invalid JSON" });
    }

    if (patch is null)
        return Results.BadRequest(new { error = "empty body" });

    if (patch.ModelName is { Length: > 128 } || patch.ReplyTonePreset is { Length: > 64 })
        return Results.BadRequest(new { error = "modelName or replyTonePreset is too long" });

    var updated = current;
    if (!string.IsNullOrWhiteSpace(patch.ModelName)) updated = updated with { ModelName = patch.ModelName };
    if (!string.IsNullOrWhiteSpace(patch.ReplyTonePreset)) updated = updated with { ReplyTonePreset = patch.ReplyTonePreset };
    if (patch.Generation is not null)
    {
        var g = patch.Generation;
        updated = updated with
        {
            GenerationMaxTokens = Math.Clamp(g.MaxTokens ?? updated.GenerationMaxTokens, 1, 2048),
            GenerationTemperature = ClampFinite(g.Temperature ?? updated.GenerationTemperature, 0.0f, 2.0f, updated.GenerationTemperature),
            GenerationTopP = ClampFinite(g.TopP ?? updated.GenerationTopP, 0.0f, 1.0f, updated.GenerationTopP),
            GenerationTopK = Math.Clamp(g.TopK ?? updated.GenerationTopK, 0, 200),
            GenerationRepeatPenalty = ClampFinite(g.RepeatPenalty ?? updated.GenerationRepeatPenalty, 0.5f, 2.0f, updated.GenerationRepeatPenalty),
            GenerationMaxReplyChars = Math.Clamp(g.MaxReplyChars ?? updated.GenerationMaxReplyChars, 1, 4000)
        };
    }
    if (patch.Tts is not null)
    {
        var t = patch.Tts;
        if (!string.IsNullOrWhiteSpace(t.Mode) &&
            !string.Equals(t.Mode, nameof(TtsMode.OpenVoice), StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = "OpenVoice V2 is the only supported TTS backend" });
        }
        if (t.OpenVoiceUrl is { } configuredUrl)
        {
            var trimmedUrl = configuredUrl.Trim();
            if (trimmedUrl.Length > 2048 ||
                !Uri.TryCreate(trimmedUrl, UriKind.Absolute, out var parsedUrl) ||
                (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps))
            {
                return Results.BadRequest(new { error = "openVoiceUrl must be an http(s) URL no longer than 2048 characters" });
            }

            updated = updated with { OpenVoiceUrl = trimmedUrl };
        }
        updated = updated with { TtsMode = TtsMode.OpenVoice };
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

// Per-user Discord text chat: own history, own memories, speaker names.
// voice=true also synthesizes her reply so the bridge can send it as a
// Discord voice message.


app.MapPost("/api/chat/discord", async (HttpContext ctx, TextChatService textChat, ITtsClient ttsClient) =>
{
    using var sr = new StreamReader(ctx.Request.Body);
    var body = await sr.ReadToEndAsync();
    DiscordChatRequest? payload;
    try
    {
        payload = string.IsNullOrWhiteSpace(body)
            ? null
            : JsonSerializer.Deserialize<DiscordChatRequest>(body, bodyJsonOptions);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "Invalid JSON" });
    }

    if (payload is null || string.IsNullOrWhiteSpace(payload.UserId) || string.IsNullOrWhiteSpace(payload.Text))
        return Results.BadRequest(new { error = "userId and text are required" });
    if (payload.UserId.Length > 64 || payload.Text.Length > 4000 || (payload.UserName?.Length ?? 0) > 128)
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

    string reply;
    try
    {
        reply = await textChat.ReplyAsync(payload.UserId, payload.UserName ?? "someone", payload.Text, ctx.RequestAborted);
    }
    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
    {
        return Results.StatusCode(499);
    }
    catch (Exception ex)
    {
        DevLog.WriteLine("Api: discord text chat failed: {0}", ex);
        return Results.Json(new { error = "Chat provider unavailable" }, statusCode: 502);
    }

    string? audio = null;
    double? durationSecs = null;
    string? waveform = null;
    string? ttsTextOut = null;
    string? engineUsed = null;
    if (payload.Voice)
    {
        try
        {
            // Guardrail: cap synthesis length — very long replies would produce
            // huge voice messages and stall the TTS engines on the small instance.
            var ttsText = reply;
            const int MaxTtsChars = 280;
            if (ttsText.Length > MaxTtsChars)
            {
                const string suffix = "...";
                var contentLimit = MaxTtsChars - suffix.Length;
                var cut = ttsText.LastIndexOf(' ', contentLimit);
                ttsText = (cut > 0 ? ttsText[..cut] : ttsText[..contentLimit]) + suffix;
            }

            // The selected TTS provider owns its voice/reference configuration;
            // the request carries only generated text and detected language.
            var language = TtsLanguageDetector.Detect(ttsText);
            var wav = await ttsClient.SynthesizeWavAsync(ttsText, language, ctx.RequestAborted);
            engineUsed = ttsClient.Name;

            if (wav.Length > 0)
            {
                audio = Convert.ToBase64String(wav);
                (durationSecs, waveform) = AnalyzeVoiceWav(wav);
                ttsTextOut = ttsText;
                DevLog.WriteLine("Api: discord chat voice via {0} ({1} bytes)", engineUsed, wav.Length);
            }
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("Api: discord chat TTS synthesis failed: {0}", ex.Message);
        }
    }

    return Results.Ok(new { text = reply, audio, tts_text = ttsTextOut, duration_secs = durationSecs, waveform });
});

// Health lives outside the auth fallback via [AllowAnonymous] on the controller action.

// SPA fallback: any unmatched GET serves the web app shell (auth policy does NOT
// apply to it, or the login page could never load).
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

/* language detection is owned by the OpenVoice client */
/*
static bool MentionsJapanese(string text)
{
    var keywords = (Environment.GetEnvironmentVariable("TSUKI_VOICE_JAPANESE_KEYWORDS") ??
                    "japanese,japan,日本語,日本,nihongo")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var t = text.ToLowerInvariant();
    return keywords.Any(k => t.Contains(k.ToLowerInvariant()));
}
*/

static float ClampFinite(float value, float min, float max, float fallback)
{
    return float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

static (double DurationSecs, string Waveform) AnalyzeVoiceWav(byte[] wav)
{
    // Standard 44-byte WAV header: sampleRate @24, byteRate @28, "data" @36.
    int FindChunk(string id, int start)
    {
        int pos = start;
        while (pos + 8 <= wav.Length)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            var size = BitConverter.ToInt32(wav, pos + 4);
            if (chunkId == id) return pos;
            pos += 8 + size + (size % 2);
        }
        return -1;
    }

    var dataPos = FindChunk("data", 12);
    if (dataPos < 0) return (0, string.Empty);
    var dataSize = BitConverter.ToInt32(wav, dataPos + 4);
    var sampleRate = BitConverter.ToInt32(wav, 24);
    var byteRate = BitConverter.ToInt32(wav, 28);
    if (sampleRate <= 0 || byteRate <= 0) return (0, string.Empty);

    var duration = Math.Round(dataSize / (double)byteRate, 2);
    var bytesPerSample = byteRate / sampleRate / 1; // mono 16-bit
    var sampleCount = dataSize / Math.Max(1, bytesPerSample);

    const int bins = 64;
    var step = Math.Max(1, sampleCount / bins);
    var amps = new byte[bins];
    var max = 1;
    for (var b = 0; b < bins; b++)
    {
        byte peak = 0;
        var s0 = b * step;
        for (var i = s0; i < s0 + step && i < sampleCount; i++)
        {
            var off = dataPos + 8 + i * bytesPerSample;
            if (off + 1 >= wav.Length) break;
            var v = (byte)(Math.Abs(BitConverter.ToInt16(wav, off)) >> 8);
            if (v > peak) peak = v;
            if (v > max) max = v;
        }
        amps[b] = peak;
    }

    var waveform = Convert.ToBase64String(amps.Select(a => (byte)(a * 255 / max)).ToArray());
    return (duration, waveform);
}

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
    public string? OpenVoiceUrl { get; set; }
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

sealed class DiscordChatRequest
{
    public string UserId { get; set; } = string.Empty;
    public string? UserName { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool Voice { get; set; }
}

/// <summary>Falls back to a no-op memory service when semantic memory is disabled.</summary>
sealed class NullSemanticMemoryService : ISemanticMemoryService
{
    public static readonly NullSemanticMemoryService Instance = new();

    public Task<bool> EnsureReadyAsync(CancellationToken ct = default) => Task.FromResult(false);
    public Task AddMemoryAsync(string text, string source = "voicechat", string? userId = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<SemanticMemoryHit>> SearchAsync(string query, int topK = 5, string? userId = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SemanticMemoryHit>>([]);
    public Task DeleteOlderThanAsync(TimeSpan age, CancellationToken ct = default) => Task.CompletedTask;
}
