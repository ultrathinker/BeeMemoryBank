using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Blind.Format2;
using Dapper;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// A node's database on disk with its identity (a plaintext test key, v=0) and the repositories the format-2 writer and the
/// blind store use: enough to build, read and adopt packages without a host. Disposing removes the folder.
/// </summary>
public sealed class Format2TestNode : IDisposable
{
    public string DataPath { get; }
    public DbConnectionFactory Factory { get; }
    public NodeIdentity Identity { get; }
    public byte[] PrivateKey { get; }
    public EventLogRepository Events { get; }
    public WhitelistRepository Whitelist { get; }
    public SyncPositionRepository Positions { get; }

    private Format2TestNode(string dataPath, DbConnectionFactory factory, NodeIdentity identity, byte[] privateKey)
    {
        DataPath = dataPath;
        Factory = factory;
        Identity = identity;
        PrivateKey = privateKey;
        Events = new EventLogRepository(factory);
        Whitelist = new WhitelistRepository(factory);
        Positions = new SyncPositionRepository(factory);
    }

    public string DbPath => Path.Combine(DataPath, "beememorybank.db");

    /// <param name="sealedVault">The node's database is a SEALED (encrypted) file under a random page key, as a full node after "Encrypt this node".</param>
    public static async Task<Format2TestNode> CreateAsync(Guid? nodeId = null, string name = "node", bool sealedVault = false)
    {
        DapperConfig.Configure();
        var dir = Path.Combine(Path.GetTempPath(), "bmb_f2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var factory = sealedVault
            ? DbConnectionFactory.CreateSealedForTests("f2sealed")
            : new DbConnectionFactory(Path.Combine(dir, "beememorybank.db"));
        await new MigrationRunner(factory).RunMigrationsAsync();
        var (pub, priv) = Ed25519Signer.GenerateKeyPair();
        var identity = new NodeIdentity
        {
            NodeId = nodeId ?? Guid.NewGuid(), DisplayName = name, Ed25519PublicKey = pub, Ed25519PrivateKey = priv,
            Ed25519PrivateKeyV = 0, CreatedAt = DateTime.UtcNow, InitialSyncCompleted = true,
        };
        await new NodeIdentityRepository(factory).CreateAsync(identity);
        return new Format2TestNode(dir, factory, identity, priv);
    }

    public BlindPackageV2Writer Writer(bool servingIsSuperadmin = false) =>
        new(Factory, new NodeIdentityRepository(Factory), Whitelist, Positions, Events,
            new BlindPackageV2WriterOptions(
                (_, payload) => Ed25519Signer.Sign(PrivateKey, payload),
                Path.Combine(DataPath, "packages"), Path.Combine(DataPath, "staging"),
                _ => Task.FromResult(servingIsSuperadmin)));

    /// <summary>Trusts <paramref name="node"/> in this node's whitelist.</summary>
    public async Task TrustAsync(Guid node, byte[] publicKey, bool superadmin, string name = "peer")
    {
        await Whitelist.CreateAsync(new WhitelistEntry
        {
            NodeId = node, DisplayName = name, Ed25519PublicKey = publicKey, Status = "A", IsSuperadmin = superadmin,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, LamportTs = 1, SourceNodeId = Identity.NodeId,
        });
    }

    public async Task<long> CountAsync(string table)
    {
        using var conn = Factory.CreateConnection();
        return await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM \"{table}\"");
    }

    public void Dispose()
    {
        Factory.Dispose();
        try { Directory.Delete(DataPath, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
