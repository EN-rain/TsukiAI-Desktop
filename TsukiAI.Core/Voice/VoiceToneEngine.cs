using System.Text.RegularExpressions;

namespace TsukiAI.VoiceChat.Services;

/// <summary>
/// Emotion-to-voice tone engine shared by the desktop app, voice channel, and web.
/// Maps a reply's tone to a VOICEVOX emotion style (one speaker, multiple styles)
/// plus prosody knobs (intonation/pitch/speed) patched into the audio query.
/// Styles default to Mochiko-san's emotion set; override via
/// TSUKI_VOICE_TONE_STYLES="normal=20,happy=79,sad=77,angry=78,calm=80".
/// </summary>
public static class VoiceToneEngine
{
    public static readonly string[] KnownTones = ["normal", "happy", "sad", "angry", "calm"];

    public static Dictionary<string, int> ToneStyles()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["normal"] = 20, // Mochiko-san Normal
            ["happy"] = 79,  // Joy
            ["sad"] = 77,    // Crying
            ["angry"] = 78,  // Anger
            ["calm"] = 80,   // Relaxed
        };

        var raw = Environment.GetEnvironmentVariable("TSUKI_VOICE_TONE_STYLES");
        if (string.IsNullOrWhiteSpace(raw)) return map;

        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && int.TryParse(kv[1], out var id))
                map[kv[0].ToLowerInvariant()] = id;
        }
        return map;
    }

    public static int StyleFor(string tone)
    {
        var map = ToneStyles();
        return map.TryGetValue(tone, out var id) ? id : map["normal"];
    }

    /// <summary>
    /// Picks a tone from an LLM emotion hint (when usable) plus text heuristics.
    /// </summary>
    public static string ClassifyTone(string text, string? emotionHint = null)
    {
        var hint = (emotionHint ?? string.Empty).Trim().ToLowerInvariant();
        if (hint.Contains("joy") || hint.Contains("happy") || hint.Contains("excit") || hint.Contains("playful"))
            return "happy";
        if (hint.Contains("sad") || hint.Contains("cry") || hint.Contains("melanchol"))
            return "sad";
        if (hint.Contains("angry") || hint.Contains("annoy"))
            return "angry";
        if (hint.Contains("calm") || hint.Contains("sleep") || hint.Contains("relax"))
            return "calm";

        var t = text.ToLowerInvariant();
        if (Regex.IsMatch(t, @"\b(sorry|sigh|aww|sad|lonely|alone|tired|miss you|hugs)\b") || t.Contains("..."))
            return "sad";
        if (Regex.IsMatch(t, @"\b(mad|angry|hate|ugh|grr)\b"))
            return "angry";
        if (text.Contains('!') || Regex.IsMatch(t, @"\b(haha|lol|lmao|yay|yey|nice|great|love|wooo|yippie)\b"))
            return "happy";
        return "normal";
    }

    public static (float Intonation, float Pitch, float Speed) ProsodyFor(string tone) => tone switch
    {
        "happy" => (1.35f, 0.12f, 1.05f),
        "sad" => (0.65f, -0.08f, 0.92f),
        "angry" => (1.25f, 0.05f, 1.08f),
        "calm" => (0.85f, -0.03f, 0.95f),
        _ => (1.0f, 0.0f, 1.0f),
    };

    /// <summary>Patches intonation/pitch/speed into a VOICEVOX audio query JSON.</summary>
    public static string PatchQuery(string queryJson, string tone)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(queryJson);
            if (node is null) return queryJson;

            var (intonation, pitch, speed) = ProsodyFor(tone);
            node["intonationScale"] = intonation;
            node["pitchScale"] = pitch;
            node["speedScale"] = speed;
            return node.ToJsonString();
        }
        catch
        {
            return queryJson;
        }
    }

    /// <summary>
    /// Full tone-aware synthesis: audio_query on the emotion style, prosody patch,
    /// synthesis. Returns the raw VOICEVOX WAV (24kHz mono) — callers convert to
    /// their target format themselves so the web path can let ffmpeg resample
    /// with high quality instead of the rough custom converter.
    /// </summary>
    public static async Task<byte[]> SynthesizeAsync(
        string text,
        VoicevoxClient voicevox,
        CancellationToken ct,
        string? emotionHint = null,
        string? correlationId = null)
    {
        var tone = ClassifyTone(text, emotionHint);
        var styleId = StyleFor(tone);

        // Pre-convert common English words to tuned katakana (accent softening).
        text = EnglishKanaSoftener.Apply(text);

        var queryJson = await voicevox.AudioQueryAsync(text, styleId, ct, correlationId);
        if (string.IsNullOrWhiteSpace(queryJson))
            return Array.Empty<byte>();

        queryJson = PatchQuery(queryJson, tone);

        return await voicevox.SynthesizeFromQueryAsync(queryJson, styleId, ct, correlationId);
    }
}
