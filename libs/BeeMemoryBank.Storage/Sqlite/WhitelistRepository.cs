using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class WhitelistRepository(DbConnectionFactory factory) : BaseRepository(factory), IWhitelistRepository
{
    // Every read returns the whole row, auto_accept_restore included: the UI checkbox reads it from here, while EventApplier reads it via the dedicated
    // GetAutoAcceptRestoreAsync -- leaving it out made the checkbox render OFF even though the flag is honored.
    private const string Columns = @"
                node_id                    AS NodeId,
                display_name               AS DisplayName,
                ed25519_public_key         AS Ed25519PublicKey,
                api_address                AS ApiAddress,
                can_generate_embeddings    AS CanGenerateEmbeddings,
                status                     AS Status,
                created_at                 AS CreatedAt,
                updated_at                 AS UpdatedAt,
                deleted_at                 AS DeletedAt,
                auto_accept_restore        AS AutoAcceptRestore,
                auto_accept_dek_rotation   AS AutoAcceptDekRotation,
                is_superadmin              AS IsSuperadmin,
                lamport_ts                 AS LamportTs,
                source_node_id             AS SourceNodeId,
                last_protocol_version      AS LastProtocolVersion,
                last_protocol_seen_at      AS LastProtocolSeenAt,
                last_capabilities          AS LastCapabilities,
                last_app_version           AS LastAppVersion,
                last_capabilities_seen_at  AS LastCapabilitiesSeenAt,
                tls_spki                   AS TlsSpki,
                tls_trust                  AS TlsTrust,
                member_lamport_ts          AS MemberLamportTs,
                member_source_node_id      AS MemberSourceNodeId,
                superadmin_lamport_ts      AS SuperadminLamportTs,
                superadmin_source_node_id  AS SuperadminSourceNodeId,
                name_lamport_ts            AS NameLamportTs,
                name_source_node_id        AS NameSourceNodeId,
                address_lamport_ts         AS AddressLamportTs,
                address_source_node_id     AS AddressSourceNodeId,
                tls_lamport_ts             AS TlsLamportTs,
                tls_source_node_id         AS TlsSourceNodeId,
                undated_fields             AS UndatedFields";

    public async Task<WhitelistEntry?> GetByNodeIdAsync(Guid nodeId, bool includeDeleted = false)
    {
        using var conn = OpenConnection();
        // With includeDeleted the row comes back whatever its status: revoked (a tombstone too), unconfirmed.
        var sql = includeDeleted
            ? $"SELECT {Columns} FROM tbl_whitelist WHERE node_id = @nodeId"
            : $"SELECT {Columns} FROM tbl_whitelist WHERE node_id = @nodeId COLLATE NOCASE AND status = 'A'";
        return Normalized(await conn.QuerySingleOrDefaultAsync<WhitelistEntry>(sql, new { nodeId }));
    }

    /// <summary>
    /// A blind node never carries authority, whatever its row says (plan 3.2, BMB-42). Migration 033
    /// cleans rows at rest; this covers a row written since by any path that bypasses the guarded writes
    /// (a snapshot, an older build, a hand edit), so no decision built on these reads — the superadmin
    /// gate, auto-accept of a rotation or restore — ever sees a blind superadmin.
    /// </summary>
    private static WhitelistEntry? Normalized(WhitelistEntry? entry)
    {
        if (entry != null && BlindNodeId.IsBlind(entry.NodeId))
        {
            entry.IsSuperadmin = false;
            entry.AutoAcceptDekRotation = false;
            entry.AutoAcceptRestore = false;
        }
        return entry;
    }

    public async Task<List<WhitelistEntry>> GetAllActiveAsync()
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<WhitelistEntry>(
            $"SELECT {Columns} FROM tbl_whitelist WHERE status = 'A' ORDER BY (substr(display_name,1,1)='_') DESC, display_name"))
            .Select(e => Normalized(e)!).ToList();
    }

    public async Task<List<WhitelistEntry>> GetAllRevokedAsync()
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<WhitelistEntry>(
            $"SELECT {Columns} FROM tbl_whitelist WHERE status = 'R' ORDER BY node_id"))
            .Select(e => Normalized(e)!).ToList();
    }

    public async Task CreateAsync(WhitelistEntry entry)
    {
        using var conn = OpenConnection();
        await InsertAsync(conn, null, entry);
    }

    /// <summary><see cref="CreateAsync"/> on the caller's connection, inside its transaction (a restore writes its peers in one).</summary>
    public static async Task InsertAsync(System.Data.IDbConnection conn, System.Data.IDbTransaction? tx, WhitelistEntry entry)
    {
        // A caller that set only the row's version (it knows nothing of concerns) writes the row whole at that version (see the method).
        entry.NormalizeVersionsForWrite();
        await conn.ExecuteAsync(
            $@"INSERT INTO tbl_whitelist
              (node_id, display_name, ed25519_public_key, api_address, can_generate_embeddings, status, created_at, updated_at, is_superadmin, lamport_ts, source_node_id, tls_spki, tls_trust,
               {ConcernColumns})
              VALUES (@NodeId, @DisplayName, @Ed25519PublicKey, @ApiAddress, @CanGenerateEmbeddings, @Status, @CreatedAt, @UpdatedAt, @IsSuperadmin, @LamportTs, @SourceNodeId, @TlsSpki, @TlsTrust,
               {ConcernValues})",
            Parameters(entry), tx);
    }

    // The version of each concern (migration 045, WhitelistConcerns), written as the entity carries them.
    private const string ConcernColumns =
        "member_lamport_ts, member_source_node_id, superadmin_lamport_ts, superadmin_source_node_id, name_lamport_ts, name_source_node_id, " +
        "address_lamport_ts, address_source_node_id, tls_lamport_ts, tls_source_node_id, undated_fields";

    private const string ConcernValues =
        "@MemberLamportTs, @MemberSourceNodeId, @SuperadminLamportTs, @SuperadminSourceNodeId, @NameLamportTs, @NameSourceNodeId, " +
        "@AddressLamportTs, @AddressSourceNodeId, @TlsLamportTs, @TlsSourceNodeId, @UndatedFields";

    private const string ConcernAssignments = @"
                  member_lamport_ts = @MemberLamportTs, member_source_node_id = @MemberSourceNodeId,
                  superadmin_lamport_ts = @SuperadminLamportTs, superadmin_source_node_id = @SuperadminSourceNodeId,
                  name_lamport_ts = @NameLamportTs, name_source_node_id = @NameSourceNodeId,
                  address_lamport_ts = @AddressLamportTs, address_source_node_id = @AddressSourceNodeId,
                  tls_lamport_ts = @TlsLamportTs, tls_source_node_id = @TlsSourceNodeId,
                  undated_fields = @UndatedFields";

    private static object Parameters(WhitelistEntry entry) => new
    {
        entry.NodeId,
        entry.DisplayName,
        entry.Ed25519PublicKey,
        entry.ApiAddress,
        entry.CanGenerateEmbeddings,
        entry.Status,
        entry.CreatedAt,
        entry.UpdatedAt,
        entry.DeletedAt,
        IsSuperadmin = entry.IsSuperadmin ? 1 : 0,
        entry.LamportTs,
        entry.SourceNodeId,
        entry.TlsSpki,
        entry.TlsTrust,
        entry.MemberLamportTs,
        entry.MemberSourceNodeId,
        entry.SuperadminLamportTs,
        entry.SuperadminSourceNodeId,
        entry.NameLamportTs,
        entry.NameSourceNodeId,
        entry.AddressLamportTs,
        entry.AddressSourceNodeId,
        entry.TlsLamportTs,
        entry.TlsSourceNodeId,
        UndatedFields = (int)entry.UndatedFields
    };

    public async Task UpdateAsync(WhitelistEntry entry)
    {
        entry.NormalizeVersionsForWrite();
        using var conn = OpenConnection();
        // deleted_at: none on an active row; a revoked row keeps the one it has, or takes the entity's (the applier revoking a row).
        await conn.ExecuteAsync(
            $@"UPDATE tbl_whitelist
              SET display_name = @DisplayName,
                  api_address = @ApiAddress,
                  ed25519_public_key = @Ed25519PublicKey,
                  status = @Status,
                  can_generate_embeddings = @CanGenerateEmbeddings,
                  is_superadmin = @IsSuperadmin,
                  updated_at = @UpdatedAt,
                  lamport_ts = @LamportTs,
                  source_node_id = @SourceNodeId,
                  tls_spki = @TlsSpki,
                  tls_trust = @TlsTrust,
                  deleted_at = CASE WHEN @Status = 'A' THEN NULL ELSE COALESCE(deleted_at, @DeletedAt) END,{ConcernAssignments}
              WHERE node_id = @NodeId",
            Parameters(entry));
    }

    public async Task RevokeAsync(Guid nodeId, RowVersion version)
    {
        using var conn = OpenConnection();
        // The version lands on the row in the same statement that revokes, because the row IS what
        // a later whitelist_add is judged against — see this method's interface doc.
        // Membership is the concern a revoke writes (migration 045); the row's own version follows it, a local revoke being the newest write.
        await conn.ExecuteAsync(
            @"UPDATE tbl_whitelist
              SET status = 'R', deleted_at = @now, updated_at = @now,
                  lamport_ts = @lamportTs, source_node_id = @sourceNodeId,
                  member_lamport_ts = @lamportTs, member_source_node_id = @sourceNodeId
              WHERE node_id = @nodeId",
            new { nodeId, now = DateTime.UtcNow, lamportTs = version.LamportTs, sourceNodeId = version.SourceNodeId });
    }

    public async Task<bool> CreateTombstoneAsync(Guid nodeId, RowVersion version, byte[]? publicKey = null)
    {
        using var conn = OpenConnection();
        var now = DateTime.UtcNow;
        // No address, no name, no authority. OR IGNORE, and a NOT EXISTS for the other spelling of the id: a row that is there already (written in
        // between, or stored in another case) is never replaced by a tombstone; the caller compares versions against that row instead.
        var written = await conn.ExecuteAsync(
            @"INSERT OR IGNORE INTO tbl_whitelist
              (node_id, display_name, ed25519_public_key, can_generate_embeddings, status, created_at, updated_at, deleted_at, is_superadmin,
               lamport_ts, source_node_id, member_lamport_ts, member_source_node_id)
              SELECT @nodeId, '', @key, 0, 'R', @now, @now, @now, 0, @lamportTs, @sourceNodeId, @lamportTs, @sourceNodeId
              WHERE NOT EXISTS (SELECT 1 FROM tbl_whitelist WHERE node_id = @nodeId COLLATE NOCASE)",
            new { nodeId, key = publicKey ?? [], now, lamportTs = version.LamportTs, sourceNodeId = version.SourceNodeId });
        return written > 0;
    }

    public async Task SetVersionAsync(Guid nodeId, RowVersion version)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_whitelist SET lamport_ts = @lamportTs, source_node_id = @sourceNodeId,
                  member_lamport_ts = @lamportTs, member_source_node_id = @sourceNodeId
              WHERE node_id = @nodeId COLLATE NOCASE",
            new { nodeId, lamportTs = version.LamportTs, sourceNodeId = version.SourceNodeId });
    }

    public async Task RecordProtocolVersionAsync(Guid nodeId, int protocolVersion, DateTime seenAt)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_whitelist SET last_protocol_version = @protocolVersion, last_protocol_seen_at = @seenAt
              WHERE node_id = @nodeId COLLATE NOCASE",
            new { nodeId, protocolVersion, seenAt });
    }

    public async Task RecordCapabilitiesAsync(Guid nodeId, IReadOnlyCollection<string> capabilities, string? appVersion, DateTime seenAt)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_whitelist SET last_capabilities = @caps, last_app_version = @appVersion, last_capabilities_seen_at = @seenAt
              WHERE node_id = @nodeId COLLATE NOCASE",
            new { nodeId, caps = string.Join(",", capabilities), appVersion, seenAt });
    }

    public async Task ResetLocalBookkeepingAsync(Guid nodeId, DateTime now)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_whitelist
              SET created_at = @now, deleted_at = NULL, last_protocol_version = NULL, last_protocol_seen_at = NULL,
                  last_capabilities = NULL, last_app_version = NULL, last_capabilities_seen_at = NULL
              WHERE node_id = @nodeId COLLATE NOCASE",
            new { nodeId, now });
    }

    public async Task<bool> FillMissingTlsTrustAsync(Guid nodeId, string trust, string tlsSpki, Guid trustedResponder)
    {
        // A blind node is never the source of a key, whatever its row says (see Normalized): a function of the id, so nothing can change it between here and the write.
        if (BlindNodeId.IsBlind(trustedResponder)) return false;
        using var conn = OpenConnection();
        // One statement, so the tests and the write are not apart: an event that wrote a mode or a key a moment ago is never overwritten, a row whose TLS trust
        // an event ever wrote (undated bit cleared: an explicit "off" leaves empty columns too) is left alone, and the responder is still an active superadmin.
        // Only the two columns change - no version column, no updated_at, the concern stays undated - so nothing is replicated and a later event wins as it always did.
        var written = await conn.ExecuteAsync(
            @"UPDATE tbl_whitelist
              SET tls_trust = @trust, tls_spki = @tlsSpki
              WHERE node_id = @nodeId COLLATE NOCASE AND status = 'A'
                AND COALESCE(tls_trust, '') = '' AND COALESCE(tls_spki, '') = ''
                AND (undated_fields & @tlsConcern) = @tlsConcern
                AND EXISTS (SELECT 1 FROM tbl_whitelist responder
                            WHERE responder.node_id = @trustedResponder COLLATE NOCASE AND responder.status = 'A' AND responder.is_superadmin = 1)",
            new { nodeId, trust, tlsSpki, trustedResponder, tlsConcern = (int)WhitelistConcerns.Tls });
        return written > 0;
    }

    public async Task<bool> GetAutoAcceptRestoreAsync(string nodeId)
    {
        if (BlindNodeId.IsBlind(nodeId)) return false; // see Normalized
        using var conn = OpenConnection();
        // status = 'A' filter: a revoked peer ('R') with auto_accept_restore=1 from before
        // revocation must NOT trigger auto-apply on incoming events. Their Ed25519 key still
        // passes signature verification in EventApplier, so without this filter a revoked
        // peer could force a destructive restore.
        var val = await conn.ExecuteScalarAsync<long?>(
            "SELECT auto_accept_restore FROM tbl_whitelist WHERE node_id = @nodeId COLLATE NOCASE AND status = 'A'",
            new { nodeId });
        return val.GetValueOrDefault() != 0;
    }

    public async Task SetAutoAcceptRestoreAsync(string nodeId, bool autoAccept)
    {
        using var conn = OpenConnection();
        // node_id stored UPPERCASE; callers pass lowercase Guid.ToString(). COLLATE NOCASE so
        // UPDATE actually matches. Same in SetAutoAcceptDekRotationAsync below. Neither flag is ever
        // stored for a blind node, whoever asks (see Normalized): it must not exist at rest either.
        await conn.ExecuteAsync(
            @"UPDATE tbl_whitelist
              SET auto_accept_restore = @autoAccept, updated_at = @now
              WHERE node_id = @nodeId COLLATE NOCASE",
            new { nodeId, autoAccept = autoAccept && !BlindNodeId.IsBlind(nodeId) ? 1 : 0, now = DateTime.UtcNow });
    }

    public async Task<bool> GetAutoAcceptDekRotationAsync(string nodeId)
    {
        if (BlindNodeId.IsBlind(nodeId)) return false; // see Normalized
        using var conn = OpenConnection();
        // status = 'A' filter — same rationale as GetAutoAcceptRestoreAsync above.
        var val = await conn.ExecuteScalarAsync<long?>(
            "SELECT auto_accept_dek_rotation FROM tbl_whitelist WHERE node_id = @nodeId COLLATE NOCASE AND status = 'A'",
            new { nodeId });
        return val.GetValueOrDefault() != 0;
    }

    public async Task SetAutoAcceptDekRotationAsync(string nodeId, bool autoAccept)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_whitelist
              SET auto_accept_dek_rotation = @autoAccept, updated_at = @now
              WHERE node_id = @nodeId COLLATE NOCASE",
            new { nodeId, autoAccept = autoAccept && !BlindNodeId.IsBlind(nodeId) ? 1 : 0, now = DateTime.UtcNow });
    }
}
