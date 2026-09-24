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
///  - A null caller key means "no stable identity to scope to": every method is then a no-op / miss,
///    so the caller is simply re-prompted (fail closed).
/// </summary>
public sealed class ProtectedUnlockCache
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, (string passphrase, DateTime expiresUtc)> _entries = new();

    /// <summary>Caches the passphrase for <see cref="Ttl"/>; returns when that entry expires (UTC).</summary>
    public DateTime Remember(string? callerKey, Guid articleId, string passphrase)
    {
        var now = DateTime.UtcNow;
        var expiresUtc = now.Add(Ttl);
        if (callerKey == null) return expiresUtc;
        // Opportunistic sweep so passphrases of articles that were unlocked-but-never-edited don't
        // linger in memory past their TTL (TryGet only evicts the key it's asked about). Remember is
        // infrequent (fires on unlock), so an O(n) pass over the tiny dictionary is cheap.
        foreach (var kv in _entries)
            if (now >= kv.Value.expiresUtc)
                _entries.TryRemove(kv.Key, out _);
        _entries[Key(callerKey, articleId)] = (passphrase, expiresUtc);
        return expiresUtc;
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
            if (DateTime.UtcNow < e.expiresUtc)
            {
                expiresUtc = e.expiresUtc;
                return e.passphrase;
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
    /// <see cref="Ttl"/> after the lock (finding M8). SessionService.Lock() is called from
    /// several places besides the /lock endpoint (node reset, snapshot/network restore, the
    /// process-shutdown hook) — wiring through the event means all of them are covered by this
    /// one subscription rather than each caller having to remember to clear this cache too.
    /// </summary>
    public void Clear() => _entries.Clear();

    private static string Key(string callerKey, Guid articleId) => $"{callerKey}|{articleId}";
}
