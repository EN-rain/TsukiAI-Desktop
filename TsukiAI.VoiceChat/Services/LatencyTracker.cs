using System.Collections.Concurrent;

namespace TsukiAI.VoiceChat.Services;

public sealed record LatencyPercentiles(double P50, double P95, double P99, int SampleCount);

public sealed class LatencyTracker
{
    private readonly ConcurrentDictionary<string, SlidingWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;
    private const int MaxSamples = 100;
    private static readonly TimeSpan SampleTtl = TimeSpan.FromMinutes(5);

    public LatencyTracker(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void RecordLatency(string operationName, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(operationName))
            return;

        var durationMs = Math.Max(0, duration.TotalMilliseconds);
        var window = _windows.GetOrAdd(operationName.Trim(), _ => new SlidingWindow(_timeProvider));
        window.Add(durationMs);
    }

    public LatencyPercentiles? GetPercentiles(string operationName)
    {
        if (string.IsNullOrWhiteSpace(operationName))
            return null;

        if (!_windows.TryGetValue(operationName.Trim(), out var window))
            return null;

        return window.CalculatePercentiles();
    }

    private sealed class SlidingWindow
    {
        private readonly TimeProvider _timeProvider;
        private readonly Queue<(DateTimeOffset Timestamp, double DurationMs)> _samples = new();
        private readonly object _lock = new();

        public SlidingWindow(TimeProvider timeProvider)
        {
            _timeProvider = timeProvider;
        }

        public void Add(double durationMs)
        {
            lock (_lock)
            {
                EvictStale();
                _samples.Enqueue((_timeProvider.GetUtcNow(), durationMs));
                while (_samples.Count > MaxSamples)
                    _samples.Dequeue();
            }
        }

        public LatencyPercentiles? CalculatePercentiles()
        {
            lock (_lock)
            {
                EvictStale();
                if (_samples.Count == 0)
                    return null;

                var values = _samples.Select(s => s.DurationMs).OrderBy(v => v).ToArray();
                return new LatencyPercentiles(
                    GetPercentile(values, 0.50),
                    GetPercentile(values, 0.95),
                    GetPercentile(values, 0.99),
                    values.Length);
            }
        }

        private void EvictStale()
        {
            var now = _timeProvider.GetUtcNow();
            while (_samples.Count > 0 && now - _samples.Peek().Timestamp > SampleTtl)
                _samples.Dequeue();
        }

        private static double GetPercentile(double[] sorted, double percentile)
        {
            if (sorted.Length == 1)
                return sorted[0];

            var index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
            return sorted[index];
        }
    }
}
