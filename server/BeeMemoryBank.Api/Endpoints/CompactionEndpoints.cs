using System.Text.Json;
using BeeMemoryBank.Api.Middleware;
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
        var group = app.MapGroup("/api/admin/compact").WithTags("Compaction");

        group.MapGet("/preview", async (CompactionService svc, HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var preview = await svc.PreviewAsync();
            return Results.Ok(preview);
        });

        group.MapPost("/", async (CompactionRequest req, CompactionService svc,
            SessionService session, HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            try
            {
                var result = await svc.ExecuteAsync(req.ExplicitCp, req.Reason);
                return Results.Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
        });

        group.MapGet("/checkpoints", async (DbConnectionFactory connFactory, HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

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
