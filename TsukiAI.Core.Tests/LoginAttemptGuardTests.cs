using Xunit;
using TsukiAI.Api.Infrastructure;

namespace TsukiAI.Core.Tests;

public sealed class LoginAttemptGuardTests
{
    [Fact]
    public void EightReservationsBlockUntilWindowExpires()
    {
        var now = DateTimeOffset.UtcNow;
        var guard = new LoginAttemptGuard(now: () => now);
        for (var i = 0; i < 8; i++)
            Assert.True(guard.TryAcquire("proxy"));
        Assert.False(guard.TryAcquire("proxy"));
        Assert.True(guard.TryAcquire("different"));
        now = now.AddMinutes(5).AddSeconds(1);
        Assert.True(guard.TryAcquire("proxy"));
    }

    [Fact]
    public void SuccessfulSignInClearsAttemptHistory()
    {
        var guard = new LoginAttemptGuard(maxFailures: 2);
        Assert.True(guard.TryAcquire("proxy"));
        Assert.True(guard.TryAcquire("proxy"));
        Assert.False(guard.TryAcquire("proxy"));
        guard.Succeeded("proxy");
        Assert.True(guard.TryAcquire("proxy"));
    }

    [Fact]
    public async Task ConcurrentAttemptsCannotBypassTheCap()
    {
        var guard = new LoginAttemptGuard(maxFailures: 5);
        var results = await Task.WhenAll(Enumerable.Range(0, 100)
            .Select(_ => Task.Run(() => guard.TryAcquire("proxy"))));
        Assert.Equal(5, results.Count(allowed => allowed));
    }
}
