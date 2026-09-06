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

    var payload = JsonSerializer.Deserialize<AddMemoryRequest>(body, bodyJsonOptions);
    if (payload is null || string.IsNullOrWhiteSpace(payload.Text))
        return Results.BadRequest(new { error = "text is required" });

    await memory.AddMemoryAsync(payload.Text, payload.Source ?? "web", ct: ctx.RequestAborted);
    return Results.Ok(new { status = "ok" });
});

app.MapGet("/api/memory/search", async (HttpContext ctx, string q, int? k, ISemanticMemoryService memory) =>
{
    if (string.IsNullOrWhiteSpace(q))
        return Results.BadRequest(new { error = "q is required" });

    var hits = await memory.SearchAsync(q, k ?? 5, ct: ctx.RequestAborted);
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

    var result = await pipeline.ProcessTextAsync("web", payload.Text, ct: ctx.RequestAborted, synthesizeAudio: false);
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

// Per-user Discord text chat: own history, own memories, speaker names.
// voice=true also synthesizes her reply so the bridge can send it as a
// Discord voice message.

// MiniMax cloud English TTS: MINIMAX_API_KEYS is a comma-separated list of API
// keys — rotation sticks to the last working key and advances on failure
// (out of credits, etc.), so she keeps speaking as long as any key has balance.
var minimaxKeys = (Environment.GetEnvironmentVariable("MINIMAX_API_KEYS")
    ?? Environment.GetEnvironmentVariable("MINIMAX_API_KEY") ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var minimaxVoiceId = Environment.GetEnvironmentVariable("MINIMAX_VOICE_ID")?.Trim() ?? "English_Soft-spokenGirl";
var minimaxModel = Environment.GetEnvironmentVariable("MINIMAX_MODEL")?.Trim() is { Length: > 0 } mm ? mm : "speech-2.8-hd";
var minimaxHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
var minimaxKeyIndex = 0;
if (minimaxKeys.Length > 0)
    DevLog.WriteLine("Api: MiniMax English TTS enabled (model {0}, {1} key(s))", minimaxModel, minimaxKeys.Length);

app.MapPost("/api/chat/discord", async (HttpContext ctx, TextChatService textChat, VoicevoxClient voicevox, TranslationService translation, AppSettings settings) =>
{
    using var sr = new StreamReader(ctx.Request.Body);
    var body = await sr.ReadToEndAsync();
    var payload = string.IsNullOrWhiteSpace(body)
        ? null
        : JsonSerializer.Deserialize<DiscordChatRequest>(body, bodyJsonOptions);

    if (payload is null || string.IsNullOrWhiteSpace(payload.UserId) || string.IsNullOrWhiteSpace(payload.Text))
        return Results.BadRequest(new { error = "userId and text are required" });

    var reply = await textChat.ReplyAsync(payload.UserId, payload.UserName ?? "someone", payload.Text, ctx.RequestAborted);

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
                var cut = ttsText.LastIndexOf(' ', MaxTtsChars);
                ttsText = cut > 0 ? ttsText[..cut] + "…" : ttsText[..MaxTtsChars];
            }

            // Language routing: Japanese keyword -> DeepL + VOICEVOX (emotion
            // tones); otherwise English -> local Kokoro. Falls back to VOICEVOX
            // if Kokoro is unreachable.
            var useJapanese = MentionsJapanese(payload.Text);
            byte[] wav = Array.Empty<byte>();
            if (useJapanese && settings.VoiceTranslateToJapanese && translation.IsEnabled)
            {
                var ja = await translation.TranslateToJapaneseAsync(ttsText, ctx.RequestAborted);
                if (!string.IsNullOrWhiteSpace(ja))
                    ttsText = ja.Trim();

                wav = await VoiceToneEngine.SynthesizeAsync(ttsText, voicevox, ctx.RequestAborted);
                engineUsed = "voicevox";
            }
            else if (minimaxKeys.Length > 0)
            {
                // MiniMax cloud TTS (exact voice). Rotates across keys: sticks
                // to the last working key, advances on failure (out of credits,
                // invalid key, etc.). Final fallback: VOICEVOX so she never
                // goes silent.
                for (var attempt = 0; attempt < minimaxKeys.Length && wav.Length == 0; attempt++)
                {
                    var keyIndex = (minimaxKeyIndex + attempt) % minimaxKeys.Length;
                    try
                    {
                        var speech = new
                        {
                            model = minimaxModel,
                            text = ttsText,
                            voice_setting = new { voice_id = minimaxVoiceId, speed = 1.0, vol = 1.0 },
                            audio_setting = new { format = "wav", sample_rate = 32000, channel = 1 },
                        };
                        using var req = new HttpRequestMessage(HttpMethod.Post,
                            "https://api.minimax.io/v1/t2a_v2");
                        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", minimaxKeys[keyIndex]);
                        req.Content = JsonContent.Create(speech);
                        using var resp = await minimaxHttp.SendAsync(req, ctx.RequestAborted);
                        var respBody = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
                        var doc = System.Text.Json.Nodes.JsonNode.Parse(respBody);
                        var hexAudio = doc?["data"]?["audio"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(hexAudio))
                            throw new Exception("no audio in response: " + respBody[..Math.Min(150, respBody.Length)]);
                        wav = Convert.FromHexString(hexAudio);
                        minimaxKeyIndex = keyIndex; // stick to the working key
                        engineUsed = "minimax";
                    }
                    catch (Exception keyEx)
                    {
                        DevLog.WriteLine("Api: minimax key #{0} failed: {1}", keyIndex + 1, keyEx.Message);
                    }
                }

                if (wav.Length == 0)
                {
                    DevLog.WriteLine("Api: all minimax keys failed, falling back to VOICEVOX");
                    wav = await VoiceToneEngine.SynthesizeAsync(ttsText, voicevox, ctx.RequestAborted);
                    engineUsed = "voicevox-fallback";
                }
            }
            else
            {
                DevLog.WriteLine("Api: no MINIMAX_API_KEYS configured, falling back to VOICEVOX");
                wav = await VoiceToneEngine.SynthesizeAsync(ttsText, voicevox, ctx.RequestAborted);
                engineUsed = "voicevox-fallback";
            }

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

static bool MentionsJapanese(string text)
{
    var keywords = (Environment.GetEnvironmentVariable("TSUKI_VOICE_JAPANESE_KEYWORDS") ??
                    "japanese,japan,日本語,日本,nihongo")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var t = text.ToLowerInvariant();
    return keywords.Any(k => t.Contains(k.ToLowerInvariant()));
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
