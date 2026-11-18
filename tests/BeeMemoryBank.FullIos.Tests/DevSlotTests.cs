using BeeMemoryBank.Core.Models;
using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos.Tests;

/// <summary>
/// the Android Debug slot card offered to delete every <c>user</c> slot no active account points at, which may be a
/// person's own door (an account whose link to it was lost). It now offers only the dev password's slot it added itself (the vault's own guard is
/// in Core.Tests, SlotRemovalPersonDoorTests).
/// </summary>
public class DevSlotTests
{
    private static MasterKeyStore Slot(int id, string type) => new() { SlotId = id, SlotType = type };

    [Fact]
    public void AUserSlotNoAccountPointsAt_IsNotOfferedForDeletion_UnlessItIsTheDevSlotThisPhoneAdded()
    {
        var orphan = Slot(7, KeySlotTypes.User);
        var login = new HashSet<int> { 1 };

        DevSlot.CanDelete(orphan, login, devSlotId: null, slotCount: 3).Should().BeFalse("it may be a person's door whose account lost its link");
        DevSlot.CanDelete(orphan, login, devSlotId: 9, slotCount: 3).Should().BeFalse();
        DevSlot.CanDelete(orphan, login, devSlotId: 7, slotCount: 3).Should().BeTrue("the dev password this phone added");
    }

    [Fact]
    public void ALoginSlot_TheLegacyPassword_AndTheLastSlot_AreNeverOffered()
    {
        var login = new HashSet<int> { 1 };

        DevSlot.CanDelete(Slot(1, KeySlotTypes.User), login, devSlotId: 1, slotCount: 3).Should().BeFalse("an account signs in with it");
        DevSlot.CanDelete(Slot(2, KeySlotTypes.Password), login, devSlotId: null, slotCount: 3).Should().BeFalse();
        DevSlot.CanDelete(Slot(3, KeySlotTypes.Recovery), login, devSlotId: null, slotCount: 1).Should().BeFalse("the last slot");
    }

    [Fact]
    public void OtherDoors_StayOffered_AndTheVaultJudgesThem()
    {
        var login = new HashSet<int> { 1 };

        DevSlot.CanDelete(Slot(4, KeySlotTypes.Recovery), login, devSlotId: null, slotCount: 3).Should().BeTrue();
        DevSlot.CanDelete(Slot(5, KeySlotTypes.QuickUnlock), login, devSlotId: null, slotCount: 3).Should().BeTrue();
    }

    [Fact]
    public void TheAddedSlot_IsTheOneNewUserSlot_OrNothingWhenThatIsNotClear()
    {
        MasterKeyStore[] before = [Slot(1, KeySlotTypes.User), Slot(2, KeySlotTypes.Recovery)];

        DevSlot.Added(before, [.. before, Slot(3, KeySlotTypes.User)]).Should().Be(3);
        DevSlot.Added(before, before).Should().BeNull();
        DevSlot.Added(before, [.. before, Slot(3, KeySlotTypes.User), Slot(4, KeySlotTypes.User)]).Should().BeNull("two new slots: neither is known to be the dev password");
        DevSlot.Added(before, [.. before, Slot(3, KeySlotTypes.Recovery)]).Should().BeNull();
    }
}
