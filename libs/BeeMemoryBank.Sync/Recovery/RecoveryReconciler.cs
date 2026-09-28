using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Dapper;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync.Recovery;

/// <summary>
/// Continuous reconciliation on a full node (plan 6.4, 6.8). Two jobs, both "publish only what is
/// missing", so a quiet mesh sends nothing:
/// <list type="bullet">
/// <item>Chain links. For every master DEK this node retired (<see cref="IRetiredMasterDekStore"/>) it
/// can rebuild the pair (retired key, the key that replaced it) from the local chain material and
/// publish the retired key wrapped under its successor. The first run on a node back-fills every
/// rotation it ever applied; the initiator also runs it right after accept.</item>
/// <item>Sealed secrets. A secret still sealed under a retired key is re-sealed under the current one.</item>
/// </list>
/// An existing link or seal counts only if it actually opens and verifies: a forged link that merely
/// carries the right fingerprints must not silence the real one.
/// </summary>
public class RecoveryReconciler(
    SessionService session,
    IRetiredMasterDekStore retiredStore,
    IDbConnectionFactory connFactory,
    RecoveryEventPublisher publisher,
    ILogger<RecoveryReconciler> logger) : IRecoveryReconciler
{
    // Unlock catch-up, the post-rotation call and the Api's watcher can all fire at once.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        if (!session.IsUnlocked) return;
        await Gate.WaitAsync(ct);
        try
        {
            await PublishMissingLinksAsync();
            await ResealSecretsAsync();
        }
        catch (SessionLockedException)
        {
            // Locked mid-run: the next unlock runs it again.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Recovery reconciliation failed; it runs again at the next unlock or rotation");
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task PublishMissingLinksAsync()
    {
        var current = session.GetMasterDek();
        try
        {
            foreach (var (name, wrapped, iv) in retiredStore.ListWrapped())
            {
                var commitId = name[IRetiredMasterDekStore.KeyNamePrefix.Length..];
                var oldDek = NodeDataKeyEnvelope.TryUnwrap(name, wrapped, iv, current);
                if (oldDek == null) continue;
                byte[]? newDek = null;
                try
                {
                    newDek = await SuccessorOfAsync(commitId, oldDek);
                    if (newDek == null)
                    {
                        logger.LogInformation(
                            "Retired DEK of rotation {CommitId} has no local chain material; no link can be published for it", commitId);
                        continue;
                    }

                    var oldFp = DekFingerprint.Of(oldDek);
                    var newFp = DekFingerprint.Of(newDek);
                    if (await HasVerifiedLinkAsync(oldFp, newFp, newDek)) continue;

                    var (linkWrapped, linkIv) = MasterKeyManager.WrapMasterDek(oldDek, newDek);
                    await publisher.PublishAsync(EventTypes.RetiredLinkSet, new RetiredLinkSetPayload(
                        CommitId: commitId,
                        OldFingerprint: oldFp,
                        NewFingerprint: newFp,
                        Wrapped: Convert.ToBase64String(linkWrapped),
                        Iv: Convert.ToBase64String(linkIv)));
                    logger.LogInformation("Published chain link for rotation {CommitId}", commitId);
                }
                finally
                {
                    Array.Clear(oldDek);
                    if (newDek != null) Array.Clear(newDek);
                }
            }
        }
        finally
        {
            Array.Clear(current);
        }
    }

    // The rotation's chain material is the new DEK wrapped under the old one, kept locally by every
    // node that applied it (tbl_dek_rotation_state, migration 020).
    private async Task<byte[]?> SuccessorOfAsync(string commitId, byte[] oldDek)
    {
        using var conn = connFactory.CreateConnection();
        var chain = await conn.QuerySingleOrDefaultAsync<(string? Enc, string? Iv)?>(
            @"SELECT chain_encrypted_new_dek AS Enc, chain_iv AS Iv FROM tbl_dek_rotation_state
              WHERE event_id = @Id COLLATE NOCASE",
            new { Id = commitId });
        if (chain is not { Enc: not null, Iv: not null }) return null;
        try
        {
            return MasterKeyManager.UnwrapMasterDek(
                Convert.FromBase64String(chain.Value.Enc), Convert.FromBase64String(chain.Value.Iv), oldDek);
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private async Task<bool> HasVerifiedLinkAsync(string oldFp, string newFp, byte[] newDek)
    {
        using var conn = connFactory.CreateConnection();
        var links = await conn.QueryAsync<(byte[] Wrapped, byte[] Iv)>(
            "SELECT wrapped, iv FROM tbl_dek_retired_link WHERE old_fingerprint = @Old AND new_fingerprint = @New",
            new { Old = oldFp, New = newFp });
        foreach (var link in links)
        {
            try
            {
                var opened = MasterKeyManager.UnwrapMasterDek(link.Wrapped, link.Iv, newDek);
                var ok = DekFingerprint.Of(opened) == oldFp;
                Array.Clear(opened);
                if (ok) return true;
            }
            catch (System.Security.Cryptography.CryptographicException) { }
        }
        return false;
    }

    private async Task ResealSecretsAsync()
    {
        var candidates = session.GetCandidateDeks();
        try
        {
            if (candidates.Length == 0) return;
            var current = candidates[0];
            var currentFp = DekFingerprint.Of(current);
            var byFingerprint = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var dek in candidates) byFingerprint.TryAdd(DekFingerprint.Of(dek), dek);

            using var conn = connFactory.CreateConnection();
            var stale = await conn.QueryAsync<(string Name, string Fp, byte[] Wrapped, byte[] Iv)>(
                "SELECT name, dek_fingerprint, wrapped, iv FROM tbl_sealed_secret WHERE status = 'A' AND dek_fingerprint <> @Fp ORDER BY name",
                new { Fp = currentFp });

            foreach (var row in stale)
            {
                if (!byFingerprint.TryGetValue(row.Fp, out var oldDek)) continue;
                var secret = SealedSecretCrypto.TryOpen(row.Name, row.Wrapped, row.Iv, oldDek);
                if (secret == null) continue; // a seal that does not open is not ours to carry forward
                try
                {
                    var (wrapped, iv) = SealedSecretCrypto.Seal(row.Name, secret, current);
                    await publisher.PublishAsync(EventTypes.SealedSecretSet, new SealedSecretSetPayload(
                        row.Name, currentFp, Convert.ToBase64String(wrapped), Convert.ToBase64String(iv)));
                    logger.LogInformation("Re-sealed {Name} under the current DEK", row.Name);
                }
                finally
                {
                    Array.Clear(secret);
                }
            }
        }
        finally
        {
            foreach (var dek in candidates) Array.Clear(dek);
        }
    }
}
