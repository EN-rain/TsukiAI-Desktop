namespace TsukiAI.Api.Infrastructure;

/// <summary>
/// Bounded login throttling for the single-owner API. A reverse proxy can
/// present one source address for all clients; never trust caller-supplied
/// X-Forwarded-For/CF-Connecting-IP in this guard.
/// </summary>
public sealed class LoginAttemptGuard
{
    private readonly Dictionary<string, Queue<DateTimeOffset>> _attempts = new();
    private readonly object _sync = new();
    private readonly TimeSpan _window;
    private readonly int _maxFailures;
    private readonly Func<DateTimeOffset> _now;

    public LoginAttemptGuard(int maxFailures = 8, TimeSpan? window = null,
        Func<DateTimeOffset>? now = null)
    {
        if (maxFailures < 1) throw new ArgumentOutOfRangeException(nameof(maxFailures));
        _maxFailures = maxFailures;
        _window = window ?? TimeSpan.FromMinutes(5);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    // Reserve the attempt *before* evaluating the password. Splitting check
    // and record allowed concurrent wrong-password requests to bypass the cap.
    public bool TryAcquire(string client)
    {
        lock (_sync)
        {
            Prune(client);
            if (!_attempts.TryGetValue(client, out var attempts))
                _attempts[client] = attempts = new Queue<DateTimeOffset>();
            if (attempts.Count >= _maxFailures) return false;
            attempts.Enqueue(_now());
            return true;
        }
    }

    public void Succeeded(string client)
    {
        lock (_sync) _attempts.Remove(client);
    }

    private void Prune(string client)
    {
        if (!_attempts.TryGetValue(client, out var failures)) return;
        var cutoff = _now() - _window;
        while (failures.Count > 0 && failures.Peek() <= cutoff)
            failures.Dequeue();
        if (failures.Count == 0) _attempts.Remove(client);
    }
}
