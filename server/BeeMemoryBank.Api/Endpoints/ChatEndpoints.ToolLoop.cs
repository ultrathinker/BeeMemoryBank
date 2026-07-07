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
