using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync.Recovery;

/// <summary>
/// Publishes a freshly written key slot as this node's device box (plan 6.3). The box IS the slot —
/// same salt, same parameters, same wrapped bytes — so it opens with exactly the password that unlocks
/// this device and costs nothing to build. A newer device box of this node supersedes the older one
/// everywhere (one active box per author and kind), so boxes for old passwords do not pile up.
/// </summary>
public class DeviceBoxPublisher(
    RecoveryEventPublisher publisher,
    INodeIdentityRepository nodeRepo,
    ILogger<DeviceBoxPublisher> logger) : IRecoveryBoxPublisher
{
    public async Task PublishDeviceBoxAsync(MasterKeyStore slot, byte[] dek)
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
            if (preset == null || !RecoveryBoxKdf.IsWellFormed(slot.Salt, slot.EncryptedMasterDek, slot.IV))
            {
                logger.LogWarning("Key slot {SlotId} has non-default parameters or shape; no device box published", slot.SlotId);
                return;
            }

            var identity = await nodeRepo.GetAsync()
                ?? throw new InvalidOperationException("Node is not initialized.");

            await publisher.PublishAsync(EventTypes.RecoveryBoxSet, new RecoveryBoxSetPayload(
                BoxId: Guid.NewGuid().ToString(),
                Kind: RecoveryBoxKdf.KindDevice,
                AuthorNodeId: identity.NodeId.ToString(),
                DekFingerprint: DekFingerprint.Of(dek),
                EpochHint: identity.DekEpoch,
                KdfPreset: preset,
                Salt: Convert.ToBase64String(slot.Salt!),
                Wrapped: Convert.ToBase64String(slot.EncryptedMasterDek),
                Iv: Convert.ToBase64String(slot.IV)), dek);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Publishing the device box for key slot {SlotId} failed", slot.SlotId);
        }
    }
}
