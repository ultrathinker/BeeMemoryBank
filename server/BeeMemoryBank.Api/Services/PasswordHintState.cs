using BeeMemoryBank.Hosting.AspNetCore;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// The throttle of the anonymous password-hint lookup (POST /api/session/password-hint). The endpoint answers for a
/// username before anybody signs in, so it must not become a free directory: it is bounded per client address, with
/// the same <see cref="SlidingWindowRateLimiter"/> the rest of the product uses. The Web layer has its own bucket for
/// the same route class (<c>PublicRateLimitMiddleware.PasswordHint</c>).
///
/// <para>There is deliberately <b>no per-username bucket</b>. It would protect nothing: an enumerator needs one
/// lookup per name to learn whether that name has a hint, so a shared counter never trips for a distributed caller,
/// while anyone could spend ten lookups on a real person's name and lock that person out of their own hint for the
/// whole window. A refusal therefore keys on the address alone and cannot depend on whether a name exists.</para>
///
/// <para>The window is deliberately larger than the recovery-key reset's: a hint lookup is not a credential check
/// (no Argon2id is spent, nothing is unlocked), and a person may mistype their own name a few times. It is still
/// small enough that enumerating a roster of usernames from one address is not practical.</para>
/// </summary>
public sealed class PasswordHintState
{
    public const int MaxAttempts = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>The one sentence for "no hint": an unknown name and a user without one get exactly this.</summary>
    public const string NoHintMessage =
        "No hint is available for this account. Use a recovery key, or ask an administrator to reset the password.";

    private readonly SlidingWindowRateLimiter _perIp = new(MaxAttempts, Window);

    /// <summary>Records one lookup; false when the address has used its budget (nothing is recorded then).</summary>
    public bool TryAcquire(string ip) => _perIp.TryAcquire(ip);

    /// <summary>A successful lookup forgives the address, like a good sign-in clears the login bucket.</summary>
    public void ResetIp(string ip) => _perIp.Reset(ip);

    /// <summary>TEST-ONLY: the limiter is a process-wide singleton; a test that burns a budget must be able to clear it.</summary>
    public void ResetAllForTests() => _perIp.ResetAll();
}