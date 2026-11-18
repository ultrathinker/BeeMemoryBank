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
                // Do not rely on the dek_rotation_commit event in tbl_event: CompactionService
                // deletes everything at or below the checkpoint, and the initiator compacts right
                // after rotating. Once that row is gone the chain cannot be walked, reachedTarget
                // stays false, and the user whose slot needs re-wrapping can never unlock this node
                // again. The material lives in tbl_dek_rotation_state, written in the same
                // statement that marked the rotation Applied (see DekRewrapper and migration 020)
                // -- a local table that is never synced and that nothing compacts.
                //
                // The fallback is not dead code: rotations applied before migration 020 have no
                // local copy and never will, so for those the event log is still the only source.
                // If it has already been compacted away, nothing here can recover them.
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

            // ADR 0012: the door is written again as a sealed (v2) door for the same secret, through SlotDoors so that a sealed node gets
            // the pending copy before the commit and the rewrite after it. The legacy single slot keeps the v1 wrap: its type changes when
            // LegacyPasswordSlotMigrationService migrates it, and a box is bound to its kind. The row is replaced only while it still holds
            // the door that was opened (a concurrent rewrap wins).
            MasterKeyStore rewrapped;
            if (Doors.NeedsUpgrade(slot) || slot.DoorVersion == Doors.V2)
            {
                rewrapped = Doors.WithDoor(slot, Doors.Seal(slot.SlotType, kek, slot.Salt, currentCandidate));
            }
            else
            {
                var (newEncDek, newIv) = MasterKeyManager.WrapMasterDek(currentCandidate, kek);
                rewrapped = new MasterKeyStore
                {
                    SlotId = slot.SlotId, SlotType = slot.SlotType, Salt = slot.Salt,
                    ArgonMemory = slot.ArgonMemory, ArgonIterations = slot.ArgonIterations,
                    ArgonParallelism = slot.ArgonParallelism, CreatedAt = slot.CreatedAt,
                    EncryptedMasterDek = newEncDek, IV = newIv, DoorVersion = Doors.V1,
                    DekFingerprint = DekFingerprint.Of(currentCandidate),
                };
            }
            var mirror = scope.ServiceProvider.GetService<BeeMemoryBank.Vault.Sealed.IUnlockMaterialMirror>();
            if (!await BeeMemoryBank.Vault.Sealed.SlotDoors.ReplaceSlotDoorAsync(keySlotRepo, mirror, rewrapped, slot.EncryptedMasterDek, logger))
            {
                // Someone replaced the door meanwhile: the DEK reached is still the current one, the door is theirs.
                logger.LogInformation("Key slot {SlotId} was replaced while it was rewrapped; the other write stays", slot.SlotId);
                return new LazyRewrapResult(true, currentCandidate);
            }

            logger.LogInformation("Lazy-rewrapped key slot {SlotId} to current DEK epoch (door format {Version})", slot.SlotId, rewrapped.DoorVersion);

            // The rewrapped slot holds the NEW DEK: publish it as this device's box, or the blind
            // nodes keep only a box for the retired key (plan 6.5).
            await scope.ServiceProvider.GetRequiredService<IRecoveryBoxPublisher>()
                .PublishDeviceBoxAsync(rewrapped, currentCandidate, kek);

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
    /// <c>(null, null)</c> when neither has it — the caller skips that link.
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
