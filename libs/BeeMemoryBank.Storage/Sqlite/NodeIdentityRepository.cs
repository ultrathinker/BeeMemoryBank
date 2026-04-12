using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class NodeIdentityRepository(DbConnectionFactory factory) : BaseRepository(factory), INodeIdentityRepository
{
    public async Task<NodeIdentity?> GetAsync()
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<NodeIdentity>(
            @"SELECT
                node_id               AS NodeId,
                display_name          AS DisplayName,
                ed25519_public_key    AS Ed25519PublicKey,
                ed25519_private_key   AS Ed25519PrivateKey,
                can_generate_embeddings AS CanGenerateEmbeddings,
                created_at            AS CreatedAt
              FROM tbl_node_identity LIMIT 1");
    }

    public async Task CreateAsync(NodeIdentity identity)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_node_identity
              (node_id, display_name, ed25519_public_key, ed25519_private_key, can_generate_embeddings, created_at)
              VALUES (@NodeId, @DisplayName, @Ed25519PublicKey, @Ed25519PrivateKey, @CanGenerateEmbeddings, @CreatedAt)",
            new
            {
                identity.NodeId,
                identity.DisplayName,
                identity.Ed25519PublicKey,
                identity.Ed25519PrivateKey,
                identity.CanGenerateEmbeddings,
                identity.CreatedAt
            });
    }

    public async Task StoreSentinelAsync(byte[] sentinelValue)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_node_identity SET sentinel_value = @sentinelValue",
            new { sentinelValue });
    }

    public async Task<byte[]?> GetSentinelAsync()
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<byte[]?>(
            "SELECT sentinel_value FROM tbl_node_identity LIMIT 1");
    }
}
