using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.FullIos.Services;

/// <summary>
/// The Debug-only "dev password" of the Android app's slot card (a testing tool, product rule 7): an extra <c>user</c> slot no account signs in
/// with. The card remembers which slot that is, and offers to delete a <c>user</c> slot only when it is that one - a <c>user</c> slot no active
/// account points at may still be a person's door (an account whose link to it was lost), never a test password. Whatever the
/// card offers, the vault refuses a delete that would leave no door a person can open (<c>KeyManagementService.RemoveSlotAsync</c>). Platform-free,
/// so its rule is tested on any OS; the Android app links this file.
/// </summary>
public static class DevSlot
{
    /// <summary>Where the card keeps the id of the slot its dev password added.</summary>
    public const string PreferenceKey = "debug.dev-slot-id";

    /// <summary>
    /// Whether the card offers to delete <paramref name="slot"/>: never the last slot, the legacy password slot or a slot an account signs in with,
    /// and a <c>user</c> slot only when it is the dev slot this phone added (<paramref name="devSlotId"/>).
    /// </summary>
    public static bool CanDelete(MasterKeyStore slot, IReadOnlySet<int> loginSlots, int? devSlotId, int slotCount) =>
        slotCount > 1
        && slot.SlotType != KeySlotTypes.Password
        && !loginSlots.Contains(slot.SlotId)
        && (slot.SlotType != KeySlotTypes.User || slot.SlotId == devSlotId);

    /// <summary>The slot an add of the dev password made: the one <c>user</c> slot in <paramref name="after"/> that is not in <paramref name="before"/>.</summary>
    public static int? Added(IEnumerable<MasterKeyStore> before, IEnumerable<MasterKeyStore> after)
    {
        var known = before.Select(s => s.SlotId).ToHashSet();
        var added = after.Where(s => s.SlotType == KeySlotTypes.User && !known.Contains(s.SlotId)).Select(s => s.SlotId).ToList();
        return added.Count == 1 ? added[0] : null;
    }
}
