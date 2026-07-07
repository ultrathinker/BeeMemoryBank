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
public static partial class ChatEndpoints
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

        MapSettingsEndpoints(group);
        MapStreamEndpoint(group);
        MapConfirmEndpoint(group);
        MapConversationEndpoints(group);
    }

    private static void MapSettingsEndpoints(RouteGroupBuilder group)
    {
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
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static ChatModelResponse ToModelResponse(ChatModelRow m) => new(
        m.Id, m.ModelId, m.Label, m.IsText, m.IsVision, m.IsImageGen, m.ContextWindow, m.CreatedAt);

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
}
