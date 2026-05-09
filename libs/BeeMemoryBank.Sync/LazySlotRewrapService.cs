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
                SyncEvent? commitEvent;
                using (var loadConn = connFactory.CreateConnection())
                {
                    commitEvent = await loadConn.QuerySingleOrDefaultAsync<SyncEvent>(
                        @"SELECT event_id AS EventId, node_id AS NodeId, lamport_ts AS LamportTs,
                                 event_type AS EventType, payload AS Payload, signature AS Signature,
                                 protocol_version AS ProtocolVersion, created_at AS CreatedAt,
                                 entity_id AS EntityId
                          FROM tbl_event WHERE event_id = @EventId COLLATE NOCASE",
                        new { EventId = rotation.EventId });
                }

                if (commitEvent == null || commitEvent.EventType != EventTypes.DekRotationCommit)
                    continue;

                DekRotationCommitPayload? payload;
                try
                {
                    payload = JsonSerializer.Deserialize<DekRotationCommitPayload>(commitEvent.Payload, JsonOpts);
                }
                catch
                {
                    continue;
                }
                if (payload == null) continue;

                byte[] encNewDek;
                byte[] ivBytes;
                try
                {
                    encNewDek = Convert.FromBase64String(payload.EncryptedNewDek);
                    ivBytes = Convert.FromBase64String(payload.Iv);
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
}
