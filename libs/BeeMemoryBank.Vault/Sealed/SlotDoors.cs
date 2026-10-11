using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Vault.Sealed;

/// <summary>
/// The write protocol of docs/design/step-b-spec.md 4.3 for the code that creates and deletes key slots: a pending door is written
/// BEFORE the sealed transaction that adds the slot commits (rule 1), the copies of a door are retired BEFORE the delete commits
/// (rule 2), and the callers rewrite the boot store after the operation (rule 3, <see cref="RewriteAsync"/>). With no mirror, or a mirror of
/// a plain node (it does nothing), the calls are the plain repository calls they replace.
/// </summary>
public static class SlotDoors
{
    /// <summary>The door a slot row becomes. <paramref name="masterDek"/>, when the writer holds it, names the DEK the door yields (spec 3.4).</summary>
    public static PendingDoor PendingFor(MasterKeyStore slot, byte[]? masterDek) =>
        new(BootDoorKinds.Slot, slot.SlotId, slot.SlotType, slot.EncryptedMasterDek, slot.IV, slot.Salt, slot.ArgonMemory, slot.ArgonIterations,
            slot.ArgonParallelism, null, null, masterDek is { Length: CryptoConstants.KeySize } ? DekFingerprint.Of(masterDek) : null);

    /// <summary>
    /// Inserts <paramref name="slot"/> and returns its id; <c>slot.SlotId</c> is set. With a mirror and a connection factory the insert and
    /// the pending door happen inside one sealed transaction: the door is written before the commit.
    /// </summary>
    public static async Task<int> CreateSlotAsync(IKeySlotRepository slots, IDbConnectionFactory? factory, IUnlockMaterialMirror? mirror,
        MasterKeyStore slot, byte[]? masterDek)
    {
        if (mirror is null || factory is null)
        {
            slot.SlotId = await slots.CreateAsync(slot);
            return slot.SlotId;
        }

        try
        {
            using var connection = factory.CreateConnection();
            using var transaction = connection.BeginTransaction();
            try
            {
                slot.SlotId = await slots.CreateAsync(slot, transaction);
                await mirror.AddPendingDoorAsync(PendingFor(slot, masterDek));
                transaction.Commit();
            }
            catch
            {
                try { transaction.Rollback(); } catch { /* SQLite may have rolled back already; do not mask the real failure */ }
                throw;
            }
        }
        catch
        {
            await RewriteAsync(mirror);   // drops the pending copy of the insert that did not happen
            throw;
        }
        return slot.SlotId;
    }

    /// <summary>Deletes a slot; the copies of its door are retired first (rule 2).</summary>
    public static async Task DeleteSlotAsync(IKeySlotRepository slots, IUnlockMaterialMirror? mirror, int slotId)
    {
        if (mirror is not null) await mirror.RetireDoorAsync(BootDoorKinds.Slot, slotId);
        try
        {
            await slots.DeleteAsync(slotId);
        }
        catch
        {
            if (mirror is not null) await RewriteAsync(mirror);   // the delete did not happen: the door is copied back
            throw;
        }
    }

    /// <summary>
    /// Rule 3: replaces every door copy with the sealed database's current set. Best effort - the operation it follows has committed, and
    /// the watcher (4.3) repeats a failed rewrite within seconds - so a failure here is logged and never turned into a failure of the operation.
    /// </summary>
    public static async Task RewriteAsync(IUnlockMaterialMirror? mirror, ILogger? logger = null)
    {
        if (mirror is null) return;
        try
        {
            await mirror.RewriteAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "The boot store could not be rewritten after a change of unlock material; the watcher repeats it.");
        }
    }
}
