using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.FullIos.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.FullIos.Tests;

/// <summary>
/// Release 2.12: a recovery key is personal. The phone passes the person it belongs to - the administrator a new vault was made for, else the
/// phone's only administrator, else the one whose own door the typed password opens - and never lets the vault guess.
/// </summary>
public class PhoneOwnerTests
{

    private static async Task<List<MasterKeyStore>> RecoverySlotsAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IKeySlotRepository>().GetAllAsync())
            .Where(s => s.SlotType == KeySlotTypes.Recovery).ToList();
    }

    private static async Task<User> AdminAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IUserRepository>().ListActiveAsync()).Single(u => u.Role == UserRoles.Superadmin);
    }

    [Fact]
    public async Task TheRecoveryKeyOfANewVault_IsIssuedForTheAdministratorJustMade()
    {
        var (test, _) = await TestVault.CreateAsync("owner-create");
        await using var _2 = test;
        var admin = await AdminAsync(test.Services);

        var slot = (await RecoverySlotsAsync(test.Services)).Should().ContainSingle().Subject;

        slot.OwnerUserId.Should().Be(admin.Id, "the key belongs to the person the vault was just made for");
        slot.CreatedByUserId.Should().Be(admin.Id);
        slot.Label.Should().Be("Test iPhone");
    }

    [Fact]
    public async Task AnotherRecoveryKey_IsIssuedForThePhonesAdministrator()
    {
        var (test, _) = await TestVault.CreateAsync("owner-again");
        await using var _2 = test;
        var admin = await AdminAsync(test.Services);

        await test.Vault.NewRecoveryKeyAsync();

        var slots = await RecoverySlotsAsync(test.Services);
        slots.Should().HaveCount(2);
        slots.Should().OnlyContain(s => s.OwnerUserId == admin.Id);
    }

    [Fact]
    public async Task OnAPhoneWithSeveralAdministrators_ARecoveryKeyWithNoOwnerFound_IsRefused_NotGivenToSomebody()
    {
        var (test, _) = await TestVault.CreateAsync("owner-refused");
        await using var _2 = test;
        using (var scope = test.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<UserService>()
                .CreateUserAsync("bob", "Bob", "Bobs-Password-7", UserRoles.Superadmin, passwordHint: "The usual one, with a seven");

        var act = () => test.Vault.NewRecoveryKeyAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await RecoverySlotsAsync(test.Services)).Should().ContainSingle("only the key of the set-up exists");
    }

    [Fact]
    public async Task TheOwner_IsThePhonesOnlyAdministrator()
    {
        var (test, _) = await TestVault.CreateAsync("owner-sole");
        await using var _2 = test;
        var admin = await AdminAsync(test.Services);
        using var scope = test.Services.CreateScope();

        (await PhoneOwner.SoleSuperadminAsync(scope.ServiceProvider.GetRequiredService<IUserRepository>())).Should().Be(admin.Id);
        (await PhoneOwner.ForPasswordAsync(scope.ServiceProvider, null)).Should().Be(admin.Id);
    }

    [Fact]
    public async Task WithSeveralAdministrators_TheOwnerIsTheOneWhosePasswordOpensTheirDoor_AndNobodyIsGuessedWithoutIt()
    {
        var (test, _) = await TestVault.CreateAsync("owner-several");
        await using var _2 = test;
        var admin = await AdminAsync(test.Services);
        User bob;
        using (var scope = test.Services.CreateScope())
            bob = await scope.ServiceProvider.GetRequiredService<UserService>()
                .CreateUserAsync("bob", "Bob", "Bobs-Password-7", UserRoles.Superadmin, passwordHint: "The usual one, with a seven");

        using var check = test.Services.CreateScope();
        var sp = check.ServiceProvider;
        (await PhoneOwner.SoleSuperadminAsync(sp.GetRequiredService<IUserRepository>())).Should().BeNull("two administrators: nobody is guessed");
        (await PhoneOwner.ForPasswordAsync(sp, null)).Should().BeNull();
        (await PhoneOwner.ForPasswordAsync(sp, "Bobs-Password-7")).Should().Be(bob.Id);
        (await PhoneOwner.ForPasswordAsync(sp, TestVault.Password)).Should().Be(admin.Id);
        (await PhoneOwner.ForPasswordAsync(sp, "Nobodys-Password-1")).Should().BeNull();
    }

    [Fact]
    public void ThePhoneApp_NeverIssuesARecoveryKeyWithoutNamingItsOwner()
    {
        var app = Path.Combine(FullIosBoundaryTests.RepoRoot(), "mobile", "BeeMemoryBank.FullIos");
        foreach (var file in Directory.GetFiles(app, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
        {
            var source = File.ReadAllText(file);
            if (Path.GetFileName(file) == "PhoneOwner.cs") continue;
            Regex.IsMatch(source, @"\bAddRecoveryKeyAsync\(\s*\)").Should().BeFalse($"{Path.GetFileName(file)} issues a recovery key with no owner");
            Regex.IsMatch(source, @"\bIssueRecoveryKeyAsync\(\s*\)").Should().BeFalse($"{Path.GetFileName(file)} issues a recovery key with no owner");
        }
    }
}
