namespace BeeMemoryBank.Mobile.Services;

/// <summary>
/// Hands the just-verified per-article passphrase from the detail (read) page to the edit page so the
/// user isn't re-prompted right after unlocking to read. In-memory only, single article, short TTL,
/// consumed on take. Never persisted; mirrors the web's server-side recent-unlock cache, client-side.
/// </summary>
public sealed class MobileUnlockHolder
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);
    private Guid _articleId;
    private string? _passphrase;
    private DateTime _expiresUtc;

    public void Remember(Guid articleId, string passphrase)
    {
        _articleId = articleId;
        _passphrase = passphrase;
        _expiresUtc = DateTime.UtcNow.Add(Ttl);
    }

    /// <summary>Returns and consumes the passphrase if it's for this article and still fresh, else null.</summary>
    public string? Take(Guid articleId)
    {
        if (_passphrase != null && _articleId == articleId && DateTime.UtcNow < _expiresUtc)
        {
            var p = _passphrase;
            Clear();
            return p;
        }
        return null;
    }

    public void Clear()
    {
        _passphrase = null;
        _articleId = default;
    }
}
