using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BeeMemoryBank.Core.Interfaces;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class DekRotationStateRepository(DbConnectionFactory factory) : BaseRepository(factory), IDekRotationStateRepository
{
    public async Task<DekRotationStateRow?> GetAsync(string eventId)
    {
        using var conn = OpenConnection();
        var sql = @"
            SELECT
                event_id                          AS EventId,
                state                             AS State,
                proposed_event_id                 AS ProposedEventId,
                rotation_ts                       AS RotationTs,
                applied_at                        AS AppliedAt,
                error_message                     AS ErrorMessage,
                last_processed_id_article         AS LastProcessedIdArticle,
                last_processed_id_article_version  AS LastProcessedIdArticleVersion,
                last_processed_id_media           AS LastProcessedIdMedia,
                last_processed_id_conflict_version AS LastProcessedIdConflictVersion,
                last_processed_id_comment         AS LastProcessedIdComment,
                created_at                        AS CreatedAt,
                updated_at                        AS UpdatedAt
            FROM tbl_dek_rotation_state
            WHERE event_id = @eventId";

        var raw = await conn.QuerySingleOrDefaultAsync<dynamic>(sql, new { eventId });
        if (raw == null) return null;

        return MapRow(raw);
    }

    public async Task UpsertAsync(DekRotationStateRow row)
    {
        using var conn = OpenConnection();
        var sql = @"
            INSERT INTO tbl_dek_rotation_state (
                event_id, state, proposed_event_id, rotation_ts, applied_at, error_message,
                last_processed_id_article, last_processed_id_article_version,
                last_processed_id_media, last_processed_id_conflict_version,
                last_processed_id_comment, created_at, updated_at
            ) VALUES (
                @EventId, @State, @ProposedEventId, @RotationTs, @AppliedAt, @ErrorMessage,
                @LastProcessedIdArticle, @LastProcessedIdArticleVersion,
                @LastProcessedIdMedia, @LastProcessedIdConflictVersion,
                @LastProcessedIdComment, @CreatedAt, @UpdatedAt
            )
            ON CONFLICT(event_id) DO UPDATE SET
                state = excluded.state,
                proposed_event_id = excluded.proposed_event_id,
                rotation_ts = excluded.rotation_ts,
                applied_at = excluded.applied_at,
                error_message = excluded.error_message,
                last_processed_id_article = excluded.last_processed_id_article,
                last_processed_id_article_version = excluded.last_processed_id_article_version,
                last_processed_id_media = excluded.last_processed_id_media,
                last_processed_id_conflict_version = excluded.last_processed_id_conflict_version,
                last_processed_id_comment = excluded.last_processed_id_comment,
                updated_at = excluded.updated_at";

        await conn.ExecuteAsync(sql, new
        {
            row.EventId,
            State = row.State.ToString().ToUpperInvariant(),
            row.ProposedEventId,
            row.RotationTs,
            row.AppliedAt,
            row.ErrorMessage,
            row.LastProcessedIdArticle,
            row.LastProcessedIdArticleVersion,
            row.LastProcessedIdMedia,
            row.LastProcessedIdConflictVersion,
            row.LastProcessedIdComment,
            row.CreatedAt,
            row.UpdatedAt
        });
    }

    public async Task UpdateStateAsync(string eventId, DekRotationState newState, string? errorMessage = null)
    {
        using var conn = OpenConnection();
        var now = DateTime.UtcNow.ToString("O");
        var sql = @"
            UPDATE tbl_dek_rotation_state
            SET state = @State,
                error_message = COALESCE(@ErrorMessage, error_message),
                applied_at = CASE WHEN @State = 'APPLIED' THEN @Now ELSE applied_at END,
                updated_at = @Now
            WHERE event_id = @EventId";

        await conn.ExecuteAsync(sql, new
        {
            EventId = eventId,
            State = newState.ToString().ToUpperInvariant(),
            ErrorMessage = errorMessage,
            Now = now
        });
    }

    public async Task UpdateLastProcessedAsync(string eventId, string tableSuffix, long lastId)
    {
        var column = tableSuffix switch
        {
            "article" => "last_processed_id_article",
            "article_version" => "last_processed_id_article_version",
            "media" => "last_processed_id_media",
            "conflict_version" => "last_processed_id_conflict_version",
            "comment" => "last_processed_id_comment",
            _ => throw new ArgumentException($"Invalid table suffix: {tableSuffix}")
        };

        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            $"UPDATE tbl_dek_rotation_state SET {column} = @lastId, updated_at = @now WHERE event_id = @eventId",
            new { eventId, lastId, now = DateTime.UtcNow.ToString("O") });
    }

    public async Task<List<DekRotationStateRow>> GetByStateAsync(DekRotationState state)
    {
        using var conn = OpenConnection();
        var sql = @"
            SELECT
                event_id                          AS EventId,
                state                             AS State,
                proposed_event_id                 AS ProposedEventId,
                rotation_ts                       AS RotationTs,
                applied_at                        AS AppliedAt,
                error_message                     AS ErrorMessage,
                last_processed_id_article         AS LastProcessedIdArticle,
                last_processed_id_article_version  AS LastProcessedIdArticleVersion,
                last_processed_id_media           AS LastProcessedIdMedia,
                last_processed_id_conflict_version AS LastProcessedIdConflictVersion,
                last_processed_id_comment         AS LastProcessedIdComment,
                created_at                        AS CreatedAt,
                updated_at                        AS UpdatedAt
            FROM tbl_dek_rotation_state
            WHERE state = @State";

        var rawRows = await conn.QueryAsync<dynamic>(sql, new { State = state.ToString().ToUpperInvariant() });
        return rawRows.Select(MapRow).ToList();
    }

    private static DekRotationStateRow MapRow(dynamic raw) => new(
        EventId: raw.EventId,
        State: Enum.Parse<DekRotationState>((string)raw.State, true),
        ProposedEventId: raw.ProposedEventId,
        RotationTs: raw.RotationTs,
        AppliedAt: raw.AppliedAt,
        ErrorMessage: raw.ErrorMessage,
        LastProcessedIdArticle: (long?)raw.LastProcessedIdArticle,
        LastProcessedIdArticleVersion: (long?)raw.LastProcessedIdArticleVersion,
        LastProcessedIdMedia: (long?)raw.LastProcessedIdMedia,
        LastProcessedIdConflictVersion: (long?)raw.LastProcessedIdConflictVersion,
        LastProcessedIdComment: (long?)raw.LastProcessedIdComment,
        CreatedAt: raw.CreatedAt,
        UpdatedAt: raw.UpdatedAt
    );
}
