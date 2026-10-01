using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>Real API host + real blind package reader, with a locally-created v2 phone identity.</summary>
public sealed class BlindPhoneReplicaClientTests : IDisposable
{
    private readonly BlindNodeFactory _blind = new();

    public void Dispose() => _blind.Dispose();

    [Fact]
    public async Task Download_VerifiesAndAtomicallyInstallsTheServingBlindReplica()
    {
        var phoneData = Path.Combine(_blind.DataPath, "phone");
        var phoneDb = Path.Combine(phoneData, "beememorybank.db");
        var factory = new DbConnectionFactory(phoneDb);
        await new MigrationRunner(factory).RunMigrationsAsync();
        var (phonePublic, phoneSeed) = Ed25519Signer.GenerateKeyPair();
        var phoneId = BlindNodeId.NewId();
        await new NodeIdentityRepository(factory).CreateAsync(new NodeIdentity
        {
            NodeId = phoneId,
            DisplayName = "phone",
            Ed25519PublicKey = phonePublic,
            Ed25519PrivateKey = [],
            Ed25519PrivateKeyV = NodeIdentityCrypto.ExternalKeyVersion,
            CanGenerateEmbeddings = false,
            CreatedAt = DateTime.UtcNow
        });
        await _blind.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = phoneId,
            DisplayName = "phone",
            Ed25519PublicKey = phonePublic,
            Status = WhitelistStatuses.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        var server = (await _blind.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        using var http = new HttpClient(_blind.Server.CreateHandler());
        var target = BlindCallCode.Create(BlindNodeFactory.PublicAddress, server.NodeId, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", server.Ed25519PublicKey, new byte[32]);
        var client = new BlindPhoneReplicaClient(factory, new NodeIdentityRepository(factory), new SeedSigner(phoneSeed),
            phoneData, phoneDb, NullLogger<BlindPhoneReplicaClient>.Instance);

        await client.FetchAndInstallAsync(http, target, Path.Combine(phoneData, "replica"), progress: null, CancellationToken.None);

        var installed = await new NodeIdentityRepository(factory).GetAsync();
        installed!.NodeId.Should().Be(phoneId, "the v2 phone identity survives the database cutover");
        var position = await new SyncPositionRepository(factory).GetAsync(server.NodeId);
        position.Should().NotBeNull("the next pull must start at the replica checkpoint");
    }

    private sealed class SeedSigner(byte[] seed) : INodeAuthSigner
    {
        public byte[] SignChallenge(NodeIdentity identity, byte[] challengePayload) => Ed25519Signer.Sign(seed, challengePayload);
    }
}
