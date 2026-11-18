using BeeMemoryBank.Cli;
using BeeMemoryBank.Cli.Commands;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Tests;

public class SharedDoorsCliTests
{
    [Fact]
    public async Task ResetPasswordCommand_RefusesReplicatedRow_NamingAccountsNode_AfterRecoveryKeyVerified()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmb_cli_shared_" + Guid.NewGuid().ToString("N"));
        (await PlainNode.InitAsync(dir, "installer", "InstallerPass1")).Should().Be(0);

        Guid homeId;
        string ownedRecoveryKey;
        string legacyRecoveryKey;
        await using (var services = await CliServiceProvider.CreateAsync(dir))
        {
            using var scope = services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<SessionService>();
            (await session.UnlockAsync("InstallerPass1")).Should().BeTrue();
            var keyMgmt = scope.ServiceProvider.GetRequiredService<KeyManagementService>();
            ownedRecoveryKey = await keyMgmt.AddRecoveryKeyAsync();
            int legacySlotId;
            (legacyRecoveryKey, legacySlotId) = await keyMgmt.IssueRecoveryKeyAsync(label: "pre-2.12");
            var db = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
            using (var conn = db.CreateConnection())
                await Dapper.SqlMapper.ExecuteAsync(conn, "UPDATE tbl_key_slot SET owner_user_id = NULL WHERE slot_id = @Id", new { Id = legacySlotId });

            var homeRepo = scope.ServiceProvider.GetRequiredService<IAccountsHomeRepository>();
            var wlRepo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
            homeId = Guid.NewGuid();
            var (pub, _) = Ed25519Signer.GenerateKeyPair();
            await wlRepo.CreateAsync(new WhitelistEntry
            {
                NodeId = homeId,
                DisplayName = "HomeNode",
                Ed25519PublicKey = pub,
                Status = WhitelistStatuses.Active,
                IsSuperadmin = true,
                Ownership = OwnershipKinds.Shared,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await homeRepo.UpsertAsync(new AccountsHomeRecord(homeId, 1, homeId, Guid.NewGuid(), DateTime.UtcNow));

            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var slots = scope.ServiceProvider.GetRequiredService<IKeySlotRepository>();
            var dek = session.GetMasterDek();
            var salt = KeyDerivation.GenerateSalt();
            var kek = KeyDerivation.DeriveKek("AlicePass1", salt);
            var door = Doors.NewPasswordSlot(KeySlotTypes.User, kek, salt,
                CryptoConstants.DefaultArgonMemory, CryptoConstants.DefaultArgonIterations, CryptoConstants.DefaultArgonParallelism, dek);
            Array.Clear(kek);
            Array.Clear(dek);

            var aliceId = await users.CreateAsync(new User
            {
                PersonId = Guid.NewGuid(),
                Username = "alice",
                DisplayName = "Alice",
                PasswordHash = "",
                Role = UserRoles.Superadmin,
                IsActive = true,
                Replicated = true,
                CreatedAt = DateTime.UtcNow,
            });
            door.OwnerUserId = aliceId;
            var slotId = await slots.CreateAsync(door);
            var alice = (await users.GetByIdAsync(aliceId))!;
            alice.KeySlotId = slotId;
            await users.UpdateAsync(alice);
        }

        // Wrong recovery key gives generic refusal (no username oracle).
        var wrongOut = new StringWriter();
        var wrongExit = await UserCommand.HandleResetPasswordAsync(
            dir, "alice", recoveryKeyStdin: true, passwordHint: "new hint",
            input: new StringReader("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\nNewPassword1\n"),
            output: wrongOut);
        wrongExit.Should().Be(1);
        wrongOut.ToString().Should().Be("Error: the username or the recovery key is not correct." + Environment.NewLine);

        // Another superadmin's personal (2.12) recovery key also gives generic refusal (no oracle).
        var otherOwnerOut = new StringWriter();
        var otherOwnerExit = await UserCommand.HandleResetPasswordAsync(
            dir, "alice", recoveryKeyStdin: true, passwordHint: "new hint",
            input: new StringReader($"{ownedRecoveryKey}\nNewPassword1\n"),
            output: otherOwnerOut);
        otherOwnerExit.Should().Be(1);
        otherOwnerOut.ToString().Should().Be("Error: the username or the recovery key is not correct." + Environment.NewLine);

        // Valid ownerless recovery key refuses replicated row naming the accounts node.
        var okOut = new StringWriter();
        var okExit = await UserCommand.HandleResetPasswordAsync(
            dir, "alice", recoveryKeyStdin: true, passwordHint: "new hint",
            input: new StringReader($"{legacyRecoveryKey}\nNewPassword1\n"),
            output: okOut);
        okExit.Should().Be(1);
        okOut.ToString().Should().Contain("HomeNode").And.Contain(homeId.ToString("D"));
    }
}
