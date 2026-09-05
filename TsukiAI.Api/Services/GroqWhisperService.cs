using System.Net.Http.Headers;
using System.Text;
using TsukiAI.Core.Services;
using TsukiAI.VoiceChat.Services;

namespace TsukiAI.Api.Services;

/// <summary>
/// Server-side STT for the web app: forwards browser audio (webm/opus from
/// MediaRecorder, or WAV) straight to Groq's Whisper endpoint. Groq accepts those
/// containers natively, so no client-side conversion is needed.
/// </summary>
public sealed class GroqWhisperService : IWhisperService, IDisposable
{
    private const string Endpoint = "https://api.groq.com/openai/v1/audio/transcriptions";
    private const string DefaultModel = "whisper-large-v3";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly AudioProcessingService _audioProcessing;

    public GroqWhisperService(string apiKey, string? model = null, AudioProcessingService? audioProcessing = null)
    {
        _apiKey = (apiKey ?? string.Empty).Trim();
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model!.Trim();
        _audioProcessing = audioProcessing ?? new AudioProcessingService();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    public bool IsConfigured => _apiKey.Length > 0;

    /// <summary>Transcribes raw browser audio bytes (webm/wav/mp3 container).</summary>
    public async Task<TranscriptionResult> TranscribeAsync(
        byte[] audioBytes, string fileExtension, string? language = null, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            DevLog.WriteLine("[STT][Groq] No TSUKI_GROQ_STT_API_KEY configured; returning empty transcription.");
            return new TranscriptionResult(string.Empty, "en", 0f);
        }

        if (audioBytes is null || audioBytes.Length == 0)
            return new TranscriptionResult(string.Empty, "en", 0f);

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(audioBytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        form.Add(fileContent, "file", $"audio.{SanitizeExtension(fileExtension)}");
        form.Add(new StringContent(_model), "model");
        form.Add(new StringContent("json"), "response_format");
        if (!string.IsNullOrWhiteSpace(language) && !language.Equals("auto", StringComparison.OrdinalIgnoreCase))
            form.Add(new StringContent(language!), "language");

        using var resp = await _http.PostAsync(Endpoint, form, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var error = await resp.Content.ReadAsStringAsync(ct);
            DevLog.WriteLine("[STT][Groq] transcription failed: status={0}, error={1}", (int)resp.StatusCode, Truncate(error));
            throw new InvalidOperationException($"Groq STT failed with status {(int)resp.StatusCode}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        var lang = doc.RootElement.TryGetProperty("language", out var l) ? l.GetString() ?? "en" : "en";

        DevLog.WriteLine("[STT][Groq] ok, chars={0}, language={1}", text.Length, lang);
        return new TranscriptionResult(text.Trim(), lang, 1f);
    }

    /// <summary>
    /// Compatibility path for the desktop Discord PCM contract (48k stereo s16le).
    /// Reuses AudioProcessingService to downmix, wraps the 16k mono PCM in a WAV
    /// container and sends it to Groq.
    /// </summary>
    public async Task<TranscriptionResult> TranscribeDiscordPcmAsync(byte[] pcm48kStereo, CancellationToken ct = default)
    {
        var mono16k = _audioProcessing.ConvertDiscordToWhisperFormat(pcm48kStereo);
        var wav = WrapPcmInWav(mono16k, sampleRate: 16000, channels: 1);
        return await TranscribeAsync(wav, "wav", ct: ct);
    }

    private static byte[] WrapPcmInWav(byte[] pcm, int sampleRate, int channels)
    {
        const int headerSize = 44;
        var wav = new byte[headerSize + pcm.Length];
        var bitsPerSample = 16;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = channels * bitsPerSample / 8;

        Span<byte> h = wav;
        "RIFF"u8.CopyTo(h);
        WriteInt(h, 4, 36 + pcm.Length);
        "WAVE"u8.CopyTo(h[8..]);
        "fmt "u8.CopyTo(h[12..]);
        WriteInt(h, 16, 16);
        h[20] = 1; // PCM
        h[22] = (byte)channels;
        WriteInt(h, 24, sampleRate);
        WriteInt(h, 28, byteRate);
        h[32] = (byte)blockAlign;
        h[34] = (byte)bitsPerSample;
        "data"u8.CopyTo(h[36..]);
        WriteInt(h, 40, pcm.Length);
        pcm.CopyTo(wav.AsSpan(headerSize));
        return wav;
    }

    private static void WriteInt(Span<byte> span, int offset, int value)
    {
        span[offset] = (byte)value;
        span[offset + 1] = (byte)(value >> 8);
        span[offset + 2] = (byte)(value >> 16);
        span[offset + 3] = (byte)(value >> 24);
    }

    private static string SanitizeExtension(string ext)
    {
        var clean = new StringBuilder(ext.TrimStart('.').Length);
        foreach (var c in ext.TrimStart('.'))
        {
            if (char.IsAsciiLetterOrDigit(c))
                clean.Append(char.ToLowerInvariant(c));
        }

        return clean.Length == 0 ? "wav" : clean.ToString();
    }

    private static string Truncate(string? value, int max = 220)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : $"{trimmed[..max]}...";
    }

    public void Dispose() => _http.Dispose();
}
