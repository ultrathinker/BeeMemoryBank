using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using Dapper;

namespace BeeMemoryBank.Api.Startup;

/// <summary>
/// Startup work of a blind node (BMB_ROLE=blind, plan 3.4–3.5), run right after migrations — which
/// need no DEK, so a blind node upgrades its schema like any other node.
/// </summary>
public static class BlindRoleStartup
{
    /// <summary>
    /// Where the blind node's own in-flight uploads live (seed parts, packages being assembled).
    /// Nothing in here outlives a process: whatever a previous run left is an interrupted transfer.
    /// </summary>
    public static string TempDir(string dataPath) => Path.Combine(dataPath, "blind-tmp");

    public static async Task RunAsync(IServiceProvider services, string dataPath, ILogger logger)
    {
        CleanTempDir(dataPath, logger);

        RefuseDekMaterial(dataPath, services.GetRequiredService<IDbConnectionFactory>());

        using var scope = services.CreateScope();
        var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
        var key = services.GetRequiredService<FileNodeKey>();
        var config = services.GetRequiredService<IConfiguration>();
        await EnsureIdentityAsync(nodeRepo, key, config["BMB_NODE_NAME"], logger);

        // A restore_network whose reseed flag a crash may have lost (plan 5.3), and a rotation a
        // crash left in Committing.
        await services.GetRequiredService<BeeMemoryBank.Sync.IRestoreInitiator>().RetryPendingRestoresAsync();
        await scope.ServiceProvider.GetRequiredService<IDekRotationApplier>().RetryPendingAutoAcceptsAsync();
    }

    /// <summary>
    /// A blind node has no setup wizard: it gets its identity on first start — a NodeId with the
    /// blind mark and a key in a 0600 file next to the database (row v=2, no private columns).
    /// The key is written first; a start that died before the row was written finishes the job
    /// from the key on the next start (nobody can know that node yet, so a fresh id is right).
    /// Every other inconsistency is fatal rather than repaired: a new key would silently stop
    /// matching the whitelist rows the mesh holds for this node.
    /// </summary>
    internal static async Task EnsureIdentityAsync(
        INodeIdentityRepository nodeRepo, FileNodeKey key, string? displayName, ILogger logger)
    {
        var identity = await nodeRepo.GetAsync();
        if (identity == null)
        {
            var publicKey = key.Exists ? key.ReadPublicKey() : key.Create();
            var nodeId = BlindNodeId.NewId();
            await nodeRepo.CreateAsync(new NodeIdentity
            {
                NodeId = nodeId,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Blind node" : displayName.Trim(),
                Ed25519PublicKey = publicKey,
                Ed25519PrivateKey = [],
                Ed25519PrivateKeyIV = null,
                Ed25519PrivateKeyV = NodeIdentityCrypto.ExternalKeyVersion,
                CanGenerateEmbeddings = false,
                InitialSyncCompleted = false,
                CreatedAt = DateTime.UtcNow
            });
            logger.LogInformation("Blind node identity created: {NodeId}", nodeId);
            return;
        }

        if (identity.Ed25519PrivateKeyV != NodeIdentityCrypto.ExternalKeyVersion || !BlindNodeId.IsBlind(identity.NodeId))
            throw new InvalidOperationException(
                $"BMB_ROLE=blind, but this database belongs to a full node ({identity.NodeId}). A blind node " +
                "must never run on a full node's data: start it with an empty data volume.");
        // v=2 means the database holds no private key at all. A seed left in these columns would
        // travel in every copy and backup of the database, which is what keeping it outside is for.
        if (identity.Ed25519PrivateKey.Length != 0 || identity.Ed25519PrivateKeyIV != null)
            throw new InvalidOperationException(
                "Blind node: the identity row keeps private key material in the database. Refusing to run on it.");
        if (!key.Exists)
            throw new InvalidOperationException($"Blind node: identity key {key.Path} is missing.");
        if (!key.Matches(identity.Ed25519PublicKey))
            throw new InvalidOperationException(
                $"Blind node: identity key {key.Path} does not belong to node {identity.NodeId}.");
    }

    /// <summary>
    /// Fail closed on anything that could put the master DEK into this process (plan 3.4): a key
    /// slot (a password opens it), an agent with a wrapped DEK, the OS auto-unlock secret, the
    /// update unlock handoff. None belongs on a blind volume; finding one means a full node's data
    /// was copied here, and the blind role refuses to start rather than run next to it.
    /// </summary>
    private static void RefuseDekMaterial(string dataPath, IDbConnectionFactory db)
    {
        var found = new List<string>();
        using (var conn = db.CreateConnection())
        {
            if (conn.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_key_slot") > 0) found.Add("key slots");
            if (conn.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_agent WHERE encrypted_dek IS NOT NULL") > 0)
                found.Add("agents with a wrapped master key");
        }
        foreach (var file in new[] { "os-auto-unlock.dat", "update-unlock.dat" })
            if (File.Exists(Path.Combine(dataPath, file))) found.Add(file);

        if (found.Count > 0)
            throw new InvalidOperationException(
                $"BMB_ROLE=blind, but this data volume holds master-key material ({string.Join(", ", found)}). " +
                "A blind node must never be able to unlock: start it with an empty data volume.");
    }

    private static void CleanTempDir(string dataPath, ILogger logger)
    {
        var dir = TempDir(dataPath);
        if (!Directory.Exists(dir)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
                logger.LogInformation("Blind node: removed leftover temporary {Path}", entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Blind node: could not remove leftover temporary {Path}", entry);
            }
        }
    }
}
