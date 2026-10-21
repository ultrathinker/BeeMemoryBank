using BeeMemoryBank.Hosting.AspNetCore;

namespace BeeMemoryBank.Api.Services.Recovery;

/// <summary>
/// What stops somebody from guessing a restore code (srvfix9, RV7-01): a budget of WRONG codes per source address, shared by the three routes that take a code
/// (<c>restore/package</c>, <c>restore/events</c>, <c>claim</c>). <see cref="MaxFailures"/> wrong codes in <see cref="Window"/> from one address and that address is answered
/// <c>429</c> with a <c>Retry-After</c> - before its next code is looked at, so a blocked address cannot test one more guess, even by accident - until the window has run out.
///
/// <para><b>Why the failures are the ADDRESS's and not the codes'.</b> The node used to count every wrong code against every open code and revoke them all at five. Anybody who could
/// reach the node could therefore revoke the code of a device that was about to restore, with five requests and no token, and the owner had to issue a new one. A code is 80 random
/// bits (16 characters of a 32-letter alphabet), so what has to be stopped is not "five guesses" but "an address that keeps guessing": at five a window an address makes under 1500
/// guesses a day against 2^80, and a stranger can burn nothing but its own budget. Right codes cost nothing, so a device that restores is never stopped by the failures of others, except
/// by an address it shares with them (a NAT) for up to one window.</para>
///
/// <para>The same count takes the other ways to be wrong on the claim route: a body that is not JSON, is not valid, or is longer than its limit is an attempt too, because the node reads
/// it (at most 16 KiB) for a caller it has not identified. In memory, per process, one instance per host, so a restart forgets the counts: the budget is a throttle, not a record.
/// At most <see cref="MaxAddresses"/> addresses are counted at once; an address the table has no room for is not counted (the table is full of addresses that are still counting -
/// ten thousand of them - and refusing a newcomer would let that many addresses lock everybody else out). What stays open: an attacker with that many addresses guesses at most
/// <see cref="MaxFailures"/> times per address and window against 2^80 - not a way to find a code, and nothing it does revokes or spends one.</para>
/// </summary>
public sealed class BlindRestoreCodeThrottle
{
    /// <summary>Wrong codes one address may send in a <see cref="Window"/>.</summary>
    public const int MaxFailures = 5;

    /// <summary>The longest an address that used its budget waits: the window.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>The most addresses counted at once.</summary>
    public const int MaxAddresses = 10_000;

    private readonly SlidingWindowRateLimiter _failures = new(MaxFailures, Window, MaxAddresses);
    private readonly TimeProvider _time;

    public BlindRestoreCodeThrottle(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>True when <paramref name="source"/> has used its budget; <paramref name="retryAfter"/> is when the oldest failure leaves the window (at least a second).</summary>
    public bool IsBlocked(string source, out TimeSpan retryAfter)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (_failures.CountFor(source, now) < MaxFailures)
        {
            retryAfter = TimeSpan.Zero;
            return false;
        }
        retryAfter = _failures.RetryAfter(source, now) ?? Window;
        if (retryAfter < TimeSpan.FromSeconds(1)) retryAfter = TimeSpan.FromSeconds(1);
        return true;
    }

    /// <summary>One more wrong attempt from <paramref name="source"/>.</summary>
    public void Fail(string source) => _failures.TryAcquire(source, _time.GetUtcNow().UtcDateTime);
}
