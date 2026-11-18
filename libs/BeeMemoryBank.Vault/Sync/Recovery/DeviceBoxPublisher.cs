using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Metadata;
using Dapper;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync.Recovery;

/// <summary>
/// Publishes a freshly written key slot as this node's device box (plan 6.3). The box IS the slot —
/// same salt, same parameters, same wrapped bytes — so it opens with exactly the password that unlocks
/// this device and costs nothing to build. A newer device box of this node supersedes the older one
/// everywhere (one active box per author and kind), so boxes for old passwords do not pile up.
///
/// <para>Sealed doors (2.12, ADR 0012, plan WS-A4): a v2 user door is published as <c>0x02 || door_pk || box</c> beside the same salt and
/// preset, but only once every active peer (full and blind) has shown the capability <c>door-box-v1</c>: an older node could not open it.
/// Until then a caller that holds the password's KEK publishes the old shape (the DEK wrapped under that KEK), and a rotation publishes
/// nothing (<see cref="RepublishForDoorsAsync"/>).</para>
/// </summary>
public class DeviceBoxPublisher(
    RecoveryEventPublisher publisher,
    INodeIdentityRepository nodeRepo,
    ILogger<DeviceBoxPublisher> logger,
    IWhitelistRepository? whitelist = null,
    IKeySlotRepository? keySlots = null,
    IDbConnectionFactory? db = null) : IRecoveryBoxPublisher
{
    public Task PublishDeviceBoxAsync(MasterKeyStore slot, byte[] dek) => PublishDeviceBoxAsync(slot, dek, null);

    public async Task PublishDeviceBoxAsync(MasterKeyStore slot, byte[] dek, byte[]? kek)
    {
        try
        {
            // Only password slots are a master password. Recovery-key and OS auto-unlock slots open the
            // vault with something else, and putting those on a blind node would hand out a second,
            // weaker way in that no password change ever revokes.
            if (slot.SlotType is not ("user" or "password"))
                return;

            var preset = RecoveryBoxKdf.PresetForSlot(
                slot.ArgonMemory ?? 0, slot.ArgonIterations ?? 0, slot.ArgonParallelism ?? 0);
            byte[]? wrapped;
            byte[] iv = slot.IV;
            if (slot.DoorVersion != Doors.V2)
            {
                wrapped = slot.EncryptedMasterDek;   // a v1 slot is copied as it is
            }
            else if (await EveryPeerOpensV2BoxesAsync())
            {
                wrapped = slot.SlotType == KeySlotTypes.User && slot.DoorPublicKey is { Length: DoorBox.PublicKeySize } && DoorBox.IsWellFormed(slot.EncryptedMasterDek)
                    ? RecoveryBoxKdf.DoorV2Wrapped(slot.DoorPublicKey, slot.EncryptedMasterDek)
                    : null;
            }
            else if (kek is { Length: CryptoConstants.KeySize })
            {
                // A peer cannot open a v2 box yet: the old shape, from the password this caller holds.
                (wrapped, iv) = MasterKeyManager.WrapMasterDek(dek, kek);
            }
            else
            {
                logger.LogInformation("Key slot {SlotId} is a sealed door and a peer cannot open such a box yet; no device box published now", slot.SlotId);
                return;
            }

            if (preset == null || wrapped == null || !RecoveryBoxKdf.IsWellFormed(RecoveryBoxKdf.KindDevice, slot.Salt, wrapped, iv))
            {
                logger.LogWarning("Key slot {SlotId} has non-default parameters or shape; no device box published", slot.SlotId);
                return;
            }

            var identity = await nodeRepo.GetAsync()
                ?? throw new InvalidOperationException("Node is not initialized.");

            var boxId = Guid.NewGuid().ToString();
            await publisher.PublishAsync(EventTypes.RecoveryBoxSet, new RecoveryBoxSetPayload(
                BoxId: boxId,
                Kind: RecoveryBoxKdf.KindDevice,
                AuthorNodeId: identity.NodeId.ToString(),
                DekFingerprint: DekFingerprint.Of(dek),
                EpochHint: identity.DekEpoch,
                KdfPreset: preset,
                Salt: Convert.ToBase64String(slot.Salt!),
                Wrapped: Convert.ToBase64String(wrapped),
                Iv: Convert.ToBase64String(iv)), dek);
            await RememberSourceAsync(boxId, slot.SlotId, DekFingerprint.Of(dek));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Publishing the device box for key slot {SlotId} failed", slot.SlotId);
        }
    }

    /// <summary>
    /// Remembers which slot a device box copied, as the answer of "does this slot's password open that box" (the cache the cleanup of covered
    /// boxes reads): true at publish time, with the DEK it holds. The box's salt is scrubbed once it is inactive; this row is how
    /// <see cref="RepublishForDoorsAsync"/> still finds its door. Best effort: without it the salt or rule (2) decides.
    /// </summary>
    private async Task RememberSourceAsync(string boxId, int slotId, string dekFingerprint)
    {
        if (db is null) return;
        try
        {
            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                @"INSERT OR REPLACE INTO tbl_recovery_box_check (box_id, slot_id, opens, dek_fingerprint, checked_at)
                  VALUES (@b, @s, 1, @f, @t)",
                new { b = boxId.ToLowerInvariant(), s = slotId, f = dekFingerprint, t = DateTime.UtcNow.ToString("O") });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("The slot a device box copied could not be recorded ({Type})", ex.GetType().Name);
        }
    }

    public async Task RepublishForDoorsAsync(byte[] dek)
    {
        if (keySlots is null || db is null) return;
        try
        {
            if (!await EveryPeerOpensV2BoxesAsync()) return;   // the v1 box stays; the bridge covers it
            var identity = await nodeRepo.GetAsync() ?? throw new InvalidOperationException("Node is not initialized.");
            var doors = (await keySlots.GetAllAsync())
                .Where(s => s.SlotType == KeySlotTypes.User && s.DoorVersion == Doors.V2 && s.Salt is not null).ToList();
            if (doors.Count == 0) return;

            MasterKeyStore? door = null;
            using (var conn = db.CreateConnection())
            {
                // (0) On a shared non-home node, prefer the primary person (first listed superadmin from accounts_home).
                var homeNodeId = await conn.QueryFirstOrDefaultAsync<string?>(
                    "SELECT node_id FROM tbl_accounts_home WHERE id = 1");
                if (homeNodeId is not null
                    && !string.Equals(homeNodeId, identity.NodeId.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    var primaryPid = await conn.QueryFirstOrDefaultAsync<string?>(
                        "SELECT primary_person_id FROM tbl_shared_doors_state WHERE id = 1");
                    if (!string.IsNullOrWhiteSpace(primaryPid))
                    {
                        var primarySlotId = await conn.QueryFirstOrDefaultAsync<long?>(
                            @"SELECT s.slot_id FROM tbl_key_slot s JOIN tbl_user u ON u.key_slot_id = s.slot_id
                               WHERE s.slot_type = 'user' AND s.door_version = 2 AND u.role = 'superadmin' AND u.is_active = 1
                                 AND u.person_id = @pid COLLATE NOCASE
                               ORDER BY u.id ASC LIMIT 1",
                            new { pid = primaryPid });
                        door = primarySlotId is { } ps ? doors.FirstOrDefault(d => d.SlotId == ps) : null;
                    }
                }

                // (1) The door this node's latest device box copied, active or not. An inactive box (superseded, or retired by a strong
                // box) has its salt scrubbed (EventApplier.ScrubBoxesAsync), so the door is found through the slot recorded when the box was
                // published (tbl_recovery_box_check, see RememberSourceAsync); the salt still finds it for a box published before that.
                if (door is null)
                {
                    var latest = await conn.QueryFirstOrDefaultAsync<(string BoxId, byte[]? Salt)?>(
                        @"SELECT box_id, salt FROM tbl_recovery_box WHERE kind = 'device' AND author_node_id = @me COLLATE NOCASE
                           ORDER BY lamport_ts DESC, created_at DESC LIMIT 1", new { me = identity.NodeId.ToString() });
                    if (latest is { } box)
                    {
                        if (box.Salt is { Length: > 0 } salt)
                            door = doors.FirstOrDefault(d => d.Salt!.AsSpan().SequenceEqual(salt));
                        if (door is null)
                        {
                            var source = await conn.QueryFirstOrDefaultAsync<long?>(
                                "SELECT slot_id FROM tbl_recovery_box_check WHERE box_id = @b COLLATE NOCASE AND opens = 1 ORDER BY checked_at DESC LIMIT 1",
                                new { b = box.BoxId });
                            door = source is { } sourceSlot ? doors.FirstOrDefault(d => d.SlotId == sourceSlot) : null;
                        }
                    }
                }

                // (2) Else the sealed door of the active superadmin who signed in last (never signed in last, then the lowest id).
                if (door is null)
                {
                    var slotId = await conn.QueryFirstOrDefaultAsync<long?>(
                        @"SELECT s.slot_id FROM tbl_key_slot s JOIN tbl_user u ON u.key_slot_id = s.slot_id
                           WHERE s.slot_type = 'user' AND s.door_version = 2 AND u.role = 'superadmin' AND u.is_active = 1
                           ORDER BY u.last_login_at IS NULL, u.last_login_at DESC, u.id ASC LIMIT 1");
                    door = slotId is { } id ? doors.FirstOrDefault(d => d.SlotId == id) : null;
                }
            }
            if (door is null || door.DekFingerprint != DekFingerprint.Of(dek)) return;
            await PublishDeviceBoxAsync(door, dek);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Publishing the device box after a change of doors failed; the next sign-in or rotation publishes it");
        }
    }

    /// <summary>Every active peer, full and blind, has shown <c>door-box-v1</c> (this node's own observations). No peers: true.</summary>
    private async Task<bool> EveryPeerOpensV2BoxesAsync()
    {
        if (whitelist is null) return true;
        var peers = await whitelist.GetAllActiveAsync();
        return peers.All(p => MetadataCapabilities.HasDoorBoxV1(MetadataCapabilities.Parse(p.LastCapabilities)));
    }
}
