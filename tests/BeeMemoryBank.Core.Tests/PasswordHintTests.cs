using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// The mandatory password hint of release 2.7.5: the one policy every entry point shares, and the service layer
/// that stores it (while still accepting null for seeds, tools and tests).
/// </summary>
public class PasswordHintTests : TestFixture
{
    private UserService Users = null!;
    private UserRepository UserRepo = null!;
    private KeySlotRepository KeySlots = null!;
    private RoleRepository Roles = null!;
    private FolderAccessService FolderAccess = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", "AdminPass1");
        await Session.UnlockAsync("AdminPass1");

        UserRepo = new UserRepository(Factory);
        KeySlots = new KeySlotRepository(Factory);
        Roles = new RoleRepository(Factory);
        FolderAccess = new FolderAccessService(new ServiceCollection()
            .AddSingleton<IDbConnectionFactory>(_ => Factory)
            .AddScoped<IUserRepository>(_ => UserRepo)
            .AddScoped<IRoleRepository>(_ => Roles)
            .AddScoped<IRoleAclRepository>(_ => new RoleAclRepository(Factory))
            .AddScoped<IFolderAclRepository>(_ => new FolderAclRepository(Factory))
            .AddScoped<IFolderRepository>(_ => new FolderRepository(Factory, ScopeHolder))
            .AddScoped(_ => ScopeHolder)
            .BuildServiceProvider());
        Users = new UserService(UserRepo, KeySlots, Session, Roles, FolderAccess);
    }

    // ── the policy ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AGoodHint_IsAccepted_AndTrimmed()
    {
        PasswordHintPolicy.Validate("  the blue folder  ", "Correct-Horse-9", "admin").Should().BeNull();
        PasswordHintPolicy.Normalize("  the blue folder  ").Should().Be("the blue folder");
        PasswordHintPolicy.NormalizeAndValidate("  the blue folder  ", "Correct-Horse-9", "admin").Should().Be("the blue folder");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void AMissingHint_IsRefusedWithTheOneRequiredSentence(string? hint)
        => PasswordHintPolicy.Validate(hint, "Correct-Horse-9", "admin").Should().Be(PasswordHintPolicy.RequiredMessage);

    [Theory]
    [InlineData("ab", false, "2 characters")]
    [InlineData("abc", true, "3 characters")]
    [InlineData("012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789", true, "120 characters")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890", false, "121 characters")]
    public void TheLengthBounds_AreThreeToAHundredAndTwenty(string hint, bool accepted, string because)
    {
        var problem = PasswordHintPolicy.Validate(hint, "Correct-Horse-9", "admin");
        if (accepted) problem.Should().BeNull(because);
        else problem.Should().Contain("3 to 120", because);
    }

    [Fact]
    public void ControlCharactersAndLineBreaks_AreRefused()
    {
        PasswordHintPolicy.Validate("blue\nfolder", "Correct-Horse-9", "admin").Should().Contain("line breaks");
        PasswordHintPolicy.Validate("blue\tfolder", "Correct-Horse-9", "admin").Should().Contain("control characters");
    }

    [Theory]
    [InlineData("My password is Correct-Horse-9, written down", true)]
    [InlineData("my password is correct-horse-9", true)]
    [InlineData("correct-horse-9", true)]
    [InlineData("something about horses", false)]
    public void TheHintMustNotContainThePassword_CaseInsensitively(string hint, bool refused)
    {
        var problem = PasswordHintPolicy.Validate(hint, "Correct-Horse-9", "admin");
        if (refused) problem.Should().Contain("must not contain the password");
        else problem.Should().BeNull();
    }

    [Fact]
    public void TheHintMustNotContainTheUsername()
    {
        PasswordHintPolicy.Validate("admin's private note", "Correct-Horse-9", "Admin").Should().Contain("must not contain the username");
        PasswordHintPolicy.Validate("the administrator's note", "Correct-Horse-9", "carol").Should().BeNull("the username is a different word");
    }

    [Fact]
    public void AUnicodeHint_IsAllowed_AndCountedAsText()
    {
        PasswordHintPolicy.Validate("café-über-ß", "Correct-Horse-9", "admin").Should().BeNull();
        PasswordHintPolicy.Validate("日本語のヒント", "Correct-Horse-9", "admin").Should().BeNull();
    }

    // ── the service layer ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreatingAUser_StoresTheHint_AndItIsReadableWhileTheVaultIsLocked()
    {
        var bob = await Users.CreateUserAsync("bob", "Bob", "BobPass1x", UserRoles.User, passwordHint: "  the blue folder  ");

        var stored = await UserRepo.GetByIdAsync(bob.Id);
        stored!.PasswordHint.Should().Be("the blue folder");

        Session.Lock();
        Session.IsUnlocked.Should().BeFalse();
        (await UserRepo.GetPasswordHintAsync("bob")).Should().Be("the blue folder",
            "the sign-in page needs it before any password opens the vault");
    }

    [Fact]
    public async Task CreatingAUser_WithoutAHint_IsAllowedForInternalCallers()
    {
        var bob = await Users.CreateUserAsync("bob", "Bob", "BobPass1x", UserRoles.User);

        (await UserRepo.GetPasswordHintAsync("bob")).Should().BeNull();
    }

    [Fact]
    public async Task CreatingAUser_WithAnInvalidHint_IsRefused()
    {
        var act = () => Users.CreateUserAsync("bob", "Bob", "BobPass1x", UserRoles.User, passwordHint: "bob's own note");

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*must not contain the username*");
        (await UserRepo.GetByUsernameAsync("bob")).Should().BeNull("nothing is written when the hint is refused");
    }

    [Fact]
    public async Task ANewPassword_WithAHint_ReplacesTheOldHint()
    {
        var bob = await Users.CreateUserAsync("bob", "Bob", "BobPass1x", UserRoles.User, passwordHint: "the old hint");

        await Users.ChangePasswordAsync(bob.Id, "BobPass1x", "BobPass2x", passwordHint: "the new hint");

        (await UserRepo.GetPasswordHintAsync("bob")).Should().Be("the new hint");
    }

    [Fact]
    public async Task ANewPassword_WithoutAHint_KeepsTheHintThePersonWrote()
    {
        var bob = await Users.CreateUserAsync("bob", "Bob", "BobPass1x", UserRoles.User, passwordHint: "the old hint");

        await Users.AdminChangePasswordAsync(bob.Id, "BobPass2x");

        (await UserRepo.GetPasswordHintAsync("bob")).Should().Be("the old hint", "an internal caller does not erase what a person wrote");
    }

    [Fact]
    public async Task ANewPassword_WhoseHintContainsIt_IsRefusedBeforeAnythingIsWritten()
    {
        var bob = await Users.CreateUserAsync("bob", "Bob", "BobPass1x", UserRoles.User, passwordHint: "the old hint");

        var act = () => Users.ChangePasswordAsync(bob.Id, "BobPass1x", "BobPass2x", passwordHint: "it is BobPass2x");

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*must not contain the password*");
        (await UserRepo.GetByIdAsync(bob.Id))!.PasswordHint.Should().Be("the old hint");
        (await Users.AuthenticateAsync("bob", "BobPass1x")).Should().NotBeNull("the old password still works: the change did not happen");
    }

    [Fact]
    public async Task TheRecoveryKeyReset_StoresTheHintOfTheNewPassword()
    {
        var key = await KeyManagement.AddRecoveryKeyAsync();

        var result = await Users.ResetPasswordWithRecoveryKeyAsync("admin", key, "AdminPass2", passwordHint: "the sign-in hint");

        result.Succeeded.Should().BeTrue();
        (await UserRepo.GetPasswordHintAsync("admin")).Should().Be("the sign-in hint");
    }

    [Fact]
    public async Task TheSoftPrompt_AddsAHintWithoutTouchingThePassword()
    {
        var admin = await UserRepo.GetByUsernameAsync("admin");
        admin!.PasswordHint.Should().BeNull();

        await Users.SetPasswordHintAsync(admin.Id, "  the sign-in hint  ");

        (await UserRepo.GetPasswordHintAsync("admin")).Should().Be("the sign-in hint");
        (await Users.AuthenticateAsync("admin", "AdminPass1")).Should().NotBeNull("the password is unchanged");
    }

    [Fact]
    public async Task TheSoftPrompt_StillRefusesTheUsernameAndLengthRules()
    {
        var admin = await UserRepo.GetByUsernameAsync("admin");

        var byName = () => Users.SetPasswordHintAsync(admin!.Id, "I am admin");
        await byName.Should().ThrowAsync<ArgumentException>().WithMessage("*must not contain the username*");

        var tooShort = () => Users.SetPasswordHintAsync(admin!.Id, "ab");
        await tooShort.Should().ThrowAsync<ArgumentException>().WithMessage("*3 to 120*");
    }
}