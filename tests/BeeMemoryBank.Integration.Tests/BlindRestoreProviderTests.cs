using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Plan 3.4: a blind node never serves a network restore — it holds no restore snapshot and is
/// itself waiting for a reseed — so a full node fetching a restore never picks it as a seeder.
/// </summary>
public class BlindRestoreProviderTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();

    public Task InitializeAsync() => _factory.InitializeNodeAsync(password: "blindRestoreProviderPw");

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task BlindPeer_IsNeverASeederCandidate()
    {
        using var scope = _factory.Services.CreateScope();
        var whitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        var identity = await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync();
        await whitelist.CreateAsync(new WhitelistEntry
        {
            NodeId = BlindNodeId.NewId(),
            DisplayName = "Blind",
            Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey,
            ApiAddress = "http://127.0.0.1:1",
            Status = "A",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        // An originator with no address of its own and a payload URL that is not a candidate
        // either: the blind peer is the only address left to try.
        var originator = new WhitelistEntry { NodeId = Guid.NewGuid(), DisplayName = "Hub", Status = "A" };
        var payload = new RestoreNetworkEventPayload("00", DateTime.UtcNow.ToString("O"), 1,
            DateTime.UtcNow.AddHours(1).ToString("O"), SourceUrl: "http://not-a-candidate", FilterSecrets: true);
        var dest = Path.Combine(_factory.DataPath, "restore-probe.bin");

        var act = () => scope.ServiceProvider.GetRequiredService<RestoreInitiatorService>()
            .DownloadFromWhitelistedSeederAsync(Guid.NewGuid().ToString(), payload, originator, identity!, whitelist, dest);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("No reachable seeders found*");
    }
}
