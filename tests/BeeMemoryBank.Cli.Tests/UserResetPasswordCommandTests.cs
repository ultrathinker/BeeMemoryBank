using BeeMemoryBank.Cli;
using BeeMemoryBank.Cli.Commands;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Tests;

/// <summary>
/// <c>bmb user reset-password --user NAME --recovery-key-stdin</c> (BMB-156): the command-line twin of the
/// Sign In page's "Forgot your password? Use a recovery key". Secrets arrive on stdin, never as arguments;
/// refusals are one sentence; the result is checked through the same sign-in / unlock paths a person uses.
/// </summary>
public class UserResetPasswordCommandTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "bmb_cli_reset_" + Guid.NewGuid().ToString("N"));

    private const string NodeName = "admin";
    private const string OldPassword = "OldAdminPass1";
    private const string NewPassword = "NewAdminPass2";

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private async Task<string> InitWithRecoveryKeyAsync()
    {
        (await InitCommand.HandleAsync(_tempDir, NodeName, OldPassword)).Should().Be(0);
        await using var services = await CliServiceProvider.CreateAsync(_tempDir);
        using var scope = services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<SessionService>().UnlockAsync(OldPassword)).Should().BeTrue();
        return await scope.ServiceProvider.GetRequiredService<KeyManagementService>().AddRecoveryKeyAsync();
    }

    private async Task<(int Exit, string Output)> ResetAsync(string user, string stdin)
    {
        var output = new StringWriter();
        var exit = await UserCommand.HandleResetPasswordAsync(_tempDir, user, recoveryKeyStdin: true, new StringReader(stdin), output);
        return (exit, output.ToString());
    }

    private async Task<(bool OldSignsIn, bool NewSignsIn, bool NewUnlocks)> ProbeAsync()
    {
        await using var services = await CliServiceProvider.CreateAsync(_tempDir);
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserService>();
        var oldOk = await users.AuthenticateAsync(NodeName, OldPassword) != null;
        var newOk = await users.AuthenticateAsync(NodeName, NewPassword) != null;
        var unlocks = await scope.ServiceProvider.GetRequiredService<SessionService>().UnlockAsync(NewPassword);
        return (oldOk, newOk, unlocks);
    }

    [Fact]
    public async Task RightKeyOnStdin_ChangesThePassword_AndTheNewOneUnlocks()
    {
        var key = await InitWithRecoveryKeyAsync();

        var (exit, output) = await ResetAsync(NodeName, key + "\n" + NewPassword + "\n");

        exit.Should().Be(0, output);
        output.Should().Contain("Password changed");
        output.Should().NotContain(key).And.NotContain(NewPassword);
        var (oldOk, newOk, unlocks) = await ProbeAsync();
        oldOk.Should().BeFalse();
        newOk.Should().BeTrue();
        unlocks.Should().BeTrue("the key slot was re-wrapped under the new password");
    }

    [Fact]
    public async Task EveryRefusal_IsTheSameSentence()
    {
        var key = await InitWithRecoveryKeyAsync();
        var wrong = Convert.ToBase64String(new byte[32]);

        var outputs = new List<string>();
        foreach (var (user, k) in new[] { (NodeName, wrong), ("ghost", key), ("ghost", wrong) })
        {
            var (exit, output) = await ResetAsync(user, k + "\n" + NewPassword + "\n");
            exit.Should().Be(1);
            outputs.Add(output);
        }

        outputs.Distinct().Should().HaveCount(1, "the command must not say which of the two was wrong");
        outputs[0].Should().Contain("the username or the recovery key is not correct");
        var (oldOk, newOk, _) = await ProbeAsync();
        oldOk.Should().BeTrue("nothing was changed");
        newOk.Should().BeFalse();
    }

    [Fact]
    public async Task AnOrdinaryUser_IsRefused_EvenWithTheRightKey()
    {
        var key = await InitWithRecoveryKeyAsync();
        await using (var services = await CliServiceProvider.CreateAsync(_tempDir))
        {
            using var scope = services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<SessionService>().UnlockAsync(OldPassword);
            await scope.ServiceProvider.GetRequiredService<UserService>()
                .CreateUserAsync("bob", "Bob", "BobPass12345", UserRoles.User);
        }

        var (exit, output) = await ResetAsync("bob", key + "\n" + NewPassword + "\n");

        exit.Should().Be(1);
        output.Should().Contain("the username or the recovery key is not correct");
        await using var check = await CliServiceProvider.CreateAsync(_tempDir);
        using var checkScope = check.CreateScope();
        (await checkScope.ServiceProvider.GetRequiredService<UserService>().AuthenticateAsync("bob", "BobPass12345"))
            .Should().NotBeNull("bob's password is untouched");
    }

    [Fact]
    public async Task AWeakPassword_IsRefusedWithTheRule_AndNothingChanges()
    {
        var key = await InitWithRecoveryKeyAsync();

        var (exit, output) = await ResetAsync(NodeName, key + "\nshort\n");

        exit.Should().Be(1);
        output.Should().Contain("at least 8 characters");
        (await ProbeAsync()).OldSignsIn.Should().BeTrue();
    }

    [Fact]
    public async Task MissingStdinLines_AreExplained()
    {
        await InitWithRecoveryKeyAsync();

        var (exit, output) = await ResetAsync(NodeName, "only-one-line\n");

        exit.Should().Be(1);
        output.Should().Contain("line 1 of stdin is the recovery key and line 2 is the new password");
    }

    [Fact]
    public async Task TheResetIsAudited_WithoutTheKey()
    {
        var key = await InitWithRecoveryKeyAsync();
        (await ResetAsync(NodeName, key + "\n" + NewPassword + "\n")).Exit.Should().Be(0);

        await using var services = await CliServiceProvider.CreateAsync(_tempDir);
        using var scope = services.CreateScope();
        using var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        var rows = conn.Query<(string Actor, string Details)>(
            "SELECT actor_type AS Actor, details AS Details FROM tbl_audit_log WHERE action = 'user_password_recovery_reset'").ToList();
        rows.Should().ContainSingle();
        rows[0].Actor.Should().Be("cli");
        rows[0].Details.Should().NotContain(key).And.NotContain(NewPassword);
    }
}
