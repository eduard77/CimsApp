using Microsoft.Extensions.Caching.Memory;

namespace CimsApp.Services.Auth;

/// <summary>
/// B-002 progressive-back-off complement to the rate-limiter.
/// Where `anon-login` (ASP.NET Core RateLimiting) is a hard
/// requests-per-minute cap on the endpoint regardless of outcome,
/// this tracker tightens the screw on credential-testing patterns
/// specifically: it counts CONSECUTIVE FAILED login attempts per
/// caller IP and locks the IP out once a threshold is breached
/// within a sliding window. A successful login resets the counter.
///
/// Defaults: 5 failures within a 15-minute sliding window → locked
/// out for the remaining window. Tuned for v1.0 internal use; both
/// numbers are constructor-injected so tests can dial them in
/// without sleeping.
/// </summary>
public interface ILoginAttemptTracker
{
    bool IsLockedOut(string ipAddress);
    void RecordFailure(string ipAddress);
    void RecordSuccess(string ipAddress);
}

public class LoginAttemptTracker(
    IMemoryCache cache,
    int lockoutThreshold = 5,
    TimeSpan? window = null) : ILoginAttemptTracker
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromMinutes(15);

    public bool IsLockedOut(string ipAddress) =>
        cache.TryGetValue(KeyFor(ipAddress), out int count)
        && count >= lockoutThreshold;

    public void RecordFailure(string ipAddress)
    {
        var key = KeyFor(ipAddress);
        cache.TryGetValue(key, out int count);
        // Re-set with the same window each time. This makes the
        // window "sliding" — repeated failures keep the lockout
        // alive; once the IP goes quiet for a full window, the
        // counter ages out.
        cache.Set(key, count + 1, _window);
    }

    public void RecordSuccess(string ipAddress) =>
        cache.Remove(KeyFor(ipAddress));

    private static string KeyFor(string ip) => $"login-fail:{ip}";
}
