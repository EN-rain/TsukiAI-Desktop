using FluentAssertions;
using FsCheck;
using FsCheck.Xunit;
using TsukiAI.VoiceChat.Services;

namespace TsukiAI.VoiceChat.Tests.Services;

public sealed class LatencyTrackerTests
{
    [Fact]
    public void RecordLatency_StoresMeasurements()
    {
        var tracker = new LatencyTracker();

        tracker.RecordLatency("llm", TimeSpan.FromMilliseconds(100));
        tracker.RecordLatency("llm", TimeSpan.FromMilliseconds(200));

        var p = tracker.GetPercentiles("llm");
        p.Should().NotBeNull();
        p!.SampleCount.Should().Be(2);
    }

    [Fact]
    public void GetPercentiles_CalculatesExpectedValues()
    {
        var tracker = new LatencyTracker();
        foreach (var n in Enumerable.Range(1, 10))
            tracker.RecordLatency("tts", TimeSpan.FromMilliseconds(n * 10));

        var p = tracker.GetPercentiles("tts");
        p.Should().NotBeNull();
        p!.P50.Should().Be(50);
        p.P95.Should().Be(100);
        p.P99.Should().Be(100);
    }

    [Fact]
    public void SlidingWindow_EvictsStaleSamples()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var tracker = new LatencyTracker(time);

        tracker.RecordLatency("stt", TimeSpan.FromMilliseconds(100));
        time.Advance(TimeSpan.FromMinutes(6));
        tracker.RecordLatency("stt", TimeSpan.FromMilliseconds(120));

        var p = tracker.GetPercentiles("stt");
        p.Should().NotBeNull();
        p!.SampleCount.Should().Be(1);
    }

    [Fact]
    public async Task RecordLatency_IsThreadSafe()
    {
        var tracker = new LatencyTracker();
        await Task.WhenAll(
            Enumerable.Range(0, 500).Select(i => Task.Run(() =>
                tracker.RecordLatency("llm", TimeSpan.FromMilliseconds(i % 100)))));

        var p = tracker.GetPercentiles("llm");
        p.Should().NotBeNull();
        p!.SampleCount.Should().Be(100);
    }

    [Fact]
    public void GetPercentiles_UnknownOperation_ReturnsNull()
    {
        var tracker = new LatencyTracker();
        tracker.GetPercentiles("unknown").Should().BeNull();
    }

    [Property]
    public void Percentiles_AreAscending(NonEmptyArray<int> input)
    {
        var tracker = new LatencyTracker();
        foreach (var value in input.Get)
            tracker.RecordLatency("p", TimeSpan.FromMilliseconds(Math.Abs(value % 5000)));

        var p = tracker.GetPercentiles("p");
        p.Should().NotBeNull();
        p!.P50.Should().BeLessThanOrEqualTo(p.P95);
        p.P95.Should().BeLessThanOrEqualTo(p.P99);
    }

    [Property]
    public void Percentiles_AreWithinSampleBounds(NonEmptyArray<int> input)
    {
        var values = input.Get.Select(v => (double)Math.Abs(v % 5000)).ToArray();
        var tracker = new LatencyTracker();
        foreach (var value in values)
            tracker.RecordLatency("x", TimeSpan.FromMilliseconds(value));

        var p = tracker.GetPercentiles("x");
        p.Should().NotBeNull();
        p!.P50.Should().BeInRange(values.Min(), values.Max());
        p.P95.Should().BeInRange(values.Min(), values.Max());
        p.P99.Should().BeInRange(values.Min(), values.Max());
    }

    [Property]
    public void Eviction_MaintainsWindowSizeConstraints(NonEmptyArray<int> input)
    {
        var tracker = new LatencyTracker();
        foreach (var value in input.Get)
            tracker.RecordLatency("window", TimeSpan.FromMilliseconds(Math.Abs(value % 2000)));

        var p = tracker.GetPercentiles("window");
        p.Should().NotBeNull();
        p!.SampleCount.Should().BeLessThanOrEqualTo(100);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta)
        {
            _now = _now.Add(delta);
        }
    }
}
