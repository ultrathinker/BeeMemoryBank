using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.TestSupport;

/// <summary>
/// A whitelist row as a device that joined long ago really holds it (BMB-337), built with the statements those releases ran and the real migration 045,
/// not with a row set by hand to the state a test would like. The column lists are copied from the releases (git: <c>5a817ef4</c> is 2.3.0, <c>v2.7.5</c>);
/// the columns migration 045 added take the defaults an existing row gets from its ALTERs (version 0, no source node, nothing undated), and
/// <see cref="RunMigration045Async"/> then runs the real migration over them, as the start of 2.8.0 does. The database must have run the migrations already
/// (the columns are there), and Dapper must be configured (<c>DapperConfig.Configure()</c>, as every fixture does).
/// </summary>
public static class LegacyWhitelistRows
{
    /// <summary>
    /// The row the join of 2.3.0 wrote for a peer it learned from its host, and for the host itself (<c>NodeSetupService</c> and <c>WhitelistRepository.CreateAsync</c> of that
    /// release): the key and the address the host gave, the authority it reported, NO mode and NO pin (the join did not carry them), and no version at all.
    /// </summary>
    public static async Task InsertAsOf230JoinAsync(DbConnectionFactory db, Guid nodeId, string displayName, byte[] publicKey, string? apiAddress, bool isSuperadmin)
    {
        using var conn = db.CreateConnection();
        var now = DateTime.UtcNow;
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_whitelist
              (node_id, display_name, ed25519_public_key, api_address, can_generate_embeddings, status, created_at, updated_at, is_superadmin, lamport_ts, source_node_id, tls_spki, tls_trust)
              VALUES (@NodeId, @DisplayName, @Ed25519PublicKey, @ApiAddress, @CanGenerateEmbeddings, @Status, @CreatedAt, @UpdatedAt, @IsSuperadmin, @LamportTs, @SourceNodeId, @TlsSpki, @TlsTrust)",
            new
            {
                NodeId = nodeId, DisplayName = displayName, Ed25519PublicKey = publicKey, ApiAddress = apiAddress, CanGenerateEmbeddings = false, Status = "A",
                CreatedAt = now, UpdatedAt = now, IsSuperadmin = isSuperadmin ? 1 : 0, LamportTs = 0L, SourceNodeId = (Guid?)null, TlsSpki = (string?)null, TlsTrust = (string?)null
            });
    }

    /// <summary>
    /// What the applier of 2.7.5 left on a row for a <c>whitelist_update</c> it took (<c>ApplyWhitelistUpdateAsync</c> and <c>WhitelistRepository.UpdateAsync</c> of that release):
    /// the fields the event names, and the row's ONE version (the event's), nothing per concern. <paramref name="tlsOff"/> is <c>tls_trust: none</c>: both columns empty.
    /// </summary>
    public static async Task ApplyUpdateAsOf275Async(DbConnectionFactory db, Guid nodeId, string? apiAddress, bool tlsOff, long lamportTs, Guid sourceNodeId)
    {
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_whitelist
              SET api_address = COALESCE(@apiAddress, api_address),
                  tls_spki = CASE WHEN @tlsOff = 1 THEN NULL ELSE tls_spki END,
                  tls_trust = CASE WHEN @tlsOff = 1 THEN NULL ELSE tls_trust END,
                  updated_at = @now, lamport_ts = @lamportTs, source_node_id = @sourceNodeId
              WHERE node_id = @nodeId COLLATE NOCASE",
            new { nodeId, apiAddress, tlsOff = tlsOff ? 1 : 0, now = DateTime.UtcNow, lamportTs, sourceNodeId });
    }

    /// <summary>
    /// The start of 2.8.0 on such a database: the ledger says migration 045 has not run (the columns are there, as right after its ALTERs, which the runner skips as
    /// already applied), and the real runner applies the real file: every concern of a row nobody versioned starts at the row's version, the fields undated.
    /// </summary>
    public static async Task RunMigration045Async(DbConnectionFactory db)
    {
        using (var conn = db.CreateConnection())
        {
            // The downgrade guard stops every DELETE from the ledger; a test that rolls the ledger back drops it first (the runner puts it back).
            await conn.ExecuteAsync("DROP TRIGGER IF EXISTS trg_migration_ledger_guard");
            await conn.ExecuteAsync("DELETE FROM tbl_migration WHERE version = 45");
        }
        await new MigrationRunner(db).RunMigrationsAsync();
    }
}
