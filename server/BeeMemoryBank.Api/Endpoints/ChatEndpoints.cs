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
        // "Am I allowed to use chat?" — ungated by the group-wide ChatAccessEndpointFilter
        // (registered directly on app, NOT on the /api/chat group below) so a blocked user can
        // still ask and get a straight yes/no answer for UI gating. Still behind the internal-key
        // gate (RequireInternalKey). Superadmins and agent callers always pass; everyone else needs
        // BOTH the node-wide toggle AND their own per-user flag.
        app.MapGet("/api/chat/access", async (HttpContext ctx, IUserRepository userRepo, ChatSettingsRepository chatSettingsRepo) =>
        {
            var caller = CallerIdentity.Extract(ctx);
            if (caller.IsSuperadmin || caller.AgentId.HasValue)
                return Results.Ok(new { allowed = true });

            if (caller.UserId is null)
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 401);

            if (!await chatSettingsRepo.GetChatGloballyEnabledAsync())
                return Results.Ok(new { allowed = false });

            var user = await userRepo.GetByIdAsync(caller.UserId.Value);
            return Results.Ok(new { allowed = user?.ChatAccess ?? false });
        }).RequireInternalKey();

        var group = app.MapGroup("/api/chat").WithTags("Chat").RequireInternalKey().RequireChatAccess();

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
            // An OpenRouter model slug is always a clean "provider/model-name" string and never
            // contains whitespace. A whitespace-containing ModelId is broken data (e.g. someone
            // pasted a label like "For Generate: ..." into the slug field): it corrupts the chat
            // model picker downstream. Reject it defensively so the API can never persist bad data,
            // regardless of caller (the Admin UI validates too, but never trust the client alone).
            if (req.ModelId.Trim().Any(char.IsWhiteSpace))
                return Results.Json(new ErrorResponse("ModelId must not contain whitespace — use the exact OpenRouter slug (e.g. provider/model-name)."), statusCode: 400);
            if (string.IsNullOrWhiteSpace(req.Label))
                return Results.Json(new ErrorResponse("Label is required"), statusCode: 400);
            if (req.ContextWindow is not null and <= 0)
                return Results.Json(new ErrorResponse("Context window must be a positive number of tokens."), statusCode: 400);

            var model = new ChatModelRow
            {
                Id = Guid.NewGuid(),
                ModelId = req.ModelId.Trim(),
                Label = req.Label.Trim(),
                IsText = req.IsText,
                IsVision = req.IsVision,
                IsImageGen = req.IsImageGen,
                ContextWindow = req.ContextWindow,
                CreatedAt = DateTime.UtcNow
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

        // Updates a model's three capability booleans (admin catalogue edit dialog). Superadmin-only.
        // No crypto → no IsUnlocked gate.
        group.MapPatch("/models/{id:guid}", async (Guid id, UpdateChatModelRequest req,
            ChatSettingsRepository repo, HttpContext ctx) =>
        {
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);
            if (req.ContextWindow is not null and <= 0)
                return Results.Json(new ErrorResponse("Context window must be a positive number of tokens."), statusCode: 400);

            await repo.UpdateModelMetadataAsync(id, req.IsText, req.IsVision, req.IsImageGen, req.ContextWindow);
            return Results.Ok();
        });

        // Pinned default models: three nullable GUIDs in chat_settings. Each dropdown's "Default"
        // option maps to null (use oldest-with-property); picking a specific model pins it.
        // Superadmin-only.
        group.MapGet("/settings/defaults", async (ChatSettingsRepository repo, HttpContext ctx) =>
        {
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);

            var (textId, visionId, imageGenId) = await repo.GetDefaultModelIdsAsync();
            return Results.Ok(new { defaultTextModelId = textId, defaultVisionModelId = visionId, defaultImageGenModelId = imageGenId });
        });

        group.MapPatch("/settings/defaults", async (UpdateChatDefaultsRequest req,
            ChatSettingsRepository repo, HttpContext ctx) =>
        {
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);

            await repo.SetDefaultModelIdsAsync(req.DefaultTextModelId, req.DefaultVisionModelId, req.DefaultImageGenModelId);
            return Results.Ok(new { defaultTextModelId = req.DefaultTextModelId, defaultVisionModelId = req.DefaultVisionModelId, defaultImageGenModelId = req.DefaultImageGenModelId });
        });

        // Auto-approve writes (opt-in, superadmin-only): when enabled, the streaming tool loop
        // executes write tool calls immediately instead of pausing for a human Allow/Deny. ACL,
        // the destructive-op cap, and audit tagging still apply in full — only the human-in-the-
        // loop pause is skipped. Article history/restore is the accepted safety net.
        group.MapGet("/settings/auto-approve", async (ChatSettingsRepository repo, HttpContext ctx) =>
        {
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);

            var enabled = await repo.GetAutoApproveWritesAsync();
            return Results.Ok(new { autoApproveWrites = enabled });
        });

        group.MapPatch("/settings/auto-approve", async (UpdateAutoApproveRequest req,
            ChatSettingsRepository repo, HttpContext ctx) =>
        {
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);

            await repo.SetAutoApproveWritesAsync(req.Enabled);
            return Results.Ok(new { autoApproveWrites = req.Enabled });
        });

        // Allow AI chat for users: node-wide kill switch. When off, no one except superadmins
        // can use the AI chat feature (web UI), regardless of each user's individual "Can use AI
        // chat" setting. Per-user settings are preserved while this is off. Superadmin-only.
        group.MapGet("/settings/chat-enabled", async (ChatSettingsRepository repo, HttpContext ctx) =>
        {
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);

            var enabled = await repo.GetChatGloballyEnabledAsync();
            return Results.Ok(new { chatGloballyEnabled = enabled });
        });

        group.MapPatch("/settings/chat-enabled", async (UpdateChatEnabledRequest req,
            ChatSettingsRepository repo, HttpContext ctx) =>
        {
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);

            await repo.SetChatGloballyEnabledAsync(req.Enabled);
            return Results.Ok(new { chatGloballyEnabled = req.Enabled });
        });

        // Effective TEXT model (read-only). Open to ANY authenticated caller (group-level
        // internal-key check only — no role gate): the chat page shows the model name in the
        // composer for regular users too. Exposes ONLY {modelId,label} — no key/settings data.
        // Resolution reuses ResolveEffectiveModelAsync (pinned-if-set-else-oldest-with-capability),
        // identical to how /stream picks the model, so the label always matches what a send uses.
        group.MapGet("/settings/effective-text-model", async (ChatSettingsRepository repo) =>
        {
            var defaults = await repo.GetDefaultModelIdsAsync();
            var effective = await repo.ResolveEffectiveModelAsync("is_text", defaults.TextId);
            // No text model configured → 200 with nulls (the UI just hides the label; the
            // send path already produces its own clear error in that case).
            return Results.Ok(new { modelId = effective?.ModelId, label = effective?.Label });
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
            ChatAttachmentRepository attachRepo, ChatDestructiveOpCounter destructiveCounter,
            ILogger<Program> logger) =>
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

            if (req is null || string.IsNullOrWhiteSpace(req.Message))
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

            // ── Resolve the three effective models (admin-configured, node-global) ──
            // The text model is the sole entry point for every chat turn. Vision and image-gen are
            // resolved too so the loop can delegate/branch as needed. Each uses the pinned default
            // id if set+existing, else the oldest model with the matching capability flag.
            var defaults = await repo.GetDefaultModelIdsAsync();
            var effectiveText = await repo.ResolveEffectiveModelAsync("is_text", defaults.TextId);
            if (effectiveText is null)
            {
                await JsonError(400, "No text model is configured. Ask a superadmin to add one under Admin → AI / Chat.");
                return;
            }
            var effectiveVision = await repo.ResolveEffectiveModelAsync("is_vision", defaults.VisionId);
            var effectiveImageGen = await repo.ResolveEffectiveModelAsync("is_image_gen", defaults.ImageGenId);

            // ── Image-attachment validation ──
            // The server decides how to route attached images based on the effective vision model
            // (the client no longer picks a model). If no vision model is configured at all, reject
            // gracefully with a clear message.
            var attachments = new List<(byte[] Bytes, string Mime)>();
            if (req.Attachments is { Count: > 0 })
            {
                if (effectiveVision is null)
                {
                    await JsonError(400, "No vision model is configured. Attach an image once a superadmin adds a vision model under Admin → AI / Chat.");
                    return;
                }
                if (req.Attachments.Count > MaxAttachmentsPerMessage)
                {
                    await JsonError(400, $"Too many images (max {MaxAttachmentsPerMessage} per message).");
                    return;
                }
                foreach (var reqAtt in req.Attachments)
                {
                    var (okBytes, okMime, attachError) = ValidateAttachment(reqAtt);
                    if (okBytes is null)
                    {
                        await JsonError(400, attachError ?? "Invalid image attachment.");
                        return;
                    }
                    attachments.Add((okBytes, okMime));
                }
            }

            // ── Vision delegation ──
            // If images are attached AND the effective vision model is a DIFFERENT model than the
            // effective text model, make a separate non-streaming, tools-less OpenRouter call to the
            // vision model (all the images + the user's message text). The resulting description is
            // injected into the text model's context so it can answer as a normal text-only turn.
            // If the vision model IS the text model, the images are attached inline instead (current
            // behavior — the text model handles vision itself, single call).
            string? visionDescription = null;
            bool textModelIsVision = effectiveVision is not null && effectiveVision.Id == effectiveText.Id;
            if (attachments.Count > 0 && !textModelIsVision)
            {
                try
                {
                    visionDescription = await RunVisionDelegationAsync(repo, openRouter, logger, keys,
                        effectiveVision!.ModelId, req.Message, attachments, ct);
                }
                catch (AllKeysExhaustedException ex)
                {
                    await JsonError(502, ex.Message);
                    return;
                }
                catch (OpenRouterHttpException ex)
                {
                    await JsonError(502, ex.Message);
                    return;
                }
                catch (InvalidOperationException ex)
                {
                    await JsonError(502, ex.Message);
                    return;
                }
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
                try
                {
                    await convoRepo.CreateAsync(convo);
                }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to create chat conversation {Id}", conversationId); }

                // Homepage chat: the FIRST send from /Tree pins the brand-new conversation to the
                // caller's homepage (clearing any prior pin atomically). /AI never sends
                // pinToHome, so its behavior is unchanged. Only a NEWLY created conversation can
                // be pinned here — continuing an existing conversation (req.ConversationId set)
                // never touches the pin. Separate try/catch from CreateAsync above so a pin
                // failure is never misreported as a conversation-creation failure.
                if (req.PinToHome)
                {
                    try { await convoRepo.SetHomePinnedAsync(userId, conversationId); }
                    catch (Exception ex) { logger.LogWarning(ex, "Failed to pin conversation {Id} to home", conversationId); }
                }
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
                    // Re-attach prior user-uploaded image(s) as vision content parts so the model
                    // keeps them in context across turns — but ONLY when the effective text model
                    // IS the vision model (single-model vision). In the delegation case (text
                    // model != vision model), the text model is text-only and must not receive
                    // prior images; it relies on injected descriptions instead. Generated-image
                    // attachments are always skipped here (display-only).
                    if (textModelIsVision && row.Role == "user"
                        && attachmentsByMessage.TryGetValue(row.Id, out var atts))
                    {
                        var imgs = atts.Where(a => a.Kind == ChatAttachmentKind.UserUpload
                            && a.Blob is { Length: > 0 }).ToList();
                        if (imgs.Count > 0)
                            m.ImageDataUrls = imgs.Select(a => BuildVisionDataUrl(a.Blob!, a.Mime)).ToList();
                    }
                    convoMessages.Add(m);
                    // Attachment manifest: surface the ids of any image attachments on this message
                    // (both user uploads and generated images) so the model can address them with
                    // bee_insert_image_into_article. Runs for user AND assistant rows — generated
                    // images hang off assistant messages. In-memory only; regenerated per request.
                    if (attachmentsByMessage.TryGetValue(row.Id, out var manifestAtts))
                        AppendAttachmentManifest(convoMessages, manifestAtts);
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

            // Phase 5 (vision): persist each validated attachment linked to the new user message and
            // arm the egress image parts. Stored as the ORIGINAL (validated) bytes so reopening the
            // conversation renders faithful images; the egress resize happens in BuildVisionDataUrl.
            // The images are attached inline to the text model's request ONLY when the text model
            // handles vision itself (textModelIsVision). In the delegation case the images were
            // already analyzed by the separate vision model; the text model sees only the injected
            // description.
            // Collect the successfully-persisted attachments for this new turn so an attachment
            // manifest can be emitted after the user message (lets the model reference them by id).
            var persistedAtts = new List<ChatAttachment>();
            if (attachments.Count > 0)
            {
                var imageDataUrls = new List<string>(attachments.Count);
                foreach (var att in attachments)
                {
                    var newAttachment = new ChatAttachment
                    {
                        Id = Guid.NewGuid(),
                        MessageId = newUserMessage.Id,
                        Kind = ChatAttachmentKind.UserUpload,
                        Mime = att.Mime,
                        Blob = att.Bytes,
                        CreatedAt = DateTime.UtcNow
                    };
                    try
                    {
                        await attachRepo.CreateAsync(newAttachment);
                        persistedAtts.Add(newAttachment);
                    }
                    catch (Exception ex) { logger.LogWarning(ex, "Failed to persist chat attachment"); }
                    if (textModelIsVision)
                        imageDataUrls.Add(BuildVisionDataUrl(att.Bytes, att.Mime));
                }
                if (imageDataUrls.Count > 0)
                    newUserTurn.ImageDataUrls = imageDataUrls;
            }

            // Vision delegation: inject the vision model's description as a context message BEFORE
            // the user turn so the text model has the analysis available when it reads the question.
            if (visionDescription is not null)
            {
                convoMessages.Add(new ChatToolMessage
                {
                    Role = "system",
                    Content = "[Image analysis from vision model]: " + visionDescription
                });
            }
            convoMessages.Add(newUserTurn);
            // Attachment manifest for the just-sent user turn (in-memory only; emitted AFTER the
            // user message so it reads naturally in the transcript, mirroring the history path).
            AppendAttachmentManifest(convoMessages, persistedAtts);

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

            // The streaming tool loop is shared with the confirm endpoint (it resumes the SAME loop
            // after a human approves/denies a write tool call). The loop owns its own cancellation /
            // egress-error handling; this wrapper only ensures the transient plaintext keys are
            // dropped once the turn is fully done. Image generation is now reached via the
            // generate_image tool from within this loop (not a per-request category branch).
            try
            {
                await RunToolLoopAsync(new ChatLoopContext(
                    Ctx: ctx, OpenRouter: openRouter, Dispatcher: dispatcher, Repo: repo,
                    MsgRepo: msgRepo, ConvoRepo: convoRepo, AttachRepo: attachRepo, Logger: logger,
                    Keys: keys, Model: effectiveText.ModelId,
                    EffectiveImageGenModelId: effectiveImageGen?.ModelId ?? "",
                    ConversationId: conversationId,
                    ConvoMessages: convoMessages, Ct: ct, Sse: Sse, DestructiveCounter: destructiveCounter,
                    ContextWindow: effectiveText.ContextWindow));
            }
            finally
            {
                keys = null!;
            }
        });

        // ── Phase 3: human-in-the-loop confirm gate ──────────────────────────────────
        //
        // Resumes a turn that paused on a write tool call (the /stream loop emitted confirm_required
        // and returned without executing the write). The client posts {toolCallId, allow,
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
            ChatAttachmentRepository attachRepo, ChatDestructiveOpCounter destructiveCounter,
            ILogger<Program> logger) =>
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

            // Resolve the effective models the SAME way /stream does (admin-configured, node-global —
            // never trust a client-supplied model). The resumed loop uses the effective text model;
            // generate_image (if called in the continuation) uses the effective image-gen model.
            List<ChatMessage> history;
            try { history = await msgRepo.ListByConversationAsync(conversationId); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load chat history for {Id}", conversationId);
                await JsonError(500, "Failed to load conversation");
                return;
            }

            var defaults = await repo.GetDefaultModelIdsAsync();
            var effectiveText = await repo.ResolveEffectiveModelAsync("is_text", defaults.TextId);
            if (effectiveText is null)
            {
                await JsonError(400, "No text model is configured. Ask a superadmin to add one under Admin → AI / Chat.");
                return;
            }
            var effectiveImageGen = await repo.ResolveEffectiveModelAsync("is_image_gen", defaults.ImageGenId);
            string resumeModel = effectiveText.ModelId;

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

                // Load this conversation's attachments once (same as /stream) so the attachment
                // manifest can be regenerated for the resumed turn — the model may need to reference
                // an image id in a bee_insert_image_into_article call during the continuation.
                Dictionary<Guid, List<ChatAttachment>> attachmentsByMessage = new();
                try
                {
                    foreach (var a in await attachRepo.ListByConversationAsync(conversationId))
                    {
                        if (!attachmentsByMessage.TryGetValue(a.MessageId, out var list))
                            attachmentsByMessage[a.MessageId] = list = new();
                        list.Add(a);
                    }
                }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to load chat attachments for {Id}", conversationId); }

                foreach (var row in history)
                {
                    var mm = new ChatToolMessage { Role = row.Role, Content = row.ContentText };
                    if (!string.IsNullOrEmpty(row.ToolCallsJson))
                        mm.ToolCalls = JsonSerializer.Deserialize<List<ChatToolCall>>(row.ToolCallsJson, SseJsonOpts);
                    if (!string.IsNullOrEmpty(row.ToolCallId))
                        mm.ToolCallId = row.ToolCallId;
                    convoMessages.Add(mm);
                    // Attachment manifest (in-memory only): surface image ids so the model can address
                    // them with bee_insert_image_into_article during the resumed continuation.
                    if (attachmentsByMessage.TryGetValue(row.Id, out var manifestAtts))
                        AppendAttachmentManifest(convoMessages, manifestAtts);
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
                    (toolResultJson, toolOk, toolError) = await ExecuteApprovedWriteAsync(
                        ctx, dispatcher, destructiveCounter, conversationId, pending.Name, pending.ArgumentsJson);
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

                var articleId = req.Allow ? TryExtractArticleId(toolResultJson, pending.Name) : null;
                await Sse("confirm_resolved", new { toolCallId = req.ToolCallId, allowed = req.Allow, ok = toolOk, error = toolError, toolName = pending.Name, articleId });

                try
                {
                    await RunToolLoopAsync(new ChatLoopContext(
                        Ctx: ctx, OpenRouter: openRouter, Dispatcher: dispatcher, Repo: repo,
                        MsgRepo: msgRepo, ConvoRepo: convoRepo, AttachRepo: attachRepo, Logger: logger,
                        Keys: keys, Model: resumeModel,
                        EffectiveImageGenModelId: effectiveImageGen?.ModelId ?? "",
                        ConversationId: conversationId,
                        ConvoMessages: convoMessages, Ct: ct, Sse: Sse, DestructiveCounter: destructiveCounter,
                        ContextWindow: effectiveText.ContextWindow));
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
        m.Id, m.ModelId, m.Label, m.IsText, m.IsVision, m.IsImageGen, m.ContextWindow, m.CreatedAt);

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

    /// <summary>Max number of images accepted on a single user turn. Mirrored client-side in
    /// chat.js; bounds both the egress payload size (each image becomes its own content part) and
    /// the vision-delegation call's cost.</summary>
    public const int MaxAttachmentsPerMessage = 10;

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

    /// <summary>Vision delegation: makes a SEPARATE, non-streaming, tools-less OpenRouter call to
    /// the effective vision model with all the attached images + the user's message text. Returns
    /// the text description the vision model produced, which the caller injects into the text
    /// model's context. Uses multi-key failover (same helper as the main loop). Never returns null —
    /// on a no-content response, returns a placeholder so the text model has something to work
    /// with.</summary>
    private static async Task<string> RunVisionDelegationAsync(
        ChatSettingsRepository repo, OpenRouterClient openRouter, ILogger logger,
        IReadOnlyList<KeyMaterial> keys, string visionModelId, string userMessage,
        List<(byte[] Bytes, string Mime)> images, CancellationToken ct)
    {
        var dataUrls = images.Select(i => BuildVisionDataUrl(i.Bytes, i.Mime)).ToList();
        var visionMessages = new List<ChatToolMessage>
        {
            new()
            {
                Role = "user",
                // Plain multimodal question — no Bee tools exposed to the vision model.
                Content = "Describe and answer based on " + (dataUrls.Count > 1 ? "these images: " : "this image: ") + userMessage,
                ImageDataUrls = dataUrls
            }
        };

        // CompleteWithToolsAsync with tools=null goes through ToWire() which serializes the image as
        // a multimodal content array — exactly what a vision model needs. With no tools, ToolCalls is
        // null and we just read Content.
        var result = await RunWithFailoverAsync(repo, keys,
            (pk, token) => openRouter.CompleteWithToolsAsync(pk, visionModelId, visionMessages, null, token),
            ct);

        return string.IsNullOrEmpty(result.Content)
            ? "(The vision model provided no description of the image.)"
            : result.Content;
    }

    /// <summary>Handles a <c>generate_image</c> tool call from the text model's tool loop. Makes a
    /// non-streaming OpenRouter call to the effective image-gen model with the given prompt, stores
    /// the resulting image(s) as chat_attachment (kind=generated-image) linked to the assistant
    /// message that requested it, emits <c>event: image</c> SSE frames so the UI renders them
    /// inline, and feeds a tool result back so the text model can continue. If no image-gen model is
    /// configured, returns a graceful error tool result (not a crash) so the model can tell the user
    /// in plain text. Reuses the existing GenerateImageAsync + ResolveImageSourceToBytesAsync
    /// plumbing from the old image-gen path.</summary>
    private static async Task RunGenerateImageToolAsync(
        ChatLoopContext lc, ResolvedToolCall tc, JsonElement args, Guid assistantMessageId)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Local error-JSON helper (ChatEndpoints has no access to ChatToolDispatcher.ErrorJson).
        string Err(string msg) => JsonSerializer.Serialize(new { error = msg }, SseJsonOpts);

        // No image-gen model configured → graceful error tool result.
        if (string.IsNullOrEmpty(lc.EffectiveImageGenModelId))
        {
            sw.Stop();
            var noModelJson = Err("No image generation model is configured. Tell the user that image generation is not available on this node.");
            lc.ConvoMessages.Add(new ChatToolMessage { Role = "tool", ToolCallId = tc.Id, Content = noModelJson });
            await SafePersistToolMessage(lc.MsgRepo, lc.Logger, lc.ConversationId, tc.Id, noModelJson);
            await lc.Sse("tool_call_result", new { tool = tc.Name, callId = tc.Id, ok = true, durationMs = (int)sw.ElapsedMilliseconds, error = (string?)null });
            return;
        }

        var prompt = args.TryGetProperty("prompt", out var pEl) ? pEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            sw.Stop();
            var badArgsJson = Err("prompt is required");
            lc.ConvoMessages.Add(new ChatToolMessage { Role = "tool", ToolCallId = tc.Id, Content = badArgsJson });
            await SafePersistToolMessage(lc.MsgRepo, lc.Logger, lc.ConversationId, tc.Id, badArgsJson);
            await lc.Sse("tool_call_result", new { tool = tc.Name, callId = tc.Id, ok = false, durationMs = (int)sw.ElapsedMilliseconds, error = "prompt is required" });
            return;
        }

        // Build a single-user-message request with just the prompt (no conversation context — the
        // text model already refined the prompt based on the conversation).
        var imgMessages = new List<ChatToolMessage>
        {
            new() { Role = "user", Content = prompt }
        };

        try
        {
            var result = await RunWithFailoverAsync(lc.Repo, lc.Keys,
                (pk, token) => lc.OpenRouter.GenerateImageAsync(pk, lc.EffectiveImageGenModelId, imgMessages, token),
                lc.Ct);

            // Materialize each image source → bytes, store as attachment, emit inline SSE event.
            // Reuses the same ResolveImageSourceToBytesAsync + ChatImageEvent plumbing as the old
            // image-gen path. savedIds carries the stored attachment ids back to the model so it can
            // address them with bee_insert_image_into_article.
            var savedCount = 0;
            var savedIds = new List<Guid>();
            foreach (var source in result.ImageSources)
            {
                var (imgBytes, imgMime) = await ResolveImageSourceToBytesAsync(source, lc.Ct);
                if (imgBytes is null || imgBytes.Length == 0) continue;

                var attachmentId = Guid.NewGuid();
                try
                {
                    await lc.AttachRepo.CreateAsync(new ChatAttachment
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
                    lc.Logger.LogWarning(ex, "Failed to persist generated image attachment");
                    continue;
                }

                savedCount++;
                savedIds.Add(attachmentId);
                var inlineUrl = "data:" + imgMime + ";base64," + Convert.ToBase64String(imgBytes);
                await lc.Sse("image", new ChatImageEvent(attachmentId, inlineUrl, imgMime));
            }

            sw.Stop();

            // Don't claim success if nothing was actually produced/stored — the model would otherwise
            // hallucinate "here's your image" from a fake-positive tool result.
            if (savedCount == 0)
            {
                var noImgJson = Err("The image model did not return an image. Tell the user image generation failed and they can try again.");
                lc.ConvoMessages.Add(new ChatToolMessage { Role = "tool", ToolCallId = tc.Id, Content = noImgJson });
                await SafePersistToolMessage(lc.MsgRepo, lc.Logger, lc.ConversationId, tc.Id, noImgJson);
                await lc.Sse("tool_call_result", new { tool = tc.Name, callId = tc.Id, ok = false, durationMs = (int)sw.ElapsedMilliseconds, error = "No image returned" });
                return;
            }

            var okJson = JsonSerializer.Serialize(new
            {
                ok = true,
                message = "Image generated.",
                attachmentIds = savedIds,
                hint = "Use an attachmentId with bee_insert_image_into_article to save this image into an article."
            }, SseJsonOpts);
            lc.ConvoMessages.Add(new ChatToolMessage { Role = "tool", ToolCallId = tc.Id, Content = okJson });
            await SafePersistToolMessage(lc.MsgRepo, lc.Logger, lc.ConversationId, tc.Id, okJson);
            await lc.Sse("tool_call_result", new { tool = tc.Name, callId = tc.Id, ok = true, durationMs = (int)sw.ElapsedMilliseconds, error = (string?)null });
        }
        catch (OperationCanceledException) { throw; }
        catch (AllKeysExhaustedException ex)
        {
            sw.Stop();
            var errJson = Err("Image generation failed (all API keys exhausted): " + ex.Message);
            lc.ConvoMessages.Add(new ChatToolMessage { Role = "tool", ToolCallId = tc.Id, Content = errJson });
            await SafePersistToolMessage(lc.MsgRepo, lc.Logger, lc.ConversationId, tc.Id, errJson);
            await lc.Sse("tool_call_result", new { tool = tc.Name, callId = tc.Id, ok = false, durationMs = (int)sw.ElapsedMilliseconds, error = ex.Message });
        }
        catch (OpenRouterHttpException ex)
        {
            sw.Stop();
            var errJson = Err("Image generation failed: " + ex.Message);
            lc.ConvoMessages.Add(new ChatToolMessage { Role = "tool", ToolCallId = tc.Id, Content = errJson });
            await SafePersistToolMessage(lc.MsgRepo, lc.Logger, lc.ConversationId, tc.Id, errJson);
            await lc.Sse("tool_call_result", new { tool = tc.Name, callId = tc.Id, ok = false, durationMs = (int)sw.ElapsedMilliseconds, error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            sw.Stop();
            var errJson = Err("Image generation failed: " + ex.Message);
            lc.ConvoMessages.Add(new ChatToolMessage { Role = "tool", ToolCallId = tc.Id, Content = errJson });
            await SafePersistToolMessage(lc.MsgRepo, lc.Logger, lc.ConversationId, tc.Id, errJson);
            await lc.Sse("tool_call_result", new { tool = tc.Name, callId = tc.Id, ok = false, durationMs = (int)sw.ElapsedMilliseconds, error = ex.Message });
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
        "bee_insert_image_into_article" => "Preparing to insert an image…",
        "generate_image" => "Generating an image…",
        _ => "Working…"
    };

    // Appends a compact system message after a transcript message that has image attachments,
    // so the model can reference them by id in bee_insert_image_into_article. Ordinals are
    // per-message, in created_at order (matches the visual order in the UI). No-op when the list
    // is empty. The manifest message is in-memory only (never persisted to chat_message) and is
    // regenerated from chat_attachment on every request, so it survives confirm-resume and
    // conversation reopen with zero schema impact.
    private static void AppendAttachmentManifest(
        List<ChatToolMessage> msgs, List<ChatAttachment> atts)
    {
        if (atts.Count == 0) return;
        var lines = atts.Select((a, i) =>
            $"{i + 1}) attachmentId={a.Id} kind={a.Kind} mime={a.Mime}");
        msgs.Add(new ChatToolMessage
        {
            Role = "system",
            Content = "[Images attached to the previous message — use attachmentId with "
                    + "bee_insert_image_into_article to place one into an article]\n"
                    + string.Join("\n", lines)
        });
    }

    // First ~120 chars of the first user message → conversation title (plan §2 Phase 2). This is
    // stored as-is, permanently, at conversation-creation time — the sidebar's own CSS ellipsis
    // (text-overflow:ellipsis) truncates it further for DISPLAY depending on the sidebar's current
    // width, but it can never show more than what's actually stored here, no matter how wide the
    // user drags the sidebar. 120 (up from an earlier 40) gives real room for that resizing to
    // reveal more text. Truncated on a grapheme-ish boundary (Substring is fine here; titles are
    // display-only).
    private static string TitleFromMessage(string message)
    {
        var trimmed = message.Trim();
        return trimmed.Length <= 120 ? trimmed : trimmed[..120] + "…";
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
        ChatAttachmentRepository AttachRepo,
        ILogger Logger,
        IReadOnlyList<KeyMaterial> Keys,
        string Model,
        string EffectiveImageGenModelId,
        Guid ConversationId,
        List<ChatToolMessage> ConvoMessages,
        CancellationToken Ct,
        Func<string, object, Task> Sse,
        ChatDestructiveOpCounter DestructiveCounter,
        // The effective text model's context-window size (tokens), for the context-fill % metric.
        // Null when unset — ComputeContextFill returns null in that case.
        int? ContextWindow);

    /// <summary>Computes the context-fill percentage (0–100) from prompt tokens vs the model's
    /// context window. Returns null when either value is missing or the window is invalid.</summary>
    private static int? ComputeContextFill(int? promptTokens, int? contextWindow)
        => promptTokens is int p && contextWindow is int w && w > 0
            ? Math.Clamp((int)Math.Round(p * 100.0 / w), 0, 100)
            : null;

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
        const int maxIterations = 25;
        // Per-turn metrics (persisted only on the final assistant message): wall-clock duration,
        // prompt tokens of the last reporting iteration (≈ context size at the final answer),
        // summed completion tokens across iterations, and how many tool calls were processed.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int? promptTokensLast = null;
        int? completionTokensTotal = null;
        int toolCallsCount = 0;
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

                // Accumulate per-iteration token usage. promptTokensLast ends up as the prompt size
                // of the last iteration that reported usage (the context size at the final answer);
                // completionTokensTotal sums output across iterations.
                if (turn.PromptTokens is int pt) promptTokensLast = pt;
                if (turn.CompletionTokens is int ctok) completionTokensTotal = (completionTokensTotal ?? 0) + ctok;

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
                            TokensIn = promptTokensLast,
                            TokensOut = completionTokensTotal,
                            ToolCallsCount = toolCallsCount,
                            DurationMs = sw.ElapsedMilliseconds,
                            CreatedAt = DateTime.UtcNow
                        });
                        await lc.ConvoRepo.TouchAsync(lc.ConversationId);
                    }
                    catch (Exception ex) { lc.Logger.LogWarning(ex, "Failed to persist assistant message"); }

                    await lc.Sse("done", new {
                        content = finalContent, model = finalModel, conversationId = lc.ConversationId, messageId,
                        promptTokens = promptTokensLast,
                        completionTokens = completionTokensTotal,
                        toolCallsCount,
                        durationMs = sw.ElapsedMilliseconds,
                        contextFillPercent = ComputeContextFill(promptTokensLast, lc.ContextWindow),
                        contextWindow = lc.ContextWindow,
                        createdAt = DateTime.UtcNow
                    });
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
                var assistantMessageId = Guid.NewGuid();
                try
                {
                    await lc.MsgRepo.CreateAsync(new ChatMessage
                    {
                        Id = assistantMessageId,
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
                    toolCallsCount++; // counts every processed call: reads, writes (incl. auto-approved),
                                      // generate_image, malformed-args ones — all of them.
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

                    // generate_image: needs OpenRouter egress + attachment storage + SSE, so it's
                    // handled here (not in the dispatcher). Intercepted before the write-tool check
                    // and before InvokeAsync — the dispatcher has no OpenRouter/SSE access.
                    if (tc.Name == "generate_image")
                    {
                        await RunGenerateImageToolAsync(lc, tc, args, assistantMessageId);
                        continue;
                    }

                    if (ChatToolDispatcher.IsWriteTool(tc.Name))
                    {
                        if (await lc.Repo.GetAutoApproveWritesAsync())
                        {
                            // Auto-approve (opt-in, superadmin-only, Admin -> AI/Chat): skip the human
                            // confirm gate and execute immediately, through the SAME defense-in-depth
                            // path /confirm uses (ACL via CallerScope reuse, the destructive-op cap,
                            // the ChatWriteExecItemsKey marker, audit tagging). tool_call_start was
                            // already emitted above for this call.
                            var (autoJson, autoOk, autoErr) = await ExecuteApprovedWriteAsync(
                                lc.Ctx, lc.Dispatcher, lc.DestructiveCounter, lc.ConversationId, tc.Name, tc.ArgumentsJson);
                            var autoArticleId = TryExtractArticleId(autoJson, tc.Name);
                            lc.ConvoMessages.Add(new ChatToolMessage { Role = "tool", ToolCallId = tc.Id, Content = autoJson });
                            await SafePersistToolMessage(lc.MsgRepo, lc.Logger, lc.ConversationId, tc.Id, autoJson);
                            await lc.Sse("confirm_resolved", new { toolCallId = tc.Id, allowed = true, ok = autoOk, error = autoErr, toolName = tc.Name, articleId = autoArticleId });
                            continue;
                        }

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
                    TokensIn = promptTokensLast,
                    TokensOut = completionTokensTotal,
                    ToolCallsCount = toolCallsCount,
                    DurationMs = sw.ElapsedMilliseconds,
                    CreatedAt = DateTime.UtcNow
                });
                await lc.ConvoRepo.TouchAsync(lc.ConversationId);
            }
            catch (Exception ex) { lc.Logger.LogWarning(ex, "Failed to persist cap message"); }
            await lc.Sse("done", new {
                content = capContent, model = lc.Model, conversationId = lc.ConversationId, messageId = capMessageId,
                promptTokens = promptTokensLast,
                completionTokens = completionTokensTotal,
                toolCallsCount,
                durationMs = sw.ElapsedMilliseconds,
                contextFillPercent = ComputeContextFill(promptTokensLast, lc.ContextWindow),
                contextWindow = lc.ContextWindow,
                createdAt = DateTime.UtcNow
            });
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

    /// <summary>Executes ONE approved write tool call under full defense-in-depth — shared by
    /// /confirm (human clicked Allow) and RunToolLoopAsync's auto-approve path (superadmin opted
    /// out of the confirm gate). Handles, in order: the atomic destructive-op cap reservation
    /// (never both check-then-act), argument parsing (graceful on malformed JSON), forcing
    /// bee_delete_article's confirm=true (the caller's approval — human click or the auto-approve
    /// setting — IS the confirmation), and the ChatWriteExecItemsKey marker that
    /// ChatToolDispatcher.InvokeAsync requires before it will run ANY write tool. Never throws —
    /// always returns a graceful tool-result JSON, exactly like the tools themselves.</summary>
    private static async Task<(string Json, bool Ok, string? Error)> ExecuteApprovedWriteAsync(
        HttpContext ctx, ChatToolDispatcher dispatcher, ChatDestructiveOpCounter destructiveCounter,
        Guid conversationId, string toolName, string? argumentsJson)
    {
        bool reservedDestructive = ChatToolDispatcher.IsDestructiveTool(toolName)
            && destructiveCounter.TryReserve(conversationId);
        bool capRefused = ChatToolDispatcher.IsDestructiveTool(toolName) && !reservedDestructive;

        if (capRefused)
        {
            var msg = $"Destructive operation cap reached for this conversation (max {destructiveCounter.Cap} {toolName} calls). Start a new conversation to continue.";
            return (JsonSerializer.Serialize(new { error = msg }, SseJsonOpts), false, msg);
        }

        JsonElement execArgs;
        bool parsed;
        try
        {
            var raw = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;
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
            if (reservedDestructive) destructiveCounter.Release(conversationId);
            return ("{\"error\":\"malformed arguments JSON\"}", false, "malformed arguments JSON");
        }

        // The caller's approval (human Allow click, or the auto-approve setting) satisfies
        // bee_delete_article's confirm=true two-step requirement — avoids double-prompting.
        if (toolName == "bee_delete_article")
            execArgs = EnsureConfirmTrue(execArgs);

        ctx.Items[ChatToolDispatcher.ChatWriteExecItemsKey] = true;
        try
        {
            var result = await dispatcher.InvokeAsync(toolName, execArgs, ctx);
            if (reservedDestructive && (!result.Ok || result.Json.Contains("\"error\"")))
                destructiveCounter.Release(conversationId);
            return (result.Json, result.Ok, result.Error);
        }
        finally
        {
            ctx.Items.Remove(ChatToolDispatcher.ChatWriteExecItemsKey);
        }
    }

    /// <summary>Pulls the article <c>id</c> out of a write tool's result JSON (SaveArticleAsync /
    /// UpdateArticleAsync / AppendToArticleAsync / ReplaceInArticleAsync all include it via OkJson)
    /// so the UI can render a direct "open article" link. Never for bee_delete_article — the
    /// article is gone (hidden), a link would 404. Tolerates malformed/error JSON (no "id") by
    /// returning null — never throws.</summary>
    private static string? TryExtractArticleId(string toolResultJson, string toolName)
    {
        if (toolName == "bee_delete_article") return null;
        try
        {
            using var doc = JsonDocument.Parse(toolResultJson);
            if (doc.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                return idEl.GetString();
        }
        catch (JsonException) { /* no id present — not every tool result carries one */ }
        return null;
    }

    /// <summary>Request body for the confirm endpoint. <c>toolCallId</c>+<c>allow</c> are required;
    /// <c>systemPrompt</c> lets the client forward the same instructions used for the original turn
    /// so the resumed completion stays consistent. The server resolves the model internally (never
    /// trusts client input) and resolves name/args from the transcript.</summary>
    public record ChatConfirmRequest(
        [property: JsonPropertyName("toolCallId")] string ToolCallId,
        [property: JsonPropertyName("allow")] bool Allow,
        [property: JsonPropertyName("systemPrompt")] string? SystemPrompt);
}
