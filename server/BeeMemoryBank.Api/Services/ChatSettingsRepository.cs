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

    private const string ModelCols = @"id AS Id, model_id AS ModelId, label AS Label,
        category AS Category, default_for_category AS DefaultForCategory, enabled AS Enabled";

    public async Task<List<Models.ChatModelRow>> ListEnabledAsync()
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<Models.ChatModelRow>(
            $"SELECT {ModelCols} FROM chat_model WHERE enabled = 1 ORDER BY category ASC, label ASC")).ToList();
    }

    /// <summary>All models (enabled + disabled) for the admin catalogue UI. Ordered for stable display.</summary>
    public async Task<List<Models.ChatModelRow>> ListAllModelsAsync()
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<Models.ChatModelRow>(
            $"SELECT {ModelCols} FROM chat_model ORDER BY category ASC, label ASC")).ToList();
    }

    /// <summary>Phase 5: resolves the category of a model by its model_id (the value the
    /// per-conversation picker sends), preferring enabled models but falling back to any row so an
    /// admin can still pick a disabled-by-typ model. Returns null when the model_id is unknown
    /// (the caller then treats it as plain "text"). Used by the chat stream endpoint to decide
    /// whether to accept an attached image (vision only) or run the image-generation path.</summary>
    public async Task<string?> GetCategoryByModelIdAsync(string modelId)
    {
        using var conn = OpenConnection();
        // Enabled first; if none enabled, fall back to a disabled row with the same id.
        var enabled = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT category FROM chat_model WHERE model_id = @modelId AND enabled = 1 LIMIT 1",
            new { modelId });
        if (enabled != null) return enabled;
        return await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT category FROM chat_model WHERE model_id = @modelId LIMIT 1",
            new { modelId });
    }

    /// <summary>True iff a model with the given <paramref name="modelId"/> is in the
    /// admin-curated enabled catalogue. Used by the chat completion endpoints (/stream, /message,
    /// /confirm's resume model) to reject a client-supplied model that isn't enabled — so an
    /// authenticated non-superadmin user cannot pick an arbitrary/expensive OpenRouter model never
    /// added to the catalogue and run it on the shared, node-global, admin-funded key(s)
    /// (plan §1 "curated/categorized model catalogue").</summary>
    public async Task<bool> IsModelEnabledAsync(string modelId)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<bool>(
            "SELECT COUNT(1) FROM chat_model WHERE model_id = @modelId AND enabled = 1",
            new { modelId });
    }

    /// <summary>Toggles a model's enabled flag (admin catalogue). Label/category are immutable here.</summary>
    public async Task UpdateModelMetadataAsync(Guid id, bool? enabled)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE chat_model SET enabled = COALESCE(@enabled, enabled) WHERE id = @id",
            new { id, enabled });
    }

    public async Task CreateAsync(Models.ChatModelRow model)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO chat_model (id, model_id, label, category, default_for_category, enabled)
              VALUES (@Id, @ModelId, @Label, @Category, @DefaultForCategory, @Enabled)",
            model);
    }

    public async Task DeleteModelAsync(Guid id)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync("DELETE FROM chat_model WHERE id = @id", new { id });
    }
}
