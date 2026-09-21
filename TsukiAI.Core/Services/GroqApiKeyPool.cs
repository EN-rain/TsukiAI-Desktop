namespace TsukiAI.Core.Services;

/// <summary>
/// File/environment-backed Groq key rotation for speech requests.
/// Keys are never written to logs; rejected keys are cooled down temporarily.
/// </summary>
public sealed class GroqApiKeyPool
{
    private const int AuthFailureCooldownMinutes = 15;
    private const int RateLimitCooldownSeconds = 30;
    private const int TransientFailureCooldownSeconds = 5;

    private readonly string[] _keys;
    private readonly bool _rotateOnSuccess;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _cooldownUntil = new(StringComparer.Ordinal);
    private int _cursor;

    public GroqApiKeyPool(IEnumerable<string>? keys, bool rotateOnSuccess = false)
    {
        _keys = NormalizeKeys(keys).ToArray();
        _rotateOnSuccess = rotateOnSuccess;
    }

    /// <summary>Use round-robin on successful calls for rate-aware API key distribution.</summary>
    public GroqApiKeyPool WithRoundRobin() => new(_keys, rotateOnSuccess: true);

    public int Count => _keys.Length;

    /// <summary>Configured keys for internal, authenticated metrics labeling.</summary>
    public IReadOnlyList<string> GetAllKeys() => Array.AsReadOnly(_keys);

    /// <summary>
    /// Loads the external file first. If it is missing or empty, the supplied
    /// environment values are parsed in order and de-duplicated.
    /// </summary>
    public static GroqApiKeyPool LoadFromFileOrValues(string? filePath, params string?[] fallbackValues)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            try
            {
                var fileKeys = Parse(File.ReadAllText(filePath.Trim()));
                if (fileKeys.Count > 0)
                    return new GroqApiKeyPool(fileKeys);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                DevLog.WriteLine("Groq key file unavailable; using environment fallback. error={0}", ex.Message);
            }
        }

        return new GroqApiKeyPool(fallbackValues.SelectMany(Parse));
    }

    /// <summary>Parses newline/comma-separated keys and assignment-style values.</summary>
    public static IReadOnlyList<string> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        var keys = new List<string>();
        foreach (var item in raw.Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var value = item.Trim();
            if (value.Length == 0 || value.StartsWith("#", StringComparison.Ordinal))
                continue;

            var equals = value.IndexOf('=');
            if (equals > 0 && IsEnvironmentName(value[..equals].Trim()))
                value = value[(equals + 1)..].Trim();

            value = value.Trim().Trim('"', '\'');
            if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                value = value[7..].Trim();

            if (value.Length > 0 && !keys.Contains(value, StringComparer.Ordinal))
                keys.Add(value);
        }

        return keys;
    }

    /// <summary>Returns the current key first, excluding cooled-down keys when possible.</summary>
    public IReadOnlyList<string> GetCandidates(DateTimeOffset? now = null)
    {
        lock (_gate)
        {
            if (_keys.Length == 0)
                return [];

            var at = now ?? DateTimeOffset.UtcNow;
            var ordered = OrderedKeys();
            var available = ordered
                .Where(key => !_cooldownUntil.TryGetValue(key, out var until) || until <= at)
                .ToList();

            return available.Count > 0 ? available : ordered;
        }
    }

    /// <summary>
    /// LLM retries only eligible keys: when every key is cooling down, the
    /// current turn fails fast instead of hammering the entire rejected pool.
    /// GetCandidates retains its legacy fallback behavior for STT.
    /// </summary>
    public IReadOnlyList<string> GetAvailableCandidates(DateTimeOffset? now = null)
    {
        lock (_gate)
        {
            if (_keys.Length == 0)
                return [];

            var at = now ?? DateTimeOffset.UtcNow;
            return OrderedKeys()
                .Where(key => !_cooldownUntil.TryGetValue(key, out var until) || until <= at)
                .ToList();
        }
    }

    public void MarkSuccess(string key)
    {
        lock (_gate)
        {
            var index = Array.IndexOf(_keys, key);
            if (index < 0)
                return;

            _cursor = _rotateOnSuccess ? (index + 1) % _keys.Length : index;
            _cooldownUntil.Remove(key);
        }
    }

    public void MarkFailure(string key, int statusCode, TimeSpan? retryAfter = null)
    {
        lock (_gate)
        {
            var index = Array.IndexOf(_keys, key);
            if (index < 0)
                return;

            _cursor = (index + 1) % _keys.Length;
            var cooldown = statusCode is 401 or 403
                ? TimeSpan.FromMinutes(AuthFailureCooldownMinutes)
                : statusCode == 429
                    ? TimeSpan.FromSeconds(RateLimitCooldownSeconds)
                    : TimeSpan.FromSeconds(TransientFailureCooldownSeconds);
            if (statusCode == 429 && retryAfter is { } requested && requested > cooldown)
                cooldown = requested;
            _cooldownUntil[key] = DateTimeOffset.UtcNow.Add(cooldown);
        }
    }

    private List<string> OrderedKeys()
    {
        var ordered = new List<string>(_keys.Length);
        for (var offset = 0; offset < _keys.Length; offset++)
            ordered.Add(_keys[(_cursor + offset) % _keys.Length]);
        return ordered;
    }

    private static IEnumerable<string> NormalizeKeys(IEnumerable<string>? keys)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in keys ?? [])
        {
            foreach (var parsed in Parse(raw))
            {
                if (seen.Add(parsed))
                    yield return parsed;
            }
        }
    }

    private static bool IsEnvironmentName(string value)
    {
        if (value.Length == 0 || !(char.IsLetter(value[0]) || value[0] == '_'))
            return false;

        return value.Skip(1).All(c => char.IsLetterOrDigit(c) || c == '_');
    }
}
