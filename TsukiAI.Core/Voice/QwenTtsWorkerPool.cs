using System.Buffers.Binary;
using System.Net;
using System.Text;
using TsukiAI.Core.Services;

namespace TsukiAI.VoiceChat.Services;

/// <summary>
/// One private Qwen endpoint in the sentence-level worker pool.
/// The API key is intentionally never included in logs or status responses.
/// </summary>
public sealed record QwenTtsWorkerConfiguration(
    string Name,
    string BaseUrl,
    string ApiKey)
{
    /// <summary>
    /// Parses one worker per line using: name|baseUrl|apiKey.
    /// Blank lines and lines beginning with # are ignored.
    /// </summary>
    public static IReadOnlyList<QwenTtsWorkerConfiguration> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        var workers = new List<QwenTtsWorkerConfiguration>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = raw.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var entry in entries)
        {
            if (entry.Length > 0 && entry[0] == '#')
                continue;

            var parts = entry.Split('|', StringSplitOptions.None);
            if (parts.Length != 3)
            {
                throw new FormatException(
                    "Each Qwen worker must use name|baseUrl|apiKey format.");
            }

            var name = parts[0].Trim();
            var baseUrl = NormalizeBaseUrl(parts[1]);
            var apiKey = parts[2].Trim();

            if (name.Length == 0 || name.Length > 32)
                throw new FormatException("Qwen worker names must be 1 to 32 characters.");
            if (!names.Add(name))
                throw new FormatException($"Qwen worker name '{name}' is duplicated.");
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new FormatException($"Qwen worker '{name}' must have an http(s) base URL.");
            }
            if (apiKey.Length == 0)
                throw new FormatException($"Qwen worker '{name}' must have an API key.");

            workers.Add(new QwenTtsWorkerConfiguration(name, baseUrl, apiKey));
        }

        return workers;
    }

    public static IReadOnlyList<QwenTtsWorkerConfiguration> LoadFromFileOrValue(
        string? filePath,
        string? inlineValue)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var path = filePath.Trim();
            if (!File.Exists(path))
                throw new FileNotFoundException("Configured Qwen worker file was not found.", path);

            return Parse(File.ReadAllText(path));
        }

        return Parse(inlineValue);
    }

    private static string NormalizeBaseUrl(string raw)
    {
        var value = (raw ?? string.Empty).Trim().TrimEnd('/');
        if (value.Length == 0)
            return value;
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = "http://" + value;
        }

        return value;
    }
}

/// <summary>
/// Splits a completed LLM reply into terminal sentences without splitting
/// common abbreviations, decimals, or dotted abbreviations.
/// </summary>
public static class TtsSentenceSplitter
{
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "vs", "etc",
        "e.g", "i.e", "a.m", "p.m", "u.s", "u.k"
    };

    public static IReadOnlyList<string> Split(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
            return [];

        var sentences = new List<string>();
        var start = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (!IsTerminalPunctuation(current) || !IsSentenceBoundary(value, index, current))
                continue;

            var end = index + 1;
            while (end < value.Length && IsTrailingPunctuation(value[end]))
                end++;
            while (end < value.Length && IsClosingDelimiter(value[end]))
                end++;

            var sentence = value[start..end].Trim();
            if (sentence.Length > 0)
                sentences.Add(sentence);
            start = end;
            while (start < value.Length && char.IsWhiteSpace(value[start]))
                start++;
            index = start - 1;
        }

        var remainder = value[start..].Trim();
        if (remainder.Length > 0)
            sentences.Add(remainder);

        return sentences;
    }

    private static bool IsSentenceBoundary(string text, int index, char punctuation)
    {
        if (punctuation is '。' or '！' or '？')
            return true;
        if (punctuation is '!' or '?')
            return true;

        if (index > 0 && char.IsDigit(text[index - 1]) &&
            index + 1 < text.Length && char.IsDigit(text[index + 1]))
            return false;

        if (punctuation == '.' && index + 1 < text.Length && text[index + 1] == '.')
            return false;

        if (index + 1 < text.Length &&
            !char.IsWhiteSpace(text[index + 1]) &&
            !IsClosingDelimiter(text[index + 1]))
            return false;

        var tokenStart = index - 1;
        while (tokenStart >= 0 &&
               (char.IsLetter(text[tokenStart]) || text[tokenStart] == '.'))
            tokenStart--;
        tokenStart++;

        var token = text[tokenStart..index].Trim('.');
        if (Abbreviations.Contains(token))
            return false;
        if (token.Length == 1 && char.IsLetter(token[0]))
            return false;
        if (token.Contains('.', StringComparison.Ordinal))
        {
            var pieces = token.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (pieces.Length > 1 && pieces.All(piece =>
                    piece.Length is >= 1 and <= 3 && piece.All(char.IsLetter)))
                return false;
        }

        return true;
    }

    private static bool IsTerminalPunctuation(char value) => value is
        '.' or '!' or '?' or '。' or '！' or '？';

    private static bool IsTrailingPunctuation(char value) => value is
        '.' or '!' or '?' or '。' or '！' or '？';

    private static bool IsClosingDelimiter(char value) => value is
        '"' or '\'' or '’' or '”' or ')' or ']' or '}' or '」' or '』' or '）';
}

/// <summary>
/// Dispatches sentences to private Qwen workers in A/B/C/A... order. Each
/// worker is serialized locally because one model process should not receive
/// concurrent inference requests. Results are reassembled by sentence index.
/// </summary>
public sealed class RoundRobinQwenTtsClient : ITtsClient
{
    private const int DefaultWorkerAttempts = 1;
    private readonly Worker[] _workers;
    private readonly Func<QwenTtsWorkerConfiguration, QwenTtsClient> _clientFactory;

    public RoundRobinQwenTtsClient(
        IReadOnlyList<QwenTtsWorkerConfiguration> configurations,
        Func<QwenTtsWorkerConfiguration, QwenTtsClient>? clientFactory = null)
    {
        if (configurations is null || configurations.Count == 0)
            throw new ArgumentException("At least one Qwen worker is required.", nameof(configurations));

        _clientFactory = clientFactory ?? (configuration => new QwenTtsClient(
            configuration.BaseUrl,
            configuration.ApiKey,
            maxAttempts: DefaultWorkerAttempts));
        _workers = configurations
            .Select(configuration => new Worker(configuration, _clientFactory(configuration)))
            .ToArray();
    }

    public string Name => $"Qwen3-TTS 0.6B Base (full ICL, {_workers.Length}-worker sentence pool)";

    public bool IsConfigured => _workers.Any(worker => worker.Client.IsConfigured);

    public async Task<byte[]> SynthesizeWavAsync(
        string text,
        string language,
        CancellationToken ct = default,
        string? correlationId = null)
    {
        var sentences = TtsSentenceSplitter.Split(text);
        if (sentences.Count == 0)
            return Array.Empty<byte>();

        var results = new byte[sentences.Count][];
        var tasks = Enumerable.Range(0, _workers.Length)
            .Where(workerIndex => workerIndex < sentences.Count)
            .Select(workerIndex => ProcessWorkerAssignmentsAsync(
                workerIndex,
                sentences,
                results,
                language,
                correlationId,
                ct))
            .ToArray();

        await Task.WhenAll(tasks);
        return QwenTtsWavConcatenator.Concat(results);
    }

    public async Task<bool> IsAliveAsync(CancellationToken ct = default)
    {
        var statuses = await Task.WhenAll(_workers.Select(async worker =>
        {
            await worker.Gate.WaitAsync(ct);
            try
            {
                return await worker.Client.IsAliveAsync(ct);
            }
            finally
            {
                worker.Gate.Release();
            }
        }));

        return statuses.Any(status => status);
    }

    private async Task ProcessWorkerAssignmentsAsync(
        int workerIndex,
        IReadOnlyList<string> sentences,
        byte[][] results,
        string language,
        string? correlationId,
        CancellationToken ct)
    {
        for (var sentenceIndex = workerIndex;
             sentenceIndex < sentences.Count;
             sentenceIndex += _workers.Length)
        {
            results[sentenceIndex] = await SynthesizeWithFailoverAsync(
                sentenceIndex,
                sentences[sentenceIndex],
                language,
                correlationId,
                ct);
        }
    }

    private async Task<byte[]> SynthesizeWithFailoverAsync(
        int sentenceIndex,
        string sentence,
        string language,
        string? correlationId,
        CancellationToken ct)
    {
        var preferredWorker = sentenceIndex % _workers.Length;
        Exception? lastError = null;

        for (var offset = 0; offset < _workers.Length; offset++)
        {
            var workerIndex = (preferredWorker + offset) % _workers.Length;
            var worker = _workers[workerIndex];
            try
            {
                await worker.Gate.WaitAsync(ct);
                try
                {
                    var requestCorrelation = string.IsNullOrWhiteSpace(correlationId)
                        ? $"sentence-{sentenceIndex + 1}/worker-{worker.Configuration.Name}"
                        : $"{correlationId}/sentence-{sentenceIndex + 1}/worker-{worker.Configuration.Name}";
                    return await worker.Client.SynthesizeWavAsync(
                        sentence,
                        language,
                        ct,
                        requestCorrelation);
                }
                finally
                {
                    worker.Gate.Release();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                DevLog.WriteLine(
                    "[QwenTTS][Pool] worker={0}, sentence={1}, failed; trying next worker",
                    worker.Configuration.Name,
                    sentenceIndex + 1);
            }
        }

        throw new InvalidOperationException(
            $"All Qwen TTS workers failed for sentence {sentenceIndex + 1}.",
            lastError);
    }

    public void Dispose()
    {
        foreach (var worker in _workers)
        {
            worker.Client.Dispose();
            worker.Gate.Dispose();
        }
    }

    private sealed class Worker
    {
        public Worker(QwenTtsWorkerConfiguration configuration, QwenTtsClient client)
        {
            Configuration = configuration;
            Client = client;
        }

        public QwenTtsWorkerConfiguration Configuration { get; }
        public QwenTtsClient Client { get; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}

public static class QwenTtsWavConcatenator
{
    private const int MaxCombinedWavBytes = 64 * 1024 * 1024;

    public static byte[] Concat(IReadOnlyList<byte[]> segments)
    {
        if (segments is null || segments.Count == 0)
            return Array.Empty<byte>();

        var parsed = segments.Select(Parse).ToArray();
        var first = parsed[0].Format;
        var dataLength = parsed.Sum(segment => segment.Data.Length);
        if (dataLength > MaxCombinedWavBytes - 44)
            throw new InvalidDataException("Combined Qwen TTS audio exceeds the size limit.");

        foreach (var segment in parsed.Skip(1))
        {
            if (segment.Format != first)
                throw new InvalidDataException("Qwen workers returned incompatible WAV formats.");
        }

        var wav = new byte[44 + dataLength];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), checked((uint)(36 + dataLength)));
        "WAVE"u8.CopyTo(wav.AsSpan(8));
        "fmt "u8.CopyTo(wav.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), first.AudioFormat);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), first.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), first.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), first.ByteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), first.BlockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), first.BitsPerSample);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), checked((uint)dataLength));

        var destination = 44;
        foreach (var segment in parsed)
        {
            segment.Data.CopyTo(wav, destination);
            destination += segment.Data.Length;
        }

        return wav;
    }

    private static ParsedWav Parse(byte[] wav)
    {
        if (wav is null || wav.Length < 12 ||
            !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Qwen TTS returned an invalid WAV container.");

        WavFormat? format = null;
        byte[]? data = null;
        for (var position = 12; position + 8 <= wav.Length;)
        {
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(position + 4, 4));
            var payloadStart = position + 8;
            var payloadEnd = checked((long)payloadStart + chunkSize);
            if (payloadEnd > wav.Length)
                throw new InvalidDataException("Qwen TTS returned a truncated WAV chunk.");

            var chunkId = Encoding.ASCII.GetString(wav, position, 4);
            if (chunkId == "fmt " && chunkSize >= 16)
            {
                format = new WavFormat(
                    BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(payloadStart)),
                    BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(payloadStart + 2)),
                    BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(payloadStart + 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(payloadStart + 8)),
                    BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(payloadStart + 12)),
                    BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(payloadStart + 14)));
            }
            else if (chunkId == "data")
            {
                data = wav.AsSpan(payloadStart, checked((int)chunkSize)).ToArray();
            }

            var next = payloadEnd + (chunkSize & 1);
            if (next <= position || next > wav.Length)
                throw new InvalidDataException("Qwen TTS returned invalid WAV chunk boundaries.");
            position = checked((int)next);
        }

        if (format is null || data is null || format.Value.AudioFormat != 1 ||
            format.Value.Channels == 0 || format.Value.BitsPerSample != 16 || data.Length == 0)
            throw new InvalidDataException("Qwen TTS returned unsupported WAV audio.");

        return new ParsedWav(format.Value, data);
    }

    private readonly record struct WavFormat(
        ushort AudioFormat,
        ushort Channels,
        uint SampleRate,
        uint ByteRate,
        ushort BlockAlign,
        ushort BitsPerSample);

    private sealed record ParsedWav(WavFormat Format, byte[] Data);
}
