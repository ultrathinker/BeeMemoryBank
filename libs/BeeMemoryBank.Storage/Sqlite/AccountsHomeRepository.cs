using System.Data;
using System.Globalization;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public sealed class AccountsHomeRepository(DbConnectionFactory factory) : BaseRepository(factory), IAccountsHomeRepository
{
    private sealed class Row
    {
        public string NodeId { get; set; } = "";
        public long LamportTs { get; set; }
        public string? SourceNodeId { get; set; }
        public string? EventId { get; set; }
        public string UpdatedAt { get; set; } = "";
    }

    public async Task<AccountsHomeRecord?> GetAsync(IDbTransaction? transaction = null)
    {
        const string sql =
            @"SELECT node_id AS NodeId, lamport_ts AS LamportTs, source_node_id AS SourceNodeId,
                     event_id AS EventId, updated_at AS UpdatedAt
              FROM tbl_accounts_home WHERE id = 1";
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
        if (row is null || !Guid.TryParseExact(row.NodeId, "D", out var nodeId)) return null;
        Guid? sourceNodeId = Guid.TryParseExact(row.SourceNodeId, "D", out var src) ? src : null;
        Guid? eventId = Guid.TryParseExact(row.EventId, "D", out var evt) ? evt : null;
        var updatedAt = DateTime.TryParse(row.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToUniversalTime()
            : DateTime.UnixEpoch;
        return new AccountsHomeRecord(nodeId, row.LamportTs, sourceNodeId, eventId, updatedAt);
    }

    public async Task UpsertAsync(AccountsHomeRecord record, IDbTransaction? transaction = null)
    {
        const string sql =
            @"INSERT INTO tbl_accounts_home (id, node_id, lamport_ts, source_node_id, event_id, updated_at)
              VALUES (1, @NodeId, @LamportTs, @SourceNodeId, @EventId, @UpdatedAt)
              ON CONFLICT(id) DO UPDATE SET
                  node_id = excluded.node_id,
                  lamport_ts = excluded.lamport_ts,
                  source_node_id = excluded.source_node_id,
                  event_id = excluded.event_id,
                  updated_at = excluded.updated_at";
        var args = new
        {
            NodeId = record.NodeId.ToString("D"),
            record.LamportTs,
            SourceNodeId = record.SourceNodeId?.ToString("D"),
            EventId = record.EventId?.ToString("D"),
            UpdatedAt = record.UpdatedAt.ToUniversalTime().ToString("O"),
        };
        if (transaction is not null)
        {
            await transaction.Connection!.ExecuteAsync(sql, args, transaction);
            return;
        }
        using var conn = OpenConnection();
        await conn.ExecuteAsync(sql, args);
    }
}
