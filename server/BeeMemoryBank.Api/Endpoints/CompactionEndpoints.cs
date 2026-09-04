using System.Text.Json;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Api.Endpoints;

public static class CompactionEndpoints
{
    public static void MapCompactionEndpoints(this WebApplication app)
    {
        // FOUND during the RequireSuperadmin sweep: this group carried NO role check at all —
        // only the internal-key gate and, on the POST, an unlocked session. Event-log compaction
        // rewrites shared sync history and is as operator-level as everything else under
        // /api/admin, so it is superadmin-only now. (This is a separate MapGroup from
        // /api/admin, not a child of it — group filters do not cascade by path prefix.)
        var group = app.MapGroup("/api/admin/compact").WithTags("Compaction")
            .RequireInternalKey().RequireSuperadmin();

        group.MapGet("/preview", async (CompactionService svc) =>
        {
            var preview = await svc.PreviewAsync();
            return Results.Ok(preview);
        });

        group.MapPost("/", async (CompactionRequest req, CompactionService svc,
            SessionService session) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            // No local catch. This used to flatten every InvalidOperationException to 400, which
            // meant "another compaction is already in progress" — a retryable collision — arrived
            // as a malformed-request error, and so did a missing node identity. CompactionService
            // now throws typed exceptions and ExceptionStatusMap turns them into 409 / 400 / 409
            // respectively, in the one place those pairs are asserted.
            var result = await svc.ExecuteAsync(req.ExplicitCp, req.Reason);
            return Results.Ok(result);
        });

        group.MapGet("/checkpoints", async (DbConnectionFactory connFactory) =>
        {
            using var conn = connFactory.CreateConnection();
            var rows = await conn.QueryAsync<(long SequenceNum, string Payload, DateTime CreatedAt, Guid NodeId)>(
                @"SELECT sequence_num, payload, created_at, node_id
                  FROM tbl_event
                  WHERE event_type = 'snapshot_checkpoint'
                  ORDER BY sequence_num DESC
                  LIMIT 50");

            var checkpoints = rows.Select(r => new
            {
                sequenceNum = r.SequenceNum,
                nodeId = r.NodeId,
                createdAt = r.CreatedAt,
                payload = JsonDocument.Parse(r.Payload).RootElement
            });
            return Results.Ok(checkpoints);
        });
    }
}
