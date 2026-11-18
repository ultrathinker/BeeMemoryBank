using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Release 2.12 (plan WS-B2 / E4 step S0): PersonDoors.RevokeAllOfAsync revokes one person's user door, recovery keys and auto-unlock agents'
/// doors in one transaction, in the boot-store order — and leaves everybody else's doors alone.
/// </summary>
public class PersonDoorsTests : TestFixture
{
    private KeySlotRepository Slots = null!;
    private UserRepository UserRepo = null!;
    private AgentRepository Agents = null!;
    private UserService Users = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", "AdminPass1");
        await Session.UnlockAsync("AdminPass1");
        Slots = new KeySlotRepository(Factory);
        UserRepo = new UserRepository(Factory);
        Agents = new AgentRepository(Factory);
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

    private async Task<int> WrappedAgentAsync(int ownerId)
    {
        var apiKey = AgentKeyHelper.GenerateApiKey();
        var dek = Session.GetMasterDek();
        try
        {
            var (cipher, iv, salt) = AgentKeyHelper.EncryptDekV1(apiKey, dek);
            return await Agents.CreateAsync(new Agent
            {
                Name = "agent", KeyPrefix = AgentKeyHelper.GetKeyPrefix(apiKey), KeyHash = AgentKeyHelper.ComputeKeyHash(apiKey),
                EncryptedDek = cipher, DekIV = iv, Salt = salt, KdfVersion = 1, Status = "A", CreatedAt = DateTime.UtcNow, OwnerUserId = ownerId,
            });
        }
        finally { Array.Clear(dek); }
    }

    [Fact]
    public async Task RevokeAllOf_RemovesThePersonsUserDoor_RecoveryKeys_AndAgentDoors()
    {
        var carol = await Users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);
        var carolKey = await KeyManagement.AddRecoveryKeyAsync(carol.Id);
        var carolAgent = await WrappedAgentAsync(carol.Id);
        var carolSlot = (await UserRepo.GetByIdAsync(carol.Id))!.KeySlotId!.Value;

        await PersonDoors.RevokeAllOfAsync(Factory, carol.Id, Slots, mirror: null);

        var slots = await Slots.GetAllAsync();
        slots.Should().NotContain(s => s.SlotId == carolSlot, "the person's password door is gone");
        slots.Should().NotContain(s => s.SlotType == KeySlotTypes.Recovery && s.OwnerUserId == carol.Id, "the person's recovery keys are gone");
        (await Agents.GetByIdAsync(carolAgent)).Should().BeNull("the person's auto-unlock agent is revoked");
        (await UserRepo.GetByIdAsync(carol.Id))!.KeySlotId.Should().BeNull("no dangling pointer makes them look provisioned");
        Session.Lock();
        (await Session.UnlockAsync("CarolPass1")).Should().BeFalse("the person's password no longer opens the vault");
        (await Session.UnlockAsync(carolKey)).Should().BeFalse("the person's recovery key no longer opens the vault");
    }

    [Fact]
    public async Task RevokeAllOf_LeavesAnotherPersonsDoorsAlone()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        var carol = await Users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);
        var adminKey = await KeyManagement.AddRecoveryKeyAsync(admin.Id);
        var carolKey = await KeyManagement.AddRecoveryKeyAsync(carol.Id);
        var adminAgent = await WrappedAgentAsync(admin.Id);
        var adminSlot = (await UserRepo.GetByIdAsync(admin.Id))!.KeySlotId!.Value;

        await PersonDoors.RevokeAllOfAsync(Factory, carol.Id, Slots, mirror: null);

        var slots = await Slots.GetAllAsync();
        slots.Should().Contain(s => s.SlotId == adminSlot, "admin's password door stays");
        slots.Should().Contain(s => s.SlotType == KeySlotTypes.Recovery && s.OwnerUserId == admin.Id, "admin's recovery key stays");
        (await Agents.GetByIdAsync(adminAgent)).Should().NotBeNull("admin's agent stays");
        Session.Lock();
        (await Session.UnlockAsync("AdminPass1")).Should().BeTrue("admin still opens the vault");
        (await Session.UnlockAsync(adminKey)).Should().BeTrue("admin's recovery key still opens the vault");
    }

    [Fact]
    public async Task RevokeAllOf_ASecondTime_IsANoOp()
    {
        var carol = await Users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);
        await KeyManagement.AddRecoveryKeyAsync(carol.Id);
        await PersonDoors.RevokeAllOfAsync(Factory, carol.Id, Slots, mirror: null);
        var before = (await Slots.GetAllAsync()).Count;

        await PersonDoors.RevokeAllOfAsync(Factory, carol.Id, Slots, mirror: null);

        (await Slots.GetAllAsync()).Should().HaveCount(before);
    }
}
