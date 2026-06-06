using System.Collections.Concurrent;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Short-lived in-memory store of recently-verified per-article passphrases. Its ONLY purpose is to
/// make the read→edit handoff seamless: after a user unlocks a protected article, the Edit page can
/// open (and save) for ~60s without re-prompting for the passphrase.
///
/// Security rationale:
///  - The passphrase NEVER leaves the server (it is not returned to the browser, not stored in
///    sessionStorage/localStorage). This is consistent with the API session already holding the
///    master DEK in memory.
///  - Deliberately NOT consulted on the View page load — viewing stays stateless, so a reload or
///    re-navigation re-locks ("walk away and someone reloads" remains safe). Only the Edit flow reads it.
///  - Keyed by caller identity + article, so one user's unlock cannot unlock another caller's session.
///  - Entries auto-expire after the TTL and are dropped on explicit re-lock / unprotect / passphrase change.
/// </summary>
public sealed class ProtectedUnlockCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, (string passphrase, DateTime expiresUtc)> _entries = new();

    public void Remember(string callerKey, Guid articleId, string passphrase)
    {
        var now = DateTime.UtcNow;
        // Opportunistic sweep so passphrases of articles that were unlocked-but-never-edited don't
        // linger in memory past their TTL (TryGet only evicts the key it's asked about). Remember is
        // infrequent (fires on unlock), so an O(n) pass over the tiny dictionary is cheap.
        foreach (var kv in _entries)
            if (now >= kv.Value.expiresUtc)
                _entries.TryRemove(kv.Key, out _);
        _entries[Key(callerKey, articleId)] = (passphrase, now.Add(Ttl));
    }

    /// <summary>Returns the cached passphrase if still fresh, else null (and evicts the stale entry).</summary>
    public string? TryGet(string callerKey, Guid articleId)
    {
        var k = Key(callerKey, articleId);
        if (_entries.TryGetValue(k, out var e))
        {
            if (DateTime.UtcNow < e.expiresUtc) return e.passphrase;
            _entries.TryRemove(k, out _);
        }
        return null;
    }

    public void Forget(string callerKey, Guid articleId) => _entries.TryRemove(Key(callerKey, articleId), out _);

    private static string Key(string callerKey, Guid articleId) => $"{callerKey}|{articleId}";
}
