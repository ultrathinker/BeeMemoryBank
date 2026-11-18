using System.Data;
using System.Globalization;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public sealed class SharedDoorsStateRepository(DbConnectionFactory factory) : BaseRepository(factory), ISharedDoorsStateRepository
{
    private sealed class Row
    {
        public string SelfOwnership { get; set; } = OwnershipKinds.Unknown;
        public long SelfOwnershipLamportTs { get; set; }
        public string? SelfOwnershipSourceNodeId { get; set; }
        public long LastIssuedVersion { get; set; }
        public string? LastPublishedDigest { get; set; }
        public long LatestReceivedVersion { get; set; }
        public string? LatestEventId { get; set; }
        public string? LatestAuthorNodeId { get; set; }
        public string? LatestPayloadJson { get; set; }
        public long PendingApply { get; set; }
        public long AppliedVersion { get; set; }
        public string? AppliedAt { get; set; }
        public string? PrimaryPersonId { get; set; }
        public string? LastError { get; set; }
        public long AccountsHomeChanged { get; set; }
    }

    private static readonly SharedDoorsStateRecord Empty = new(
        OwnershipKinds.Unknown, 0, null, 0, null, 0, null, null, null, false, 0, null, null, null, false);

    public async Task<SharedDoorsStateRecord> GetAsync(IDbTransaction? transaction = null)
    {
        const string sql =
            @"SELECT self_ownership AS SelfOwnership,
                     self_ownership_lamport_ts AS SelfOwnershipLamportTs,
                     self_ownership_source_node_id AS SelfOwnershipSourceNodeId,
                     last_issued_version AS LastIssuedVersion,
                     last_published_digest AS LastPublishedDigest,
                     latest_received_version AS LatestReceivedVersion,
                     latest_event_id AS LatestEventId,
                     latest_author_node_id AS LatestAuthorNodeId,
                     latest_payload_json AS LatestPayloadJson,
                     pending_apply AS PendingApply,
                     applied_version AS AppliedVersion,
                     applied_at AS AppliedAt,
                     primary_person_id AS PrimaryPersonId,
                     last_error AS LastError,
                     accounts_home_changed AS AccountsHomeChanged
              FROM tbl_shared_doors_state WHERE id = 1";
        Row? row;
        if (transaction is not null)
        {
            row = await transaction.Connection!.QueryFirstOrDefaultAsync<Row>(sql, transaction: transaction);
        }
        else
        {
            using var conn = OpenConnection();
            row = await conn.QueryFirstOrDefaultAsync<Row>(sql);
        }
        if (row is null) return Empty;
        return new SharedDoorsStateRecord(
            OwnershipKinds.NormalizeOrNull(row.SelfOwnership) ?? OwnershipKinds.Unknown,
            row.SelfOwnershipLamportTs,
            Guid.TryParseExact(row.SelfOwnershipSourceNodeId, "D", out var sSrc) ? sSrc : null,
            row.LastIssuedVersion,
            row.LastPublishedDigest,
            row.LatestReceivedVersion,
            Guid.TryParseExact(row.LatestEventId, "D", out var eid) ? eid : null,
            Guid.TryParseExact(row.LatestAuthorNodeId, "D", out var aid) ? aid : null,
            row.LatestPayloadJson,
            row.PendingApply != 0,
            row.AppliedVersion,
            DateTime.TryParse(row.AppliedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ? dt.ToUniversalTime() : null,
            Guid.TryParseExact(row.PrimaryPersonId, "D", out var pid) ? pid : null,
            row.LastError,
            row.AccountsHomeChanged != 0);
    }

    public Task SetSelfOwnershipAsync(string ownership, long lamportTs, Guid? sourceNodeId, IDbTransaction? transaction = null) =>
        ExecuteUpsertAsync(
            @"INSERT INTO tbl_shared_doors_state (id, self_ownership, self_ownership_lamport_ts, self_ownership_source_node_id)
              VALUES (1, @Ownership, @LamportTs, @SourceNodeId)
              ON CONFLICT(id) DO UPDATE SET
                  self_ownership = excluded.self_ownership,
                  self_ownership_lamport_ts = excluded.self_ownership_lamport_ts,
                  self_ownership_source_node_id = excluded.self_ownership_source_node_id",
            new
            {
                Ownership = OwnershipKinds.NormalizeOrNull(ownership) ?? OwnershipKinds.Unknown,
                LamportTs = lamportTs,
                SourceNodeId = sourceNodeId?.ToString("D"),
            },
            transaction);

    public async Task<long> AllocateNextVersionAsync(IDbTransaction? transaction = null, long atLeast = 0)
    {
        const string sql =
            @"INSERT INTO tbl_shared_doors_state (id, last_issued_version)
              VALUES (1, MAX(@atLeast, 0) + 1)
              ON CONFLICT(id) DO UPDATE SET
                  last_issued_version = MAX(tbl_shared_doors_state.last_issued_version, @atLeast) + 1
              RETURNING last_issued_version";
        var args = new { atLeast = Math.Max(0, atLeast) };
        if (transaction is not null)
            return await transaction.Connection!.ExecuteScalarAsync<long>(sql, args, transaction);
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<long>(sql, args);
    }

    public Task RecordPublishedDigestAsync(string digest, IDbTransaction? transaction = null) =>
        ExecuteUpsertAsync(
            @"INSERT INTO tbl_shared_doors_state (id, last_published_digest)
              VALUES (1, @Digest)
              ON CONFLICT(id) DO UPDATE SET
                  last_published_digest = excluded.last_published_digest",
            new { Digest = digest },
            transaction);

    public Task InvalidatePublishedDigestAsync(IDbTransaction? transaction = null) =>
        ExecuteUpsertAsync(
            @"INSERT INTO tbl_shared_doors_state (id, last_published_digest)
              VALUES (1, NULL)
              ON CONFLICT(id) DO UPDATE SET
                  last_published_digest = NULL",
            new { },
            transaction);

    public Task RecordIssuedAsync(long version, string digest, IDbTransaction? transaction = null) =>
        ExecuteUpsertAsync(
            @"INSERT INTO tbl_shared_doors_state (id, last_issued_version, last_published_digest)
              VALUES (1, @Version, @Digest)
              ON CONFLICT(id) DO UPDATE SET
                  last_issued_version = excluded.last_issued_version,
                  last_published_digest = excluded.last_published_digest",
            new { Version = version, Digest = digest },
            transaction);

    public Task RecordReceivedAsync(long version, Guid eventId, Guid authorNodeId, string payloadJson, IDbTransaction? transaction = null) =>
        ExecuteUpsertAsync(
            @"INSERT INTO tbl_shared_doors_state (id, latest_received_version, latest_event_id, latest_author_node_id, latest_payload_json, pending_apply)
              VALUES (1, @Version, @EventId, @AuthorNodeId, @PayloadJson, 1)
              ON CONFLICT(id) DO UPDATE SET
                  latest_received_version = excluded.latest_received_version,
                  latest_event_id = excluded.latest_event_id,
                  latest_author_node_id = excluded.latest_author_node_id,
                  latest_payload_json = excluded.latest_payload_json,
                  pending_apply = 1",
            new
            {
                Version = version,
                EventId = eventId.ToString("D"),
                AuthorNodeId = authorNodeId.ToString("D"),
                PayloadJson = payloadJson,
            },
            transaction);

    public Task ClearPendingAsync(string? lastError = null, IDbTransaction? transaction = null) =>
        ExecuteUpsertAsync(
            @"INSERT INTO tbl_shared_doors_state (id, pending_apply, last_error)
              VALUES (1, 0, @LastError)
              ON CONFLICT(id) DO UPDATE SET
                  pending_apply = 0,
                  last_error = excluded.last_error",
            new { LastError = lastError },
            transaction);

    public Task RecordAppliedAsync(long appliedVersion, Guid? primaryPersonId, string? lastError, IDbTransaction? transaction = null) =>
        ExecuteUpsertAsync(
            @"INSERT INTO tbl_shared_doors_state (id, pending_apply, applied_version, applied_at, primary_person_id, last_error)
              VALUES (1, 0, @AppliedVersion, @AppliedAt, @PrimaryPersonId, @LastError)
              ON CONFLICT(id) DO UPDATE SET
                  pending_apply = 0,
                  applied_version = excluded.applied_version,
                  applied_at = excluded.applied_at,
                  primary_person_id = excluded.primary_person_id,
                  last_error = excluded.last_error",
            new
            {
                AppliedVersion = appliedVersion,
                AppliedAt = DateTime.UtcNow.ToString("O"),
                PrimaryPersonId = primaryPersonId?.ToString("D"),
                LastError = lastError,
            },
            transaction);

    public Task RecordApplyErrorAsync(string error, IDbTransaction? transaction = null) =>
        ExecuteUpsertAsync(
            @"INSERT INTO tbl_shared_doors_state (id, last_error)
              VALUES (1, @LastError)
              ON CONFLICT(id) DO UPDATE SET
                  last_error = excluded.last_error",
            new { LastError = error },
            transaction);

    public Task ClearAccountsHomeChangedAsync(IDbTransaction? transaction = null) =>
        ExecuteUpsertAsync(
            @"INSERT INTO tbl_shared_doors_state (id, accounts_home_changed)
              VALUES (1, 0)
              ON CONFLICT(id) DO UPDATE SET
                  accounts_home_changed = 0",
            new { },
            transaction);

    public Task MarkAccountsHomeChangedAsync(IDbTransaction? transaction = null) =>
        ExecuteUpsertAsync(
            @"INSERT INTO tbl_shared_doors_state (id, accounts_home_changed)
              VALUES (1, 1)
              ON CONFLICT(id) DO UPDATE SET
                  accounts_home_changed = 1",
            new { },
            transaction);

    private async Task ExecuteUpsertAsync(string sql, object args, IDbTransaction? transaction)
    {
        if (transaction is not null)
        {
            await transaction.Connection!.ExecuteAsync(sql, args, transaction);
            return;
        }
        using var conn = OpenConnection();
        await conn.ExecuteAsync(sql, args);
    }
}
