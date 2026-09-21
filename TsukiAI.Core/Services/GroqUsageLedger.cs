using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TsukiAI.Core.Services;

/// <summary>
/// Append-only records for actual outbound Groq HTTP attempts. The API and
/// Discord bridge write different files on the same persistent volume.
/// Records contain fingerprints and masked labels, never credentials.
/// </summary>
public sealed class GroqUsageLedger
{
    private const string ApiFile = "groq-usage-api.jsonl";
    private const string BridgeFile = "groq-usage-bridge.jsonl";
    private static readonly ConcurrentDictionary<string, object> FileGates = new(StringComparer.Ordinal);
    private static readonly Lazy<GroqUsageLedger> DefaultLedger =
        new(() => new GroqUsageLedger(SettingsService.GetBaseDir()));
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly string _directory;
    public static GroqUsageLedger Default => DefaultLedger.Value;

    public GroqUsageLedger(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("A persistent usage directory is required.", nameof(directory));
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, ApiFile);
        lock (FileGates.GetOrAdd(path, _ => new object()))
        {
            // A start marker is durable even when the service makes zero calls.
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                Append(path, new UsageEvent("tracking_started", DateTimeOffset.UtcNow, null, null, null));
        }
    }

    public void RecordAttempt(string apiKey, string kind, DateTimeOffset? when = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Cannot count an attempt without a configured key.", nameof(apiKey));
        if (kind is not ("llm" or "stt" or "other"))
            throw new ArgumentOutOfRangeException(nameof(kind));

        var path = Path.Combine(_directory, ApiFile);
        lock (FileGates.GetOrAdd(path, _ => new object()))
        {
            Append(path, new UsageEvent("attempt", (when ?? DateTimeOffset.UtcNow).ToUniversalTime(),
                Fingerprint(apiKey), Mask(apiKey), kind));
        }
    }

    public GroqUsageSnapshot Snapshot(DateOnly utcDate, IEnumerable<string>? configuredKeys = null)
    {
        var dayStart = new DateTimeOffset(utcDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var dayEnd = dayStart.AddDays(1);
        var rows = new Dictionary<string, MutableKeyUsage>(StringComparer.Ordinal);
        foreach (var key in configuredKeys ?? [])
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            var id = Fingerprint(key);
            rows.TryAdd(id, new MutableKeyUsage(id, Mask(key)));
        }

        var api = Read(Path.Combine(_directory, ApiFile), dayStart, dayEnd, rows);
        var bridge = Read(Path.Combine(_directory, BridgeFile), dayStart, dayEnd, rows);
        // The first tracked UTC day may have missing earlier calls. An absent
        // bridge file also means its historical usage is unknown, never zero.
        var complete = api.Started is { } a && bridge.Started is { } b &&
            a <= dayStart && b <= dayStart && api.Valid && bridge.Valid;

        var keys = rows.Values.OrderBy(k => k.KeyId, StringComparer.Ordinal)
            .Select(k => new GroqKeyUsage(k.KeyId, k.Mask, k.Llm, k.Stt, k.Other)).ToArray();
        var llm = keys.Sum(k => k.LlmAttempts);
        var stt = keys.Sum(k => k.SttAttempts);
        var other = keys.Sum(k => k.OtherAttempts);
        return new GroqUsageSnapshot(utcDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "UTC", complete ? "complete" : "partial", complete ? llm + stt + other : null,
            llm + stt + other, llm, stt, other, api.Started, bridge.Started, keys);
    }

    public static string Fingerprint(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    public static string Mask(string key) => key.Length >= 12
        ? key[..Math.Min(4, key.Length - 5)] + "..." + key[^5..]
        : "[configured key]";

    private static void Append(string path, UsageEvent item)
    {
        var line = JsonSerializer.Serialize(item, JsonOptions) + "\n";
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var bytes = Encoding.UTF8.GetBytes(line);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static SourceRead Read(string path, DateTimeOffset from, DateTimeOffset until,
        Dictionary<string, MutableKeyUsage> rows)
    {
        if (!File.Exists(path)) return new SourceRead(null, false);
        DateTimeOffset? started = null;
        var valid = true;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var item = JsonSerializer.Deserialize<UsageEvent>(line, JsonOptions);
                if (item is null || item.TimestampUtc.Offset != TimeSpan.Zero)
                {
                    valid = false;
                    continue;
                }
                if (item.Type == "tracking_started")
                {
                    if (started is null || item.TimestampUtc < started) started = item.TimestampUtc;
                    continue;
                }
                if (item.Type != "attempt" || item.KeyId is not { Length: 64 } id ||
                    !id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                    item.KeyMask is null || item.Kind is not ("llm" or "stt" or "other"))
                {
                    valid = false;
                    continue;
                }
                if (item.TimestampUtc < from || item.TimestampUtc >= until) continue;
                if (!rows.TryGetValue(id, out var row))
                    rows.Add(id, row = new MutableKeyUsage(id, item.KeyMask));
                if (item.Kind == "llm") row.Llm++;
                else if (item.Kind == "stt") row.Stt++;
                else row.Other++;
            }
            catch (JsonException)
            {
                // A concurrent append may expose an incomplete trailing line.
                // Never advertise complete coverage from a damaged record.
                valid = false;
            }
        }
        return new SourceRead(started, valid);
    }

    private sealed class MutableKeyUsage(string keyId, string mask)
    {
        public string KeyId { get; } = keyId;
        public string Mask { get; } = mask;
        public long Llm;
        public long Stt;
        public long Other;
    }

    private sealed record SourceRead(DateTimeOffset? Started, bool Valid);
    private sealed record UsageEvent(string Type, DateTimeOffset TimestampUtc,
        string? KeyId, string? KeyMask, string? Kind);
}

public sealed record GroqKeyUsage(
    [property: JsonPropertyName("key_id")] string KeyId,
    [property: JsonPropertyName("mask")] string Mask,
    [property: JsonPropertyName("llm_attempts")] long LlmAttempts,
    [property: JsonPropertyName("stt_attempts")] long SttAttempts,
    [property: JsonPropertyName("other_attempts")] long OtherAttempts)
{
    [JsonPropertyName("recorded_attempts")]
    public long RecordedAttempts => LlmAttempts + SttAttempts + OtherAttempts;
}

public sealed record GroqUsageSnapshot(
    [property: JsonPropertyName("date_utc")] string DateUtc,
    [property: JsonPropertyName("timezone")] string Timezone,
    [property: JsonPropertyName("coverage")] string Coverage,
    [property: JsonPropertyName("total_attempts")] long? TotalAttempts,
    [property: JsonPropertyName("recorded_total_attempts")] long RecordedTotalAttempts,
    [property: JsonPropertyName("llm_attempts")] long LlmAttempts,
    [property: JsonPropertyName("stt_attempts")] long SttAttempts,
    [property: JsonPropertyName("other_attempts")] long OtherAttempts,
    [property: JsonPropertyName("api_tracking_since_utc")] DateTimeOffset? ApiTrackingSinceUtc,
    [property: JsonPropertyName("bridge_tracking_since_utc")] DateTimeOffset? BridgeTrackingSinceUtc,
    [property: JsonPropertyName("per_key")] IReadOnlyList<GroqKeyUsage> PerKey);

/// <summary>Counts each HTTP send, including retries, before dispatch to Groq.</summary>
public sealed class GroqUsageHttpHandler : DelegatingHandler
{
    private readonly GroqUsageLedger _ledger;
    private readonly string _key;

    public GroqUsageHttpHandler(HttpMessageHandler inner, GroqUsageLedger ledger, string key)
        : base(inner)
    {
        _ledger = ledger;
        _key = key;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested &&
            request.RequestUri?.Host.Equals("api.groq.com", StringComparison.OrdinalIgnoreCase) == true)
        {
            var kind = request.RequestUri.AbsolutePath.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase)
                || request.RequestUri.AbsolutePath.EndsWith("/generate", StringComparison.OrdinalIgnoreCase)
                ? "llm" : "other";
            _ledger.RecordAttempt(_key, kind);
        }
        return base.SendAsync(request, cancellationToken);
    }
}
