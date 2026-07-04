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
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace BeeMemoryBank.Api.Endpoints;

/// <summary>
/// Phase 0 surface for the native AI chat (plan §2). Backend-only — no UI yet.
///
/// Group is gated by <see cref="RequireInternalKeyExtensions.RequireInternalKey(RouteGroupBuilder)"/>
/// (internal-key check) the same as every other protected group. Within it:
///  - Key CRUD + model-catalogue WRITES additionally require <c>X-User-Role == superadmin</c>
///    (mirrors <c>SnapshotEndpoints</c>).
///  - Any endpoint that encrypts/decrypts a key checks <c>session.IsUnlocked</c> first and
///    returns <c>409 {"error":"Vault is locked"}</c> when locked (encrypt needs the master DEK —
///    see plan §6 "Phase 0 acceptance made realistic").
///  - A key's plaintext is returned ONLY at creation; every other response exposes
///    <c>key_prefix</c> only.
///
/// Key encryption follows the EXACT <c>RemoteAccountService</c> precedent
/// (<c>ArticleEncryptor.Encrypt(secret, masterDek, aad)</c> with
/// <c>session.GetMasterDek()</c> + <c>Array.Clear(masterDek)</c> in <c>finally</c>). It does NOT
/// use <c>AgentKeyHelper</c> (wrong direction — see plan §6).
/// </summary>
public static class ChatEndpoints
{
    // Constant AAD for OpenRouter-key encryption (distinct from RemoteAccountService's token AAD).
    private static readonly byte[] KeyAad = "bmb-openrouter-key-v1"u8.ToArray();

    // Phase 3: in-flight confirmation guard. Prevents the SAME tool call from being processed by two
    // concurrent /confirm requests (two browser tabs / a rapid double-click). The persisted-tool-result
    // idempotency check below catches the SEQUENTIAL double-click (it sees the stored tool result); this
    // catches the CONCURRENT window where two requests both load history before either persists a tool
    // result. Keyed by conversation+toolCallId so different conversations/calls never interfere.
    // Process-singleton (resets on restart — same accepted trade-off as ChatDestructiveOpCounter).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _inFlightConfirms = new();

    public static void MapChatEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/chat").WithTags("Chat").RequireInternalKey();

        // ── API keys (superadmin-only; create decrypts the DEK path → also needs unlocked) ──

        group.MapPost("/keys", async (CreateChatKeyRequest req, ChatSettingsRepository repo,
            SessionService session, HttpContext ctx) =>
        {
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);
            // Encrypts under the master DEK → needs an unlocked vault.
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Vault is locked"), statusCode: 409);

            if (string.IsNullOrWhiteSpace(req.Label))
                return Results.Json(new ErrorResponse("Label is required"), statusCode: 400);
            if (string.IsNullOrWhiteSpace(req.ApiKey))
                return Results.Json(new ErrorResponse("ApiKey is required"), statusCode: 400);

            var plaintextKey = req.ApiKey.Trim();
            var (ciphertext, iv) = EncryptKey(plaintextKey, session);

            var key = new ChatApiKey
            {
                Id = Guid.NewGuid(),
                Label = req.Label.Trim(),
                KeyPrefix = ComputeKeyPrefix(plaintextKey),
                Ciphertext = ciphertext,
                Iv = iv,
                Enabled = true,
                Priority = req.Priority,
                CreatedAt = DateTime.UtcNow
            };
            await repo.CreateAsync(key);

            // Returned exactly once; thereafter only key_prefix is exposed.
            return Results.Created($"/api/chat/keys/{key.Id}",
                new ChatKeyCreatedResponse(key.Id, key.Label, key.KeyPrefix, plaintextKey));
        });

        group.MapGet("/keys", async (ChatSettingsRepository repo, HttpContext ctx) =>
        {
            // Listing never decrypts — only key_prefix is exposed. Still superadmin-only.
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);

            var keys = await repo.ListAsync();
            return Results.Ok(keys.Select(k => new ChatKeyResponse(
                k.Id, k.Label, k.KeyPrefix, k.Enabled, k.Priority,
                k.LastUsedAt, k.LastError, k.DisabledUntil, k.CreatedAt)));
        });

        group.MapPatch("/keys/{id:guid}", async (Guid id, UpdateChatKeyRequest req,
            ChatSettingsRepository repo, HttpContext ctx) =>
        {
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);

            var existing = await repo.GetByIdAsync(id);
            if (existing == null)
                return Results.NotFound(new ErrorResponse($"Chat key {id} not found"));

            // Metadata only — no crypto, so no IsUnlocked gate.
            await repo.UpdateMetadataAsync(id, req.Label, req.Enabled, req.Priority);
            return Results.Ok();
        });

        group.MapDelete("/keys/{id:guid}", async (Guid id, ChatSettingsRepository repo, HttpContext ctx) =>
        {
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);

            await repo.DeleteKeyAsync(id);
            return Results.NoContent();
        });

        // ── Model catalogue (writes superadmin; listing enabled models open to authenticated users) ──

        group.MapPost("/models", async (CreateChatModelRequest req, ChatSettingsRepository repo, HttpContext ctx) =>
        {
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);

            if (string.IsNullOrWhiteSpace(req.ModelId))
                return Results.Json(new ErrorResponse("ModelId is required"), statusCode: 400);
            if (string.IsNullOrWhiteSpace(req.Label))
                return Results.Json(new ErrorResponse("Label is required"), statusCode: 400);

            var model = new ChatModelRow
            {
                Id = Guid.NewGuid(),
                ModelId = req.ModelId.Trim(),
                Label = req.Label.Trim(),
                Category = string.IsNullOrWhiteSpace(req.Category) ? "text" : req.Category.Trim(),
                DefaultForCategory = req.DefaultForCategory,
                Enabled = req.Enabled
            };
            await repo.CreateAsync(model);
            return Results.Created($"/api/chat/models/{model.Id}", ToModelResponse(model));
        });

        // Plan §1: "listing enabled models for the per-conversation picker is available to any
        // authenticated user." Authenticated = internal-key check (group filter). No role gate.
        group.MapGet("/models", async (ChatSettingsRepository repo) =>
        {
            var models = await repo.ListEnabledAsync();
            return Results.Ok(models.Select(ToModelResponse));
        });

        group.MapDelete("/models/{id:guid}", async (Guid id, ChatSettingsRepository repo, HttpContext ctx) =>
        {
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);

            await repo.DeleteModelAsync(id);
            return Results.NoContent();
        });

        // Admin catalogue view: ALL models (enabled + disabled) so the AI settings UI can list and
        // re-enable disabled entries. Superadmin-only. (GET /models above returns enabled-only and
        // is open to any authenticated user, for the per-conversation picker.)
        group.MapGet("/models/all", async (ChatSettingsRepository repo, HttpContext ctx) =>
        {
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);

            var models = await repo.ListAllModelsAsync();
            return Results.Ok(models.Select(ToModelResponse));
        });

        // Toggle a model's enabled flag (admin catalogue). Superadmin-only. No crypto → no IsUnlocked gate.
        group.MapPatch("/models/{id:guid}", async (Guid id, UpdateChatModelRequest req,
            ChatSettingsRepository repo, HttpContext ctx) =>
        {
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);

            await repo.UpdateModelMetadataAsync(id, req.Enabled);
            return Results.Ok();
        });

        // ── Phase 2: STREAMING tool-loop turn + persistence ─────────────────────────
        //
        // Runs the SAME tool-call loop as /message, but writes the response as a Server-Sent Events
        // stream: text deltas (so the assistant bubble fills in incrementally), tool-call lifecycle
        // events ("tool_call_start"/"tool_call_result" so the UI can show "searching…"), and a final
        // "done" event. The browser's disconnect (HttpContext.RequestAborted) is forwarded into the
        // OpenRouter streaming call so navigating away cancels the upstream request (no wasted billing
        // — plan §1 "Cancellation", §2 Phase 2 accept).
        //
        // Persistence: on the first message with no conversationId it creates a chat_conversation
        // (title = first ~40 chars of the message); it appends each user/assistant/tool turn to
        // chat_message and touches updated_at. History is loaded from chat.db to build the working
        // message list (multi-turn tool context). All writes are best-effort (logged, never abort the
        // stream) — the live chat experience outranks a local-DB write that almost never fails.
        //
        // Validation errors (locked vault / no key / bad request) are written as normal JSON BEFORE
        // the SSE response commits, so the Web proxy can route them as ordinary errors. Once the first
        // SSE frame is flushed, any failure is reported as an `event: error` frame.
        group.MapPost("/stream", async (HttpContext ctx, ChatSettingsRepository repo,
            OpenRouterClient openRouter, ChatToolDispatcher dispatcher, SessionService session,
            ChatConversationRepository convoRepo, ChatMessageRepository msgRepo,
            ChatAttachmentRepository attachRepo, ILogger<Program> logger) =>
        {
            ChatStreamRequest? req;
            try
            {
                req = await ctx.Request.ReadFromJsonAsync<ChatStreamRequest>(SseJsonOpts);
            }
            catch
            {
                req = null;
            }

            // ── pre-stream validation: ordinary JSON errors ──
            async Task JsonError(int status, string message)
            {
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new ErrorResponse(message), SseJsonOpts));
            }

            if (req is null || string.IsNullOrWhiteSpace(req.Model))
            {
                await JsonError(400, "Model is required");
                return;
            }
            // Reject a client-supplied model that isn't in the admin-curated enabled catalogue
            // (plan §1: the per-conversation picker must only offer enabled models; an arbitrary
            // OpenRouter model must never run on the shared, admin-funded key).
            if (!await repo.IsModelEnabledAsync(req.Model))
            {
                await JsonError(400, "model not in the enabled catalogue");
                return;
            }
            if (string.IsNullOrWhiteSpace(req.Message))
            {
                await JsonError(400, "Message is required");
                return;
            }
            // Decrypting the OpenRouter key needs the master DEK → vault must be unlocked.
            if (!session.IsUnlocked)
            {
                await JsonError(409, "Vault is locked");
                return;
            }

            var keys = await DecryptAvailableKeysAsync(repo, session);
            if (keys.Count == 0)
            {
                await JsonError(409,
                    "No chat API key is available (none configured, or all are disabled/cooling down). Have a superadmin add one under /api/chat/keys.");
                return;
            }

            var ct = ctx.RequestAborted;

            // ── Phase 5: model category + image-attachment validation ──
            // Resolve the model's category to decide whether this turn accepts an attached image
            // (vision only) or runs the image-generation path. Unknown model_id → treated as text.
            var category = await repo.GetCategoryByModelIdAsync(req.Model) ?? "text";

            // Decode + validate an attached image (vision only). text/image-gen models must NOT
            // receive an image — reject with a clear error rather than letting OpenRouter reject it
            // confusingly (plan §2 Phase 5).
            byte[]? attachmentBytes = null;
            string attachmentMime = "";
            if (req.Attachment is not null)
            {
                if (category != "vision")
                {
                    await JsonError(400,
                        category == "image-gen"
                            ? "This model generates images — remove the attached image to send a message."
                            : "This model does not accept images. Select a vision model to attach one.");
                    return;
                }
                var (okBytes, okMime, attachError) = ValidateAttachment(req.Attachment);
                if (okBytes is null)
                {
                    await JsonError(400, attachError ?? "Invalid image attachment.");
                    return;
                }
                attachmentBytes = okBytes;
                attachmentMime = okMime;
            }

            // ── conversation load/create (scoped to the caller's own user_id) ──
            var identity = CallerIdentity.Extract(ctx);
            if (identity.UserId is null)
            {
                await JsonError(401, "Unauthorized");
                return;
            }
            int userId = identity.UserId.Value; // Web always forwards X-User-Id.
            Guid conversationId;
            if (req.ConversationId.HasValue)
            {
                var existing = await convoRepo.GetByIdForUserAsync(req.ConversationId.Value, userId);
                if (existing is null)
                {
                    await JsonError(404, "Conversation not found");
                    return;
                }
                conversationId = existing.Id;
            }
            else
            {
                conversationId = Guid.NewGuid();
                var convo = new ChatConversation
                {
                    Id = conversationId,
                    UserId = userId,
                    Title = TitleFromMessage(req.Message),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                try { await convoRepo.CreateAsync(convo); }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to create chat conversation {Id}", conversationId); }
            }

            // ── build the working message list: system prompt + loaded history + new user turn ──
            var convoMessages = new List<ChatToolMessage>();
            if (!string.IsNullOrWhiteSpace(req.SystemPrompt))
                convoMessages.Add(new ChatToolMessage { Role = "system", Content = req.SystemPrompt });

            // Phase 5: load this conversation's attachments once (already user-scoped via the
            // conversation lookup) so prior vision images can be re-included in the egress request
            // (multi-turn vision). user-upload attachments become image_url parts on their owning
            // user message; generated-image attachments are display-only and not re-sent.
            Dictionary<Guid, List<ChatAttachment>> attachmentsByMessage = new();
            try
            {
                var allAttachments = await attachRepo.ListByConversationAsync(conversationId);
                foreach (var a in allAttachments)
                {
                    if (!attachmentsByMessage.TryGetValue(a.MessageId, out var list))
                        attachmentsByMessage[a.MessageId] = list = new();
                    list.Add(a);
                }
            }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to load chat attachments for {Id}", conversationId); }

            try
            {
                var history = await msgRepo.ListByConversationAsync(conversationId);
                foreach (var row in history)
                {
                    var m = new ChatToolMessage { Role = row.Role, Content = row.ContentText };
                    if (!string.IsNullOrEmpty(row.ToolCallsJson))
                        m.ToolCalls = JsonSerializer.Deserialize<List<ChatToolCall>>(row.ToolCallsJson, SseJsonOpts);
                    if (!string.IsNullOrEmpty(row.ToolCallId))
                        m.ToolCallId = row.ToolCallId;
                    // Re-attach a prior user-uploaded image as a vision content part so the model
                    // keeps the image in context across turns. The egress resize keeps the payload
                    // bounded; generated-image attachments are skipped here (display-only).
                    if (row.Role == "user"
                        && attachmentsByMessage.TryGetValue(row.Id, out var atts))
                    {
                        var img = atts.FirstOrDefault(a => a.Kind == ChatAttachmentKind.UserUpload);
                        var imgBlob = img?.Blob;
                        if (imgBlob is { Length: > 0 })
                            m.ImageDataUrl = BuildVisionDataUrl(imgBlob, img!.Mime);
                    }
                    convoMessages.Add(m);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load chat history for {Id}", conversationId);
            }

            // The new user message: in-memory + persisted now so the transcript is durable even if
            // the assistant turn fails (the user can retry on the same conversation).
            var userText = req.Message;
            var newUserMessage = new ChatMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = conversationId,
                Role = "user",
                ContentText = userText,
                CreatedAt = DateTime.UtcNow
            };
            var newUserTurn = new ChatToolMessage { Role = "user", Content = userText };
            try
            {
                await msgRepo.CreateAsync(newUserMessage);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to persist chat user message"); }

            // Phase 5 (vision): persist the validated attachment linked to the new user message and
            // arm the egress image part. Stored as the ORIGINAL (validated) bytes so reopening the
            // conversation renders a faithful image; the egress resize happens in BuildVisionDataUrl.
            if (attachmentBytes is not null)
            {
                try
                {
                    await attachRepo.CreateAsync(new ChatAttachment
                    {
                        Id = Guid.NewGuid(),
                        MessageId = newUserMessage.Id,
                        Kind = ChatAttachmentKind.UserUpload,
                        Mime = attachmentMime,
                        Blob = attachmentBytes,
                        CreatedAt = DateTime.UtcNow
                    });
                }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to persist chat attachment"); }
                newUserTurn.ImageDataUrl = BuildVisionDataUrl(attachmentBytes, attachmentMime);
            }
            convoMessages.Add(newUserTurn);

            // ── commit to the SSE response ──
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            async Task Sse(string eventType, object data)
            {
                await ctx.Response.WriteAsync($"event: {eventType}\n", ct);
                await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(data, SseJsonOpts)}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }

            // Send the conversation id first so the UI can pin a brand-new conversation immediately
            // (and refresh the history sidebar) before any text arrives.
            await Sse("conversation", new { conversationId });

            // Phase 5 (image-gen): image-generation models are served through the SAME pinned
            // chat/completions endpoint but NON-streaming and WITHOUT the tool loop (a plain
            // "generate an image of X" message routed to the model). The generated image(s) are
            // materialized to bytes (decode data: URL, or fetch an http URL server-side), stored as
            // chat_attachment (kind=generated-image) on an assistant message, and rendered inline by
            // the UI via the CSP-allowed /api/chat/attachments/{id} route. See plan §2 Phase 5.
            if (category == "image-gen")
            {
                try
                {
                    await RunImageGenAsync(ctx, repo, openRouter, msgRepo, convoRepo, attachRepo,
                        logger, keys, req.Model, conversationId, convoMessages, ct, Sse);
                }
                finally
                {
                    keys = null!;
                }
                return;
            }

            // Phase 3: the streaming tool loop is shared with the confirm endpoint (it resumes the
            // SAME loop after a human approves/denies a write tool call). The loop owns its own
            // cancellation / egress-error handling; this wrapper only ensures the transient plaintext
            // keys are dropped once the turn is fully done. Phase 4: the loop carries ALL available keys
            // and fails over per-iteration (before the first byte — see StreamWithFailoverAsync).
            try
            {
                await RunToolLoopAsync(new ChatLoopContext(
                    Ctx: ctx, OpenRouter: openRouter, Dispatcher: dispatcher, Repo: repo,
                    MsgRepo: msgRepo, ConvoRepo: convoRepo, Logger: logger, Keys: keys,
                    Model: req.Model, ConversationId: conversationId,
                    ConvoMessages: convoMessages, Ct: ct, Sse: Sse));
            }
            finally
            {
                keys = null!;
            }
        });

        // ── Phase 3: human-in-the-loop confirm gate ──────────────────────────────────
        //
        // Resumes a turn that paused on a write tool call (the /stream loop emitted confirm_required
        // and returned without executing the write). The client posts {toolCallId, allow, model,
        // systemPrompt}; this endpoint:
        //   1. resolves the pending WRITE tool call from the persisted transcript (by toolCallId),
        //      rejecting non-write ids / already-resolved ids / foreign conversations;
        //   2. on allow=true  → executes the write (under the ambient CallerScope; destructive-op
        //      cap enforced here) and feeds its result back to the model;
        //      on allow=false → feeds back {"error":"User denied this action."} so the model can adapt;
        //   3. streams the CONTINUATION as a fresh SSE response by re-entering the SAME tool loop the
        //      /stream endpoint uses (RunToolLoopAsync). The UI appends the continuation to the
        //      existing assistant bubble.
        // Open to any authenticated user; ownership is enforced by the user-scoped conversation
        // lookup (a foreign id → 404). The vault must be unlocked (writes need the master DEK).
        group.MapPost("/stream/{conversationId:guid}/confirm", async (Guid conversationId, HttpContext ctx,
            ChatSettingsRepository repo, OpenRouterClient openRouter, ChatToolDispatcher dispatcher,
            SessionService session, ChatConversationRepository convoRepo, ChatMessageRepository msgRepo,
            ChatDestructiveOpCounter destructiveCounter, ILogger<Program> logger) =>
        {
            ChatConfirmRequest? req;
            try { req = await ctx.Request.ReadFromJsonAsync<ChatConfirmRequest>(SseJsonOpts); }
            catch { req = null; }

            async Task JsonError(int status, string message)
            {
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new ErrorResponse(message), SseJsonOpts));
            }

            if (req is null || string.IsNullOrWhiteSpace(req.ToolCallId))
            {
                await JsonError(400, "toolCallId is required");
                return;
            }
            // Executing a write needs the master DEK; resuming the completion needs the OpenRouter key.
            if (!session.IsUnlocked)
            {
                await JsonError(409, "Vault is locked");
                return;
            }

            // Ownership: a foreign conversation id yields 404, never a leak (mirrors /stream).
            var identity = CallerIdentity.Extract(ctx);
            if (identity.UserId is null)
            {
                await JsonError(401, "Unauthorized");
                return;
            }
            int userId = identity.UserId.Value;
            var convo = await convoRepo.GetByIdForUserAsync(conversationId, userId);
            if (convo is null)
            {
                await JsonError(404, "Conversation not found");
                return;
            }

            // Resolve the resume model: client-supplied, else the model that emitted the pending call
            // (stored on the assistant message), else 400. chat_message.model is populated on every
            // assistant turn, so the fallback is the normal path.
            List<ChatMessage> history;
            try { history = await msgRepo.ListByConversationAsync(conversationId); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load chat history for {Id}", conversationId);
                await JsonError(500, "Failed to load conversation");
                return;
            }

            string? resumeModel = string.IsNullOrWhiteSpace(req.Model)
                ? history.LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Model))?.Model
                : req.Model;
            if (string.IsNullOrWhiteSpace(resumeModel))
            {
                await JsonError(400, "model is required");
                return;
            }
            // Reject a resume model (client-supplied OR persisted fallback) that isn't in the
            // admin-curated enabled catalogue (plan §1; mirrors /stream + /message).
            if (!await repo.IsModelEnabledAsync(resumeModel))
            {
                await JsonError(400, "model not in the enabled catalogue");
                return;
            }

            // Find the pending WRITE tool call by id (the source of truth for name+args — never trust
            // the client). The pending call lives on an assistant message's tool_calls_json.
            ResolvedToolCall? pending = null;
            foreach (var m in history)
            {
                if (m.Role != "assistant" || string.IsNullOrEmpty(m.ToolCallsJson)) continue;
                List<ChatToolCall>? calls;
                try { calls = JsonSerializer.Deserialize<List<ChatToolCall>>(m.ToolCallsJson, SseJsonOpts); }
                catch { continue; }
                if (calls is null) continue;
                var match = calls.FirstOrDefault(c => c.Id == req.ToolCallId && c.Function?.Name != null);
                if (match != null)
                {
                    pending = new ResolvedToolCall(match.Id, match.Function!.Name!, match.Function.Arguments ?? "{}");
                    break;
                }
            }
            if (pending is null)
            {
                await JsonError(404, "Pending tool call not found in this conversation.");
                return;
            }
            if (!ChatToolDispatcher.IsWriteTool(pending.Name))
            {
                await JsonError(400, "Only write tool calls require confirmation.");
                return;
            }
            // Idempotency: a tool result for this call already exists → already resolved (double-click).
            if (history.Any(m => m.Role == "tool" && m.ToolCallId == req.ToolCallId))
            {
                await JsonError(409, "This tool call has already been resolved.");
                return;
            }

            // In-flight guard (Phase 3): prevents two concurrent /confirm requests for the SAME tool
            // call from both executing the write (two tabs / a rapid double-click that races the
            // idempotency check above — both load history before either persists a tool result). The
            // TryAdd sits immediately after that check (no await between them) so a concurrent request
            // that also passed idempotency cannot slip past: it either sees this entry (rejected) or
            // arrives after the tool result is durable and is caught by the idempotency check on its
            // own history load. Removed in the finally below, covering the whole processing window.
            var inFlightKey = conversationId + ":" + req.ToolCallId;
            if (!_inFlightConfirms.TryAdd(inFlightKey, 0))
            {
                await JsonError(409, "This tool call is already being processed.");
                return;
            }
            try
            {
                var keys = await DecryptAvailableKeysAsync(repo, session);
                if (keys.Count == 0)
                {
                    await JsonError(409, "No chat API key is available (none configured, or all are disabled/cooling down).");
                    return;
                }
                var ct = ctx.RequestAborted;

                // Build the working message list (optional system prompt + loaded history). The pending
                // assistant tool-call turn is already in history; the tool result is appended below so the
                // model sees the full turn (request + result) and continues.
                var convoMessages = new List<ChatToolMessage>();
                if (!string.IsNullOrWhiteSpace(req.SystemPrompt))
                    convoMessages.Add(new ChatToolMessage { Role = "system", Content = req.SystemPrompt });
                foreach (var row in history)
                {
                    var mm = new ChatToolMessage { Role = row.Role, Content = row.ContentText };
                    if (!string.IsNullOrEmpty(row.ToolCallsJson))
                        mm.ToolCalls = JsonSerializer.Deserialize<List<ChatToolCall>>(row.ToolCallsJson, SseJsonOpts);
                    if (!string.IsNullOrEmpty(row.ToolCallId))
                        mm.ToolCallId = row.ToolCallId;
                    convoMessages.Add(mm);
                }

                // Compute the tool result for the resolved call (graceful JSON in every case — never throws).
                string toolResultJson;
                bool toolOk = true;
                string? toolError = null;

                if (!req.Allow)
                {
                    toolResultJson = "{\"error\":\"User denied this action.\"}";
                }
                else
                {
                    // Atomic destructive-op cap reservation (plan §2 Phase 3). TryReserve checks AND
                    // increments in one CAS step so two concurrent Allows (e.g. two tabs each with its
                    // OWN pending destructive call — a race the per-call in-flight guard above does NOT
                    // cover) cannot both pass the cap. The reserved slot is released below when the op
                    // does not actually execute (parse failure / error result), keeping the count on
                    // EXECUTED destructive ops.
                    bool reservedDestructive = ChatToolDispatcher.IsDestructiveTool(pending.Name)
                        && destructiveCounter.TryReserve(conversationId);
                    bool capRefused = ChatToolDispatcher.IsDestructiveTool(pending.Name) && !reservedDestructive;

                    if (capRefused)
                    {
                        toolResultJson = JsonSerializer.Serialize(new
                        {
                            error = $"Destructive operation cap reached for this conversation (max {destructiveCounter.Cap} {pending.Name} calls). Start a new conversation to continue."
                        }, SseJsonOpts);
                    }
                    else
                    {
                        JsonElement execArgs;
                        bool parsed;
                        try
                        {
                            var raw = string.IsNullOrWhiteSpace(pending.ArgumentsJson) ? "{}" : pending.ArgumentsJson;
                            execArgs = JsonDocument.Parse(raw).RootElement.Clone();
                            parsed = true;
                        }
                        catch (JsonException)
                        {
                            execArgs = default;
                            parsed = false;
                        }

                        if (!parsed)
                        {
                            toolResultJson = "{\"error\":\"malformed arguments JSON\"}";
                            // Parse failure means the op never ran — release the reserved destructive slot.
                            if (reservedDestructive) destructiveCounter.Release(conversationId);
                        }
                        else
                        {
                            // The human's Allow click satisfies bee_delete_article's confirm=true two-step
                            // requirement (defense-in-depth; avoids double-prompting the user).
                            if (pending.Name == "bee_delete_article")
                                execArgs = EnsureConfirmTrue(execArgs);

                            // Confirm-gate marker: InvokeAsync refuses writes without it (so the non-streaming
                            // /message loop can't execute ungated writes). Set only for this one approved call.
                            ctx.Items[ChatToolDispatcher.ChatWriteExecItemsKey] = true;
                            try
                            {
                                var result = await dispatcher.InvokeAsync(pending.Name, execArgs, ctx);
                                toolResultJson = result.Json;
                                toolOk = result.Ok;
                                toolError = result.Error;
                                // Release the reserved slot when the op did not actually execute (error
                                // result) so the count reflects EXECUTED destructive ops (plan §2 Phase 3).
                                if (reservedDestructive && (!result.Ok || toolResultJson.Contains("\"error\"")))
                                    destructiveCounter.Release(conversationId);
                            }
                            finally
                            {
                                ctx.Items.Remove(ChatToolDispatcher.ChatWriteExecItemsKey);
                            }
                        }
                    }
                }

                // Append + persist the tool result, then resume the loop.
                convoMessages.Add(new ChatToolMessage { Role = "tool", ToolCallId = req.ToolCallId, Content = toolResultJson });
                await SafePersistToolMessage(msgRepo, logger, conversationId, req.ToolCallId, toolResultJson);

                // Commit to SSE and resume — the continuation streams to the UI, appended to the same bubble.
                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.Headers["Cache-Control"] = "no-cache";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";

                async Task Sse(string eventType, object data)
                {
                    await ctx.Response.WriteAsync($"event: {eventType}\n", ct);
                    await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(data, SseJsonOpts)}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }

                await Sse("confirm_resolved", new { toolCallId = req.ToolCallId, allowed = req.Allow, ok = toolOk, error = toolError });

                try
                {
                    await RunToolLoopAsync(new ChatLoopContext(
                        Ctx: ctx, OpenRouter: openRouter, Dispatcher: dispatcher, Repo: repo,
                        MsgRepo: msgRepo, ConvoRepo: convoRepo, Logger: logger, Keys: keys,
                        Model: resumeModel!, ConversationId: conversationId,
                        ConvoMessages: convoMessages, Ct: ct, Sse: Sse));
                }
                catch (OperationCanceledException) { /* client gone */ }
                finally
                {
                    keys = null!;
                }
            }
            finally
            {
                // Release the in-flight guard now that the tool result is durable (the idempotency
                // check catches any later retry on its own history load).
                _inFlightConfirms.TryRemove(inFlightKey, out _);
            }
        });

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

        // Load a conversation's transcript (messages oldest-first). Ownership is re-checked by the
        // user-scoped repo lookup; a foreign conversation id yields 404, never a leak. Phase 5: each
        // message carries its attachment refs (user-upload on a user turn, generated-image on an
        // assistant turn) so the UI renders inline images when reopening a conversation. The blobs
        // are NOT inlined here — the UI fetches them via GET /attachments/{id} (ownership-checked).
        group.MapGet("/conversations/{id:guid}/messages", async (Guid id, HttpContext ctx,
            ChatConversationRepository convoRepo, ChatMessageRepository msgRepo,
            ChatAttachmentRepository attachRepo) =>
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

            return Results.Ok(msgs.Select(m => new ChatMessageRowResponse(
                m.Id, m.Role, m.ContentText, m.ToolCallsJson, m.ToolCallId, m.Model, m.CreatedAt,
                byMessage.TryGetValue(m.Id, out var atts)
                    ? atts.Select(a => new ChatAttachmentRef(a.Id, a.Kind, a.Mime)).ToList()
                    : null)));
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

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static (byte[] cipher, byte[] iv) EncryptKey(string secret, SessionService session)
    {
        var masterDek = session.GetMasterDek();
        try
        {
            return ArticleEncryptor.Encrypt(secret, masterDek, KeyAad);
        }
        finally
        {
            Array.Clear(masterDek);
        }
    }

    private static string DecryptKey(byte[] ciphertext, byte[] iv, SessionService session)
    {
        var masterDek = session.GetMasterDek();
        try
        {
            return ArticleEncryptor.Decrypt(ciphertext, iv, masterDek, KeyAad);
        }
        finally
        {
            Array.Clear(masterDek);
        }
    }

    // ── Phase 4: multi-key failover ────────────────────────────────────────────
    //
    // The chat egress path no longer pins one "highest-priority enabled key". Instead it decrypts
    // every AVAILABLE key (enabled AND not currently in a cooldown window) and tries them in priority
    // order. A key-specific HTTP failure triggers a per-key circuit breaker; a transient failure just
    // advances to the next key. When every key is exhausted, a clear AllKeysExhaustedException bubbles
    // up (→ 502 JSON for the non-streaming endpoints, → an `event: error` SSE frame for the streaming
    // loop). Plan §2 Phase 4: "per-key circuit breaker (401→session-disable, 402/429→cooldown,
    // 5xx→retry-next); structured event: error when exhausted."

    /// <summary>A decrypted egress key held transiently for one chat turn. The plaintext lives only in
    /// memory for the duration of the failover attempt(s); the caller nulls the list reference when the
    /// turn ends (same posture as the prior single-key path — string memory is GC-managed).</summary>
    private sealed record KeyMaterial(Guid Id, string PlaintextKey);

    /// <summary>Every available key was tried and failed. Surfaced as a 502 (JSON endpoints) or an
    /// <c>event: error</c> SSE frame (streaming loop). Deliberately NOT derived from
    /// <see cref="InvalidOperationException"/> so it is distinguishable from a malformed-but-200
    /// upstream response (which has nothing to do with a bad key and must not be failovered).</summary>
    private sealed class AllKeysExhaustedException : Exception
    {
        public AllKeysExhaustedException(string message) : base(message) { }
    }

    private enum KeyFailureKind { Disable, Cooldown, Transient }

    // Cooldown applied on 402 (insufficient credits) / 429 (rate limit): the key is retried
    // automatically once this elapses (ListAvailableOrderedAsync re-admits it once disabled_until
    // is past). 401 (unauthorized/revoked) disables the row until an admin re-enables it.
    private static readonly TimeSpan KeyCooldownWindow = TimeSpan.FromMinutes(5);

    private static KeyFailureKind ClassifyKeyFailure(int statusCode) => statusCode switch
    {
        401 => KeyFailureKind.Disable,
        402 or 429 => KeyFailureKind.Cooldown,
        _ => KeyFailureKind.Transient
    };

    private static async Task RecordKeyOutcomeAsync(
        ChatSettingsRepository repo, Guid keyId, KeyFailureKind kind, string lastError)
    {
        switch (kind)
        {
            case KeyFailureKind.Disable:
                await repo.RecordKeyFailureAsync(keyId, disable: true, disabledUntil: null, lastError);
                break;
            case KeyFailureKind.Cooldown:
                await repo.RecordKeyFailureAsync(keyId, disable: false,
                    disabledUntil: DateTime.UtcNow + KeyCooldownWindow, lastError);
                break;
            default:
                await repo.RecordKeyFailureAsync(keyId, disable: false, disabledUntil: null, lastError);
                break;
        }
    }

    /// <summary>Decrypts every key eligible for egress right now (enabled, not cooling down), in
    /// priority order. An undecryptable key (e.g. after a DEK rotation) is skipped with a recorded
    /// note rather than failing the whole turn — it simply won't be tried. Returns an empty list only
    /// if nothing is available, which the caller maps to a clear "no key / all cooling down" error.
    /// Decryption needs the master DEK, so callers gate on <c>session.IsUnlocked</c> first.</summary>
    private static async Task<List<KeyMaterial>> DecryptAvailableKeysAsync(
        ChatSettingsRepository repo, SessionService session)
    {
        var rows = await repo.ListAvailableOrderedAsync();
        var result = new List<KeyMaterial>(rows.Count);
        foreach (var k in rows)
        {
            try
            {
                result.Add(new KeyMaterial(k.Id, DecryptKey(k.Ciphertext, k.Iv, session)));
            }
            catch (CryptographicException)
            {
                // Skip — this key can't be used until the vault/DEK situation is sorted. Note it for admin.
                await repo.RecordUsageAsync(k.Id, "decrypt failed");
            }
        }
        return result;
    }

    /// <summary>Runs <paramref name="attempt"/> against the ordered key list, retrying on a
    /// key-specific HTTP failure (401/402/429 → disable/cooldown) or a transient transport error
    /// (<see cref="HttpRequestException"/> → retry-next, no cooldown). Records success on the winning
    /// key. Throws <see cref="AllKeysExhaustedException"/> if every key failed. Used by the
    /// non-streaming endpoints; the streaming path has its own (<see cref="StreamWithFailoverAsync"/>)
    /// that additionally enforces "failover only before the first byte".</summary>
    private static async Task<T> RunWithFailoverAsync<T>(
        ChatSettingsRepository repo, IReadOnlyList<KeyMaterial> keys,
        Func<string, CancellationToken, Task<T>> attempt, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await attempt(key.PlaintextKey, ct);
                await repo.RecordKeySuccessAsync(key.Id);
                return result;
            }
            catch (OpenRouterHttpException ex)
            {
                await RecordKeyOutcomeAsync(repo, key.Id, ClassifyKeyFailure(ex.StatusCode), ex.Message);
                last = ex;
                continue;
            }
            catch (HttpRequestException ex)
            {
                // Transport/timeout — not the key's fault: try the next key, leave this one available.
                await repo.RecordUsageAsync(key.Id, ex.Message);
                last = ex;
                continue;
            }
        }
        throw new AllKeysExhaustedException(last?.Message ?? "All configured API keys failed.");
    }

    // Short display fragment (never the full secret). Mirrors AgentKeyHelper.GetKeyPrefix length.
    private static string ComputeKeyPrefix(string apiKey)
        => apiKey.Length > 12 ? string.Concat(apiKey.AsSpan(0, 12), "…") : apiKey;

    private static ChatModelResponse ToModelResponse(ChatModelRow m) => new(
        m.Id, m.ModelId, m.Label, m.Category, m.DefaultForCategory, m.Enabled);

    // ── Phase 5 helpers (vision + image generation) ────────────────────────────

    // MIME allow-list for chat image attachments (plan §2 Phase 5). Validated server-side as well
    // as client-side — never trust the client alone. GIF is accepted even though vision models see
    // only the first frame.
    private static readonly HashSet<string> AllowedImageMimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/webp", "image/gif"
    };

    // Raw upload size cap (plan §2 Phase 5: "a reasonable max size, e.g. 8MB raw upload"). Enforced
    // server-side regardless of any client-side resize.
    private const long MaxAttachmentBytes = 8L * 1024 * 1024;

    // OpenAI-recommended longest-side cap for vision inputs. Applied server-side when building the
    // egress data URL (the stored attachment keeps the original bytes for faithful display).
    private const int VisionMaxDimension = 1568;

    /// <summary>Decodes + validates an inline image attachment. Server-side MIME allow-list, size
    /// cap, AND a magic-byte check against the claimed MIME (catches a spoofed content-type). Returns
    /// the validated bytes + normalized MIME, or an error message. Never throws.</summary>
    private static (byte[]? Bytes, string Mime, string? Error) ValidateAttachment(ChatStreamAttachment att)
    {
        if (string.IsNullOrWhiteSpace(att.Mime) || !AllowedImageMimes.Contains(att.Mime))
            return (null, "", "Unsupported image type. Allowed: PNG, JPEG, WebP, GIF.");

        if (string.IsNullOrWhiteSpace(att.DataBase64))
            return (null, "", "Empty image data.");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(att.DataBase64); }
        catch { return (null, "", "Image data is not valid base64."); }

        if (bytes.Length == 0)
            return (null, "", "Empty image data.");
        if (bytes.Length > MaxAttachmentBytes)
            return (null, "", $"Image is too large ({bytes.Length / (1024 * 1024.0):F1} MB); the limit is {MaxAttachmentBytes / (1024 * 1024)} MB.");

        if (!MatchesMagicBytes(bytes, att.Mime))
            return (null, "", "Image bytes do not match the declared type.");

        // Normalize the MIME to the canonical lowercase form from the allow-list.
        var mime = AllowedImageMimes.First(m => m.Equals(att.Mime, StringComparison.OrdinalIgnoreCase));
        return (bytes, mime, null);
    }

    private static bool MatchesMagicBytes(byte[] b, string mime) => mime switch
    {
        "image/png" => b.Length >= 8
            && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47
            && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A,
        "image/jpeg" => b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF,
        "image/gif" => b.Length >= 4 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38,
        "image/webp" => b.Length >= 12
            && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 // "RIFF"
            && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50, // "WEBP"
        _ => false
    };

    /// <summary>Builds the egress vision data URL for an image, downscaling to
    /// <see cref="VisionMaxDimension"/> on the longest side and re-encoding as JPEG q85 to keep the
    /// OpenRouter payload reasonable (plan §2 Phase 5: "a simple max-dimension resize is enough").
    /// Reuses ImageSharp (already a Core dependency). Falls back to the original bytes (as a data
    /// URL) if ImageSharp cannot load/encode them.</summary>
    private static string BuildVisionDataUrl(byte[] blob, string mime)
    {
        try
        {
            using var image = Image.Load(blob);
            var w = image.Width;
            var h = image.Height;
            if (w > VisionMaxDimension || h > VisionMaxDimension)
            {
                var scale = (double)VisionMaxDimension / Math.Max(w, h);
                w = Math.Max(1, (int)Math.Round(w * scale));
                h = Math.Max(1, (int)Math.Round(h * scale));
                image.Mutate(ctx => ctx.Resize(w, h));
            }
            using var ms = new MemoryStream();
            image.SaveAsJpeg(ms, new JpegEncoder { Quality = 85 });
            return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch
        {
            return "data:" + mime + ";base64," + Convert.ToBase64String(blob);
        }
    }

    /// <summary>Phase 5: image-generation turn (non-streaming, no tool loop). Runs
    /// <see cref="OpenRouterClient.GenerateImageAsync"/> through multi-key failover, materializes
    /// each returned image to bytes, persists it as a generated-image attachment on an assistant
    /// message, and emits an <c>image</c> SSE event per image (the UI renders it inline via the
    /// CSP-allowed /attachments/{id} route) followed by <c>done</c>. Image sources are decoded from
    /// <c>data:</c> URLs directly; http(s) URLs are fetched server-side (size/time capped) so the
    /// image renders inline despite the strict <c>img-src 'self' data: blob:</c> CSP.</summary>
    private static async Task RunImageGenAsync(
        HttpContext ctx, ChatSettingsRepository repo, OpenRouterClient openRouter,
        ChatMessageRepository msgRepo, ChatConversationRepository convoRepo,
        ChatAttachmentRepository attachRepo, ILogger logger,
        IReadOnlyList<KeyMaterial> keys, string model, Guid conversationId,
        List<ChatToolMessage> convoMessages, CancellationToken ct,
        Func<string, object, Task> sse)
    {
        try
        {
            // Multi-key failover around the non-streaming image-gen call (same helper as text/vision).
            var result = await RunWithFailoverAsync(repo, keys,
                (pk, token) => openRouter.GenerateImageAsync(pk, model, convoMessages, token),
                ct);

            // Persist an assistant message carrying any caption text + the model id, and link the
            // generated image(s) to it as attachments so they persist with the conversation.
            var assistantMessageId = Guid.NewGuid();
            try
            {
                await msgRepo.CreateAsync(new ChatMessage
                {
                    Id = assistantMessageId,
                    ConversationId = conversationId,
                    Role = "assistant",
                    ContentText = result.Text,
                    Model = result.Model,
                    CreatedAt = DateTime.UtcNow
                });
                await convoRepo.TouchAsync(conversationId);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to persist image-gen assistant message"); }

            // Materialize each image source → bytes, store, emit. data: URLs decode directly; http(s)
            // URLs are fetched (the CSP blocks external img-src, so the bytes must come from 'self').
            var rendered = new List<ChatImageEvent>();
            foreach (var source in result.ImageSources)
            {
                var (imgBytes, imgMime) = await ResolveImageSourceToBytesAsync(source, ct);
                if (imgBytes is null || imgBytes.Length == 0) continue;

                var attachmentId = Guid.NewGuid();
                try
                {
                    await attachRepo.CreateAsync(new ChatAttachment
                    {
                        Id = attachmentId,
                        MessageId = assistantMessageId,
                        Kind = ChatAttachmentKind.GeneratedImage,
                        Mime = imgMime,
                        Blob = imgBytes,
                        CreatedAt = DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to persist generated image attachment");
                    continue;
                }

                // Inline a data: URL for immediate render; the persisted copy lives under /attachments.
                var inlineUrl = "data:" + imgMime + ";base64," + Convert.ToBase64String(imgBytes);
                rendered.Add(new ChatImageEvent(attachmentId, inlineUrl, imgMime));
            }

            foreach (var img in rendered)
                await sse("image", img);

            if (rendered.Count == 0 && string.IsNullOrEmpty(result.Text))
            {
                await sse("done", new { content = "(the model returned no image)", model = result.Model, conversationId, messageId = assistantMessageId });
                return;
            }
            await sse("done", new { content = result.Text ?? "", model = result.Model, conversationId, messageId = assistantMessageId });
        }
        catch (OperationCanceledException) { /* client gone */ }
        catch (AllKeysExhaustedException ex)
        {
            try { await sse("error", new { error = ex.Message }); } catch { }
        }
        catch (OpenRouterHttpException ex)
        {
            try { await sse("error", new { error = ex.Message }); } catch { }
        }
        catch (InvalidOperationException ex)
        {
            // Malformed upstream response (empty body / no choices) — not a key failure.
            try { await sse("error", new { error = ex.Message }); } catch { }
        }
    }

    /// <summary>One generated-image SSE event surfaced to the UI: the attachment id (for the
    /// persistent /attachments/{id} render + the "Save to Bee" action) and an inline data: URL for
    /// immediate rendering.</summary>
    private sealed record ChatImageEvent(
        [property: JsonPropertyName("attachmentId")] Guid AttachmentId,
        [property: JsonPropertyName("dataUrl")] string DataUrl,
        [property: JsonPropertyName("mime")] string Mime);

    /// <summary>Materializes an image source returned by an image-gen model to raw bytes + a MIME.
    /// Handles three shapes: <c>data:&lt;mime&gt;;base64,&lt;…&gt;</c> (decode directly), a bare
    /// base64 string (decode, assume image/png), and an http(s) URL (fetch server-side, size/time
    /// capped, so it renders inline under the strict <c>img-src</c> CSP). Returns null on failure.</summary>
    private static async Task<(byte[]? Bytes, string Mime)> ResolveImageSourceToBytesAsync(string source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source))
            return (null, "image/png");

        var span = source.AsSpan().Trim();
        if (span.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            // data:<mime>;base64,<payload>
            try
            {
                var comma = source.IndexOf(',');
                if (comma < 0) return (null, "image/png");
                var header = source.Substring(5, comma - 5); // after "data:"
                var mime = "image/png";
                var semi = header.IndexOf(';');
                if (semi > 0) mime = header[..semi];
                var payload = source[(comma + 1)..];
                return (Convert.FromBase64String(payload), mime);
            }
            catch { return (null, "image/png"); }
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            try
            {
                // SSRF guard lives entirely in ImageFetchClient's ConnectCallback: it resolves the
                // host ONCE, rejects loopback / RFC1918 / IPv6 ULA / link-local (incl. the
                // 169.254.169.254 cloud-metadata endpoint) / multicast / unspecified addresses, and
                // connects to the validated IP — so a malicious/compromised model cannot reach
                // internal services or the app's own ports, cannot bypass the check via a redirect
                // (AllowAutoRedirect=false), and cannot win a DNS-rebinding race (the validated
                // address IS the connected address). A disallowed host throws here and the catch
                // below maps it to a null result.
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                using var resp = await ImageFetchClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!resp.IsSuccessStatusCode) return (null, "image/png");
                // Hard cap the fetched bytes so a malicious/buggy URL can't exhaust memory.
                var declared = resp.Content.Headers.ContentLength ?? long.MaxValue;
                if (declared > 20L * 1024 * 1024) return (null, "image/png");
                using var ms = new MemoryStream();
                using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
                var buf = new byte[8192];
                int n;
                while ((n = await stream.ReadAsync(buf.AsMemory(), cts.Token)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (ms.Length > 20L * 1024 * 1024) return (null, "image/png");
                }
                var mime = resp.Content.Headers.ContentType?.MediaType ?? "image/png";
                return (ms.ToArray(), mime);
            }
            catch { return (null, "image/png"); }
        }

        // Bare base64 (e.g. a b64_json value that slipped through without a data: prefix).
        try { return (Convert.FromBase64String(source), "image/png"); }
        catch { return (null, "image/png"); }
    }

    /// <summary>True for loopback / private / link-local / multicast / broadcast / unspecified
    /// destinations — i.e. addresses the image fetch must never reach. Handles IPv4 and IPv6
    /// (including IPv4-mapped IPv6). Unknown address families are rejected (defense-in-depth).</summary>
    private static bool IsPrivateOrLoopbackAddress(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6)
            a = a.MapToIPv4();

        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            if (b[0] == 127) return true;                          // loopback 127.0.0.0/8
            if (b[0] == 10) return true;                           // private 10.0.0.0/8
            if (b[0] == 172 && (b[1] & 0xF0) == 0x10) return true; // private 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true;           // private 192.168.0.0/16
            if (b[0] == 169 && b[1] == 254) return true;           // link-local 169.254.0.0/16 (incl. cloud metadata)
            if (b[0] == 0) return true;                            // "this network"/unspecified 0.0.0.0/8
            if (b[0] >= 224) return true;                          // multicast 224.0.0.0/4 + broadcast 255.255.255.255
            return false;
        }
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(a)) return true;  // ::1
            if (a.IsIPv6LinkLocal) return true;        // fe80::/10
            if (a.IsIPv6SiteLocal) return true;        // fec0::/10 (deprecated, still block)
            var b6 = a.GetAddressBytes();
            if (b6.Length == 16)
            {
                if ((b6[0] & 0xFE) == 0xFC) return true; // ULA fc00::/7 (RFC4193, the IPv6 RFC1918 analogue)
                var allZero = true;
                for (int i = 0; i < 16; i++) { if (b6[i] != 0) { allZero = false; break; } }
                if (allZero) return true; // IPv6 unspecified "::"
            }
            return false;
        }
        return true; // unknown family → reject
    }

    // Dedicated HttpClient for fetching model-returned image URLs (image-gen). Reused across calls
    // (recommended HttpClient usage). NOT the pinned OpenRouter egress client — these are image CDN
    // URLs returned by OpenRouter/providers for a user-requested generation, fetched server-side so
    // they render inline under the strict img-src CSP. See ResolveImageSourceToBytesAsync.
    //
    // SSRF HARDENING (the handler does BOTH jobs):
    //  - AllowAutoRedirect = false → a public host that passes the address check cannot 302 to an
    //    internal address; any 3xx is a non-success status and ResolveImageSourceToBytesAsync
    //    rejects it (closes the redirect-bypass).
    //  - ConnectCallback → resolves the host to IP(s) ONCE, validates EVERY resolved address via
    //    IsPrivateOrLoopbackAddress, and connects DIRECTLY to a validated IP. The address that was
    //    validated is therefore the EXACT address connected to — closing the DNS-rebinding TOCTOU
    //    a separate "check-then-connect" pair would leave open.
    private static readonly HttpClient ImageFetchClient = BuildImageFetchClient();

    private static HttpClient BuildImageFetchClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (ctx, ct) =>
            {
                var host = ctx.DnsEndPoint.Host;
                var port = ctx.DnsEndPoint.Port;

                IPAddress[] addrs;
                if (IPAddress.TryParse(host, out var literal))
                    addrs = new[] { literal };
                else
                {
                    try { addrs = await Dns.GetHostAddressesAsync(host, ct); }
                    catch { throw new HttpRequestException($"Unable to resolve image host '{host}'."); }
                }

                // Reject the whole request if ANY resolved address is disallowed (loopback /
                // RFC1918 / IPv6 ULA / link-local incl. cloud metadata / multicast / unspecified).
                IPAddress? chosen = null;
                foreach (var a in addrs)
                {
                    if (IsPrivateOrLoopbackAddress(a))
                        throw new HttpRequestException($"Refusing to fetch an image from a private/loopback address ({a}).");
                    chosen ??= a;
                }
                if (chosen is null)
                    throw new HttpRequestException($"Image host '{host}' resolved to no usable address.");

                // Connect to the validated IP directly (not a DnsEndPoint, which would re-resolve
                // and re-open the rebinding window). ownsSocket:true so disposing this stream
                // disposes the socket.
                var socket = new Socket(chosen.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(chosen, port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
    }

    // ── Phase 2 helpers ─────────────────────────────────────────────────────────

    // camelCase + relaxed-encoder JSON for the hand-written SSE payloads (JS-idiomatic on the wire,
    // and never double-escapes non-ASCII). Reused by the /stream frames and the conversation DTOs
    // that are read back with ReadFromJsonAsync in PATCH/POST handlers.
    private static readonly JsonSerializerOptions SseJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // Friendly in-bubble label for a tool call so the UI can show "Searching your vault…" instead of
    // the raw function name. Falls back to a generic "Working…" for anything unexpected.
    private static string ToolLabel(string name) => name switch
    {
        "bee_search" => "Searching your vault…",
        "bee_search_content" => "Reading article bodies…",
        "bee_list_articles" => "Listing articles…",
        "bee_get_tree" => "Reading the tree…",
        "bee_get_article" => "Reading an article…",
        "bee_save_article" => "Preparing to create a note…",
        "bee_update_article" => "Preparing to update a note…",
        "bee_append_to_article" => "Preparing to append…",
        "bee_replace_in_article" => "Preparing to replace text…",
        "bee_delete_article" => "Preparing to delete a note…",
        _ => "Working…"
    };

    // First ~40 chars of the first user message → conversation title (plan §2 Phase 2). Truncated on
    // a grapheme-ish boundary (Substring is fine here; titles are display-only).
    private static string TitleFromMessage(string message)
    {
        var trimmed = message.Trim();
        return trimmed.Length <= 40 ? trimmed : trimmed[..40] + "…";
    }

    // Best-effort persistence of a role="tool" result message: never let a chat.db write failure
    // abort the live SSE stream.
    private static async Task SafePersistToolMessage(ChatMessageRepository repo, ILogger logger,
        Guid conversationId, string toolCallId, string content)
    {
        try
        {
            await repo.CreateAsync(new ChatMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = conversationId,
                Role = "tool",
                ContentText = content,
                ToolCallId = toolCallId,
                CreatedAt = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist chat tool message");
        }
    }

    // ── Phase 3: shared streaming tool loop ────────────────────────────────────

    /// <summary>Everything the shared tool loop needs. Both /stream (after appending+persisting the
    /// user message) and /confirm (after appending+persisting the tool result) build one of these and
    /// call <see cref="RunToolLoopAsync"/>. The continuation is streamed through <see cref="Sse"/>.</summary>
    private sealed record ChatLoopContext(
        HttpContext Ctx,
        OpenRouterClient OpenRouter,
        ChatToolDispatcher Dispatcher,
        ChatSettingsRepository Repo,
        ChatMessageRepository MsgRepo,
        ChatConversationRepository ConvoRepo,
        ILogger Logger,
        IReadOnlyList<KeyMaterial> Keys,
        string Model,
        Guid ConversationId,
        List<ChatToolMessage> ConvoMessages,
        CancellationToken Ct,
        Func<string, object, Task> Sse);

    /// <summary>
    /// The streaming tool-call loop, shared by /stream and /confirm. Runs until ONE of:
    /// <list type="bullet">
    /// <item>The model answers with plain text (no tool calls) → persist + emit <c>done</c>.</item>
    /// <item>A WRITE tool call is emitted → emit <c>confirm_required</c> and PAUSE (return). The
    /// confirm endpoint resumes this loop after the user clicks Allow/Deny. Reads in the same batch
    /// before the write have already executed + been persisted.</item>
    /// <item>The per-resume iteration cap is reached → best-effort cap notice + <c>done</c>.</item>
    /// </list>
    /// Owns its own cancellation (no-op on client disconnect) and egress-error handling (error frame),
    /// matching the previous inline /stream behaviour. Per-turn iteration cap is reused from Phase 1/2
    /// (plan §2 Phase 3); a write-paused turn can only resume via a human confirmation, so the cap
    /// resets per-resume and cannot spiral on its own.
    /// </summary>
    private static async Task RunToolLoopAsync(ChatLoopContext lc)
    {
        const int maxIterations = 8;
        try
        {
            for (int iteration = 0; iteration < maxIterations; iteration++)
            {
                lc.Ct.ThrowIfCancellationRequested();

                // Phase 4: the upstream streaming call goes through multi-key failover. It tries each
                // available key in priority order; a pre-first-byte HTTP failure (401/402/429/5xx) is
                // recorded on that key and retried with the next. A failure AFTER a delta has reached
                // the client propagates here (no splicing) and is surfaced as an event:error below.
                var turn = await StreamWithFailoverAsync(lc);

                // No tool calls → terminal answer. Persist + emit done.
                if (turn.ToolCalls is null || turn.ToolCalls.Count == 0)
                {
                    var finalContent = string.IsNullOrEmpty(turn.Content) ? "(no response)" : turn.Content;
                    var finalModel = turn.Model;
                    var messageId = Guid.NewGuid();
                    try
                    {
                        await lc.MsgRepo.CreateAsync(new ChatMessage
                        {
                            Id = messageId,
                            ConversationId = lc.ConversationId,
                            Role = "assistant",
                            ContentText = finalContent,
                            Model = finalModel,
                            CreatedAt = DateTime.UtcNow
                        });
                        await lc.ConvoRepo.TouchAsync(lc.ConversationId);
                    }
                    catch (Exception ex) { lc.Logger.LogWarning(ex, "Failed to persist assistant message"); }

                    await lc.Sse("done", new { content = finalContent, model = finalModel, conversationId = lc.ConversationId, messageId });
                    return;
                }

                // Has tool calls → record the assistant tool-call turn (memory + persistence).
                var assistantToolCalls = turn.ToolCalls.Select(tc => new ChatToolCall
                {
                    Id = tc.Id,
                    Type = "function",
                    Function = new ChatToolCallFunction { Name = tc.Name, Arguments = tc.ArgumentsJson }
                }).ToList();
                var toolCallsJson = JsonSerializer.Serialize(assistantToolCalls, SseJsonOpts);

                lc.ConvoMessages.Add(new ChatToolMessage
                {
                    Role = "assistant",
                    Content = turn.Content,
                    ToolCalls = assistantToolCalls
                });
                try
                {
                    await lc.MsgRepo.CreateAsync(new ChatMessage
                    {
                        Id = Guid.NewGuid(),
                        ConversationId = lc.ConversationId,
                        Role = "assistant",
                        ContentText = string.IsNullOrEmpty(turn.Content) ? null : turn.Content,
                        ToolCallsJson = toolCallsJson,
                        Model = turn.Model,
                        CreatedAt = DateTime.UtcNow
                    });
                }
                catch (Exception ex) { lc.Logger.LogWarning(ex, "Failed to persist assistant tool-call turn"); }

                // Process tool calls in order. Reads execute immediately under the ambient
                // CallerScope; the FIRST write tool call PAUSES the turn behind the confirm gate.
                // Any tool calls after the write in this batch are left for the resumed loop to
                // re-derive (the model re-issues them if still needed) — simplest correct behavior.
                foreach (var tc in turn.ToolCalls)
                {
                    await lc.Sse("tool_call_start", new { tool = tc.Name, callId = tc.Id, label = ToolLabel(tc.Name) });

                    JsonElement args;
                    try
                    {
                        var raw = string.IsNullOrWhiteSpace(tc.ArgumentsJson) ? "{}" : tc.ArgumentsJson;
                        args = JsonDocument.Parse(raw).RootElement.Clone();
                    }
                    catch (JsonException)
                    {
                        var errMsg = "{\"error\":\"malformed arguments JSON\"}";
                        lc.ConvoMessages.Add(new ChatToolMessage { Role = "tool", ToolCallId = tc.Id, Content = errMsg });
                        await SafePersistToolMessage(lc.MsgRepo, lc.Logger, lc.ConversationId, tc.Id, errMsg);
                        await lc.Sse("tool_call_result", new { tool = tc.Name, callId = tc.Id, ok = false, durationMs = 0, error = "malformed arguments JSON" });
                        continue;
                    }

                    if (ChatToolDispatcher.IsWriteTool(tc.Name))
                    {
                        // CONFIRM GATE: do NOT execute. Emit a short human-readable summary and PAUSE.
                        // The confirm endpoint resolves {allow} and re-enters this loop.
                        var summary = ChatToolDispatcher.SummarizeWriteCall(tc.Name, args);
                        await lc.Sse("confirm_required", new { toolCallId = tc.Id, toolName = tc.Name, argsSummary = summary });
                        return; // turn paused — the SSE stream ends here; /confirm resumes it
                    }

                    // Read tool → execute now.
                    var result = await lc.Dispatcher.InvokeAsync(tc.Name, args, lc.Ctx);
                    await lc.Sse("tool_call_result", new { tool = tc.Name, callId = tc.Id, ok = result.Ok, durationMs = result.DurationMs, error = result.Error });

                    lc.ConvoMessages.Add(new ChatToolMessage { Role = "tool", ToolCallId = tc.Id, Content = result.Json });
                    await SafePersistToolMessage(lc.MsgRepo, lc.Logger, lc.ConversationId, tc.Id, result.Json);
                }
            }

            // Loop cap — best-effort final notice (mirrors /message).
            var capContent = "(reached the maximum number of tool-call rounds without a final answer — try rephrasing)";
            var capMessageId = Guid.NewGuid();
            try
            {
                await lc.MsgRepo.CreateAsync(new ChatMessage
                {
                    Id = capMessageId,
                    ConversationId = lc.ConversationId,
                    Role = "assistant",
                    ContentText = capContent,
                    Model = lc.Model,
                    CreatedAt = DateTime.UtcNow
                });
                await lc.ConvoRepo.TouchAsync(lc.ConversationId);
            }
            catch (Exception ex) { lc.Logger.LogWarning(ex, "Failed to persist cap message"); }
            await lc.Sse("done", new { content = capContent, model = lc.Model, conversationId = lc.ConversationId, messageId = capMessageId });
        }
        catch (OperationCanceledException)
        {
            // Client disconnected mid-stream — the OpenRouter call is already cancelled (the token was
            // forwarded). Nothing to write; the socket is gone. Do NOT rethrow.
        }
        catch (AllKeysExhaustedException ex)
        {
            // Every available key failed before emitting anything → surface as an error frame.
            // (Per-key cooldown/disable was already recorded by StreamWithFailoverAsync.)
            try { await lc.Sse("error", new { error = ex.Message }); }
            catch { /* client gone */ }
        }
        catch (OpenRouterHttpException ex)
        {
            // Mid-stream failure AFTER content already streamed to the client: the failing key's
            // cooldown/error was recorded by StreamWithFailoverAsync; we cannot splice another key's
            // stream in, so surface the error and stop.
            try { await lc.Sse("error", new { error = ex.Message }); }
            catch { /* client gone */ }
        }
        catch (InvalidOperationException ex)
        {
            // Malformed upstream response (empty body / no choices) — not a key failure, no failover.
            try { await lc.Sse("error", new { error = ex.Message }); }
            catch { /* client gone */ }
        }
    }

    /// <summary>STREAMING multi-key failover for ONE iteration of the tool loop. Tries each available
    /// key in priority order; if a key fails BEFORE it has streamed any content delta for this attempt,
    /// the failure is recorded (disable/cooldown/transient) and the SAME request is retried with the
    /// next key (failover before the first byte — plan §2 Phase 4; mid-stream splicing is explicitly
    /// out of scope). If a key fails AFTER a delta has already been forwarded to the client, the
    /// exception propagates to <see cref="RunToolLoopAsync"/> (→ an <c>event: error</c> frame) — we
    /// cannot splice another key's stream into text the user has already seen. Throws
    /// <see cref="AllKeysExhaustedException"/> when no key could even start; returns the assembled turn
    /// otherwise. Records success on the key that produced the stream.</summary>
    private static async Task<ToolCompletionResult> StreamWithFailoverAsync(ChatLoopContext lc)
    {
        var tools = ChatToolDispatcher.ToolDefinitions;
        Exception? last = null;

        foreach (var key in lc.Keys)
        {
            lc.Ct.ThrowIfCancellationRequested();

            // True once ANY delta for THIS attempt has been forwarded to the client. A failure after
            // this point can no longer be retried (the user has seen partial output) → rethrow.
            var emittedThisAttempt = false;
            try
            {
                var turn = await lc.OpenRouter.StreamWithToolsAsync(
                    key.PlaintextKey, lc.Model, lc.ConvoMessages, tools,
                    async (delta, token) =>
                    {
                        emittedThisAttempt = true;
                        await lc.Sse("delta", new { text = delta });
                    },
                    lc.Ct);

                // Stream completed cleanly on this key → record success and hand the turn back to the loop.
                await lc.Repo.RecordKeySuccessAsync(key.Id);
                return turn;
            }
            catch (OpenRouterHttpException ex)
            {
                // Record the circuit-breaker decision regardless, then: if nothing streamed yet, retry
                // the next key; otherwise propagate (no splicing into already-shown output).
                await RecordKeyOutcomeAsync(lc.Repo, key.Id, ClassifyKeyFailure(ex.StatusCode), ex.Message);
                if (!emittedThisAttempt)
                {
                    last = ex;
                    continue; // failover before first byte
                }
                throw; // mid-stream failure → loop emits event:error
            }
            catch (HttpRequestException ex)
            {
                // Transport/timeout — not the key's fault: note it, retry next, no cooldown.
                await lc.Repo.RecordUsageAsync(key.Id, ex.Message);
                if (!emittedThisAttempt)
                {
                    last = ex;
                    continue;
                }
                throw;
            }
        }

        throw new AllKeysExhaustedException(last?.Message ?? "All configured API keys failed.");
    }

    /// <summary>Forces <c>confirm=true</c> on a delete tool call's args. bee_delete_article keeps its
    /// two-step <c>confirm</c> check (warns unless true); the human's Allow click satisfies it, so we
    /// set it here and let the dispatcher's own check pass — defense-in-depth, no double-prompt.</summary>
    private static JsonElement EnsureConfirmTrue(JsonElement args)
    {
        var node = (args.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(args.GetRawText())
            : JsonNode.Parse("{}")) as JsonObject ?? new JsonObject();
        node["confirm"] = true;
        return JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
    }

    /// <summary>Request body for the confirm endpoint. <c>toolCallId</c>+<c>allow</c> are required;
    /// <c>model</c>+<c>systemPrompt</c> let the client forward the same model/instructions used for
    /// the original turn so the resumed completion stays consistent (fall back to the persisted model
    /// if absent). The server resolves name/args from the transcript, never from the client.</summary>
    public record ChatConfirmRequest(
        [property: JsonPropertyName("toolCallId")] string ToolCallId,
        [property: JsonPropertyName("allow")] bool Allow,
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("systemPrompt")] string? SystemPrompt);
}
