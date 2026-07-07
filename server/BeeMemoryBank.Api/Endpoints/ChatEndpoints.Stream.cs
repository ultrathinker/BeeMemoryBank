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
    private static void MapStreamEndpoint(RouteGroupBuilder group)
    {
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
    }
}
