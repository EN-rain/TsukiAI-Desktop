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
    private readonly GroqApiKeyPool _keyPool;
    private readonly string _model;
    private readonly AudioProcessingService _audioProcessing;

    public GroqWhisperService(string apiKey, string? model = null, AudioProcessingService? audioProcessing = null)
        : this(new GroqApiKeyPool([apiKey]), model, audioProcessing)
    {
    }

    public GroqWhisperService(GroqApiKeyPool keyPool, string? model = null, AudioProcessingService? audioProcessing = null)
    {
        _keyPool = keyPool ?? throw new ArgumentNullException(nameof(keyPool));
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model!.Trim();
        _audioProcessing = audioProcessing ?? new AudioProcessingService();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public bool IsConfigured => _keyPool.Count > 0;

    /// <summary>Transcribes raw browser audio bytes (webm/wav/mp3 container).</summary>
    public async Task<TranscriptionResult> TranscribeAsync(
        byte[] audioBytes, string fileExtension, string? language = null, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            DevLog.WriteLine("[STT][Groq] No Groq STT keys configured; returning empty transcription.");
            return new TranscriptionResult(string.Empty, "en", 0f);
        }

        if (audioBytes is null || audioBytes.Length == 0)
            return new TranscriptionResult(string.Empty, "en", 0f);

        var candidates = _keyPool.GetCandidates();
        Exception? lastError = null;
        for (var index = 0; index < candidates.Count; index++)
        {
            var apiKey = candidates[index];
            try
            {
                using var form = CreateForm(audioBytes, fileExtension, language);
                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
                {
                    Content = form
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var status = (int)resp.StatusCode;
                    lastError = new InvalidOperationException($"Groq STT failed with status {status}");
                    _keyPool.MarkFailure(apiKey, status);
                    DevLog.WriteLine("[STT][Groq] key {0}/{1} failed: status={2}",
                        index + 1, candidates.Count, status);

                    if (!ShouldRotate(status) || index == candidates.Count - 1)
                        throw lastError;

                    continue;
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                var lang = doc.RootElement.TryGetProperty("language", out var l) ? l.GetString() ?? "en" : "en";

                _keyPool.MarkSuccess(apiKey);
                DevLog.WriteLine("[STT][Groq] ok, key={0}/{1}, chars={2}, language={3}",
                    index + 1, candidates.Count, text.Length, lang);
                return new TranscriptionResult(text.Trim(), lang, 1f);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = new TimeoutException("Groq STT request timed out.");
                _keyPool.MarkFailure(apiKey, 0);
                if (index == candidates.Count - 1)
                    throw lastError;
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
                _keyPool.MarkFailure(apiKey, 0);
                DevLog.WriteLine("[STT][Groq] key {0}/{1} network failure; rotating", index + 1, candidates.Count);
                if (index == candidates.Count - 1)
                    throw;
            }
        }

        throw lastError ?? new InvalidOperationException("Groq STT failed without a response.");
    }

    private MultipartFormDataContent CreateForm(byte[] audioBytes, string fileExtension, string? language)
    {
        var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(audioBytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        form.Add(fileContent, "file", $"audio.{SanitizeExtension(fileExtension)}");
        form.Add(new StringContent(_model), "model");
        form.Add(new StringContent("json"), "response_format");
        if (!string.IsNullOrWhiteSpace(language) && !language.Equals("auto", StringComparison.OrdinalIgnoreCase))
            form.Add(new StringContent(language!), "language");
        return form;
    }

    private static bool ShouldRotate(int status) =>
        status == 401 || status == 403 || status == 408 || status == 429 || status >= 500;

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

    public void Dispose() => _http.Dispose();
}
