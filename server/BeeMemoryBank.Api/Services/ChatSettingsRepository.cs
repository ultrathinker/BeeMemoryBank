using Dapper;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// CRUD for the chat key catalogue (<c>chat_api_key</c>) and model catalogue
/// (<c>chat_model</c>), both in chat.db. Node-local — never synced, never snapshotted.
///
/// Keys are stored as AES-256-GCM ciphertext under the master DEK (see
/// <c>RemoteAccountService</c> precedent). This repo persists/returns <c>byte[]</c> blobs only;
/// encrypt/decrypt happens at the call-site that has the master DEK in scope.
/// </summary>
public sealed class ChatSettingsRepository(ChatDbConnectionFactory factory) : ChatRepositoryBase(factory)
{
    // ── chat_api_key ──────────────────────────────────────────────────────────

    private const string KeyCols = @"id AS Id, label AS Label, key_prefix AS KeyPrefix,
        ciphertext AS Ciphertext, iv AS Iv, enabled AS Enabled, priority AS Priority,
        disabled_until AS DisabledUntil, last_error AS LastError, last_used_at AS LastUsedAt,
        created_at AS CreatedAt";

    public async Task<Models.ChatApiKey?> GetByIdAsync(Guid id)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<Models.ChatApiKey>(
            $"SELECT {KeyCols} FROM chat_api_key WHERE id = @id", new { id });
    }

    public async Task<List<Models.ChatApiKey>> ListAsync()
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<Models.ChatApiKey>(
            $"SELECT {KeyCols} FROM chat_api_key ORDER BY priority ASC, created_at ASC")).ToList();
    }

    /// <summary>Phase 4: enabled keys that are ALSO eligible right now — i.e. not currently in a
    /// cooldown window (<c>disabled_until</c> NULL or in the past), ordered by priority then age. This
    /// is the failover candidate list: a 402/429 sets a future <c>disabled_until</c>, dropping the key
    /// out of this set until the cooldown elapses. A 401 disables the row entirely (enabled=0) so it
    /// leaves here too. Keys past their cooldown (recovered) reappear automatically.</summary>
    public async Task<List<Models.ChatApiKey>> ListAvailableOrderedAsync()
    {
        using var conn = OpenConnection();
        var now = UtcNow();
        return (await conn.QueryAsync<Models.ChatApiKey>(
            $"SELECT {KeyCols} FROM chat_api_key WHERE enabled = 1 AND (disabled_until IS NULL OR disabled_until <= @now) ORDER BY priority ASC, created_at ASC",
            new { now })).ToList();
    }

    public async Task CreateAsync(Models.ChatApiKey key)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO chat_api_key
              (id, label, key_prefix, ciphertext, iv, enabled, priority,
               disabled_until, last_error, last_used_at, created_at)
              VALUES (@Id, @Label, @KeyPrefix, @Ciphertext, @Iv, @Enabled, @Priority,
                      @DisabledUntil, @LastError, @LastUsedAt, @CreatedAt)",
            key);
    }

    public async Task UpdateMetadataAsync(Guid id, string? label, bool? enabled, int? priority)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE chat_api_key
                 SET label   = COALESCE(@label, label),
                     enabled = COALESCE(@enabled, enabled),
                     priority = COALESCE(@priority, priority)
               WHERE id = @id",
            new { id, label, enabled, priority });
    }

    public async Task RecordUsageAsync(Guid id, string? lastError = null)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE chat_api_key SET last_used_at = @now, last_error = @lastError WHERE id = @id",
            new { id, now = UtcNow(), lastError });
    }

    /// <summary>Phase 4: records the outcome of using a key for egress on the failover path.
    /// <list type="bullet">
    /// <item><c>disable=true</c> (HTTP 401 — unauthorized/revoked) → sets <c>enabled=0</c> so the key
    /// is dropped from the candidate list until an admin re-enables it; clears <c>disabled_until</c>
    /// (a disabled key isn't cooling down, it's off).</item>
    /// <item><c>disable=false</c> + <c>disabledUntil</c> in the future (HTTP 402/429 — credits/rate
    /// limit) → sets the cooldown window; the key is skipped until it elapses, then auto-reappears.</item>
    /// <item><c>disable=false</c> + <c>disabledUntil=null</c> (HTTP 5xx / transport error — transient)
    /// → records <c>last_error</c> but leaves the key enabled/available (retry-next, no cooldown).</item>
    /// </list>
    /// <see cref="last_used_at"/> is always touched so the admin UI shows the most recent attempt.</summary>
    public async Task RecordKeyFailureAsync(Guid id, bool disable, DateTime? disabledUntil, string? lastError)
    {
        using var conn = OpenConnection();
        var now = UtcNow();
        // Store disabled_until as the same ISO-8601 "o" string format used everywhere else here
        // (e.g. last_used_at via UtcNow()), so ListAvailableOrderedAsync's `disabled_until <= @now`
        // is a consistent lexicographic string comparison rather than a DateTime/text format mix.
        var disabledStr = disabledUntil?.ToString("o");
        if (disable)
        {
            await conn.ExecuteAsync(
                @"UPDATE chat_api_key
                     SET enabled = 0, disabled_until = NULL,
                         last_error = @lastError, last_used_at = @now
                   WHERE id = @id",
                new { id, lastError, now });
        }
        else
        {
            await conn.ExecuteAsync(
                @"UPDATE chat_api_key
                     SET disabled_until = @disabledStr,
                         last_error = @lastError, last_used_at = @now
                   WHERE id = @id",
                new { id, disabledStr, lastError, now });
        }
    }

    /// <summary>Phase 4: a key served a request successfully — clear its cooldown window + last error
    /// so a recovered key shows a clean status, and stamp <c>last_used_at</c>.</summary>
    public async Task RecordKeySuccessAsync(Guid id)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE chat_api_key
                 SET disabled_until = NULL, last_error = NULL, last_used_at = @now
               WHERE id = @id",
            new { id, now = UtcNow() });
    }

    public async Task DeleteKeyAsync(Guid id)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync("DELETE FROM chat_api_key WHERE id = @id", new { id });
    }

    // ── chat_model ────────────────────────────────────────────────────────────
    //
    // Each model has three independent capability booleans (is_text, is_vision, is_image_gen).
    // A model existing in the catalogue means it's usable — there is no enabled/disabled concept
    // anymore. Everything is sorted by created_at ASC (oldest first), consistently, so the admin
    // grid and every dropdown that lists models present the same order.

    private const string ModelCols = @"id AS Id, model_id AS ModelId, label AS Label,
        is_text AS IsText, is_vision AS IsVision, is_image_gen AS IsImageGen, created_at AS CreatedAt";

    /// <summary>All models for the picker / catalogue, oldest-first. Since "a model existing means
    /// it's usable", this returns every row (the old enabled filter is gone).</summary>
    public async Task<List<Models.ChatModelRow>> ListEnabledAsync()
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<Models.ChatModelRow>(
            $"SELECT {ModelCols} FROM chat_model ORDER BY created_at ASC")).ToList();
    }

    /// <summary>All models for the admin catalogue UI. Ordered oldest-first by created_at.</summary>
    public async Task<List<Models.ChatModelRow>> ListAllModelsAsync()
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<Models.ChatModelRow>(
            $"SELECT {ModelCols} FROM chat_model ORDER BY created_at ASC")).ToList();
    }

    /// <summary>Fetches a single model by its GUID id (used to check whether a pinned default
    /// still exists). Returns null when the id is unknown.</summary>
    public async Task<Models.ChatModelRow?> GetModelByIdAsync(Guid id)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<Models.ChatModelRow>(
            $"SELECT {ModelCols} FROM chat_model WHERE id = @id", new { id });
    }

    /// <summary>Resolves the effective model for a given capability at the start of a chat turn.
    /// <para>Logic: if a model is pinned for this capability (via chat_settings) AND that model
    /// still exists in the catalogue, use it. Otherwise fall back to the oldest model (by
    /// created_at) that has the capability flag set. Returns null when no model has the
    /// capability at all (the caller degrades gracefully — e.g. rejects an image attachment if
    /// no vision model is configured).</para>
    /// <param name="propertyColumn">One of <c>"is_text"</c>, <c>"is_vision"</c>,
    /// <c>"is_image_gen"</c>.</param>
    /// <param name="pinnedIdStr">The pinned model GUID as a string (from chat_settings), or
    /// null/empty for "Default" (oldest-with-property).</param></summary>
    public async Task<Models.ChatModelRow?> ResolveEffectiveModelAsync(string propertyColumn, string? pinnedIdStr)
    {
        using var conn = OpenConnection();

        if (!string.IsNullOrWhiteSpace(pinnedIdStr) && Guid.TryParse(pinnedIdStr, out var pinnedId))
        {
            var pinned = await conn.QuerySingleOrDefaultAsync<Models.ChatModelRow>(
                $"SELECT {ModelCols} FROM chat_model WHERE id = @id", new { id = pinnedId });
            if (pinned != null) return pinned;
        }

        // Fall back to the oldest model with the requested capability. The column name is validated
        // by the switch (never interpolated from untrusted input).
        var col = propertyColumn switch
        {
            "is_text" => "is_text",
            "is_vision" => "is_vision",
            "is_image_gen" => "is_image_gen",
            _ => throw new ArgumentException($"Unknown model property column: {propertyColumn}", nameof(propertyColumn))
        };
        return await conn.QuerySingleOrDefaultAsync<Models.ChatModelRow>(
            $"SELECT {ModelCols} FROM chat_model WHERE {col} = 1 ORDER BY created_at ASC LIMIT 1");
    }

    /// <summary>Updates a model's three capability booleans (admin catalogue edit dialog).
    /// Each is optional (null = unchanged).</summary>
    public async Task UpdateModelMetadataAsync(Guid id, bool? isText, bool? isVision, bool? isImageGen)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE chat_model
                 SET is_text    = COALESCE(@isText, is_text),
                     is_vision  = COALESCE(@isVision, is_vision),
                     is_image_gen = COALESCE(@isImageGen, is_image_gen)
               WHERE id = @id",
            new { id, isText, isVision, isImageGen });
    }

    public async Task CreateAsync(Models.ChatModelRow model)
    {
        using var conn = OpenConnection();
        // 'text' is a placeholder for the legacy NOT NULL category column (unused by app code).
        await conn.ExecuteAsync(
            @"INSERT INTO chat_model (id, model_id, label, category, is_text, is_vision, is_image_gen, created_at)
              VALUES (@Id, @ModelId, @Label, 'text', @IsText, @IsVision, @IsImageGen, @CreatedAt)",
            model);
    }

    public async Task DeleteModelAsync(Guid id)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync("DELETE FROM chat_model WHERE id = @id", new { id });
    }

    // ── chat_settings — pinned default models (single row, id=1) ────────────────

    /// <summary>Returns the three pinned default-model ids (nullable GUID strings). Null means
    /// "Default" (use the oldest model with the matching property).</summary>
    public async Task<(string? TextId, string? VisionId, string? ImageGenId)> GetDefaultModelIdsAsync()
    {
        using var conn = OpenConnection();
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT default_text_model_id, default_vision_model_id, default_image_gen_model_id FROM chat_settings WHERE id = 1");
        if (row == null) return (null, null, null);
        return ((string?)row.default_text_model_id, (string?)row.default_vision_model_id, (string?)row.default_image_gen_model_id);
    }

    /// <summary>Sets the three pinned default-model ids. Pass null/empty to un-pin (fall back to
    /// oldest-with-property).</summary>
    public async Task SetDefaultModelIdsAsync(string? textId, string? visionId, string? imageGenId)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE chat_settings
                 SET default_text_model_id    = @textId,
                     default_vision_model_id  = @visionId,
                     default_image_gen_model_id = @imageGenId
               WHERE id = 1",
            new { textId, visionId, imageGenId });
    }

    // ── chat_settings (single row, id=1) ────────────────────────────────────────

    /// <summary>Superadmin-only opt-in: when true, the streaming tool loop executes write tool
    /// calls immediately (still ACL-checked, still destructive-op-capped, still audit-tagged)
    /// instead of pausing for a human Allow/Deny. Off by default.</summary>
    public async Task<bool> GetAutoApproveWritesAsync()
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<bool>(
            "SELECT auto_approve_writes FROM chat_settings WHERE id = 1");
    }

    public async Task SetAutoApproveWritesAsync(bool enabled)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE chat_settings SET auto_approve_writes = @enabled WHERE id = 1",
            new { enabled });
    }

    public async Task<bool> GetChatGloballyEnabledAsync()
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<bool>(
            "SELECT chat_globally_enabled FROM chat_settings WHERE id = 1");
    }

    public async Task SetChatGloballyEnabledAsync(bool enabled)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE chat_settings SET chat_globally_enabled = @enabled WHERE id = 1",
            new { enabled });
    }
}
