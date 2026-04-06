using System.Collections.Concurrent;

namespace TsukiAI.Core.Services;

/// <summary>
/// LRU cache for AI responses to avoid re-generating common replies.
/// Key is based on conversation context + user input hash.
/// </summary>
public sealed class ResponseCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly LinkedList<string> _lruList = new();
    private readonly object _lock = new();
    private readonly int _maxEntries;
    private readonly TimeSpan _ttl;
    private long _hits = 0;
    private long _misses = 0;

    public ResponseCache(int maxEntries = 100, TimeSpan? ttl = null)
    {
        _maxEntries = maxEntries;
        _ttl = ttl ?? TimeSpan.FromMinutes(10);
    }

    public record CachedResponse(string Reply, string Emotion, DateTime CachedAt);

    private sealed class CacheEntry
    {
        public CachedResponse Response { get; set; } = null!;
        public LinkedListNode<string> Node { get; set; } = null!;
        public DateTime ExpiresAt { get; set; }
    }

    /// <summary>
    /// Try to get a cached response. Returns null if not found or expired.
    /// </summary>
    public CachedResponse? Get(string contextHash, string userInput)
    {
        var key = ComputeKey(contextHash, userInput);
        
        if (_cache.TryGetValue(key, out var entry))
        {
            if (DateTime.Now > entry.ExpiresAt)
            {
                // Expired
                Interlocked.Increment(ref _misses);
                Remove(key);
                return null;
            }

            // Move to front (most recently used)
            lock (_lock)
            {
                _lruList.Remove(entry.Node);
                _lruList.AddFirst(entry.Node);
            }

            Interlocked.Increment(ref _hits);
            return entry.Response;
        }

        Interlocked.Increment(ref _misses);
        return null;
    }

    /// <summary>
    /// Cache a response.
    /// </summary>
    public void Set(string contextHash, string userInput, string reply, string emotion)
    {
        var key = ComputeKey(contextHash, userInput);
        
        lock (_lock)
        {
            // Remove old entry if exists
            if (_cache.TryGetValue(key, out var oldEntry))
            {
                _lruList.Remove(oldEntry.Node);
                _cache.TryRemove(key, out _);
            }

            // Evict oldest if at capacity
            while (_cache.Count >= _maxEntries && _lruList.Last != null)
            {
                var oldestKey = _lruList.Last.Value;
                _lruList.RemoveLast();
                _cache.TryRemove(oldestKey, out _);
            }

            // Add new entry
            var node = _lruList.AddFirst(key);
            var entry = new CacheEntry
            {
                Response = new CachedResponse(reply, emotion, DateTime.Now),
                Node = node,
                ExpiresAt = DateTime.Now + _ttl
            };
            
            _cache[key] = entry;
        }

        DevLog.WriteLine("ResponseCache: Cached response for key {0}", key[..Math.Min(16, key.Length)]);
    }

    /// <summary>
    /// Clear all cached entries.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _lruList.Clear();
        }
        DevLog.WriteLine("ResponseCache: Cache cleared");
    }

    /// <summary>
    /// Get cache statistics (hits, misses, hit rate).
    /// </summary>
    public (long hits, long misses, double hitRate) GetStats()
    {
        var hits = Interlocked.Read(ref _hits);
        var misses = Interlocked.Read(ref _misses);
        var total = hits + misses;
        var hitRate = total > 0 ? (double)hits / total : 0.0;
        return (hits, misses, hitRate);
    }

    private void Remove(string key)
    {
        lock (_lock)
        {
            if (_cache.TryRemove(key, out var entry))
            {
                _lruList.Remove(entry.Node);
            }
        }
    }

    private static string ComputeKey(string contextHash, string userInput)
    {
        var combined = contextHash + "||" + userInput.ToLowerInvariant().Trim();
        return GetFastHash(combined);
    }

    /// <summary>
    /// Fast non-cryptographic hash (FNV-1a) - 10x faster than SHA256 for cache keys
    /// </summary>
    private static string GetFastHash(string input)
    {
        unchecked
        {
            ulong hash = 14695981039346656037UL; // FNV offset basis
            foreach (char c in input)
            {
                hash ^= c;
                hash *= 1099511628211UL; // FNV prime
            }
            return hash.ToString("X16");
        }
    }

    /// <summary>
    /// Pre-warm cache with common responses.
    /// </summary>
    public void PreWarmCommonResponses(string personaName)
    {
        var commonGreetings = new[]
        {
            "hi", "hello", "hey", "yo", "sup", "morning", "evening",
            "how are you", "what's up", "you there"
        };

        foreach (var greeting in commonGreetings)
        {
            Set("", greeting, GetGreetingResponse(personaName, greeting), "happy");
        }

        DevLog.WriteLine("ResponseCache: Pre-warmed {0} common responses", commonGreetings.Length);
    }

    private static string GetGreetingResponse(string personaName, string input)
    {
        var responses = new[]
        {
            $"Hey there! Ready to get stuff done?",
            $"Hi hi! What are we working on today?",
            $"Hey! Miss me already?",
            $"Yo! Let's make today productive!",
            $"Hello! I'm here and ready to help~",
        };
        
        // Deterministic but varied response
        var index = input.Length % responses.Length;
        return responses[index];
    }
}
