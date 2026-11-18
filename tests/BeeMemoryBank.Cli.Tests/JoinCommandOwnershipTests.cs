using System.Net;
using System.Text.Json;
using BeeMemoryBank.Cli.Commands;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using BeeMemoryBank.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Tests;

[Collection("CliSealedStatics")]
public class JoinCommandOwnershipTests : IDisposable
{
    private const string Password = "cliJoinOwnershipPassword";

    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "bmb_cli_join_own_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        JoinedNode.Reset();
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Join_SendsSharedAndLogin_AndStoresRemoteAndInheritedOwnership()
    {
        var hostId = Guid.NewGuid();
        var sharedPeer = Guid.NewGuid();
        var personalPeer = Guid.NewGuid();
        var unknownPeer = Guid.NewGuid();
        var alicePersonId = Guid.NewGuid();
        var bobPersonId = Guid.NewGuid();

        var (started, port) = PortBinding.StartHttpListener();
        using var listener = started;
        var host = new FakeJoinHost(hostId);

        string? capturedJoinBody = null;
        var serve = Task.Run(async () =>
        {
            var path = "";
            for (var request = 0; request < 4 && path != "/api/sync/snapshot/for-join"; request++)
            {
                var ctx = await listener.GetContextAsync();
                path = ctx.Request.Url!.AbsolutePath;
                using var reader = new StreamReader(ctx.Request.InputStream);
                var body = await reader.ReadToEndAsync();
                if (request == 0) capturedJoinBody = body;

                var (status, headers, bytes) = path == "/api/join"
                    ? (200, new Dictionary<string, string>(), System.Text.Encoding.UTF8.GetBytes(
                        BuildResponseJson(host, bobPersonId, sharedPeer, personalPeer, unknownPeer, alicePersonId)))
                    : host.Answer(path) ?? (404, new Dictionary<string, string>(), []);
                ctx.Response.StatusCode = status;
                foreach (var (name, value) in headers) ctx.Response.Headers[name] = value;
                ctx.Response.ContentType = path == "/api/sync/snapshot/for-join" ? "application/gzip" : "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(
            _tempDir, $"http://localhost:{port}", Password, "BuildBox",
            output: output, login: "alice", shared: true);
        await serve;
        rc.Should().Be(0, output.ToString());

        using (var sentDoc = JsonDocument.Parse(capturedJoinBody!))
        {
            sentDoc.RootElement.GetProperty("login").GetString().Should().Be("alice");
            sentDoc.RootElement.GetProperty("ownership").GetString().Should().Be(OwnershipKinds.Shared);
        }
        output.ToString().Should().Contain("Joined as Alice Display");

        await using var services = await JoinedNode.OpenAsync(_tempDir, Password);
        using var scope = services.CreateScope();
        var rows = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();

        var hostRow = (await rows.GetByNodeIdAsync(hostId))!;
        hostRow.Ownership.Should().Be(OwnershipKinds.Personal);
        hostRow.OwnerPersonId.Should().Be(bobPersonId);
        hostRow.OwnerLabel.Should().Be("Bob Display");
        hostRow.NeedsReview.Should().BeFalse();
        hostRow.VersionOf(WhitelistConcerns.Ownership).Should().Be(new RowVersion(14, sharedPeer));

        var sharedRow = (await rows.GetByNodeIdAsync(sharedPeer))!;
        sharedRow.Ownership.Should().Be(OwnershipKinds.Shared);
        sharedRow.NeedsReview.Should().BeFalse();
        sharedRow.VersionOf(WhitelistConcerns.Ownership).Should().Be(new RowVersion(9, hostId));

        var personalRow = (await rows.GetByNodeIdAsync(personalPeer))!;
        personalRow.Ownership.Should().Be(OwnershipKinds.Personal);
        personalRow.OwnerPersonId.Should().Be(alicePersonId);
        personalRow.OwnerLabel.Should().Be("Alice Display");
        personalRow.NeedsReview.Should().BeFalse();

        var unknownRow = (await rows.GetByNodeIdAsync(unknownPeer))!;
        unknownRow.Ownership.Should().Be(OwnershipKinds.Unknown);
        unknownRow.NeedsReview.Should().BeTrue();
    }

    private static string BuildResponseJson(FakeJoinHost host, Guid bobPersonId,
        Guid sharedPeer, Guid personalPeer, Guid unknownPeer, Guid alicePersonId)
    {
        var dek = host.Dek;
        var salt = KeyDerivation.GenerateSalt();
        var (encDek, iv) = MasterKeyManager.WrapMasterDek(dek, KeyDerivation.DeriveKek(Password, salt));
        string Key() => Convert.ToBase64String(Ed25519Signer.GenerateKeyPair().publicKey);

        return JsonSerializer.Serialize(new
        {
            joinedAsDisplayName = "Alice Display",
            remoteNode = new
            {
                nodeId = host.HostId,
                displayName = "Host",
                ed25519PublicKeyB64 = host.PublicKeyB64,
                protocolVersion = BeeMemoryBank.Sync.Metadata.EventClasses.SealedProtocol,
                ownership = OwnershipKinds.Personal,
                ownerPersonId = bobPersonId,
                ownerLabel = "Bob Display",
                ownershipVersion = new { lamportTs = 14L, sourceNodeId = sharedPeer, undated = false }
            },
            keySlot = new
            {
                encryptedMasterDekB64 = Convert.ToBase64String(encDek),
                ivB64 = Convert.ToBase64String(iv),
                saltB64 = Convert.ToBase64String(salt),
                argonMemory = CryptoConstants.DefaultArgonMemory,
                argonIterations = CryptoConstants.DefaultArgonIterations,
                argonParallelism = CryptoConstants.DefaultArgonParallelism
            },
            whitelist = new object[]
            {
                new
                {
                    nodeId = sharedPeer,
                    displayName = "Shared Server",
                    ed25519PublicKeyB64 = Key(),
                    apiAddress = (string?)null,
                    isSuperadmin = true,
                    ownership = OwnershipKinds.Shared,
                    versions = new
                    {
                        membership = new { lamportTs = 5L, sourceNodeId = host.HostId, undated = false },
                        superadmin = new { lamportTs = 5L, sourceNodeId = host.HostId, undated = false },
                        name = new { lamportTs = 5L, sourceNodeId = host.HostId, undated = false },
                        address = new { lamportTs = 5L, sourceNodeId = host.HostId, undated = false },
                        tls = new { lamportTs = 5L, sourceNodeId = host.HostId, undated = false },
                        ownership = new { lamportTs = 9L, sourceNodeId = host.HostId, undated = false },
                    }
                },
                new
                {
                    nodeId = personalPeer,
                    displayName = "Alice Phone",
                    ed25519PublicKeyB64 = Key(),
                    apiAddress = (string?)null,
                    isSuperadmin = false,
                    ownership = OwnershipKinds.Personal,
                    ownerPersonId = alicePersonId,
                    ownerLabel = "Alice Display"
                },
                new
                {
                    nodeId = unknownPeer,
                    displayName = "Old Peer",
                    ed25519PublicKeyB64 = Key(),
                    apiAddress = (string?)null,
                    isSuperadmin = false
                }
            }
        });
    }
}
