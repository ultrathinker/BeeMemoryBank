using System.Text;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.FullIos.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.FullIos.Tests;

/// <summary>The phone's vault: made here with a recovery key shown once, opened and closed with the master password, joined only by a code or an https address.</summary>
public class FullVaultTests
{
    [Theory]
    [InlineData("short1A", "short1A", "at least 8 characters")]
    [InlineData("alllowercase1", "alllowercase1", "uppercase")]
    [InlineData("NoDigitsHere", "NoDigitsHere", "digit")]
    [InlineData("Correct-Horse-9", "Correct-Horse-8", "not the same")]
    public void ANewMasterPassword_FollowsTheProductsRule_AndMustBeRepeated(string password, string repeated, string complaint) =>
        FullVault.CheckNewPassword(password, repeated).Should().Contain(complaint);

    [Fact]
    public void AGoodNewMasterPassword_IsAccepted() => FullVault.CheckNewPassword("Correct-Horse-9", "Correct-Horse-9").Should().BeNull();

    [Theory]
    [InlineData("  Anna's phone ", "iPhone", "Anna's phone")]
    [InlineData("", "iPhone", "iPhone")]
    [InlineData(null, null, "iPhone")]
    public void TheNodeName_IsWhatWasTyped_OrTheDevicesName(string? typed, string? device, string expected) =>
        FullVault.NodeName(typed, device).Should().Be(expected);

    [Theory]
    [InlineData("bmb.example.com", "https://bmb.example.com")]
    [InlineData("https://bmb.example.com/", "https://bmb.example.com")]
    [InlineData("https://bmb.example.com:8443", "https://bmb.example.com:8443")]
    [InlineData("http://192.168.1.5:5300", null)]
    [InlineData("https://bmb.example.com/api/join", null)]
    [InlineData("https://user:pw@bmb.example.com", null)]
    [InlineData("https://bmb.example.com/?x=1", null)]
    public void AServerAddress_IsHttpsWithAHostAndNothingElse(string typed, string? expected) =>
        FullVault.ServerAddress(typed).Should().Be(expected);

    [Fact]
    public async Task Create_MakesAnOpenVault_WithARecoveryKeyThatOpensItToo()
    {
        var (test, recovery) = await TestVault.CreateAsync("create");
        await using var _ = test;

        (await test.Vault.GetStateAsync()).Should().Be(VaultState.Unlocked);
        Convert.FromBase64String(recovery).Should().HaveCount(32, "a recovery key is 256 random bits");
        (await test.Vault.IdentityAsync())!.DisplayName.Should().Be("Test iPhone");

        test.Vault.Lock();
        (await test.Vault.GetStateAsync()).Should().Be(VaultState.Locked);

        var opened = await test.Services.GetRequiredService<SessionService>().TryOpenWithRecoveryKeyAsync(recovery);
        opened.Should().NotBeNull("the recovery key shown at creation is a working key slot of this vault");
        Array.Clear(opened!);
    }

    [Fact]
    public async Task Unlock_TakesOnlyTheMasterPassword()
    {
        var (test, _) = await TestVault.CreateAsync("unlock");
        await using var _2 = test;
        test.Vault.Lock();

        (await test.Vault.UnlockAsync("Wrong-Horse-9")).Should().BeFalse();
        test.Vault.IsUnlocked.Should().BeFalse();
        (await test.Vault.UnlockAsync(TestVault.Password)).Should().BeTrue();
        test.Vault.IsUnlocked.Should().BeTrue();
    }

    [Fact]
    public async Task Lock_RaisesLocked_SoThePagesCanLeave()
    {
        var (test, _) = await TestVault.CreateAsync("locked-event");
        await using var _2 = test;
        var raised = 0;
        test.Vault.Locked += () => raised++;

        test.Vault.Lock();
        test.Vault.Lock();

        raised.Should().Be(1, "a second Lock on a locked vault does nothing");
    }

    [Fact]
    public async Task ASecondVault_IsRefused()
    {
        var (test, _) = await TestVault.CreateAsync("twice");
        await using var _2 = test;

        var act = () => test.Vault.CreateAsync("Again", TestVault.Password);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already holds a memory bank*");
    }

    [Fact]
    public async Task NeitherThePasswordNorTheRecoveryKey_IsWrittenAnywhereInTheDataFolder()
    {
        var (test, recovery) = await TestVault.CreateAsync("no-secrets");
        await using (test)
        {
            await test.Notes.SaveAsync(null, "A note", "/", "text");
            test.Vault.Lock();
        }
        SqliteConnection.ClearAllPools();

        foreach (var file in Directory.GetFiles(test.Paths.DataDirectory, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            foreach (var secret in new[] { TestVault.Password, recovery })
            {
                Contains(bytes, Encoding.UTF8.GetBytes(secret)).Should().BeFalse($"{Path.GetFileName(file)} must not hold a secret (UTF-8)");
                Contains(bytes, Encoding.Unicode.GetBytes(secret)).Should().BeFalse($"{Path.GetFileName(file)} must not hold a secret (UTF-16)");
            }
        }
    }

    [Fact]
    public async Task ADamagedJoinCode_IsRefusedOnThePhone_AndNothingIsSent()
    {
        await using var test = await TestVault.PrepareAsync("join-damaged");

        var act = () => test.Vault.JoinAsync("iPhone", "bmb-join:?a=https%3A%2F%2F192.168.1.5%3A5311&s=short", TestVault.Password);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage(JoinCode.NotValidMessage);
        (await test.Vault.GetStateAsync()).Should().Be(VaultState.NoVault);
    }

    [Fact]
    public async Task APlainHttpAddress_IsRefused_BeforeThePasswordGoesAnywhere()
    {
        await using var test = await TestVault.PrepareAsync("join-http");

        var act = () => test.Vault.JoinAsync("iPhone", "http://192.168.1.5:5300", TestVault.Password);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*https*");
        (await test.Vault.GetStateAsync()).Should().Be(VaultState.NoVault);
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
}
