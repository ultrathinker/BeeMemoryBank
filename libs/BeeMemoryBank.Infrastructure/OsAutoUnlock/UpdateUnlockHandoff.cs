using System.Buffers.Binary;
using System.Security.Cryptography;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Infrastructure.Secrets;

namespace BeeMemoryBank.Infrastructure.OsAutoUnlock;

/// <summary>
/// Carries an unlocked vault across the restart of a desktop app update, so the user is not asked
/// for the password again just because the app replaced its own files.
///
/// <para>Right before the desktop app stops the node to apply an update, it asks the running API
/// to <see cref="WriteAsync"/>: the master DEK goes to <c>&lt;dataPath&gt;/update-unlock.dat</c>,
/// protected by the platform's <see cref="IUserSecretStore"/> (Windows DPAPI for the current user
/// with application entropy; a macOS Keychain item) together with an expiry a few minutes
/// ahead. The next API start calls <see cref="TryConsumeAsync"/> before anything else can unlock:
/// the file is deleted first and used only if the delete succeeded, then decrypted, checked for
/// expiry and verified against the sentinel.</para>
///
/// <para>Why this is not a weaker OS auto-unlock: the file exists for the ~1–2 minutes between the
/// stop and the start, only after an explicit "update now", and is gone on the first start
/// whether it was used or not. During that window it is protected exactly like the
/// <see cref="OsAutoUnlockService"/> secret — by the Windows user account — and anyone who can
/// read it as that user could equally have read the DEK from the running, unlocked process a
/// moment earlier. A handoff that is never consumed (the app is not started again) stays on disk
/// DPAPI-protected and expires; the next start deletes it.</para>
/// </summary>
public sealed class UpdateUnlockHandoff(SessionService session, string dataPath, Func<DateTime>? utcNow = null, IUserSecretStore? secretStore = null)
{
    private readonly IUserSecretStore _secretStore = secretStore ?? UserSecretStores.CreateDefault(dataPath);
    /// <summary>How long a handoff stays usable. An update restart takes about a minute.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private static readonly byte[] Entropy = "BeeMemoryBank.UpdateUnlockHandoff.v1"u8.ToArray();
    private const byte FormatVersion = 1;
    private const int HeaderLength = 1 + sizeof(long);

    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);

    public string FilePath => Path.Combine(dataPath, "update-unlock.dat");
    public bool IsSupported => _secretStore.IsSupported;

    /// <summary>
    /// Writes the handoff for the next start. Returns <c>false</c> (and writes nothing) when the
    /// session is locked — there is nothing to carry over.
    /// </summary>
    public async Task<bool> WriteAsync()
    {
        if (!_secretStore.IsSupported || !session.IsUnlocked) return false;

        byte[] dek;
        try { dek = session.GetMasterDek(); }
        catch (SessionLockedException) { return false; }

        var payload = new byte[HeaderLength + dek.Length];
        try
        {
            payload[0] = FormatVersion;
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(1), (_utcNow() + Lifetime).Ticks);
            dek.CopyTo(payload, HeaderLength);
            _secretStore.Write("update-unlock", "default", payload);
            return true;
        }
        finally
        {
            Array.Clear(dek);
            Array.Clear(payload);
        }
    }

    /// <summary>
    /// Unlocks the session from a pending handoff, if there is a valid one. The file is removed in
    /// every case; a handoff that cannot be removed is not used. Never throws.
    /// </summary>
    public async Task<bool> TryConsumeAsync(INodeIdentityRepository nodeRepo)
    {
        if (!_secretStore.IsSupported) return false;

        byte[]? payload;
        try
        {
            payload = _secretStore.Read("update-unlock", "default");
            if (payload is null) return false;
            // One-use means delete before inspecting or unlocking; a delete failure is fail-closed.
            _secretStore.Delete("update-unlock", "default");
        }
        catch
        {
            try { _secretStore.Delete("update-unlock", "default"); } catch { }
            return false;
        }

        if (session.IsUnlocked) return true;

        try
        {
            if (payload.Length <= HeaderLength || payload[0] != FormatVersion) return false;

            var expiresUtc = new DateTime(BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(1)), DateTimeKind.Utc);
            var now = _utcNow();
            // Also refuse an expiry further out than one lifetime: a clock that jumped backwards
            // must not stretch the window.
            if (now >= expiresUtc || expiresUtc - now > Lifetime) return false;

            var dek = payload.AsSpan(HeaderLength).ToArray();
            var sentinel = await nodeRepo.GetSentinelAsync();
            if (sentinel == null || !MasterKeyManager.VerifySentinel(sentinel, dek))
            {
                Array.Clear(dek);
                return false;
            }

            session.UnlockWithDek(dek); // ownership transfers
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Array.Clear(payload);
        }
    }
}
