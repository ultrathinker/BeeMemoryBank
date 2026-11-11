using System.Text.Json;
using BeeMemoryBank.Api.Services.Providers;
using BeeMemoryBank.Core.Services;
using Dapper;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// CRUD for the provider keys and the model catalogue, both in chat.db. Node-local — never synced, never snapshotted.
///
/// <para><b>Two tables each, by provider.</b> The built-in OpenRouter provider (<see cref="BuiltInOpenRouter"/>) keeps its keys in
/// <c>chat_api_key</c> and its models in <c>chat_model</c>, exactly as 2.9.0 wrote them; every other provider's keys live in
/// <c>chat_provider_key</c> and its models in <c>chat_provider_model</c>. 2.9.0 sends every row of <c>chat_api_key</c> to openrouter.ai, so
/// a node rolled back to it must find there nothing but OpenRouter keys, and nothing this version deleted: a key of another provider is
/// never written to the legacy table, and a delete by id removes the row from whichever table holds it. The upgrade needs no copy and no
/// unlocked vault: the legacy rows ARE the built-in provider's. Reads that list "all keys" or "all models" read both tables.</para>
///
/// <para>Keys are stored as AES-256-GCM ciphertext under the node chat key (<see cref="ChatDataProtector"/> — the master DEK directly would
/// not survive a DEK rotation, since chat.db is outside the rotation transaction). A legacy key keeps 2.9.0's constant AAD. A key of
/// another provider is sealed with its own id in the AAD and its provider's id inside the sealed text, so a row moved to another provider
/// does not open: a key reaches only its own provider. <see cref="SealSecretAsync"/> / <see cref="OpenSecretsAsync"/> are the only places a
/// provider key is encrypted or decrypted; both need an unlocked vault.</para>
/// </summary>
public sealed class ChatSettingsRepository(ChatDbConnectionFactory factory, ChatDataProtector protector)
    : ChatRepositoryBase(factory)
{
    // ── keys: chat_api_key (built-in OpenRouter) and chat_provider_key (every other provider) ─────────────────

    // Constant AAD of a chat_api_key secret (2.9.0's, distinct from RemoteAccountService's token AAD).
    private static readonly byte[] KeyAad = "bmb-openrouter-key-v1"u8.ToArray();

    // The key prefix is the first characters of the secret itself, so it gets its own AAD and is
    // sealed like the secret; the AAD binds it to its row.
    private static byte[] PrefixAad(Guid id) => System.Text.Encoding.UTF8.GetBytes($"bmb-chat-api-key-prefix-v1:{id:D}");

    /// <summary>The AAD of a chat_provider_key secret (also spelled in BeeMemoryBank.Rekey's ChatColumns).</summary>
    public static byte[] ProviderKeyAad(Guid id) => System.Text.Encoding.UTF8.GetBytes($"bmb-chat-provider-key-v1:{id:D}");

    /// <summary>The AAD of a chat_provider_key prefix (also spelled in BeeMemoryBank.Rekey's ChatColumns).</summary>
    public static byte[] ProviderPrefixAad(Guid id) => System.Text.Encoding.UTF8.GetBytes($"bmb-chat-provider-key-prefix-v1:{id:D}");

    /// <summary>What a chat_provider_key row seals: its provider's id, a newline, the secret.</summary>
    private static string BoundSecret(Guid providerId, string secret) => $"{providerId:D}\n{secret}";

    /// <summary>Shown instead of a sealed key prefix while the vault is locked or it does not open.</summary>
    public const string SealedPrefixPlaceholder = "[sealed]";

    private const string KeyCommonCols = @"id AS Id, label AS Label,
        key_prefix_ciphertext AS KeyPrefixCiphertext, key_prefix_iv AS KeyPrefixIv, key_prefix_key_v AS KeyPrefixKeyVersion,
        ciphertext AS Ciphertext, iv AS Iv, key_v AS KeyVersion, enabled AS Enabled, priority AS Priority,
        disabled_until AS DisabledUntil, last_error AS LastError, last_used_at AS LastUsedAt,
        created_at AS CreatedAt";

    private const string LegacyKeySelect = "SELECT @builtIn AS ProviderId, key_prefix AS KeyPrefix, " + KeyCommonCols + " FROM chat_api_key";
    private const string ProviderKeySelect = "SELECT provider_id AS ProviderId, '' AS KeyPrefix, " + KeyCommonCols + " FROM chat_provider_key";

    private static object P(object? extra = null)
    {
        var p = new DynamicParameters(extra);
        p.Add("builtIn", BuiltInOpenRouter.Id);
        return p;
    }

    public async Task<Models.ChatApiKey?> GetByIdAsync(Guid id)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<Models.ChatApiKey>(
            $"SELECT * FROM ({LegacyKeySelect} UNION ALL {ProviderKeySelect}) WHERE Id = @id LIMIT 1", P(new { id }));
    }

    /// <summary>Every key of every provider, by provider then priority.</summary>
    public async Task<List<Models.ChatApiKey>> ListAsync()
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<Models.ChatApiKey>(
            $"SELECT * FROM ({LegacyKeySelect} UNION ALL {ProviderKeySelect}) ORDER BY ProviderId, Priority ASC, CreatedAt ASC", P())).ToList();
    }

    /// <summary>The keys of one provider, by priority.</summary>
    public async Task<List<Models.ChatApiKey>> ListByProviderAsync(Guid providerId)
    {
        using var conn = OpenConnection();
        var sql = providerId == BuiltInOpenRouter.Id
            ? $"{LegacyKeySelect} ORDER BY priority ASC, created_at ASC"
            : $"{ProviderKeySelect} WHERE provider_id = @providerId ORDER BY priority ASC, created_at ASC";
        return (await conn.QueryAsync<Models.ChatApiKey>(sql, P(new { providerId }))).ToList();
    }

    /// <summary>A provider's enabled keys that are ALSO eligible right now — i.e. not currently in a
    /// cooldown window (<c>disabled_until</c> NULL or in the past), ordered by priority then age. This
    /// is the failover candidate list: a 402/429 sets a future <c>disabled_until</c>, dropping the key
    /// out of this set until the cooldown elapses. A 401 disables the row entirely (enabled=0) so it
    /// leaves here too. Keys past their cooldown (recovered) reappear automatically.</summary>
    public async Task<List<Models.ChatApiKey>> ListAvailableOrderedAsync(Guid providerId)
    {
        using var conn = OpenConnection();
        var now = UtcNow();
        const string available = "enabled = 1 AND (disabled_until IS NULL OR disabled_until <= @now)";
        var sql = providerId == BuiltInOpenRouter.Id
            ? $"{LegacyKeySelect} WHERE {available} ORDER BY priority ASC, created_at ASC"
            : $"{ProviderKeySelect} WHERE provider_id = @providerId AND {available} ORDER BY priority ASC, created_at ASC";
        return (await conn.QueryAsync<Models.ChatApiKey>(sql, P(new { now, providerId }))).ToList();
    }

    /// <summary>Writes a sealed key (<see cref="SealSecretAsync"/> first) into its provider's table. A key of the built-in provider goes to
    /// chat_api_key, as 2.9.0 wrote it, and makes sure the provider's row exists; any other key goes to chat_provider_key.</summary>
    public async Task CreateAsync(Models.ChatApiKey key)
    {
        using var conn = OpenConnection();
        if (key.ProviderId == BuiltInOpenRouter.Id)
        {
            using var tx = conn.BeginTransaction();
            await conn.ExecuteAsync(ChatProviderRepository.EnsureBuiltInSql, ChatProviderRepository.BuiltInParameters(), tx);
            await conn.ExecuteAsync(
                @"INSERT INTO chat_api_key
                  (id, label, key_prefix, key_prefix_ciphertext, key_prefix_iv, key_prefix_key_v,
                   ciphertext, iv, key_v, enabled, priority,
                   disabled_until, last_error, last_used_at, created_at)
                  VALUES (@Id, @Label, '', @KeyPrefixCiphertext, @KeyPrefixIv, @KeyPrefixKeyVersion,
                          @Ciphertext, @Iv, @KeyVersion, @Enabled, @Priority,
                          @DisabledUntil, @LastError, @LastUsedAt, @CreatedAt)",
                key, tx);
            tx.Commit();
            return;
        }
        await conn.ExecuteAsync(
            @"INSERT INTO chat_provider_key
              (id, provider_id, label, key_prefix_ciphertext, key_prefix_iv, key_prefix_key_v,
               ciphertext, iv, key_v, enabled, priority,
               disabled_until, last_error, last_used_at, created_at)
              VALUES (@Id, @ProviderId, @Label, @KeyPrefixCiphertext, @KeyPrefixIv, @KeyPrefixKeyVersion,
                      @Ciphertext, @Iv, @KeyVersion, @Enabled, @Priority,
                      @DisabledUntil, @LastError, @LastUsedAt, @CreatedAt)",
            key);
    }

    /// <summary>Encrypts <paramref name="secret"/> and <paramref name="key"/>'s KeyPrefix under the chat key into its sealed columns, ready
    /// for <see cref="CreateAsync"/>, which never writes the prefix in plaintext. <see cref="Models.ChatApiKey.ProviderId"/> must be set:
    /// it decides the table, the AADs and the provider binding.</summary>
    public async Task SealSecretAsync(Models.ChatApiKey key, string secret)
    {
        if (key.ProviderId == Guid.Empty) key.ProviderId = BuiltInOpenRouter.Id;
        using var lease = await protector.AcquireAsync();
        if (key.ProviderId == BuiltInOpenRouter.Id)
        {
            (key.Ciphertext, key.Iv) = lease.EncryptText(secret, KeyAad);
            (key.KeyPrefixCiphertext, key.KeyPrefixIv) = lease.EncryptText(key.KeyPrefix, PrefixAad(key.Id));
        }
        else
        {
            (key.Ciphertext, key.Iv) = lease.EncryptText(BoundSecret(key.ProviderId, secret), ProviderKeyAad(key.Id));
            (key.KeyPrefixCiphertext, key.KeyPrefixIv) = lease.EncryptText(key.KeyPrefix, ProviderPrefixAad(key.Id));
        }
        key.KeyVersion = ChatDataProtector.ChatKeyVersion;
        key.KeyPrefixKeyVersion = ChatDataProtector.ChatKeyVersion;
    }

    /// <summary>
    /// Fills in each key's <see cref="Models.ChatApiKey.KeyPrefix"/> from its sealed columns. A
    /// legacy row keeps its plaintext prefix; a sealed one reads as
    /// <see cref="SealedPrefixPlaceholder"/> while the vault is locked or when it does not open.
    /// </summary>
    public async Task OpenPrefixesAsync(IReadOnlyList<Models.ChatApiKey> keys)
    {
        ChatKeyLease? lease = null;
        if (keys.Any(k => k.KeyPrefixKeyVersion is not null))
        {
            try { lease = await protector.TryAcquireForReadAsync(); }
            catch (Core.Exceptions.SessionLockedException) { }
        }

        using (lease)
        {
            foreach (var k in keys.Where(k => k.KeyPrefixKeyVersion is not null))
            {
                var aad = k.ProviderId == BuiltInOpenRouter.Id ? PrefixAad(k.Id) : ProviderPrefixAad(k.Id);
                k.KeyPrefix = (k.KeyPrefixKeyVersion == ChatDataProtector.ChatKeyVersion
                               && k.KeyPrefixCiphertext is { Length: > 0 } && k.KeyPrefixIv is { Length: > 0 }
                        ? protector.TryDecryptText(lease, k.KeyPrefixCiphertext, k.KeyPrefixIv, k.KeyPrefixKeyVersion, aad)
                        : null) ?? SealedPrefixPlaceholder;
            }
        }
    }

    /// <summary>
    /// Seals up to <paramref name="batchSize"/> key prefixes still in the plaintext column of chat_api_key (chat_provider_key has none);
    /// returns how many it looked at (0 = nothing left). Guarded by <c>key_prefix_key_v IS NULL</c>, so it is safe to overlap with itself.
    /// </summary>
    public async Task<int> MigrateLegacyPrefixBatchAsync(int batchSize, CancellationToken ct)
    {
        using var conn = OpenConnection();
        var legacy = (await conn.QueryAsync<(Guid Id, string KeyPrefix)>(
            "SELECT id, key_prefix FROM chat_api_key WHERE key_prefix_key_v IS NULL AND key_prefix != '' LIMIT @batchSize",
            new { batchSize })).ToList();
        if (legacy.Count == 0) return 0;

        using var lease = await protector.AcquireAsync(ct);
        foreach (var (id, prefix) in legacy)
        {
            if (ct.IsCancellationRequested) break;
            var (ciphertext, iv) = lease.EncryptText(prefix, PrefixAad(id));
            await conn.ExecuteAsync(
                @"UPDATE chat_api_key
                  SET key_prefix = '', key_prefix_ciphertext = @ciphertext, key_prefix_iv = @iv, key_prefix_key_v = @version
                  WHERE id = @id AND key_prefix_key_v IS NULL",
                new { id, ciphertext, iv, version = ChatDataProtector.ChatKeyVersion });
        }
        return legacy.Count;
    }

    /// <summary>
    /// Decrypts each key's secret, index-aligned with <paramref name="keys"/>; an entry is null when
    /// that row opens under no key this node holds, or (a key of a provider other than the built-in one) when the provider it is
    /// sealed for is not the provider its row names. The caller skips it and records why. Legacy
    /// master-DEK rows open through the current or a retired master DEK until migrated.
    /// </summary>
    public async Task<string?[]> OpenSecretsAsync(IReadOnlyList<Models.ChatApiKey> keys)
    {
        var result = new string?[keys.Count];
        if (keys.Count == 0) return result;

        using var lease = keys.Any(k => k.KeyVersion == ChatDataProtector.ChatKeyVersion)
            ? await protector.TryAcquireForReadAsync()
            : null;
        for (var i = 0; i < keys.Count; i++)
        {
            var k = keys[i];
            if (k.ProviderId == BuiltInOpenRouter.Id)
            {
                result[i] = protector.TryDecryptText(lease, k.Ciphertext, k.Iv, k.KeyVersion, KeyAad);
                continue;
            }
            // A provider's key is only ever sealed under the chat key (there is no legacy master-DEK form of it).
            var bound = k.KeyVersion == ChatDataProtector.ChatKeyVersion
                ? protector.TryDecryptText(lease, k.Ciphertext, k.Iv, k.KeyVersion, ProviderKeyAad(k.Id))
                : null;
            var expected = $"{k.ProviderId:D}\n";
            result[i] = bound is not null && bound.StartsWith(expected, StringComparison.Ordinal) ? bound[expected.Length..] : null;
        }
        return result;
    }

    /// <summary>
    /// Moves provider keys still sealed directly under the master DEK (<c>key_v IS NULL</c>, chat_api_key only: they predate the chat key)
    /// onto the chat key; one that opens under no available master DEK is marked <c>-1</c> and left as
    /// it is. Same contract as <c>ChatMessageRepository.MigrateLegacyBatchAsync</c>. The table
    /// holds a handful of rows, so no partial index backs the scan.
    /// </summary>
    /// <summary>Provider keys still sealed directly under the master DEK.</summary>
    public async Task<int> CountLegacyAsync()
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM chat_api_key WHERE key_v IS NULL");
    }

    public async Task<int> MigrateLegacyBatchAsync(int batchSize, SessionService session, CancellationToken ct)
    {
        using var conn = OpenConnection();
        var legacy = (await conn.QueryAsync<Models.ChatApiKey>(
            $"{LegacyKeySelect} WHERE key_v IS NULL LIMIT @batchSize",
            P(new { batchSize }))).ToList();
        if (legacy.Count == 0) return 0;

        using var lease = await protector.AcquireAsync(ct);
        foreach (var row in legacy)
        {
            if (ct.IsCancellationRequested) break;

            var secret = protector.TryDecryptText(null, row.Ciphertext, row.Iv, keyVersion: null, KeyAad);
            if (secret is null)
            {
                await conn.ExecuteAsync(
                    "UPDATE chat_api_key SET key_v = @Marker WHERE id = @Id AND key_v IS NULL",
                    new { row.Id, Marker = ChatDataProtector.LegacyUnreadable });
                continue;
            }

            var (ciphertext, iv) = lease.EncryptText(secret, KeyAad);
            await conn.ExecuteAsync(
                "UPDATE chat_api_key SET ciphertext = @Ciphertext, iv = @Iv, key_v = @Version WHERE id = @Id AND key_v IS NULL",
                new { row.Id, Ciphertext = ciphertext, Iv = iv, Version = ChatDataProtector.ChatKeyVersion });
        }
        return legacy.Count;
    }

    // Every write by id below runs on both key tables: an id lives in exactly one of them, the other statement changes nothing.
    private static readonly string[] KeyTables = ["chat_api_key", "chat_provider_key"];

    private async Task ExecuteOnKeyTablesAsync(string sqlWithTable, object parameters)
    {
        using var conn = OpenConnection();
        foreach (var table in KeyTables)
            await conn.ExecuteAsync(sqlWithTable.Replace("{table}", table), parameters);
    }

    public Task UpdateMetadataAsync(Guid id, string? label, bool? enabled, int? priority) =>
        ExecuteOnKeyTablesAsync(
            @"UPDATE {table}
                 SET label   = COALESCE(@label, label),
                     enabled = COALESCE(@enabled, enabled),
                     priority = COALESCE(@priority, priority)
               WHERE id = @id",
            new { id, label, enabled, priority });

    public Task RecordUsageAsync(Guid id, string? lastError = null) =>
        ExecuteOnKeyTablesAsync(
            "UPDATE {table} SET last_used_at = @now, last_error = @lastError WHERE id = @id",
            new { id, now = UtcNow(), lastError });

    /// <summary>Records the outcome of using a key for egress on the failover path.
    /// <list type="bullet">
    /// <item><c>disable=true</c> (HTTP 401 — unauthorized/revoked) → sets <c>enabled=0</c> so the key
    /// is dropped from the candidate list until an admin re-enables it; clears <c>disabled_until</c>
    /// (a disabled key isn't cooling down, it's off).</item>
    /// <item><c>disable=false</c> + <c>disabledUntil</c> in the future (HTTP 402/429 — credits/rate
    /// limit) → sets the cooldown window; the key is skipped until it elapses, then auto-reappears.</item>
    /// <item><c>disable=false</c> + <c>disabledUntil=null</c> (HTTP 5xx / transport error — transient)
    /// → records <c>last_error</c> but leaves the key enabled/available (retry-next, no cooldown).</item>
    /// </list>
    /// <c>last_used_at</c> is always touched so the admin UI shows the most recent attempt.</summary>
    public Task RecordKeyFailureAsync(Guid id, bool disable, DateTime? disabledUntil, string? lastError)
    {
        var now = UtcNow();
        // Store disabled_until as the same ISO-8601 "o" string format used everywhere else here
        // (e.g. last_used_at via UtcNow()), so ListAvailableOrderedAsync's `disabled_until <= @now`
        // is a consistent lexicographic string comparison rather than a DateTime/text format mix.
        var disabledStr = disabledUntil?.ToString("o");
        return disable
            ? ExecuteOnKeyTablesAsync(
                @"UPDATE {table}
                     SET enabled = 0, disabled_until = NULL,
                         last_error = @lastError, last_used_at = @now
                   WHERE id = @id",
                new { id, lastError, now })
            : ExecuteOnKeyTablesAsync(
                @"UPDATE {table}
                     SET disabled_until = @disabledStr,
                         last_error = @lastError, last_used_at = @now
                   WHERE id = @id",
                new { id, disabledStr, lastError, now });
    }

    /// <summary>A key served a request successfully — clear its cooldown window + last error
    /// so a recovered key shows a clean status, and stamp <c>last_used_at</c>.</summary>
    public Task RecordKeySuccessAsync(Guid id) =>
        ExecuteOnKeyTablesAsync(
            @"UPDATE {table}
                 SET disabled_until = NULL, last_error = NULL, last_used_at = @now
               WHERE id = @id",
            new { id, now = UtcNow() });

    /// <summary>Deletes a key from whichever table holds it — from chat_api_key too, so a node rolled back to 2.9.0 does not find it.</summary>
    public Task DeleteKeyAsync(Guid id) => ExecuteOnKeyTablesAsync("DELETE FROM {table} WHERE id = @id", new { id });

    // ── models: chat_model (built-in OpenRouter) and chat_provider_model (every other provider) ─────────────
    //
    // Each model has three independent capability booleans (is_text, is_vision, is_image_gen).
    // A model existing in the catalogue means it's usable — there is no enabled/disabled concept
    // anymore. Everything is sorted by created_at ASC (oldest first), consistently, so the admin
    // grid and every dropdown that lists models present the same order.

    private const string ModelCommonCols = @"id AS Id, model_id AS ModelId, label AS Label,
        is_text AS IsText, is_vision AS IsVision, is_image_gen AS IsImageGen,
        context_window AS ContextWindow, created_at AS CreatedAt";

    private const string AllModels =
        "SELECT @builtIn AS ProviderId, " + ModelCommonCols + " FROM chat_model UNION ALL " +
        "SELECT provider_id AS ProviderId, " + ModelCommonCols + " FROM chat_provider_model";

    /// <summary>All models of every provider for the picker / catalogue, oldest-first.</summary>
    public Task<List<Models.ChatModelRow>> ListEnabledAsync() => ListAllModelsAsync();

    /// <summary>All models for the admin catalogue UI. Ordered oldest-first by created_at.</summary>
    public async Task<List<Models.ChatModelRow>> ListAllModelsAsync()
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<Models.ChatModelRow>($"SELECT * FROM ({AllModels}) ORDER BY CreatedAt ASC", P())).ToList();
    }

    /// <summary>Fetches a single model by its GUID id (used to check whether a pinned default
    /// still exists). Returns null when the id is unknown.</summary>
    public async Task<Models.ChatModelRow?> GetModelByIdAsync(Guid id)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<Models.ChatModelRow>($"SELECT * FROM ({AllModels}) WHERE Id = @id LIMIT 1", P(new { id }));
    }

    /// <summary>Resolves the effective model for a given capability at the start of a chat turn.
    /// <para>Logic: if a model is pinned for this capability (via chat_settings) AND that model
    /// still exists in the catalogue, use it. Otherwise fall back to the oldest model (by
    /// created_at), of any provider, that has the capability flag set. Returns null when no model has the
    /// capability at all (the caller degrades gracefully — e.g. rejects an image attachment if
    /// no vision model is configured).</para>
    /// <param name="propertyColumn">One of <c>"is_text"</c>, <c>"is_vision"</c>,
    /// <c>"is_image_gen"</c>.</param>
    /// <param name="pinnedIdStr">The pinned model GUID as a string (from chat_settings), or
    /// null/empty for "Default" (oldest-with-property).</param></summary>
    public async Task<Models.ChatModelRow?> ResolveEffectiveModelAsync(string propertyColumn, string? pinnedIdStr)
    {
        // The column name is validated by the switch (never interpolated from untrusted input).
        var col = propertyColumn switch
        {
            "is_text" => "IsText",
            "is_vision" => "IsVision",
            "is_image_gen" => "IsImageGen",
            _ => throw new ArgumentException($"Unknown model property column: {propertyColumn}", nameof(propertyColumn))
        };

        if (!string.IsNullOrWhiteSpace(pinnedIdStr) && Guid.TryParse(pinnedIdStr, out var pinnedId)
            && await GetModelByIdAsync(pinnedId) is { } pinned)
            return pinned;

        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<Models.ChatModelRow>(
            $"SELECT * FROM ({AllModels}) WHERE {col} = 1 ORDER BY CreatedAt ASC LIMIT 1", P());
    }

    /// <summary>The models of one provider, oldest first.</summary>
    public async Task<List<Models.ChatModelRow>> ListModelsByProviderAsync(Guid providerId)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<Models.ChatModelRow>(
            $"SELECT * FROM ({AllModels}) WHERE ProviderId = @providerId ORDER BY CreatedAt ASC", P(new { providerId }))).ToList();
    }

    /// <summary>Updates a model's three capability booleans + context window (admin catalogue edit
    /// dialog). The booleans are optional (null = unchanged via COALESCE). <paramref name="contextWindow"/>
    /// is set UNCONDITIONALLY (null clears it) — unlike the COALESCE booleans, null must be able to
    /// clear the value. The only PATCH caller always sends the field, so "omitted = cleared" is fine.</summary>
    public async Task UpdateModelMetadataAsync(Guid id, bool? isText, bool? isVision, bool? isImageGen, int? contextWindow)
    {
        using var conn = OpenConnection();
        foreach (var table in new[] { "chat_model", "chat_provider_model" })
            await conn.ExecuteAsync(
                $@"UPDATE {table}
                     SET is_text    = COALESCE(@isText, is_text),
                         is_vision  = COALESCE(@isVision, is_vision),
                         is_image_gen = COALESCE(@isImageGen, is_image_gen),
                         context_window = @contextWindow
                   WHERE id = @id",
                new { id, isText, isVision, isImageGen, contextWindow });
    }

    /// <summary>A model of the built-in provider goes to chat_model (as 2.9.0 wrote it, and the provider's row is made sure of); any other
    /// to chat_provider_model.</summary>
    public async Task CreateAsync(Models.ChatModelRow model)
    {
        if (model.ProviderId == Guid.Empty) model.ProviderId = BuiltInOpenRouter.Id;
        using var conn = OpenConnection();
        if (model.ProviderId == BuiltInOpenRouter.Id)
        {
            using var tx = conn.BeginTransaction();
            await conn.ExecuteAsync(ChatProviderRepository.EnsureBuiltInSql, ChatProviderRepository.BuiltInParameters(), tx);
            // 'text' is a placeholder for the legacy NOT NULL category column (unused by app code).
            await conn.ExecuteAsync(
                @"INSERT INTO chat_model (id, model_id, label, category, is_text, is_vision, is_image_gen, context_window, created_at)
                  VALUES (@Id, @ModelId, @Label, 'text', @IsText, @IsVision, @IsImageGen, @ContextWindow, @CreatedAt)",
                model, tx);
            tx.Commit();
            return;
        }
        await conn.ExecuteAsync(
            @"INSERT INTO chat_provider_model (id, provider_id, model_id, label, is_text, is_vision, is_image_gen, context_window, created_at)
              VALUES (@Id, @ProviderId, @ModelId, @Label, @IsText, @IsVision, @IsImageGen, @ContextWindow, @CreatedAt)",
            model);
    }

    public async Task DeleteModelAsync(Guid id)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync("DELETE FROM chat_model WHERE id = @id", new { id });
        await conn.ExecuteAsync("DELETE FROM chat_provider_model WHERE id = @id", new { id });
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

    /// <summary>The longest fallback chain a default may have.</summary>
    public const int MaxFallbackModels = 10;

    /// <summary>The ordered fallback models of the default text model (and of Dream's model): chat_settings.fallback_text_model_ids, a JSON
    /// array of model ids. Empty when none is set or the value does not parse.</summary>
    public async Task<List<Guid>> GetFallbackModelIdsAsync()
    {
        using var conn = OpenConnection();
        var json = await conn.ExecuteScalarAsync<string?>("SELECT fallback_text_model_ids FROM chat_settings WHERE id = 1");
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<Guid>>(json)?.Distinct().Take(MaxFallbackModels).ToList() ?? []; }
        catch (JsonException) { return []; }
    }

    public async Task SetFallbackModelIdsAsync(IReadOnlyList<Guid> ids)
    {
        using var conn = OpenConnection();
        var json = ids.Count == 0 ? null : JsonSerializer.Serialize(ids.Distinct().Take(MaxFallbackModels));
        await conn.ExecuteAsync("UPDATE chat_settings SET fallback_text_model_ids = @json WHERE id = 1", new { json });
    }

    // ── chat_user_settings (per-user) ───────────────────────────────────

    /// <summary>Per-user opt-in:
    /// when true, the streaming tool loop executes THIS USER's write tool calls immediately (still
    /// ACL-checked, still destructive-op-capped, still audit-tagged) instead of pausing for a human
    /// Allow/Deny. Off by default; no row for a user means false, not a table miss.</summary>
    public async Task<bool> GetAutoApproveWritesAsync(int userId)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<bool>(
            "SELECT COALESCE((SELECT auto_approve_writes FROM chat_user_settings WHERE user_id = @userId), 0)",
            new { userId });
    }

    public async Task SetAutoApproveWritesAsync(int userId, bool enabled)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO chat_user_settings (user_id, auto_approve_writes) VALUES (@userId, @enabled)
              ON CONFLICT(user_id) DO UPDATE SET auto_approve_writes = @enabled",
            new { userId, enabled });
    }

    // ── chat_settings (single row, id=1) ────────────────────────────────────────

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
