using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace BeeMemoryBank.Api.Endpoints;

public static partial class ChatEndpoints
{
    private static void MapConversationEndpoints(RouteGroupBuilder group)
    {
        // ── Phase 2: conversation history (all scoped to the caller's own user_id) ──

        // List the caller's conversations, newest first.
        group.MapGet("/conversations", async (HttpContext ctx, ChatConversationRepository convoRepo) =>
        {
            var identity = CallerIdentity.Extract(ctx);
            if (identity.UserId is null)
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 401);
            var userId = identity.UserId.Value;
            var list = await convoRepo.ListByUserAsync(userId);
            return Results.Ok(list.Select(c => new ChatConversationResponse(c.Id, c.Title, c.CreatedAt, c.UpdatedAt)));
        });

        // ── Homepage pinned chat ─────────────────────────────────────────────────
        // The pin is a flag on the user's OWN chat_conversation row (is_home_pinned); at most
        // one per user (enforced in SetHomePinnedAsync). Read + clear only — the pin is SET
        // exclusively by /stream's conversation-creation path (pinToHome), so no set endpoint
        // is exposed. Per-user, no role gate (mirrors /conversations).

        // The caller's home-pinned conversation id (null when none). Self-heals: a stale flag
        // pointing at a row the user no longer owns cannot occur (the query is user-scoped),
        // and a deleted conversation simply has no row, so null comes back naturally.
        group.MapGet("/home-pinned", async (HttpContext ctx, ChatConversationRepository convoRepo) =>
        {
            var identity = CallerIdentity.Extract(ctx);
            if (identity.UserId is null)
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 401);
            var pinnedId = await convoRepo.GetHomePinnedIdAsync(identity.UserId.Value);
            return Results.Ok(new HomePinnedResponse(pinnedId));
        });

        // "Close chat" / "New chat" on the homepage: clears the caller's pin. NEVER deletes
        // the conversation — the row (and its messages) stays fully intact and listed in /AI.
        group.MapDelete("/home-pinned", async (HttpContext ctx, ChatConversationRepository convoRepo) =>
        {
            var identity = CallerIdentity.Extract(ctx);
            if (identity.UserId is null)
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 401);
            await convoRepo.ClearHomePinAsync(identity.UserId.Value);
            return Results.NoContent();
        });

        // Load a conversation's transcript (messages oldest-first). Ownership is re-checked by the
        // user-scoped repo lookup; a foreign conversation id yields 404, never a leak. Phase 5: each
        // message carries its attachment refs (user-upload on a user turn, generated-image on an
        // assistant turn) so the UI renders inline images when reopening a conversation. The blobs
        // are NOT inlined here — the UI fetches them via GET /attachments/{id} (ownership-checked).
        group.MapGet("/conversations/{id:guid}/messages", async (Guid id, HttpContext ctx,
            ChatConversationRepository convoRepo, ChatMessageRepository msgRepo,
            ChatAttachmentRepository attachRepo, ChatSettingsRepository repo) =>
        {
            var identity = CallerIdentity.Extract(ctx);
            if (identity.UserId is null)
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 401);
            var userId = identity.UserId.Value;
            var convo = await convoRepo.GetByIdForUserAsync(id, userId);
            if (convo is null)
                return Results.NotFound(new ErrorResponse("Conversation not found"));

            var msgs = await msgRepo.ListByConversationAsync(id);

            // Group attachments by message_id (one pass) so the transcript build is N+0, not N+1.
            var byMessage = new Dictionary<Guid, List<ChatAttachment>>();
            try
            {
                foreach (var a in await attachRepo.ListByConversationAsync(id))
                {
                    if (!byMessage.TryGetValue(a.MessageId, out var list))
                        byMessage[a.MessageId] = list = new();
                    list.Add(a);
                }
            }
            catch { /* transcript still renders without attachments */ }

            // Build a ModelId → ContextWindow map (first wins on duplicates) so each message can
            // report its context-fill %. Loaded once for the whole transcript.
            var contextWindowByModelId = new Dictionary<string, int?>(StringComparer.Ordinal);
            try
            {
                foreach (var mdl in await repo.ListAllModelsAsync())
                {
                    if (!string.IsNullOrEmpty(mdl.ModelId) && !contextWindowByModelId.ContainsKey(mdl.ModelId))
                        contextWindowByModelId[mdl.ModelId] = mdl.ContextWindow;
                }
            }
            catch { /* metrics are best-effort — transcript still renders without them */ }

            return Results.Ok(msgs.Select(m =>
            {
                int? msgContextWindow = null;
                if (!string.IsNullOrEmpty(m.Model) && contextWindowByModelId.TryGetValue(m.Model, out var cw))
                    msgContextWindow = cw;
                return new ChatMessageRowResponse(
                    m.Id, m.Role, m.ContentText, m.ToolCallsJson, m.ToolCallId, m.Model,
                    // SQLite round-trips DateTime as Kind=Unspecified, which System.Text.Json
                    // serializes WITHOUT a "Z" suffix — the client's `new Date(...)` then parses
                    // it as LOCAL time instead of UTC, showing a wrong tooltip timestamp on
                    // reload whenever the server's TZ differs from the browser's. The live SSE
                    // `done` event uses DateTime.UtcNow (Kind=Utc, serializes with "Z") and is
                    // unaffected — only this history path needs the explicit re-stamp.
                    DateTime.SpecifyKind(m.CreatedAt, DateTimeKind.Utc),
                    m.TokensIn, m.TokensOut, m.ToolCallsCount, m.DurationMs,
                    ComputeContextFill(m.TokensIn, msgContextWindow), msgContextWindow,
                    byMessage.TryGetValue(m.Id, out var atts)
                        ? atts.Select(a => new ChatAttachmentRef(a.Id, a.Kind, a.Mime)).ToList()
                        : null);
            }));
        });

        // ── Phase 5: serve a chat attachment's bytes (ownership-checked) ────────────────────
        // Renders inline images in the transcript. Ownership is enforced by the join
        // chat_attachment → chat_message → chat_conversation(user_id) inside GetByIdForUserAsync,
        // so a foreign id yields 404, never a leak. Cacheable (immutable content). The CSP allows
        // 'self' for img-src, so this route renders without any CSP change.
        group.MapGet("/attachments/{id:guid}", async (Guid id, HttpContext ctx,
            ChatAttachmentRepository attachRepo) =>
        {
            var identity = CallerIdentity.Extract(ctx);
            if (identity.UserId is null)
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 401);
            var userId = identity.UserId.Value;
            var att = await attachRepo.GetByIdForUserAsync(id, userId);
            var blob = att?.Blob;
            if (blob is null || blob.Length == 0)
                return Results.NotFound(new ErrorResponse("Attachment not found"));

            ctx.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            BeeMemoryBank.Hosting.AspNetCore.UserContentResponseHeaders.ApplyTo(ctx.Response);
            var mime = att!.Mime;
            return Results.File(blob, string.IsNullOrWhiteSpace(mime) ? "application/octet-stream" : mime);
        });

        // Rename a conversation (title). User-scoped.
        group.MapMethods("/conversations/{id:guid}", new[] { "PATCH" }, async (Guid id, HttpContext ctx,
            ChatConversationRepository convoRepo) =>
        {
            RenameConversationRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<RenameConversationRequest>(SseJsonOpts); }
            catch { body = null; }
            if (body is null || string.IsNullOrWhiteSpace(body.Title))
                return Results.Json(new ErrorResponse("title is required"), statusCode: 400);

            var identity = CallerIdentity.Extract(ctx);
            if (identity.UserId is null)
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 401);
            var userId = identity.UserId.Value;
            var convo = await convoRepo.GetByIdForUserAsync(id, userId);
            if (convo is null)
                return Results.NotFound(new ErrorResponse("Conversation not found"));

            await convoRepo.UpdateTitleAsync(id, body.Title.Trim());
            return Results.Ok();
        });

        // Delete a conversation (+ its messages). User-scoped.
        group.MapDelete("/conversations/{id:guid}", async (Guid id, HttpContext ctx,
            ChatConversationRepository convoRepo) =>
        {
            var identity = CallerIdentity.Extract(ctx);
            if (identity.UserId is null)
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 401);
            var userId = identity.UserId.Value;
            var convo = await convoRepo.GetByIdForUserAsync(id, userId);
            if (convo is null)
                return Results.NotFound(new ErrorResponse("Conversation not found"));

            await convoRepo.DeleteAsync(id);
            return Results.NoContent();
        });
    }
}
