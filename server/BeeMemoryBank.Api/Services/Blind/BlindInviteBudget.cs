using System.Net;
using System.Net.Sockets;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// A process-wide ceiling on the work one public route may cause, in front of everything that costs something (reading the body, parsing JSON, verifying a
/// signature, a database read). The per-caller limiter counts attempts of ONE address; an attacker with many addresses (a whole IPv6 prefix, rented machines) stays
/// under every per-address budget and still adds up, so this is the other half: a token bucket (a burst, then a steady rate) and a cap on requests being served at
/// the same moment. Refusing costs one lock and a comparison.
///
/// <para>A refusal says how long to wait: <c>429</c> with the time until a token is back, <c>503</c> with a second when the node is busy. Legitimate use is a
/// handful of devices (an owner holds at most 20 open invitations), far below every default here; the ceiling exists for the day somebody is not legitimate.</para>
/// </summary>
public sealed class PublicRouteBudget
{
    private readonly TimeProvider _clock;
    private readonly double _burst;
    private readonly double _refillPerSecond;
    private readonly int _maxConcurrent;
    private readonly object _lock = new();
    private double _tokens;
    private long _stamp;
    private int _inFlight;

    /// <param name="burst">Requests accepted at once from a full bucket.</param>
    /// <param name="refillPerSecond">The steady rate once the burst is used.</param>
    /// <param name="maxConcurrent">Requests being served at the same time.</param>
    public PublicRouteBudget(int burst, double refillPerSecond, int maxConcurrent, TimeProvider? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(burst, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(refillPerSecond, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrent, 1);
        _clock = clock ?? TimeProvider.System;
        _burst = burst;
        _refillPerSecond = refillPerSecond;
        _maxConcurrent = maxConcurrent;
        _tokens = burst;
        _stamp = _clock.GetTimestamp();
    }

    /// <summary>What a caller does with the answer of <see cref="TryEnter"/>: serve and dispose, or refuse with <see cref="Status"/> and <see cref="RetryAfterSeconds"/>.</summary>
    public sealed class Entry : IDisposable
    {
        private PublicRouteBudget? _owner;

        internal Entry(PublicRouteBudget? owner, int status, int retryAfterSeconds)
        {
            _owner = owner;
            Status = status;
            RetryAfterSeconds = retryAfterSeconds;
        }

        public bool Allowed => Status == 0;

        /// <summary>0 when allowed, else the HTTP status to refuse with: 429 (the rate) or 503 (too many at once).</summary>
        public int Status { get; }

        public int RetryAfterSeconds { get; }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Leave();
    }

    /// <summary>Takes a slot and a token, or says why not. Dispose the entry when the request has been served.</summary>
    public Entry TryEnter()
    {
        if (Interlocked.Increment(ref _inFlight) > _maxConcurrent)
        {
            Leave();
            return new Entry(null, 503, 1);
        }

        lock (_lock)
        {
            var now = _clock.GetTimestamp();
            _tokens = Math.Min(_burst, _tokens + _clock.GetElapsedTime(_stamp, now).TotalSeconds * _refillPerSecond);
            _stamp = now;
            if (_tokens >= 1)
            {
                _tokens -= 1;
                return new Entry(this, 0, 0);
            }
            var wait = (int)Math.Ceiling((1 - _tokens) / _refillPerSecond);
            Leave();
            return new Entry(null, 429, Math.Max(1, wait));
        }
    }

    /// <summary>Requests being served now. Test/diagnostic helper.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    private void Leave() => Interlocked.Decrement(ref _inFlight);
}

/// <summary>
/// The invitations that are open now, as the public routes need to know them before they read a body: the ids of the rows that are issued (and not run out) or redeemed
/// or confirming (and the owner's time not run out), at most as many as a node may hold (<see cref="BlindInviteService.MaxOpen"/>). The service replaces the set after every
/// change of a row and at start. A request that names one of these ids is served from a small budget of its own per id (the RESERVED lane of
/// <see cref="BlindInviteRateLimits.Admit"/>), so that a flood of requests naming ids that do not exist - which is all an attacker can send - cannot use up what the friend who
/// holds a real invitation needs.
/// </summary>
public sealed class OpenInviteIds
{
    private volatile Dictionary<string, DateTime> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(BlindInviteRoute Route, string Id), PublicRouteBudget> _lanes = new();

    /// <summary>Replaces the set with these ids and the time after which each is no longer open; the reserved budgets of ids that left it are dropped.</summary>
    public void Replace(IEnumerable<(string Id, DateTime Until)> open)
    {
        var next = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var (id, until) in open) next[id] = until;
        _open = next;
        foreach (var lane in _lanes.Keys)
            if (!next.ContainsKey(lane.Id)) _lanes.TryRemove(lane, out _);
    }

    public bool IsOpen(string inviteId, DateTime nowUtc) => _open.TryGetValue(inviteId, out var until) && until > nowUtc;

    public int Count => _open.Count;

    public PublicRouteBudget Lane(BlindInviteRoute route, string inviteId, Func<PublicRouteBudget> create) => _lanes.GetOrAdd((route, inviteId), _ => create());

    public void Clear()
    {
        _open = [];
        _lanes.Clear();
    }
}

/// <summary>Finds the invitation id a public request names, in the first bytes of its body, before any JSON is parsed or any key is touched.</summary>
public static class BlindInviteBodyPeek
{
    /// <summary>How many bytes of the body are looked at: the id is the first member of every body the app writes, a few dozen bytes in.</summary>
    public const int PrefixBytes = 1024;

    /// <summary>
    /// The 22-character base64url text of the <c>inviteId</c> member found in <paramref name="prefix"/>, or null. A scan, not a parse: it only routes the request to
    /// a lane; whether the body is a request at all, and whether the id is an invitation, is decided later by the strict parser and the table. Bounded by the prefix.
    /// </summary>
    public static string? InviteId(ReadOnlySpan<byte> prefix)
    {
        var at = prefix.IndexOf("\"inviteId\""u8);
        if (at < 0) return null;
        var i = at + "\"inviteId\"".Length;
        i = SkipSpace(prefix, i);
        if (i >= prefix.Length || prefix[i] != (byte)':') return null;
        i = SkipSpace(prefix, i + 1);
        if (i >= prefix.Length || prefix[i] != (byte)'"') return null;
        var start = ++i;
        while (i < prefix.Length && (char.IsAsciiLetterOrDigit((char)prefix[i]) || prefix[i] is (byte)'-' or (byte)'_')) i++;
        if (i >= prefix.Length || prefix[i] != (byte)'"' || i - start != 22) return null;
        return System.Text.Encoding.ASCII.GetString(prefix[start..i]);
    }

    private static int SkipSpace(ReadOnlySpan<byte> bytes, int i)
    {
        while (i < bytes.Length && bytes[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
        return i;
    }
}

/// <summary>The two public routes with a per-caller budget and a process-wide one.</summary>
public enum BlindInviteRoute { Redeem, Status }

/// <summary>
/// The answer of <see cref="BlindInviteRateLimits.Admit"/>: serve the request (and dispose this when it has been served), or refuse it with
/// <see cref="Status"/> (429 or 503) and a <c>Retry-After</c> of <see cref="RetryAfterSeconds"/>.
/// </summary>
public sealed class BlindInviteAdmission : IDisposable
{
    private readonly PublicRouteBudget.Entry? _entry;

    private BlindInviteAdmission(PublicRouteBudget.Entry? entry, int status, int retryAfterSeconds, string answer)
    {
        _entry = entry;
        Status = status;
        RetryAfterSeconds = retryAfterSeconds;
        Answer = answer;
    }

    public bool Allowed => Status == 0;
    public int Status { get; }
    public int RetryAfterSeconds { get; }
    /// <summary>The words of a refusal (a fixed sentence, nothing of the request).</summary>
    public string Answer { get; }

    internal static BlindInviteAdmission Admitted(PublicRouteBudget.Entry entry) => new(entry, 0, 0, "");

    internal static BlindInviteAdmission Refused(int status, int retryAfterSeconds, string answer) => new(null, status, retryAfterSeconds, answer);

    public void Dispose() => _entry?.Dispose();
}

/// <summary>Who a public invitation request counts against.</summary>
public static class BlindInviteCaller
{
    /// <summary>
    /// The key of the per-caller budgets: an IPv4 address as it is, an IPv6 address by its /64 (the smallest piece a provider hands to one subscriber: a single
    /// machine owns millions of addresses of it, and a budget per address would be a budget per attempt), an IPv4-mapped IPv6 address as the IPv4 one. The address is
    /// the connection's, which behind a proxy is the real client only where the deployment declared the proxy trusted (<c>BMB_TRUSTED_PROXIES</c>); undeclared, every
    /// caller shares the proxy's address and its budget, which fails closed.
    /// </summary>
    public static string KeyOf(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString() + "/64";
    }
}
