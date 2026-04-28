using CimsApp.Services.Auth;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace CimsApp.Tests.Services.Auth;

/// <summary>
/// B-002 progressive back-off: <see cref="LoginAttemptTracker"/>
/// rules. Pure tests on the tracker itself; integration with
/// <see cref="CimsApp.Services.AuthService.LoginAsync"/> covered
/// by the service-level tests in `LoginProgressiveBackOffTests`.
/// </summary>
public class LoginAttemptTrackerTests
{
    private static LoginAttemptTracker NewTracker(int threshold = 5,
        TimeSpan? window = null) =>
        new(new MemoryCache(new MemoryCacheOptions()), threshold, window);

    [Fact]
    public void Fresh_ip_is_not_locked_out()
    {
        Assert.False(NewTracker().IsLockedOut("203.0.113.7"));
    }

    [Fact]
    public void Locks_out_after_threshold_consecutive_failures()
    {
        var t = NewTracker(threshold: 3);
        t.RecordFailure("1.1.1.1");
        t.RecordFailure("1.1.1.1");
        Assert.False(t.IsLockedOut("1.1.1.1"));
        t.RecordFailure("1.1.1.1");
        Assert.True(t.IsLockedOut("1.1.1.1"));
    }

    [Fact]
    public void Lockout_is_per_ip()
    {
        var t = NewTracker(threshold: 2);
        t.RecordFailure("1.1.1.1");
        t.RecordFailure("1.1.1.1");
        Assert.True(t.IsLockedOut("1.1.1.1"));
        // A different IP is unaffected — credential testing from
        // 1.1.1.1 doesn't punish 2.2.2.2.
        Assert.False(t.IsLockedOut("2.2.2.2"));
    }

    [Fact]
    public void Success_clears_the_counter()
    {
        var t = NewTracker(threshold: 3);
        t.RecordFailure("1.1.1.1");
        t.RecordFailure("1.1.1.1");
        t.RecordSuccess("1.1.1.1");
        // After success, two more failures should NOT trigger lockout
        // (counter started fresh at zero).
        t.RecordFailure("1.1.1.1");
        t.RecordFailure("1.1.1.1");
        Assert.False(t.IsLockedOut("1.1.1.1"));
    }

    [Fact]
    public void Window_aging_is_a_sliding_window_via_cache_expiry()
    {
        // Use a 50ms window for a tight test. Each recorded failure
        // re-sets the window; once we stop recording for the full
        // window, the entry expires and IsLockedOut returns false.
        var t = NewTracker(threshold: 1, window: TimeSpan.FromMilliseconds(50));
        t.RecordFailure("1.1.1.1");
        Assert.True(t.IsLockedOut("1.1.1.1"));
        // Wait long enough for the cache entry to expire.
        Thread.Sleep(150);
        Assert.False(t.IsLockedOut("1.1.1.1"));
    }
}
