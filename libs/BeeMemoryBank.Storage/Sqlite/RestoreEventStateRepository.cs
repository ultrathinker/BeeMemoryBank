using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BeeMemoryBank.Core.Interfaces;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class RestoreEventStateRepository(DbConnectionFactory factory) : BaseRepository(factory), IRestoreEventStateRepository
{
    public async Task<RestoreEventStateRow?> GetAsync(string eventId)
    {
        using var conn = OpenConnection();
        var sql = @"
            SELECT
                event_id          AS EventId,
                state             AS State,
                superseded_by     AS SupersededBy,
                rejected_locally  AS RejectedLocally,
                applied_at        AS AppliedAt,
                error_message     AS ErrorMessage,
                created_at        AS CreatedAt,
                updated_at        AS UpdatedAt
            FROM tbl_restore_event_state
            WHERE event_id = @eventId";

        var raw = await conn.QuerySingleOrDefaultAsync<dynamic>(sql, new { eventId });
        if (raw == null) return null;

        return new RestoreEventStateRow(
            EventId: raw.EventId,
            State: Enum.Parse<RestoreEventState>((string)raw.State, true),
            SupersededBy: raw.SupersededBy,
            RejectedLocally: raw.RejectedLocally != 0,
            AppliedAt: raw.AppliedAt,
            ErrorMessage: raw.ErrorMessage,
            CreatedAt: raw.CreatedAt,
            UpdatedAt: raw.UpdatedAt
        );
    }

    public async Task UpsertAsync(RestoreEventStateRow row)
    {
        using var conn = OpenConnection();
        var sql = @"
            INSERT INTO tbl_restore_event_state (
                event_id, state, superseded_by, rejected_locally,
                applied_at, error_message, created_at, updated_at
            ) VALUES (
                @EventId, @State, @SupersededBy, @RejectedLocally,
                @AppliedAt, @ErrorMessage, @CreatedAt, @UpdatedAt
            )
            ON CONFLICT(event_id) DO UPDATE SET
                state = excluded.state,
                superseded_by = excluded.superseded_by,
                rejected_locally = excluded.rejected_locally,
                applied_at = excluded.applied_at,
                error_message = excluded.error_message,
                updated_at = excluded.updated_at";

        await conn.ExecuteAsync(sql, new
        {
            row.EventId,
            State = row.State.ToString().ToUpperInvariant(),
            row.SupersededBy,
            RejectedLocally = row.RejectedLocally ? 1 : 0,
            row.AppliedAt,
            row.ErrorMessage,
            row.CreatedAt,
            row.UpdatedAt
        });
    }

    public async Task UpdateStateAsync(string eventId, RestoreEventState newState, string? errorMessage = null)
    {
        using var conn = OpenConnection();
        var sql = @"
            UPDATE tbl_restore_event_state
            SET state = @State,
                error_message = COALESCE(@ErrorMessage, error_message),
                updated_at = @UpdatedAt
            WHERE event_id = @EventId";

        await conn.ExecuteAsync(sql, new
        {
            EventId = eventId,
            State = newState.ToString().ToUpperInvariant(),
            ErrorMessage = errorMessage,
            UpdatedAt = DateTime.UtcNow.ToString("O")
        });
    }

    public async Task MarkSupersededAsync(string oldEventId, string newEventId)
    {
        using var conn = OpenConnection();
        var sql = @"
            UPDATE tbl_restore_event_state
            SET state = @State,
                superseded_by = @SupersededBy,
                updated_at = @UpdatedAt
            WHERE event_id = @OldEventId";

        await conn.ExecuteAsync(sql, new
        {
            OldEventId = oldEventId,
            State = RestoreEventState.Superseded.ToString().ToUpperInvariant(),
            SupersededBy = newEventId,
            UpdatedAt = DateTime.UtcNow.ToString("O")
        });
    }

    public async Task<List<RestoreEventStateRow>> GetByStateAsync(RestoreEventState state)
    {
        using var conn = OpenConnection();
        var sql = @"
            SELECT
                event_id          AS EventId,
                state             AS State,
                superseded_by     AS SupersededBy,
                rejected_locally  AS RejectedLocally,
                applied_at        AS AppliedAt,
                error_message     AS ErrorMessage,
                created_at        AS CreatedAt,
                updated_at        AS UpdatedAt
            FROM tbl_restore_event_state
            WHERE state = @State";

        var rawRows = await conn.QueryAsync<dynamic>(sql, new { State = state.ToString().ToUpperInvariant() });

        return rawRows.Select(raw => new RestoreEventStateRow(
            EventId: raw.EventId,
            State: Enum.Parse<RestoreEventState>((string)raw.State, true),
            SupersededBy: raw.SupersededBy,
            RejectedLocally: raw.RejectedLocally != 0,
            AppliedAt: raw.AppliedAt,
            ErrorMessage: raw.ErrorMessage,
            CreatedAt: raw.CreatedAt,
            UpdatedAt: raw.UpdatedAt
        )).ToList();
    }
}
