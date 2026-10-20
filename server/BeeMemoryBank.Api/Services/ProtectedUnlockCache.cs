using System.Collections.Concurrent;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Short-lived in-memory store of recently-verified per-article passphrases. Its purpose is to make
/// the read→edit handoff seamless: after a user unlocks a protected article, both the View and Edit
/// pages can show its content for the rest of the TTL without re-prompting for the passphrase.
///
/// Security rationale:
///  - The passphrase NEVER leaves the server (it is not returned to the browser, not stored in
///    sessionStorage/localStorage). This is consistent with the API session already holding the
///    master DEK in memory.
///  - View DOES consult this cache (unlike the original stateless-only design) — a fresh unlock
///    now survives a reload/re-navigation for the TTL, trading "walk away and someone reloads stays
///    locked" for "don't re-prompt every few minutes while actively editing." The underlying content
///    is still only ever as exposed as the already-unlocked API session itself.
///  - Keyed by caller identity + article, so one user's unlock cannot unlock another caller's session.
///    For a browser caller the identity includes its Web login session (see ArticleEndpoints.CallerKey),
///    so an unlock in one browser does not open the article in another browser or on another device
///    signed in as the same user.
///  - Entries auto-expire after the TTL and are dropped on explicit re-lock / unprotect / passphrase change.
///  - The TTL is absolute from the last Remember (unlock or save), not sliding on reads — merely
///    viewing the article again does not extend it.
///  - A person may choose another time (15 min .. 8 h) for ONE article; it lives in that article's entry, so
///    it holds across View and Edit and across saves, and is gone with the entry (re-lock, time out, vault lock).
///  - A null caller key means "no stable identity to scope to": every method is then a no-op / miss,
///    so the caller is simply re-prompted (fail closed).
/// </summary>
public sealed class ProtectedUnlockCache
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long a verified password is remembered. 15 minutes unless <c>BMB_PROTECTED_UNLOCK_SECONDS</c> says another (30 s to 1 h):
    /// a short time lets the save that finds the unlock gone be tried without waiting a quarter of an hour.
    /// </summary>
    public static readonly TimeSpan Ttl = ParseTtl(Environment.GetEnvironmentVariable("BMB_PROTECTED_UNLOCK_SECONDS"));

    /// <summary>The configured time, or the default for nothing, a non-number, and anything outside 30 s .. 1 h.</summary>
    public static TimeSpan ParseTtl(string? seconds) =>
        int.TryParse(seconds?.Trim(), out var s) && s is >= 30 and <= 3600 ? TimeSpan.FromSeconds(s) : DefaultTtl;

    /// <summary>The shortest time a person can choose for one article (the slider of the unlock strip).</summary>
    public static readonly TimeSpan MinChosenTtl = TimeSpan.FromMinutes(15);

    /// <summary>The longest time a person can choose for one article.</summary>
    public static readonly TimeSpan MaxChosenTtl = TimeSpan.FromHours(8);

    // ttl is how long this one article keeps its password after each Remember: the node-wide Ttl until a person chooses another time.
    private readonly record struct Entry(string Passphrase, DateTime ExpiresUtc, TimeSpan Ttl);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly Func<DateTime> _now;

    public ProtectedUnlockCache() : this(() => DateTime.UtcNow) { }

    /// <summary>For tests that move the clock (an unlock that has run out cannot be waited for).</summary>
    internal ProtectedUnlockCache(Func<DateTime> utcNow) => _now = utcNow;

    /// <summary>
    /// Caches the passphrase; returns when that entry expires (UTC). A time the person chose for this article
    /// (<see cref="SetDuration"/>) is kept while the entry is alive, so a save or a re-unlock does not take it back
    /// to the default; once the entry has run out or been dropped (the article got locked) the node-wide <see cref="Ttl"/> applies again.
    /// </summary>
    public DateTime Remember(string? callerKey, Guid articleId, string passphrase)
    {
        var now = _now();
        if (callerKey == null) return now.Add(Ttl);
        // Opportunistic sweep so passphrases of articles that were unlocked-but-never-edited don't
        // linger in memory past their TTL (TryGet only evicts the key it's asked about). Remember is
        // infrequent (fires on unlock), so an O(n) pass over the tiny dictionary is cheap.
        foreach (var kv in _entries)
            if (now >= kv.Value.ExpiresUtc)
                _entries.TryRemove(kv.Key, out _);
        var key = Key(callerKey, articleId);
        var ttl = _entries.TryGetValue(key, out var alive) ? alive.Ttl : Ttl;
        var expiresUtc = now.Add(ttl);
        _entries[key] = new Entry(passphrase, expiresUtc, ttl);
        return expiresUtc;
    }

    /// <summary>
    /// Makes this article keep its password for <paramref name="duration"/> from now on (and counts it from now). Only an article that is
    /// unlocked at this moment can be given a time: false when there is nothing remembered (or it just ran out). The time is not stored
    /// anywhere else and goes with the entry.
    /// </summary>
    public bool SetDuration(string? callerKey, Guid articleId, TimeSpan duration, out DateTime expiresUtc)
    {
        expiresUtc = default;
        if (callerKey == null) return false;
        var key = Key(callerKey, articleId);
        var now = _now();
        while (_entries.TryGetValue(key, out var e))
        {
            if (now >= e.ExpiresUtc) { _entries.TryRemove(key, out _); return false; }
            var next = e with { ExpiresUtc = now.Add(duration), Ttl = duration };
            if (_entries.TryUpdate(key, next, e)) { expiresUtc = next.ExpiresUtc; return true; }
        }
        return false;
    }

    /// <summary>The time this article keeps its password after each unlock or save, if it is unlocked now; else null.</summary>
    public TimeSpan? DurationOf(string? callerKey, Guid articleId)
    {
        if (callerKey == null) return null;
        var key = Key(callerKey, articleId);
        return _entries.TryGetValue(key, out var e) && _now() < e.ExpiresUtc ? e.Ttl : null;
    }

    /// <summary>Returns the cached passphrase if still fresh, else null (and evicts the stale entry).</summary>
    public string? TryGet(string? callerKey, Guid articleId) => TryGet(callerKey, articleId, out _);

    /// <summary>
    /// Returns the cached passphrase if still fresh (with its expiry in <paramref name="expiresUtc"/>),
    /// else null (and evicts the stale entry).
    /// </summary>
    public string? TryGet(string? callerKey, Guid articleId, out DateTime expiresUtc)
    {
        expiresUtc = default;
        if (callerKey == null) return null;
        var k = Key(callerKey, articleId);
        if (_entries.TryGetValue(k, out var e))
        {
            if (_now() < e.ExpiresUtc)
            {
                expiresUtc = e.ExpiresUtc;
                return e.Passphrase;
            }
            _entries.TryRemove(k, out _);
        }
        return null;
    }

    public void Forget(string? callerKey, Guid articleId)
    {
        if (callerKey != null) _entries.TryRemove(Key(callerKey, articleId), out _);
    }

    /// <summary>
    /// Wipes every cached passphrase, for every caller and every article. Subscribed to
    /// <see cref="BeeMemoryBank.Core.Services.SessionService.Locked"/> (see
    /// SessionEndpoints.MapSessionEndpoints) so a vault lock also ends this cache's TTL window
    /// immediately, instead of letting it keep handing back plaintext for up to
    /// <see cref="Ttl"/> after the lock. SessionService.Lock() is called from
    /// several places besides the /lock endpoint (node reset, snapshot/network restore, the
    /// process-shutdown hook) — wiring through the event means all of them are covered by this
    /// one subscription rather than each caller having to remember to clear this cache too.
    /// </summary>
    public void Clear() => _entries.Clear();

    private static string Key(string callerKey, Guid articleId) => $"{callerKey}|{articleId}";
}
