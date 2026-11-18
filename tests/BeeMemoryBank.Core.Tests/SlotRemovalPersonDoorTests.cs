using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// <see cref="KeyManagementService.RemoveSlotAsync"/> - the delete behind the phones' slot cards - refused only the
/// very last slot of any kind but a recovery key, so a superadmin's own password door could go while a machine door (Face ID, OS auto-unlock)
/// was left, and a person then had no way in once that secret was gone. Every slot delete now keeps a door a person can open
/// (<see cref="BeeMemoryBank.Vault.Sealed.SlotDoors.EnsureAPersonDoorRemainsAsync"/>, the person-door guard), whatever the slot's kind.
/// </summary>
public class SlotRemovalPersonDoorTests : TestFixture
{
    private KeySlotRepository Slots = null!;
    private UserRepository UserRepo = null!;
    private KeyManagementService Keys = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", "AdminPass1");
        await Session.UnlockAsync("AdminPass1");
        Slots = new KeySlotRepository(Factory);
        UserRepo = new UserRepository(Factory);
        Keys = new KeyManagementService(Slots, Session, UserRepo, Factory);
    }

    /// <summary>Another superadmin with a sealed (v2) password door: since 2.12 a superadmin's password door goes only while one exists.</summary>
    private async Task AnotherSuperadminWithASealedDoorAsync(UserRepository users)
    {
        await Keys.AddPasswordSlotAsync(KeySlotTypes.User, "Bob-Password-1");
        var slotId = (await Slots.GetAllAsync()).Max(s => s.SlotId);
        await users.CreateAsync(new User
        {
            Username = "bob", DisplayName = "Bob", PasswordHash = UserService.HashPassword("Bob-Password-1"), Role = UserRoles.Superadmin,
            IsActive = true, CreatedAt = DateTime.UtcNow, KeySlotId = slotId,
        });
    }

    private async Task<int> AdminSlotAsync() => (await UserRepo.GetByUsernameAsync("admin"))!.KeySlotId!.Value;

    /// <summary>A machine door: the secret is a device's, not a person's (Face ID on a phone).</summary>
    private async Task<int> QuickUnlockDoorAsync()
    {
        var dek = Session.GetMasterDek();
        var secret = SecureRandom.GetBytes(32);
        try { return await BeeMemoryBank.Vault.Sealed.QuickUnlockDoors.CreateAsync(Slots, Factory, null, secret, dek); }
        finally
        {
            Array.Clear(dek);
            Array.Clear(secret);
        }
    }

    [Fact]
    public async Task ASuperadminsPasswordDoor_CannotGo_WhenOnlyAMachineDoorWouldBeLeft()
    {
        await QuickUnlockDoorAsync();
        var admin = await AdminSlotAsync();

        var act = () => Keys.RemoveSlotAsync(admin);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*last door a person can open*");
        (await Slots.GetAllAsync()).Should().Contain(s => s.SlotId == admin, "the person's door stays");
    }

    [Fact]
    public async Task AMachineDoor_AndAnExtraPasswordSlotNobodySignsInWith_CanStillGo()
    {
        var quick = await QuickUnlockDoorAsync();
        var admin = await AdminSlotAsync();
        await Keys.AddPasswordSlotAsync(KeySlotTypes.User, "Dev-Password-1");
        var dev = (await Slots.GetAllAsync()).Single(s => s.SlotType == KeySlotTypes.User && s.SlotId != admin).SlotId;

        await Keys.RemoveSlotAsync(quick);
        await Keys.RemoveSlotAsync(dev);

        (await Slots.GetAllAsync()).Select(s => s.SlotId).Should().Equal(admin);
    }

    [Fact]
    public async Task ASuperadminsPasswordDoor_CanGo_WhileAnotherPersonDoorStays()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        await QuickUnlockDoorAsync();
        await Keys.IssueRecoveryKeyAsync(admin.Id, "safe");
        await AnotherSuperadminWithASealedDoorAsync(UserRepo);

        await Keys.RemoveSlotAsync(admin.KeySlotId!.Value);

        (await Slots.GetAllAsync()).Should().NotContain(s => s.SlotId == admin.KeySlotId);
    }
}
