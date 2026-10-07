using System.Net;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Hosting.AspNetCore;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// What the "forgot your password" endpoint (<c>POST /api/session/recover-access</c>) has to remember
/// between requests: how many attempts each caller and each username has used, and whether a password
/// change still has to be announced to the mesh.
///
/// <para><b>Throttling.</b> Every attempt costs Argon2id work per recovery slot, and the page that
/// calls it is reachable without signing in. Two buckets, both counted whether or not the attempt
/// succeeds: per client IP (the Web layer passes the real address, after its own forwarded-header
/// handling; it throttles per IP itself too, this is the second layer), and per username
/// (case-insensitive, a name that does not exist counts exactly like one that does — a limiter that
/// only counted real accounts would be an enumeration oracle). The price of the per-username bucket is
/// that someone who knows the name can keep it locked; they gain nothing by it but a delay, and the
/// recovery key itself — 256 random bits — cannot be guessed at this rate.</para>
///
/// <para><b>Announcing.</b> A <c>master_password_changed</c> event is signed with the node's identity
/// key, which sits under the master key, so it can only be written while the vault is open. A reset
/// done on a locked vault (the usual case: the node restarted and nobody remembers the password)
/// remembers the change here and announces it at the next successful sign-in. The memory is the
/// process's: if the node restarts before anyone signs in, that one announcement is skipped, which only
/// means the other nodes' admins are not nudged to change their password too — each node's key slots
/// are its own either way.</para>
/// </summary>
public sealed class RecoveryAccessState
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    // Names longer than this are not usernames; hashing them into the key space would only let a
    // caller grow the dictionary with long strings.
    private const int MaxNameLength = 128;

    private readonly SlidingWindowRateLimiter _perIp = new(MaxAttempts, Window);
    private readonly SlidingWindowRateLimiter _perUser = new(MaxAttempts, Window);

    private readonly object _pendingLock = new();
    private DateTime? _pendingAnnouncement;

    /// <summary>Records one attempt; false when either bucket is used up (nothing is recorded then).</summary>
    public bool TryAcquire(string ip, string username)
    {
        // The IP goes first and a refusal there leaves the username bucket alone: a caller already
        // shut out must not keep spending the budget of the account it is aiming at. The other way
        // round, the IP attempt stays counted when the username bucket refuses — someone rotating
        // usernames from one address is exactly the shape the IP bucket exists for.
        return _perIp.TryAcquire(ip) && _perUser.TryAcquire(UserKey(username));
    }

    /// <summary>A reset worked: the legitimate owner gets a clean slate, the way a good sign-in clears the login bucket.</summary>
    public void Reset(string ip, string username)
    {
        _perIp.Reset(ip);
        _perUser.Reset(UserKey(username));
    }

    /// <summary>The same normalization the buckets use, exposed so a test can read them.</summary>
    public int AttemptsFor(string ip, string username) =>
        Math.Max(_perIp.CountFor(ip), _perUser.CountFor(UserKey(username)));

    private static string UserKey(string username)
    {
        var name = (username ?? "").Trim();
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];
        return name.ToLowerInvariant();
    }

    /// <summary>
    /// The client address as the Web layer saw it, or "unknown". Parsed rather than passed through:
    /// the value ends up in the audit log and in a dictionary key, and only an address belongs in
    /// either.
    /// </summary>
    public static string NormalizeClientIp(string? raw) =>
        IPAddress.TryParse((raw ?? "").Trim(), out var ip) ? ip.ToString() : "unknown";

    /// <summary>Remembers that a password change at <paramref name="changedAt"/> has not been announced yet.</summary>
    public void MarkAnnouncementPending(DateTime changedAt)
    {
        lock (_pendingLock) _pendingAnnouncement = changedAt;
    }

    /// <summary>Takes the pending announcement, if any, so exactly one caller announces it.</summary>
    public DateTime? TakePendingAnnouncement()
    {
        lock (_pendingLock)
        {
            var pending = _pendingAnnouncement;
            _pendingAnnouncement = null;
            return pending;
        }
    }

    /// <summary>
    /// Tells the mesh this node's master password changed, if there is a mesh and the vault is open.
    /// Returns false (and announces nothing) when the vault is locked — the caller then keeps the
    /// change pending. Mirrors the Admin → Security card (KeyEndpoints /change-password): the same
    /// event, the same "changed at" instant on both sides so an echo does not raise the banner here.
    /// </summary>
    public static async Task<bool> TryAnnounceAsync(
        DateTime changedAt,
        SessionService session,
        IWhitelistRepository whitelistRepo,
        IEventLogger eventLogger)
    {
        if (!session.IsUnlocked) return false;

        var peers = await whitelistRepo.GetAllActiveAsync();
        if (peers.Count > 0)
        {
            await eventLogger.LogMasterPasswordChangedAsync(changedAt);
            eventLogger.SignalSync();
        }
        return true;
    }
}
