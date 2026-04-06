using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using NAudio.Wave;
using TsukiAI.Core.Models;
using TsukiAI.Core.Services;

namespace TsukiAI.VoiceChat.Services;

/// <summary>
/// Captures audio from a local microphone device, applies simple RMS-based VAD,
/// and sends completed speech segments to Groq Whisper for transcription.
/// Transcribed text is fed directly into VoiceConversationPipeline.
/// Supports Other and VRChat platform modes (no Discord bridge needed).
/// </summary>
public sealed class MicrophoneCaptureService : IDisposable
{
    // VAD thresholds — tuned for typical headset/VoiceMeeter input
    private const int RmsThreshold = 300;        // speech detection floor (0–32767)
    private const int SilenceFramesCutoff = 25;  // ~500ms silence ends a segment
    private const int MinSegmentBytes = 9600;    // ~100ms at 16kHz mono — skip noise bursts
    private const int MaxSegmentSeconds = 15;

    private readonly IVoiceConversationPipeline _pipeline;
    private readonly TtsPlaybackService _playback;
    private readonly HttpClient _httpClient;

    private WaveInEvent? _waveIn;
    private readonly List<byte> _buffer = new();
    private int _silenceFrames;
    private bool _inSpeech;
    private bool _isRunning;
    private bool _disposed;

    // Runtime settings reloaded per-segment so changes take effect without restart
    private AppSettings _settings;

    public bool IsRunning => _isRunning;

    public MicrophoneCaptureService(
        IVoiceConversationPipeline pipeline,
        TtsPlaybackService playback,
        AppSettings settings)
    {
        _pipeline = pipeline;
        _playback = playback;
        _settings = settings;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Start(AppSettings settings)
    {
        if (_isRunning) return;
        _settings = settings;

        var deviceId = settings.MicrophoneDeviceId;
        if (deviceId < -1 || deviceId >= WaveIn.DeviceCount)
            deviceId = -1; // default device

        // Capture at 16kHz mono — matches Whisper's native format, avoids resampling
        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceId,
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 20  // 20ms frames, same as Discord bridge
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += (_, _) => DevLog.WriteLine("[Mic] Recording stopped");

        _buffer.Clear();
        _silenceFrames = 0;
        _inSpeech = false;
        _isRunning = true;

        _waveIn.StartRecording();
        DevLog.WriteLine("[Mic] Started capture on device={0}", deviceId);
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;
        try { _waveIn?.StopRecording(); } catch { }
        _buffer.Clear();
        DevLog.WriteLine("[Mic] Stopped capture");
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_isRunning || e.BytesRecorded == 0) return;

        var rms = CalcRms(e.Buffer, e.BytesRecorded);
        var isSpeech = rms >= RmsThreshold;

        if (isSpeech)
        {
            _inSpeech = true;
            _silenceFrames = 0;
            _buffer.AddRange(e.Buffer.Take(e.BytesRecorded));
        }
        else if (_inSpeech)
        {
            _silenceFrames++;
            _buffer.AddRange(e.Buffer.Take(e.BytesRecorded));

            var maxBytes = MaxSegmentSeconds * 16000 * 2; // 16kHz, 16-bit mono
            var silenceThresholdReached = _silenceFrames >= SilenceFramesCutoff;
            var maxLengthReached = _buffer.Count >= maxBytes;

            if (silenceThresholdReached || maxLengthReached)
            {
                FinalizeSegment();
            }
        }
    }

    private void FinalizeSegment()
    {
        if (_buffer.Count < MinSegmentBytes)
        {
            _buffer.Clear();
            _silenceFrames = 0;
            _inSpeech = false;
            return;
        }

        var pcm = _buffer.ToArray();
        _buffer.Clear();
        _silenceFrames = 0;
        _inSpeech = false;

        // Reload settings so language/key changes apply without restart
        try { _settings = SettingsService.Load(); } catch { }

        // Fire-and-forget — don't block the audio capture thread
        _ = Task.Run(() => ProcessSegmentAsync(pcm));
    }

    private async Task ProcessSegmentAsync(byte[] pcm16kMono)
    {
        try
        {
            var correlationId = Guid.NewGuid().ToString("N");
            DevLog.WriteLine("[Mic] segment_bytes={0}, correlation_id={1}", pcm16kMono.Length, correlationId);

            var text = await TranscribeAsync(pcm16kMono, correlationId);
            if (string.IsNullOrWhiteSpace(text))
            {
                DevLog.WriteLine("[Mic] STT returned empty, skipping");
                return;
            }

            DevLog.WriteLine("[Mic] transcribed={0}", text);

            var result = await _pipeline.ProcessTextAsync(
                userId: "local-mic",
                text: text,
                correlationId: correlationId);

            if (!result.Success || result.AudioPcm48kStereo.Length == 0)
            {
                DevLog.WriteLine("[Mic] pipeline returned no audio: {0}", result.ErrorMessage ?? "unknown");
                return;
            }

            // Convert Discord PCM back to WAV for TtsPlaybackService
            var wav = PcmToWav(result.AudioPcm48kStereo, 48000, 2);
            _playback.SetOutputDeviceNumber(_settings.VoiceChatOutputDeviceNumber);
            await _playback.PlayWavAsync(wav);
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("[Mic] ProcessSegmentAsync failed: {0}", ex.GetBaseException().Message);
        }
    }

    private async Task<string> TranscribeAsync(byte[] pcm16kMono, string correlationId)
    {
        var apiKey = _settings.GroqApiKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            DevLog.WriteLine("[Mic] No Groq API key configured for STT");
            return string.Empty;
        }

        try
        {
            var wav = PcmToWav(pcm16kMono, 16000, 1);

            using var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(wav);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(fileContent, "file", "audio.wav");
            form.Add(new StringContent("whisper-large-v3-turbo"), "model");
            form.Add(new StringContent("verbose_json"), "response_format");

            var lang = (_settings.SttLanguageCode ?? "auto").Trim().ToLowerInvariant();
            if (lang != "auto")
                form.Add(new StringContent(lang), "language");

            using var req = new HttpRequestMessage(HttpMethod.Post,
                "https://api.groq.com/openai/v1/audio/transcriptions");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            req.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
            req.Content = form;

            using var resp = await _httpClient.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                DevLog.WriteLine("[Mic] Groq STT error {0}: {1}", (int)resp.StatusCode, err);
                return string.Empty;
            }

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("text", out var t) ? (t.GetString() ?? string.Empty).Trim() : string.Empty;
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("[Mic] Groq STT exception: {0}", ex.GetBaseException().Message);
            return string.Empty;
        }
    }

    /// <summary>Builds a minimal WAV header around raw PCM bytes.</summary>
    private static byte[] PcmToWav(byte[] pcm, int sampleRate, int channels)
    {
        var byteRate = sampleRate * channels * 2;
        var blockAlign = channels * 2;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + pcm.Length);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16); bw.Write((short)1); bw.Write((short)channels);
        bw.Write(sampleRate); bw.Write(byteRate);
        bw.Write((short)blockAlign); bw.Write((short)16);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(pcm.Length);
        bw.Write(pcm);
        return ms.ToArray();
    }

    private static int CalcRms(byte[] buffer, int length)
    {
        if (length < 2) return 0;
        long sum = 0;
        var samples = length / 2;
        for (var i = 0; i < samples; i++)
        {
            short s = (short)(buffer[i * 2] | (buffer[i * 2 + 1] << 8));
            sum += (long)s * s;
        }
        return (int)Math.Sqrt(sum / samples);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _waveIn?.Dispose();
        _httpClient.Dispose();
    }
}
