using System.Security.Cryptography;
using BeeMemoryBank.Cli.Commands;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Tests;

/// <summary>
/// <c>bmb agent create</c> on a sealed node (docs/design/step-b-spec.md 3.6, 4.1, 4.3, red test 15.2.3): an agent that carries a wrapped DEK
/// is a door of the boot store, so that its key can wake the closed node; an agent without one is not; the boot store never holds the
/// agent's name or its key.
/// </summary>
public sealed class AgentCommandSealedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_cli_agent_" + Guid.NewGuid().ToString("N"));
    private const string Password = "AdminPass1x";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<(UnlockMaterialMirror Mirror, BootStore Boot, BootDoorStore Doors, FakeGate Gate)> OpenMirrorAsync(bool sealedNode = true)
    {
        (await InitCommand.HandleAsync(_dir, "admin", Password)).Should().Be(0);
        var vault = new DbConnectionFactory(_dir);
        var boot = new BootStore(Path.Combine(_dir, "beememorybank.boot.db"));
        await boot.EnsureCreatedAsync();
        await boot.SetMetaAsync(BootMetaKeys.NodeId, Guid.NewGuid().ToString("D"));
        await boot.SetMetaAsync(BootMetaKeys.DbkGeneration, "1");
        var doors = new BootDoorStore(boot.Connections);
        var gate = new FakeGate(isSealed: sealedNode);
        var mirror = new UnlockMaterialMirror(vault, boot, doors, new BootDbKeyStore(boot.Connections), new BootStoreWriter(boot.Connections), gate,
            new FakeVaultKeys(RandomNumberGenerator.GetBytes(32)));
        return (mirror, boot, doors, gate);
    }

    [Fact]
    public async Task ASuperadminsAgentIsADoor_AndTheBootStoreHoldsNeitherItsNameNorItsKey()
    {
        var (mirror, boot, doors, _) = await OpenMirrorAsync();
        var name = MetadataCanaries.Ascii("agent");
        var output = new StringWriter();

        (await AgentCommand.HandleCreateAsync(_dir, name, null, 0, Password, output, mirror)).Should().Be(0, output.ToString());

        var door = (await doors.ListAsync()).Should().ContainSingle(d => d.Kind == BootDoorKinds.Agent).Subject;
        door.Pending.Should().BeFalse("the rewrite after the creation made it current");
        door.DekFingerprint.Should().NotBeNull();
        door.KeyHash.Should().NotBeNullOrEmpty();
        door.KdfVersion.Should().Be(1);
        (await doors.FindAgentAsync(door.KeyHash!)).Should().ContainSingle();

        var apiKey = output.ToString().Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("bee_", StringComparison.Ordinal));
        await boot.CheckpointAsync();
        MetadataCanaries.ScanFile(boot.FilePath, [name, apiKey]).Should().BeEmpty("neither the agent's name nor its key is in the boot store");
        door.KeyHash.Should().NotBe(apiKey);
    }

    [Fact]
    public async Task AnOrdinaryUsersAgentCarriesNoWrappedDek_SoItIsNoDoor()
    {
        var (mirror, _, doors, _) = await OpenMirrorAsync();
        await using (var services = await CliServiceProvider.CreateAsync(_dir))
        {
            using var scope = services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<SessionService>();
            (await session.UnlockAsync(Password)).Should().BeTrue();
            await scope.ServiceProvider.GetRequiredService<UserService>().CreateUserAsync("bob", "Bob", "BobPass1x", UserRoles.User);
        }
        var bob = await ReadUserIdAsync("bob");

        (await AgentCommand.HandleCreateAsync(_dir, "bobs-agent", null, bob, Password, new StringWriter(), mirror)).Should().Be(0);

        (await doors.ListAsync()).Should().NotContain(d => d.Kind == BootDoorKinds.Agent);
    }

    [Fact]
    public async Task OnAPlainNodeTheMirrorIsLeftAlone()
    {
        var (mirror, _, doors, _) = await OpenMirrorAsync(sealedNode: false);

        (await AgentCommand.HandleCreateAsync(_dir, "plain-agent", null, 0, Password, new StringWriter(), mirror)).Should().Be(0);

        (await doors.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutAMirrorTheCommandIsTheOneItAlwaysWas()
    {
        (await InitCommand.HandleAsync(_dir, "admin", Password)).Should().Be(0);
        var output = new StringWriter();

        (await AgentCommand.HandleCreateAsync(_dir, "plain-agent", null, 0, Password, output)).Should().Be(0);

        output.ToString().Should().Contain("Agent created").And.Contain("can auto-unlock");
        File.Exists(Path.Combine(_dir, "beememorybank.boot.db")).Should().BeFalse("a plain node has no boot store");
    }

    private async Task<int> ReadUserIdAsync(string username)
    {
        using var c = new DbConnectionFactory(_dir).CreateConnection();
        return await c.ExecuteScalarAsync<int>("SELECT id FROM tbl_user WHERE username = @username", new { username });
    }
}
