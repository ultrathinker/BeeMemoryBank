using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Api.Endpoints;

/// <summary>The request of <c>POST /api/node/identity/rekey</c>: why (shown in the audit entry and the event).</summary>
public sealed record NodeRekeyRequest(string? Reason);

/// <summary>One retired key of this node, as the Node identity page lists it.</summary>
public sealed record NodeKeyHistoryItem(long Counter, string RetiredFingerprint, string NewFingerprint, Guid RotationEventId);

/// <summary>An identity conflict of a peer (plan WS-D D2), with both key fingerprints and the two rotations that announced them.</summary>
public sealed record NodeKeyConflictItem(Guid NodeId, string? DisplayName, long Counter, string FirstFingerprint, string SecondFingerprint, string DetectedAt,
    Guid FirstRotationEventId, Guid SecondRotationEventId);

/// <summary>The request of <c>POST /api/node/identity/conflicts/{nodeId}/confirm</c>: the rotation whose key is the genuine one.</summary>
public sealed record NodeKeyConfirmRequest(Guid RotationEventId);

/// <summary>A node whose events signed by a retired key this node took although its rotation did not list them (plan D3).</summary>
public sealed record RetiredKeyAttentionItemDto(Guid NodeId, string? DisplayName, long Count, string SetAt);

/// <summary>What <c>GET /api/node/identity</c> answers: the current key, its history, who holds a re-key back, the open conflicts, the attention marks.</summary>
public sealed record NodeIdentityResponse(
    Guid NodeId, string Fingerprint, long Counter, bool CanRekeyHere, IReadOnlyList<string> PeersHoldingBack,
    IReadOnlyList<NodeKeyHistoryItem> History, IReadOnlyList<NodeKeyConflictItem> Conflicts, IReadOnlyList<RetiredKeyAttentionItemDto> RetiredKeyAttention,
    SignedAsThisNodeItem? SignedAsThisNode = null);

/// <summary>
/// The node identity re-key (release 2.12, plan WS-D): the state the Node identity page shows and the operation. Superadmin only, never an agent:
/// a new identity key changes what every peer trusts for this node.
/// </summary>
public static class NodeIdentityEndpoints
{
    public static void MapNodeIdentityEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/node/identity").WithTags("NodeIdentity")
            .RequireInternalKey().RequireSuperadmin().RequireNonAgent();

        group.MapGet("/", async (INodeIdentityRepository nodeRepo, NodeKeyHistoryStore history, NodeIdentityRekeyService rekey,
            IWhitelistRepository whitelist, IDbConnectionFactory db) =>
        {
            var identity = await nodeRepo.GetAsync();
            if (identity is null) return Results.Json(new ErrorResponse("Node is not initialized."), statusCode: 409);
            var rows = await history.ListAsync(identity.NodeId);
            var conflicts = new List<NodeKeyConflictItem>();
            foreach (var c in await history.ListOpenConflictsAsync())
                conflicts.Add(new NodeKeyConflictItem(c.NodeId, (await whitelist.GetByNodeIdAsync(c.NodeId, includeDeleted: true))?.DisplayName, c.Counter,
                    NodeKeyTransition.Fingerprint(c.FirstNewKey), NodeKeyTransition.Fingerprint(c.SecondNewKey), c.DetectedAt,
                    c.FirstEventId, c.SecondEventId));
            var attention = new List<RetiredKeyAttentionItemDto>();
            foreach (var a in await RetiredKeyAttention.ListAsync(db))
                attention.Add(new RetiredKeyAttentionItemDto(a.NodeId, (await whitelist.GetByNodeIdAsync(a.NodeId, includeDeleted: true))?.DisplayName, a.Count, a.SetAt));
            return Results.Ok(new NodeIdentityResponse(
                identity.NodeId,
                NodeKeyTransition.Fingerprint(identity.Ed25519PublicKey),
                rows.Count == 0 ? 0 : rows.Max(r => r.Counter),
                identity.Ed25519PrivateKeyV is 0 or 1 && !BlindNodeId.IsBlind(identity.NodeId),
                await rekey.PeersHoldingBackAsync(),
                rows.OrderByDescending(r => r.Counter)
                    .Select(r => new NodeKeyHistoryItem(r.Counter, NodeKeyTransition.Fingerprint(r.PublicKey), NodeKeyTransition.Fingerprint(r.NewPublicKey), r.RotationEventId))
                    .ToList(),
                conflicts,
                attention,
                await SignedAsThisNode.GetAsync(db)));
        });

        group.MapPost("/rekey", async (NodeRekeyRequest? req, NodeIdentityRekeyService rekey, SessionService session, HttpContext ctx) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);
            var reason = req?.Reason is { Length: > 200 } given ? given[..200] : req?.Reason;
            var actor = ctx.Request.Headers["X-User-Id"].FirstOrDefault() is { Length: > 0 } uid ? $"user {uid}" : "system";
            var result = await rekey.RekeyAsync(reason, actor, ctx.RequestAborted);
            return result.Done
                ? Results.Ok(new { fingerprint = result.NewFingerprint, counter = result.Counter, rotationEventId = result.RotationEventId })
                : Results.Json(new ErrorResponse(result.Refusal!), statusCode: 409);
        });

        // Acknowledges the retired-key attention mark of a node: the administrator has looked at it.
        group.MapPost("/attention/{nodeId:guid}/acknowledge", async (Guid nodeId, IDbConnectionFactory db, IAuditLogRepository audit, HttpContext ctx) =>
        {
            if (!await RetiredKeyAttention.ClearAsync(db, nodeId))
                return Results.Json(new ErrorResponse("There is nothing to acknowledge for that device."), statusCode: 404);
            var actor = ctx.Request.Headers["X-User-Id"].FirstOrDefault() is { Length: > 0 } uid ? $"user {uid}" : "system";
            await audit.LogAsync("node", nodeId.ToString("D"), "retired_key_attention_acknowledged", actor,
                "Events of a retired identity key that its rotation did not list were looked at");
            return Results.Ok(new { acknowledged = true });
        });

        // Acknowledges the "somebody signs as this node" mark: the administrator has looked at it.
        group.MapPost("/signed-as-this-node/acknowledge", async (IDbConnectionFactory db, IAuditLogRepository audit, HttpContext ctx) =>
        {
            if (!await SignedAsThisNode.ClearAsync(db))
                return Results.Json(new ErrorResponse("There is nothing to acknowledge."), statusCode: 404);
            var actor = ctx.Request.Headers["X-User-Id"].FirstOrDefault() is { Length: > 0 } uid ? $"user {uid}" : "system";
            var self = await ctx.RequestServices.GetRequiredService<INodeIdentityRepository>().GetAsync();
            await audit.LogAsync("node", self?.NodeId.ToString("D") ?? "", "signed_as_this_node_acknowledged", actor,
                "Events signed with this node's key that this node did not write were looked at");
            return Results.Ok(new { acknowledged = true });
        });

        // Settles another node's identity conflict (plan WS-D D2): which of its two announced keys is the genuine one.
        group.MapPost("/conflicts/{nodeId:guid}/confirm", async (Guid nodeId, NodeKeyConfirmRequest? req, NodeIdentityRekeyService rekey, HttpContext ctx) =>
        {
            if (req is null || req.RotationEventId == Guid.Empty)
                return Results.BadRequest(new ErrorResponse("Name the rotation whose key is the right one."));
            var actor = ctx.Request.Headers["X-User-Id"].FirstOrDefault() is { Length: > 0 } uid ? $"user {uid}" : "system";
            var result = await rekey.ConfirmAsync(nodeId, req.RotationEventId, actor, ctx.RequestAborted);
            return result.Done
                ? Results.Ok(new { fingerprint = result.NewFingerprint, counter = result.Counter, confirmEventId = result.RotationEventId })
                : Results.Json(new ErrorResponse(result.Refusal!), statusCode: 409);
        });

        // ─── A fresh rotation envelope for a peer that re-keyed (plan WS-D D4) ───────────────
        // The caller holds a rotation envelope this node made for its previous identity key. Authenticated with its current key; answered only by
        // the node that made the rotation, while that rotation's key is still this node's current one; never for a revoked, unknown or blind node.
        app.MapPost("/api/sync/dek-envelope/{commitEventId:guid}", async (Guid commitEventId, HttpContext ctx, SyncTokenStore store,
            IWhitelistRepository whitelist, INodeIdentityRepository nodeRepo, IEventLogRepository eventLogRepo, SessionService session) =>
        {
            if (await SyncEndpoints.AuthenticatePeerAsync(ctx, store) is not { } caller) return Results.Unauthorized();
            if (BlindNodeId.IsBlind(caller) || await whitelist.GetByNodeIdAsync(caller) is not { } peer) return Results.StatusCode(403);
            if (!session.IsUnlocked) return Results.StatusCode(503);
            var identity = await nodeRepo.GetAsync();
            var commit = await eventLogRepo.GetByIdAsync(commitEventId.ToString("D"));
            if (identity is null || commit is null || commit.EventType != EventTypes.DekRotationCommit || commit.NodeId != identity.NodeId)
                return Results.NotFound();
            var payload = System.Text.Json.JsonSerializer.Deserialize<DekRotationCommitPayload>(commit.Payload);
            if (payload is null || payload.NewDekEpoch != identity.DekEpoch)
                return Results.Json(new ErrorResponse("That rotation is no longer this node's current key; the peer joins again."), statusCode: 409);
            var dek = session.GetMasterDek();
            try
            {
                var set = BeeMemoryBank.Crypto.DekEnvelope.Build(dek, commitEventId, [new BeeMemoryBank.Crypto.DekEnvelope.Recipient(caller, peer.Ed25519PublicKey)]);
                var box = set.Peers.Values.Single();
                return Results.Ok(new BeeMemoryBank.Sync.DekRotation.DekEnvelopeAnswer(set.EphemeralPublicKeyB64, box.WrappedB64, box.NonceB64));
            }
            finally
            {
                Array.Clear(dek);
            }
        }).WithTags("Sync");
    }
}
