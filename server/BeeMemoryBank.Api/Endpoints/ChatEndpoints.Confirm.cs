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
    private static void MapConfirmEndpoint(RouteGroupBuilder group)
    {
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
    }
}
