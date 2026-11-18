using System.Diagnostics;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Release 2.12 (plan WS-B2): a recovery key resets only its OWNER's password. A key with an owner naming anybody else — even another
/// superadmin — is refused exactly like a wrong key, after the same work (no oracle for who owns what). A key WITHOUT an owner (issued
/// before 2.12) keeps the older rule and resets any active superadmin.
/// </summary>
public class RecoveryKeyOwnerResetTests : TestFixture
{
    private const string AdminPassword = "AdminPass1";
    private const string NewPassword = "BrandNewPass2";

    private UserService Users = null!;
    private UserRepository UserRepo = null!;
    private KeySlotRepository KeySlots = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", AdminPassword);
        await Session.UnlockAsync(AdminPassword);
        UserRepo = new UserRepository(Factory);
        KeySlots = new KeySlotRepository(Factory);
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
        Users = new UserService(UserRepo, KeySlots, Session, roles, folderAccess, factory: Factory);
    }

    private async Task<User> SuperadminAsync(string name) => await Users.CreateUserAsync(name, name, name + "Pass1", UserRoles.Superadmin);

    /// <summary>A recovery slot with NO owner, as a key issued before 2.12 was written (a v1 wrap, owner_user_id NULL).</summary>
    private async Task<string> OwnerlessRecoveryKeyAsync()
    {
        var keyBytes = SecureRandom.GetBytes(32);
        var key = Convert.ToBase64String(keyBytes);
        Array.Clear(keyBytes);
        var salt = KeyDerivation.GenerateSalt();
        var kek = KeyDerivation.DeriveKek(key, salt);
        var dek = Session.GetMasterDek();
        try
        {
            var (wrapped, iv) = MasterKeyManager.WrapMasterDek(dek, kek);
            await KeySlots.CreateAsync(new MasterKeyStore
            {
                SlotType = KeySlotTypes.Recovery,
                EncryptedMasterDek = wrapped,
                IV = iv,
                Salt = salt,
                ArgonMemory = CryptoConstants.DefaultArgonMemory,
                ArgonIterations = CryptoConstants.DefaultArgonIterations,
                ArgonParallelism = CryptoConstants.DefaultArgonParallelism,
                CreatedAt = DateTime.UtcNow,
                DoorVersion = 1,
                OwnerUserId = null,
            });
            return key;
        }
        finally
        {
            Array.Clear(kek);
            Array.Clear(dek);
        }
    }

    [Fact]
    public async Task AnOwnersKey_ResetsItsOwner()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        var key = await KeyManagement.AddRecoveryKeyAsync(admin.Id);
        Session.Lock();

        var result = await Users.ResetPasswordWithRecoveryKeyAsync("admin", key, NewPassword);

        result.Succeeded.Should().BeTrue();
        result.UserId.Should().Be(admin.Id);
        (await Users.AuthenticateAsync("admin", NewPassword)).Should().NotBeNull();
    }

    [Fact]
    public async Task AnOwnersKey_NamingAnotherSuperadmin_IsRefusedLikeAWrongKey_AndChangesNothing()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        var carol = await SuperadminAsync("carol");
        var adminKey = await KeyManagement.AddRecoveryKeyAsync(admin.Id);
        var carolStamp = (await UserRepo.GetByIdAsync(carol.Id))!.SecurityStamp;
        var adminStamp = (await UserRepo.GetByIdAsync(admin.Id))!.SecurityStamp;
        Session.Lock();

        // admin's key naming carol: refused, and the answer is the same shape as a wrong key's.
        var refused = await Users.ResetPasswordWithRecoveryKeyAsync("carol", adminKey, NewPassword);
        var wrongKey = await Users.ResetPasswordWithRecoveryKeyAsync("carol", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", NewPassword);

        refused.Succeeded.Should().BeFalse("a recovery key resets only its owner, not another superadmin");
        refused.Outcome.Should().Be(RecoveryResetOutcome.NotEligible);
        refused.UserId.Should().Be(carol.Id, "the audit names the account that was asked for, as for any ineligible name");
        wrongKey.Outcome.Should().Be(RecoveryResetOutcome.WrongKey);
        (await Users.AuthenticateAsync("carol", "carolPass1")).Should().NotBeNull("carol's password is untouched");
        (await Users.AuthenticateAsync("carol", NewPassword)).Should().BeNull();
        (await UserRepo.GetByIdAsync(carol.Id))!.SecurityStamp.Should().Be(carolStamp);
        (await UserRepo.GetByIdAsync(admin.Id))!.SecurityStamp.Should().Be(adminStamp);
    }

    [Fact]
    public async Task AnOwnersKey_NamingAnOrdinaryUserOrAGhost_IsRefused()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        var bob = await Users.CreateUserAsync("bob", "Bob", "BobPass1", UserRoles.User);
        var adminKey = await KeyManagement.AddRecoveryKeyAsync(admin.Id);

        (await Users.ResetPasswordWithRecoveryKeyAsync("bob", adminKey, NewPassword)).Outcome.Should().Be(RecoveryResetOutcome.NotEligible);
        (await Users.ResetPasswordWithRecoveryKeyAsync("ghost", adminKey, NewPassword)).Outcome.Should().Be(RecoveryResetOutcome.NotEligible);
        (await Users.AuthenticateAsync("bob", "BobPass1")).Should().NotBeNull();
    }

    [Fact]
    public async Task AnOwnerlessKey_StillResetsAnyActiveSuperadmin()
    {
        var carol = await SuperadminAsync("carol");
        var legacyKey = await OwnerlessRecoveryKeyAsync();
        Session.Lock();

        // The pre-2.12 rule: a key with no owner resets any active superadmin, here carol (not the node's first admin).
        var result = await Users.ResetPasswordWithRecoveryKeyAsync("carol", legacyKey, NewPassword);

        result.Succeeded.Should().BeTrue("a key issued before 2.12 has no owner and keeps the old node-wide rule");
        result.UserId.Should().Be(carol.Id);
        (await Users.AuthenticateAsync("carol", NewPassword)).Should().NotBeNull();
    }

    [Fact]
    public async Task AnOwnerlessKey_StillRefusesAnOrdinaryUser()
    {
        await Users.CreateUserAsync("bob", "Bob", "BobPass1", UserRoles.User);
        var legacyKey = await OwnerlessRecoveryKeyAsync();

        (await Users.ResetPasswordWithRecoveryKeyAsync("bob", legacyKey, NewPassword)).Outcome.Should().Be(RecoveryResetOutcome.NotEligible);
        (await Users.AuthenticateAsync("bob", "BobPass1")).Should().NotBeNull();
    }

    [Fact]
    public async Task EachSuperadminsKey_ResetsOnlyThemself()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        var carol = await SuperadminAsync("carol");
        var adminKey = await KeyManagement.AddRecoveryKeyAsync(admin.Id);
        var carolKey = await KeyManagement.AddRecoveryKeyAsync(carol.Id);
        Session.Lock();

        (await Users.ResetPasswordWithRecoveryKeyAsync("admin", adminKey, NewPassword)).Succeeded.Should().BeTrue();
        (await Users.ResetPasswordWithRecoveryKeyAsync("carol", carolKey, "CarolNewPass3")).Succeeded.Should().BeTrue();
        // Crossed keys refuse.
        (await Users.ResetPasswordWithRecoveryKeyAsync("carol", adminKey, "CarolNewPass4")).Succeeded.Should().BeFalse();
        (await Users.ResetPasswordWithRecoveryKeyAsync("admin", carolKey, "AdminNewPass4")).Succeeded.Should().BeFalse();
    }

    // A valid key naming the WRONG owner must pay the same KDF work as every other refusal, so it cannot be told apart by timing.
    // A floor alone ("some work was done") would still pass if the owner-mismatch path burned once while the others burn twice; comparing
    // each refusal to the wrong-key refusal WITHIN A FACTOR proves they do the SAME work, not merely some.
    [Fact]
    [Trait("Category", "Timing")]
    public async Task AValidKeyNamingTheWrongOwner_PaysTheSameWorkAsOtherRefusals()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        await SuperadminAsync("carol");
        var adminKey = await KeyManagement.AddRecoveryKeyAsync(admin.Id);
        UserService.HashPassword("warm-up");

        async Task<TimeSpan> Time(string user, string key)
        {
            var sw = Stopwatch.StartNew();
            (await Users.ResetPasswordWithRecoveryKeyAsync(user, key, NewPassword)).Succeeded.Should().BeFalse();
            return sw.Elapsed;
        }

        // Median of several samples: Argon2id is CPU-bound and steady, so the median shrugs off scheduling jitter that a single sample catches.
        async Task<TimeSpan> Median(string user, string key)
        {
            var samples = new List<TimeSpan>();
            for (var i = 0; i < 5; i++) samples.Add(await Time(user, key));
            samples.Sort();
            return samples[samples.Count / 2];
        }

        var oneHash = TimeSpan.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            UserService.HashPassword("x" + i);
            if (sw.Elapsed < oneHash) oneHash = sw.Elapsed;
        }
        var floor = oneHash * 1.5;

        var wrongKey = await Median("admin", "wrong-key");
        var wrongOwner = await Median("carol", adminKey);
        var ghost = await Median("ghost", adminKey);

        // Some work was done (the floor)...
        wrongOwner.Should().BeGreaterThan(floor, "a valid key naming another superadmin is a refusal like the rest");
        ghost.Should().BeGreaterThan(floor, "a valid key naming a ghost");
        wrongKey.Should().BeGreaterThan(floor, "a wrong key");
        // ...and the SAME work as a wrong key, within a factor. A path that burned once while a wrong key burns twice would land near 2/3
        // of the wrong-key time and fall below the band; one that did extra work would rise above it.
        wrongOwner.Should().BeGreaterThan(wrongKey * 0.75, "the owner-mismatch refusal pays the same derivations as a wrong key, not fewer");
        wrongOwner.Should().BeLessThan(wrongKey * 1.33, "the owner-mismatch refusal pays the same derivations as a wrong key, not more");
        ghost.Should().BeGreaterThan(wrongKey * 0.75, "the ghost refusal pays the same derivations as a wrong key, not fewer");
        ghost.Should().BeLessThan(wrongKey * 1.33, "the ghost refusal pays the same derivations as a wrong key, not more");
    }
}
