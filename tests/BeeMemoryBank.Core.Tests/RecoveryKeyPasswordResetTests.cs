using System.Diagnostics;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// "Forgot your password? Use a recovery key" (BMB-156): <see cref="UserService.ResetPasswordWithRecoveryKeyAsync"/>.
/// The service is reachable without signing in, so these tests pin what must NOT happen as hard as what
/// must: no session side effect, no way for an ordinary user to be taken over, no difference between the
/// refusals, no collateral change to anyone else's slot or to any recovery key.
/// </summary>
public class RecoveryKeyPasswordResetTests : TestFixture
{
    private const string OldPassword = "AdminPass1";
    private const string NewPassword = "BrandNewPass2";

    private UserService Users = null!;
    private UserRepository UserRepo = null!;
    private KeySlotRepository KeySlots = null!;
    private string _recoveryKey = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", OldPassword);
        await Session.UnlockAsync(OldPassword);

        UserRepo = new UserRepository(Factory);
        KeySlots = new KeySlotRepository(Factory);
        var roles = new RoleRepository(Factory);
        var folderAccess = new FolderAccessService(new ServiceCollection()
            .AddSingleton<Core.Interfaces.IDbConnectionFactory>(_ => Factory)
            .AddScoped<Core.Interfaces.IUserRepository>(_ => UserRepo)
            .AddScoped<Core.Interfaces.IRoleRepository>(_ => roles)
            .AddScoped<Core.Interfaces.IRoleAclRepository>(_ => new RoleAclRepository(Factory))
            .AddScoped<Core.Interfaces.IFolderAclRepository>(_ => new FolderAclRepository(Factory))
            .AddScoped<Core.Interfaces.IFolderRepository>(_ => new FolderRepository(Factory, ScopeHolder))
            .AddScoped(_ => ScopeHolder)
            .BuildServiceProvider());
        Users = new UserService(UserRepo, KeySlots, Session, roles, folderAccess);

        _recoveryKey = await KeyManagement.AddRecoveryKeyAsync();
    }

    private async Task<List<(int Id, string Type, string Dek, string Iv, string Salt)>> SlotSnapshotAsync() =>
        (await KeySlots.GetAllAsync())
            .Select(s => (s.SlotId, s.SlotType, Convert.ToBase64String(s.EncryptedMasterDek),
                Convert.ToBase64String(s.IV), Convert.ToBase64String(s.Salt ?? [])))
            .OrderBy(s => s.SlotId).ToList();

    [Fact]
    public async Task RightKey_ReplacesThePassword_OldRefused_NewAcceptedAndUnlocks()
    {
        Session.Lock();

        var result = await Users.ResetPasswordWithRecoveryKeyAsync("admin", _recoveryKey, NewPassword);

        result.Succeeded.Should().BeTrue();
        result.UserId.Should().Be((await UserRepo.GetByUsernameAsync("admin"))!.Id);
        (await Users.AuthenticateAsync("admin", OldPassword)).Should().BeNull("the old login password is gone");
        (await Users.AuthenticateAsync("admin", NewPassword)).Should().NotBeNull();
        (await Session.UnlockAsync(OldPassword)).Should().BeFalse("the old password no longer opens the key slot either");
        (await Session.UnlockAsync(NewPassword)).Should().BeTrue("the key slot was re-wrapped under the new password");
    }

    [Fact]
    public async Task OnALockedVault_TheVaultStaysLocked()
    {
        Session.Lock();

        (await Users.ResetPasswordWithRecoveryKeyAsync("admin", _recoveryKey, NewPassword)).Succeeded.Should().BeTrue();

        Session.IsUnlocked.Should().BeFalse(
            "the reset proves the key and rewraps a slot; opening the vault for everyone is not part of it");
    }

    [Fact]
    public async Task OnAnOpenVault_TheSameKeyStaysInMemoryAndNothingIsRekeyed()
    {
        var before = Session.GetMasterDek();

        (await Users.ResetPasswordWithRecoveryKeyAsync("admin", _recoveryKey, NewPassword)).Succeeded.Should().BeTrue();

        Session.IsUnlocked.Should().BeTrue();
        Session.GetMasterDek().Should().Equal(before, "only the wrapping changed, never the master key");
    }

    [Fact]
    public async Task BumpsTheSecurityStamp_AndTheRecoveryKeyStillWorks()
    {
        var stampBefore = (await UserRepo.GetByUsernameAsync("admin"))!.SecurityStamp;

        (await Users.ResetPasswordWithRecoveryKeyAsync("admin", _recoveryKey, NewPassword)).Succeeded.Should().BeTrue();

        (await UserRepo.GetByUsernameAsync("admin"))!.SecurityStamp.Should().NotBe(stampBefore,
            "every web cookie of this user dies at its next revalidation");
        (await Users.ResetPasswordWithRecoveryKeyAsync("admin", _recoveryKey, "AnotherPass3")).Succeeded.Should().BeTrue(
            "a recovery key is not spent by a reset: older keys still work");
        (await KeySlots.GetAllAsync()).Count(s => s.SlotType == "recovery").Should().Be(1);
    }

    [Fact]
    public async Task OtherSuperadmins_AndOtherRecoveryKeys_AreUntouched()
    {
        var carol = await Users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);
        var secondKey = await KeyManagement.AddRecoveryKeyAsync();
        var carolStamp = (await UserRepo.GetByIdAsync(carol.Id))!.SecurityStamp;
        var before = await SlotSnapshotAsync();
        var adminSlotId = (await UserRepo.GetByUsernameAsync("admin"))!.KeySlotId!.Value;

        (await Users.ResetPasswordWithRecoveryKeyAsync("admin", _recoveryKey, NewPassword)).Succeeded.Should().BeTrue();

        var after = await SlotSnapshotAsync();
        after.Should().HaveCount(before.Count, "one user slot replaced by one user slot, nothing added or dropped");
        // Every slot but the reset user's own is byte-for-byte what it was.
        after.Where(s => s.Id != adminSlotId && before.Any(b => b.Id == s.Id))
            .Should().BeEquivalentTo(before.Where(b => b.Id != adminSlotId));
        after.Any(s => s.Id == adminSlotId).Should().BeFalse("the old slot of the reset user is replaced");

        (await Users.AuthenticateAsync("carol", "CarolPass1")).Should().NotBeNull("another superadmin's login password is unchanged");
        (await UserRepo.GetByIdAsync(carol.Id))!.SecurityStamp.Should().Be(carolStamp);
        Session.Lock();
        (await Session.UnlockAsync("CarolPass1")).Should().BeTrue("another superadmin's key slot still opens with her own password");
        (await Users.ResetPasswordWithRecoveryKeyAsync("admin", secondKey, "ThirdPass3")).Succeeded.Should().BeTrue(
            "the other recovery key still works");
    }

    [Fact]
    public async Task WrongKey_IsRefused_AndNothingChanges()
    {
        var slotsBefore = await SlotSnapshotAsync();
        var stampBefore = (await UserRepo.GetByUsernameAsync("admin"))!.SecurityStamp;

        var result = await Users.ResetPasswordWithRecoveryKeyAsync("admin", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", NewPassword);

        result.Succeeded.Should().BeFalse();
        result.Outcome.Should().Be(RecoveryResetOutcome.WrongKey);
        (await SlotSnapshotAsync()).Should().BeEquivalentTo(slotsBefore);
        (await UserRepo.GetByUsernameAsync("admin"))!.SecurityStamp.Should().Be(stampBefore);
        (await Users.AuthenticateAsync("admin", OldPassword)).Should().NotBeNull();
    }

    [Fact]
    public async Task APasswordOfferedAsTheKey_IsRefused()
    {
        // Only recovery slots are tried: the login password of the very account must not work as a "recovery key".
        var result = await Users.ResetPasswordWithRecoveryKeyAsync("admin", OldPassword, NewPassword);

        result.Outcome.Should().Be(RecoveryResetOutcome.WrongKey);
        (await Users.AuthenticateAsync("admin", OldPassword)).Should().NotBeNull();
    }

    [Fact]
    public async Task UnknownUser_IsRefused_EvenWithTheRightKey()
    {
        var result = await Users.ResetPasswordWithRecoveryKeyAsync("nobody", _recoveryKey, NewPassword);

        result.Succeeded.Should().BeFalse();
        result.Outcome.Should().Be(RecoveryResetOutcome.NotEligible);
        result.UserId.Should().BeNull();
        (await Users.AuthenticateAsync("admin", OldPassword)).Should().NotBeNull("no account was touched");
    }

    [Fact]
    public async Task OrdinaryUser_IsRefused_EvenWithTheRightKey_AndKeepsTheirPassword()
    {
        await Users.CreateUserAsync("bob", "Bob", "BobPass1", UserRoles.User);
        var bobBefore = await UserRepo.GetByUsernameAsync("bob");

        var result = await Users.ResetPasswordWithRecoveryKeyAsync("bob", _recoveryKey, NewPassword);

        result.Succeeded.Should().BeFalse("a recovery key is the owner's tool, not a way to take over an ordinary account");
        result.Outcome.Should().Be(RecoveryResetOutcome.NotEligible);
        (await Users.AuthenticateAsync("bob", "BobPass1")).Should().NotBeNull();
        (await Users.AuthenticateAsync("bob", NewPassword)).Should().BeNull();
        (await UserRepo.GetByUsernameAsync("bob"))!.SecurityStamp.Should().Be(bobBefore!.SecurityStamp);
        (await UserRepo.GetByUsernameAsync("bob"))!.KeySlotId.Should().BeNull("an ordinary user must not be handed a key slot");
    }

    [Fact]
    public async Task ADeletedSuperadmin_IsRefused()
    {
        var carol = await Users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);
        await Users.DeleteUserAsync(carol.Id);

        (await Users.ResetPasswordWithRecoveryKeyAsync("carol", _recoveryKey, NewPassword)).Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task ASuperadminPromotedWithoutAKeySlot_GetsOne()
    {
        var bob = await Users.CreateUserAsync("bob", "Bob", "BobPass1", UserRoles.User);
        await Users.UpdateUserAsync(bob.Id, "Bob", UserRoles.Superadmin);
        (await UserRepo.GetByUsernameAsync("bob"))!.KeySlotId.Should().BeNull("promotion defers the slot");
        Session.Lock();

        (await Users.ResetPasswordWithRecoveryKeyAsync("bob", _recoveryKey, "BobNewPass1")).Succeeded.Should().BeTrue();

        (await UserRepo.GetByUsernameAsync("bob"))!.KeySlotId.Should().NotBeNull();
        (await Session.UnlockAsync("BobNewPass1")).Should().BeTrue("the reset gave the promoted superadmin a slot under the new password");
    }

    [Fact]
    public async Task ANodeWithoutAnyRecoveryKey_RefusesLikeAWrongKey()
    {
        var recoverySlot = (await KeySlots.GetAllAsync()).Single(s => s.SlotType == "recovery");
        await KeyManagement.RemoveSlotAsync(recoverySlot.SlotId);

        var result = await Users.ResetPasswordWithRecoveryKeyAsync("admin", _recoveryKey, NewPassword);

        result.Outcome.Should().Be(RecoveryResetOutcome.WrongKey);
        (await Users.AuthenticateAsync("admin", OldPassword)).Should().NotBeNull();
    }

    [Fact]
    public async Task WeakNewPassword_ThrowsBeforeAnyWork_AndSaysNothingAboutTheKey()
    {
        var slotsBefore = await SlotSnapshotAsync();

        var weak = async () => await Users.ResetPasswordWithRecoveryKeyAsync("admin", _recoveryKey, "short");
        var weakForWrongKey = async () => await Users.ResetPasswordWithRecoveryKeyAsync("nobody", "not-the-key", "short");

        (await weak.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Be((await weakForWrongKey.Should().ThrowAsync<ArgumentException>()).Which.Message,
                "the password rules answer the same whoever asks");
        (await SlotSnapshotAsync()).Should().BeEquivalentTo(slotsBefore);
    }

    [Fact]
    public async Task AKeyWithWhitespaceAroundItOrInsideIt_StillWorks()
    {
        var pasted = "  " + _recoveryKey[..10] + "\r\n" + _recoveryKey[10..] + " \n";

        (await Users.ResetPasswordWithRecoveryKeyAsync("admin", pasted, NewPassword)).Succeeded.Should().BeTrue();
    }

    // A wrong key, unknown user, ordinary user, and node with no recovery slot must all pay the
    // same class of KDF work: the recovery-slot attempt (or a dummy for its absence), then the two
    // operations a successful reset would perform. Keep the floor comfortably below three full
    // derivations so machine scheduling does not make this security test flaky, while still
    // rejecting the old one-derivation wrong-key path.
    [Fact]
    public async Task EveryRefusal_PaysAtLeastOneKeyDerivation()
    {
        await Users.CreateUserAsync("bob", "Bob", "BobPass1", UserRoles.User);
        UserService.HashPassword("warm-up");
        var derivation = TimeSpan.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            UserService.HashPassword("x" + i);
            if (sw.Elapsed < derivation) derivation = sw.Elapsed;
        }
        var floor = derivation * 1.5;

        async Task<TimeSpan> Time(string user, string key)
        {
            var sw = Stopwatch.StartNew();
            (await Users.ResetPasswordWithRecoveryKeyAsync(user, key, NewPassword)).Succeeded.Should().BeFalse();
            return sw.Elapsed;
        }

        (await Time("nobody", "wrong-key")).Should().BeGreaterThan(floor, "unknown user, wrong key");
        (await Time("admin", "wrong-key")).Should().BeGreaterThan(floor, "real superadmin, wrong key");
        (await Time("nobody", _recoveryKey)).Should().BeGreaterThan(floor, "unknown user, right key");
        (await Time("bob", _recoveryKey)).Should().BeGreaterThan(floor, "ordinary user, right key");

        var recoverySlot = (await KeySlots.GetAllAsync()).Single(s => s.SlotType == "recovery");
        await KeyManagement.RemoveSlotAsync(recoverySlot.SlotId);
        (await Time("admin", _recoveryKey)).Should().BeGreaterThan(floor, "a node with no recovery key at all");
    }
}
