using System.Net;
using System.Net.Http.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The reset of a sealed node (docs/design/step-b-spec.md 13.1; red test 15.3.13): it ends plain, empty and startable, and what is not the node's own
/// data (the owner's snapshots) stays. Since 2.6.2 there is no way back from an encrypted node to the plain one it was (BMB-306), so a reset is
/// followed by a new setup - which is encrypted again (<see cref="FreshNodeSealedSetupTests"/>) - and not by a restore of the plain node.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public sealed class SealedNodeResetTests(ActivatedPlainTemplate template) : IClassFixture<ActivatedPlainTemplate>
{
    private const string Password = ActivatedPlainTemplate.Password;

    private async Task<SealedApiNode> StartAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmb_seal_reset_" + Guid.NewGuid().ToString("N"));
        SealedNode.CopyTree(template.PlainDir, dir, skipDatabases: false);
        var node = SealedApiNode.PlainOver(dir);
        (await node.Session.UnlockAsync(Password)).Should().BeTrue();
        return node;
    }

    private static async Task SealAsync(SealedApiNode node)
    {
        var seal = node.Services.GetRequiredService<SealMigrationService>();
        var result = await seal.RunAsync(new SealOptions());
        result.Phase.Should().Be(SealProgress.Done, result.Error);
        node.Gate.IsSealed.Should().BeTrue();
    }

    [Fact]
    public async Task The_reset_of_a_sealed_node_ends_plain_empty_and_startable()
    {
        using var node = await StartAsync();
        using var client = node.Client();
        await SealAsync(node);
        // The checkbox on a sealed node, so that its record and its key file exist to be removed.
        (await client.PostAsJsonAsync("/api/keys/server-auto-unlock/enable", new { password = Password, confirmation = "START UNLOCKED" })).EnsureSuccessStatusCode();
        File.Exists(Path.Combine(node.Dir, "server-auto-unlock.enabled")).Should().BeTrue();
        Directory.CreateDirectory(Path.Combine(node.Dir, "snapshots"));
        File.WriteAllText(Path.Combine(node.Dir, "snapshots", "bmb-snapshot-kept.tar.gz"), "kept");

        using var wrong = await client.PostAsJsonAsync("/api/init/reset", new { masterPassword = "wrong" });
        wrong.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        node.Gate.IsSealed.Should().BeTrue("a wrong password resets nothing");

        using var reset = await client.PostAsJsonAsync("/api/init/reset", new { masterPassword = Password });
        reset.StatusCode.Should().Be(HttpStatusCode.OK, await reset.Content.ReadAsStringAsync());

        node.Gate.IsSealed.Should().BeFalse("the node is plain again");
        File.Exists(Path.Combine(node.Dir, "beememorybank.boot.db")).Should().BeFalse();
        File.Exists(Path.Combine(node.Dir, "server-auto-unlock.enabled")).Should().BeFalse();
        File.Exists(Path.Combine(node.Dir, "server-auto-unlock.key")).Should().BeFalse("the node created it");
        File.Exists(Path.Combine(node.Dir, "snapshots", "bmb-snapshot-kept.tar.gz")).Should().BeTrue("the owner's snapshots survive a reset");
        (await SealedApiNode.GetJsonAsync(client, "/api/vault/status")).GetProperty("mode").GetString().Should().Be("plain");

        // Startable: a restart finds a plain folder with no database, and Setup works.
        node.Restart();
        node.Gate.IsSealed.Should().BeFalse();
        using (var scope = node.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<InitializationService>().InitializeAsync("admin", "AfterReset", "AnotherPassword1!");
        (await node.Session.UnlockAsync("AnotherPassword1!")).Should().BeTrue();
    }

    [Fact]
    public async Task A_closed_sealed_node_is_reset_with_the_password_without_unlocking_the_session()
    {
        using var node = await StartAsync();
        await SealAsync(node);
        node.Restart();
        node.Gate.State.Should().Be(VaultState.Closed);

        using var scope = node.Services.CreateScope();
        var reset = scope.ServiceProvider.GetRequiredService<NodeResetService>();
        (await reset.ResetAsync("wrong", "test")).Outcome.Should().Be(NodeResetOutcome.InvalidPassword);
        node.Gate.State.Should().Be(VaultState.Closed, "a wrong password opens nothing");

        var result = await reset.ResetAsync(Password, "test");

        result.Outcome.Should().Be(NodeResetOutcome.Done);
        result.WasSealed.Should().BeTrue();
        node.Session.IsUnlocked.Should().BeFalse();
        node.Gate.IsSealed.Should().BeFalse();
        File.Exists(Path.Combine(node.Dir, "beememorybank.boot.db")).Should().BeFalse();
    }
}
