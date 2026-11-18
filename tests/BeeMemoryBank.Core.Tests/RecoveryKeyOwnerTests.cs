using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Release 2.12 (plan WS-B2): a recovery key is PERSONAL. Every new key belongs to the superadmin who issued it, for themself: the slot records
/// the owner and the creator, and only an active superadmin qualifies. A key without an owner is only ever a row from before 2.12.
/// </summary>
public class RecoveryKeyOwnerTests : TestFixture
{
    private KeySlotRepository Slots = null!;
    private UserRepository UserRepo = null!;
    private UserService Users = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", "AdminPass1");
        await Session.UnlockAsync("AdminPass1");
        Slots = new KeySlotRepository(Factory);
        UserRepo = new UserRepository(Factory);
        var roles = new RoleRepository(Factory);
        var folderAccess = new FolderAccessService(new ServiceCollection()
            .AddSingleton<Core.Interfaces.IDbConnectionFactory>(_ => Factory)
            .AddScoped<Core.Interfaces.IUserRepository>(_ => UserRepo)
            .AddScoped<Core.Interfaces.IRoleRepository>(_ => roles)
            .AddScoped<Core.Interfaces.IRoleAclRepository>(_ => new RoleAclRepository(Factory))
            .AddScoped<Core.Interfaces.IFolderAclRepository>(_ => new FolderAclRepository(Factory))
            .AddScoped<Core.Interfaces.IFolderRepository>(_ => FolderRepo)
            .AddScoped(_ => ScopeHolder)
            .BuildServiceProvider());
        Users = new UserService(UserRepo, Slots, Session, roles, folderAccess, factory: Factory);
    }

    private async Task<MasterKeyStore> RecoverySlotAsync() => (await Slots.GetAllAsync()).Single(s => s.SlotType == KeySlotTypes.Recovery);

    [Fact]
    public async Task ANamedOwner_IsRecordedAsOwnerAndCreator_WithTheLabel()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;

        var (_, slotId) = await KeyManagement.IssueRecoveryKeyAsync(admin.Id, "the card in the safe");

        var slot = (await Slots.GetAllAsync()).Single(s => s.SlotId == slotId);
        slot.OwnerUserId.Should().Be(admin.Id, "the key belongs to the superadmin who issued it");
        slot.CreatedByUserId.Should().Be(admin.Id, "a key is issued by its owner for themself");
        slot.Label.Should().Be("the card in the safe");
    }

    [Fact]
    public async Task TheOnlySuperadmin_IsTheOwnerOfAKeyIssuedWithoutNamingOne()
    {
        // The personal-device path (a phone, a fresh CLI node): one person, so the key is theirs without a guess.
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;

        await KeyManagement.AddRecoveryKeyAsync();

        (await RecoverySlotAsync()).OwnerUserId.Should().Be(admin.Id);
    }

    [Fact]
    public async Task SeveralSuperadmins_AndNoName_RefusedRatherThanGuessed()
    {
        await KeyManagement.AddRecoveryKeyAsync();   // the first key: admin is still the only superadmin
        var carol = await Users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);

        var act = async () => await KeyManagement.AddRecoveryKeyAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*name the owner*");
        (await Slots.GetAllAsync()).Count(s => s.SlotType == KeySlotTypes.Recovery).Should().Be(1, "the refusal made no key");
        // Naming one of them is fine: the key is that person's.
        await KeyManagement.AddRecoveryKeyAsync(carol.Id);
        (await Slots.GetAllAsync()).Where(s => s.SlotType == KeySlotTypes.Recovery)
            .Should().Contain(s => s.OwnerUserId == carol.Id);
    }

    [Fact]
    public async Task AnOrdinaryUser_CannotBeTheOwnerOfANewKey()
    {
        var bob = await Users.CreateUserAsync("bob", "Bob", "BobPass1", UserRoles.User);

        var act = async () => await KeyManagement.IssueRecoveryKeyAsync(bob.Id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*active superadmin*");
        (await Slots.GetAllAsync()).Should().NotContain(s => s.SlotType == KeySlotTypes.Recovery, "the refusal made no key");
    }

    [Fact]
    public async Task ADeletedSuperadmin_CannotBeTheOwnerOfANewKey()
    {
        var carol = await Users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);
        await Users.DeleteUserAsync(carol.Id);

        var act = async () => await KeyManagement.IssueRecoveryKeyAsync(carol.Id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*active superadmin*");
        (await Slots.GetAllAsync()).Should().NotContain(s => s.SlotType == KeySlotTypes.Recovery);
    }

    [Fact]
    public async Task AnUnknownOwner_IsRefused()
    {
        var act = async () => await KeyManagement.IssueRecoveryKeyAsync(99999);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*active superadmin*");
        (await Slots.GetAllAsync()).Should().NotContain(s => s.SlotType == KeySlotTypes.Recovery);
    }

    [Fact]
    public async Task ALabel_IsTrimmed_AndAnEmptyOneIsNoLabel()
    {
        await KeyManagement.AddRecoveryKeyAsync(label: "  the spare  ");
        (await RecoverySlotAsync()).Label.Should().Be("the spare");

        await KeyManagement.AddRecoveryKeyAsync(label: "   ");
        (await Slots.GetAllAsync()).Where(s => s.SlotType == KeySlotTypes.Recovery)
            .Should().Contain(s => s.Label == null);
    }

    [Theory]
    [InlineData("a line\nbreak")]
    [InlineData("a tab\there")]
    public async Task ALabelWithControlCharacters_IsRefused(string label)
    {
        var act = async () => await KeyManagement.AddRecoveryKeyAsync(label: label);

        await act.Should().ThrowAsync<ArgumentException>();
        (await Slots.GetAllAsync()).Should().NotContain(s => s.SlotType == KeySlotTypes.Recovery);
    }

    [Fact]
    public async Task ALabelLongerThan120Characters_IsRefused()
    {
        var act = async () => await KeyManagement.AddRecoveryKeyAsync(label: new string('x', 121));

        await act.Should().ThrowAsync<ArgumentException>();
        (await Slots.GetAllAsync()).Should().NotContain(s => s.SlotType == KeySlotTypes.Recovery);
    }

    [Fact]
    public async Task AnOwnerBoundKey_StillOpensTheVault()
    {
        // Unlocking with a recovery key is unchanged: the door opens the vault for whoever holds the key.
        var key = await KeyManagement.AddRecoveryKeyAsync();
        Session.Lock();

        (await Session.UnlockAsync(key)).Should().BeTrue();
    }

    [Fact]
    public async Task ARecoveryKeyCannotBeMadeOwnerless_ViaThePasswordSlotPath()
    {
        // The legacy extra-slot path must not mint an ownerless recovery key (2.12): a recovery key is personal.
        var act = async () => await KeyManagement.AddPasswordSlotAsync(KeySlotTypes.Recovery, "SomePass1");

        await act.Should().ThrowAsync<ArgumentException>();
        (await Slots.GetAllAsync()).Should().NotContain(s => s.SlotType == KeySlotTypes.Recovery, "no ownerless recovery key was made");
    }
}
