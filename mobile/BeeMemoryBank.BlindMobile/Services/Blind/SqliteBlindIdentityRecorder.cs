using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// Records the Android blind node's v=2 identity into local SQLite table <c>tbl_node_identity</c>.
/// Per plan sections 3.5 &amp; 10:
/// 1. NodeId has the UUIDv8 blind marker (<see cref="BlindNodeId"/>).
/// 2. ed25519_private_key_v = 2 (<see cref="NodeIdentityCrypto.ExternalKeyVersion"/>), indicating
///    that the private signing seed lives outside the database (in AndroidKeyStore) and never under DEK.
/// 3. Private key columns remain empty/null; can_generate_embeddings = false.
/// </summary>
public sealed class SqliteBlindIdentityRecorder(DbConnectionFactory factory) : IBlindIdentityRecorder
{
    private static readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<BlindIdentityRecord?> GetRecordedAsync(CancellationToken ct = default)
    {
        using var conn = factory.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<NodeIdentityRow>(
            @"SELECT
                node_id                  AS NodeId,
                display_name             AS DisplayName,
                ed25519_public_key       AS Ed25519PublicKey,
                ed25519_private_key      AS Ed25519PrivateKey,
                ed25519_private_key_iv   AS Ed25519PrivateKeyIV,
                ed25519_private_key_v    AS Ed25519PrivateKeyV,
                can_generate_embeddings  AS CanGenerateEmbeddings
              FROM tbl_node_identity LIMIT 1");

        if (row is null) return null;

        // Invariant checks (Finding 2 & Finding 3):
        // Blind nodes must have:
        // 1. A blind UUIDv8 NodeId (BlindNodeId.IsBlind(node_id))
        // 2. ed25519_private_key_v = 2 (NodeIdentityCrypto.ExternalKeyVersion)
        // 3. empty private key and null/empty IV (key material is never stored in DB for blind nodes)
        // 4. can_generate_embeddings = 0 (blind nodes cannot generate embeddings)
        if (!BlindNodeId.IsBlind(row.NodeId))
        {
            throw new InvalidOperationException($"Existing node identity {row.NodeId} in database is not a blind node ID. Refusing to adopt non-blind node as blind node.");
        }

        if (row.Ed25519PrivateKeyV != NodeIdentityCrypto.ExternalKeyVersion)
        {
            throw new InvalidOperationException($"Existing node identity in database has key version {row.Ed25519PrivateKeyV}, expected v={NodeIdentityCrypto.ExternalKeyVersion}. Refusing to adopt.");
        }

        if (row.Ed25519PrivateKey is { Length: > 0 } || row.Ed25519PrivateKeyIV is { Length: > 0 })
        {
            throw new InvalidOperationException("Existing node identity contains private key material in database; blind node identities must have external keys only.");
        }

        if (row.CanGenerateEmbeddings != 0)
        {
            throw new InvalidOperationException("Existing node identity has can_generate_embeddings enabled; blind node identities must have can_generate_embeddings = 0.");
        }

        return new BlindIdentityRecord(
            row.NodeId,
            row.Ed25519PublicKey,
            row.DisplayName,
            row.Ed25519PrivateKeyV,
            row.Ed25519PrivateKey,
            row.Ed25519PrivateKeyIV,
            row.CanGenerateEmbeddings != 0);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        using var conn = factory.CreateConnection();
        await conn.ExecuteAsync("DELETE FROM tbl_node_identity");
    }

    public async Task RecordAsync(Guid nodeId, byte[] publicKey, string displayName, CancellationToken ct = default)
    {
        if (!BlindNodeId.IsBlind(nodeId))
        {
            throw new ArgumentException($"Node ID {nodeId} must have the blind marker (UUIDv8).", nameof(nodeId));
        }

        await _gate.WaitAsync(ct);
        try
        {
            using var conn = factory.CreateConnection();
            var existing = await conn.QuerySingleOrDefaultAsync<Guid?>("SELECT node_id FROM tbl_node_identity LIMIT 1");
            if (existing is not null)
            {
                if (existing == nodeId) return;
                throw new InvalidOperationException($"Node already has identity {existing}, cannot overwrite with {nodeId}.");
            }

            var rows = await conn.ExecuteAsync(
                @"INSERT INTO tbl_node_identity
                  (node_id, display_name, ed25519_public_key, ed25519_private_key,
                   ed25519_private_key_iv, ed25519_private_key_v,
                   can_generate_embeddings, initial_sync_completed, created_at)
                  SELECT @NodeId, @DisplayName, @Ed25519PublicKey, @Ed25519PrivateKey,
                         @Ed25519PrivateKeyIV, @Ed25519PrivateKeyV,
                         @CanGenerateEmbeddings, @InitialSyncCompleted, @CreatedAt
                  WHERE NOT EXISTS (SELECT 1 FROM tbl_node_identity)",
                new
                {
                    NodeId = nodeId,
                    DisplayName = displayName,
                    Ed25519PublicKey = publicKey,
                    Ed25519PrivateKey = Array.Empty<byte>(),
                    Ed25519PrivateKeyIV = (byte[]?)null,
                    Ed25519PrivateKeyV = NodeIdentityCrypto.ExternalKeyVersion, // 2
                    CanGenerateEmbeddings = 0,
                    InitialSyncCompleted = 0,
                    CreatedAt = DateTime.UtcNow
                });

            if (rows == 0)
            {
                var current = await conn.QuerySingleOrDefaultAsync<Guid?>("SELECT node_id FROM tbl_node_identity LIMIT 1");
                if (current != nodeId)
                {
                    throw new InvalidOperationException($"Node already has identity {current}, cannot overwrite with {nodeId}.");
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class NodeIdentityRow
    {
        public Guid NodeId { get; set; }
        public string DisplayName { get; set; } = "";
        public byte[] Ed25519PublicKey { get; set; } = [];
        public byte[]? Ed25519PrivateKey { get; set; }
        public byte[]? Ed25519PrivateKeyIV { get; set; }
        public int Ed25519PrivateKeyV { get; set; }
        public int CanGenerateEmbeddings { get; set; }
    }
}
