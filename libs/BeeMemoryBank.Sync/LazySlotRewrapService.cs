using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

public class LazySlotRewrapService(
    IServiceScopeFactory scopeFactory,
    DbConnectionFactory connFactory,
    IKeySlotRepository keySlotRepo,
    ILogger<LazySlotRewrapService> logger) : ILazySlotRewrapService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<LazyRewrapResult> TryRewrapAsync(
        MasterKeyStore slot,
        byte[] kek,
        byte[] unwrappedDek,
        byte[] currentSentinel)
    {
        using var scope = scopeFactory.CreateScope();
        var stateRepo = scope.ServiceProvider.GetRequiredService<IDekRotationStateRepository>();

        var appliedRotations = await stateRepo.GetByStateAsync(DekRotationState.Applied);
        if (appliedRotations.Count == 0)
        {
            logger.LogWarning("User's key slot {SlotId} sentinel mismatch but no Applied rotations found", slot.SlotId);
            return new LazyRewrapResult(false, null);
        }

        appliedRotations.Sort((a, b) => string.Compare(a.CreatedAt, b.CreatedAt, StringComparison.Ordinal));

        byte[] currentCandidate = (byte[])unwrappedDek.Clone();

        try
        {
            bool reachedTarget = false;

            foreach (var rotation in appliedRotations)
            {
                // The local copy first, the event log only as a fallback.
                //
                // This walk used to read the dek_rotation_commit event out of tbl_event, and those
                // rows do not survive: CompactionService deletes everything at or below the
                // checkpoint, and the initiator compacts automatically right after rotating. Once
                // the row was gone the chain could not be walked, reachedTarget stayed false, and
                // the user whose slot needed re-wrapping could never unlock this node again. The
                // material now lives in tbl_dek_rotation_state, written in the same statement that
                // marked the rotation Applied (see DekRewrapper and migration 020) -- a local table
                // that is never synced and that nothing compacts.
                //
                // The fallback is not dead code: rotations applied before migration 020 have no
                // local copy and never will, so for those the event log is still the only source.
                // If it has already been compacted away, they are in exactly the state they were
                // in before this change -- no worse, and nothing here can make them better.
                var (encB64, ivB64) = await LoadChainMaterialAsync(rotation.EventId);
                if (encB64 == null || ivB64 == null)
                    continue;

                byte[] encNewDek;
                byte[] ivBytes;
                try
                {
                    encNewDek = Convert.FromBase64String(encB64);
                    ivBytes = Convert.FromBase64String(ivB64);
                }
                catch
                {
                    continue;
                }

                byte[] newDek;
                try
                {
                    newDek = MasterKeyManager.UnwrapMasterDek(encNewDek, ivBytes, currentCandidate);
                }
                catch (CryptographicException)
                {
                    Array.Clear(encNewDek, 0, encNewDek.Length);
                    continue;
                }
                finally
                {
                    Array.Clear(encNewDek, 0, encNewDek.Length);
                }

                var prev = currentCandidate;
                currentCandidate = newDek;
                Array.Clear(prev, 0, prev.Length);

                // VerifySentinel decrypts the stored sentinel — direct byte-compare with
                // ComputeSentinel never matches because it uses a fresh random IV each call.
                if (MasterKeyManager.VerifySentinel(currentSentinel, currentCandidate))
                {
                    reachedTarget = true;
                    break;
                }
            }

            if (!reachedTarget)
            {
                logger.LogWarning("User's key slot {SlotId} could not reach current sentinel through Applied rotation chain", slot.SlotId);
                return new LazyRewrapResult(false, null);
            }

            var (newEncDek, newIv) = MasterKeyManager.WrapMasterDek(currentCandidate, kek);
            try
            {
                await keySlotRepo.UpdateSlotKeyAsync(slot.SlotId, newEncDek, newIv);
            }
            finally
            {
                Array.Clear(newEncDek, 0, newEncDek.Length);
                Array.Clear(newIv, 0, newIv.Length);
            }

            logger.LogInformation("Lazy-rewrapped key slot {SlotId} to current DEK epoch", slot.SlotId);

            return new LazyRewrapResult(true, currentCandidate);
        }
        catch
        {
            Array.Clear(currentCandidate, 0, currentCandidate.Length);
            throw;
        }
    }

    /// <summary>
    /// The wrapped-new-DEK and IV for one Applied rotation, as base64, from the local state row if
    /// migration 020 captured it and from the commit event in <c>tbl_event</c> otherwise. Returns
    /// <c>(null, null)</c> when neither has it — the caller skips that link, which is the same
    /// thing it did before when the event was missing.
    /// </summary>
    private async Task<(string? EncryptedNewDekB64, string? IvB64)> LoadChainMaterialAsync(string eventId)
    {
        using var conn = connFactory.CreateConnection();

        var local = await conn.QuerySingleOrDefaultAsync<(string? Enc, string? Iv)?>(
            @"SELECT chain_encrypted_new_dek AS Enc, chain_iv AS Iv
              FROM tbl_dek_rotation_state WHERE event_id = @EventId COLLATE NOCASE",
            new { EventId = eventId });

        if (local is { Enc: not null, Iv: not null })
            return (local.Value.Enc, local.Value.Iv);

        // Pre-020 rotation: fall back to the event this material arrived in, if it is still there.
        var payloadJson = await conn.QuerySingleOrDefaultAsync<string?>(
            @"SELECT payload FROM tbl_event
              WHERE event_id = @EventId COLLATE NOCASE AND event_type = @Type",
            new { EventId = eventId, Type = EventTypes.DekRotationCommit });

        if (payloadJson == null)
        {
            logger.LogWarning(
                "Rotation {EventId} has no locally stored chain material and its commit event is no longer in " +
                "the log (compacted). Any key slot that still needs this link cannot be re-wrapped.",
                eventId);
            return (null, null);
        }

        try
        {
            var payload = JsonSerializer.Deserialize<DekRotationCommitPayload>(payloadJson, JsonOpts);
            return payload == null ? (null, null) : (payload.EncryptedNewDek, payload.Iv);
        }
        catch
        {
            return (null, null);
        }
    }
}
