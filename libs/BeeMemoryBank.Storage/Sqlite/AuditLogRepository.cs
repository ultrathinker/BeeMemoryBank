using BeeMemoryBank.Core.Interfaces;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class AuditLogRepository(DbConnectionFactory factory) : BaseRepository(factory), IAuditLogRepository
{
    public async Task LogAsync(string entityType, string entityId, string action, string actorType, string details)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_audit_log (entity_type, entity_id, action, actor_type, created_at, details)
              VALUES (@entityType, @entityId, @action, @actorType, @createdAt, @details)",
            new { entityType, entityId, action, actorType, createdAt = DateTime.UtcNow.ToString("O"), details });
    }

    public async Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM tbl_audit_log WHERE created_at < @cutoff",
            new { cutoff = cutoff.ToString("O") },
            cancellationToken: cancellationToken));
    }
}
