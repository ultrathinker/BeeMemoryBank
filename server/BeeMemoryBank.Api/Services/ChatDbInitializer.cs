using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Creates the chat.db schema once at startup. Idempotent (<c>CREATE TABLE IF NOT EXISTS</c>).
///
/// This is deliberately NOT <c>MigrationRunner</c>, does NOT live under
/// <c>Storage/Migrations/*.sql</c> (that folder is glob-embedded into the Storage assembly and
/// Ghost-Hunter-managed), and is NOT registered via <c>AddStorage()</c>. It is invoked from a
/// dedicated <c>using</c> scope block in <c>Api/Program.cs</c> placed AFTER the beedb migration
/// blocks.
/// </summary>
public sealed class ChatDbInitializer
{
    private readonly ChatDbConnectionFactory _factory;
    private readonly ILogger<ChatDbInitializer> _logger;

    public ChatDbInitializer(ChatDbConnectionFactory factory, ILogger<ChatDbInitializer> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        await using var conn = (SqliteConnection)_factory.CreateConnection();

        foreach (var ddl in SchemaStatements)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = ddl;
            await cmd.ExecuteNonQueryAsync();
        }

        // Additive column migrations for existing tables. The CREATE TABLE IF NOT EXISTS
        // statements above are a no-op on an existing DB, so these guarded ALTERs bring an
        // older chat.db up to the current schema. Each is skipped if the column already exists
        // (checked via PRAGMA table_info) so re-running is safe.
        await EnsureColumnAsync(conn, "chat_model", "is_text",
            "ALTER TABLE chat_model ADD COLUMN is_text INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(conn, "chat_model", "is_vision",
            "ALTER TABLE chat_model ADD COLUMN is_vision INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(conn, "chat_model", "is_image_gen",
            "ALTER TABLE chat_model ADD COLUMN is_image_gen INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(conn, "chat_model", "context_window",
            "ALTER TABLE chat_model ADD COLUMN context_window INTEGER");
        await EnsureColumnAsync(conn, "chat_model", "created_at",
            "ALTER TABLE chat_model ADD COLUMN created_at TEXT");
        await EnsureColumnAsync(conn, "chat_settings", "default_text_model_id",
            "ALTER TABLE chat_settings ADD COLUMN default_text_model_id TEXT");
        await EnsureColumnAsync(conn, "chat_settings", "default_vision_model_id",
            "ALTER TABLE chat_settings ADD COLUMN default_vision_model_id TEXT");
        await EnsureColumnAsync(conn, "chat_settings", "default_image_gen_model_id",
            "ALTER TABLE chat_settings ADD COLUMN default_image_gen_model_id TEXT");
        await EnsureColumnAsync(conn, "chat_settings", "chat_globally_enabled",
            "ALTER TABLE chat_settings ADD COLUMN chat_globally_enabled INTEGER NOT NULL DEFAULT 1");
        // Homepage pinned chat: at most ONE conversation per user carries this flag (enforced
        // at the application layer by ChatConversationRepository.SetHomePinnedAsync's single
        // atomic UPDATE — chat.db has no index-based invariants, matching chat_settings).
        await EnsureColumnAsync(conn, "chat_conversation", "is_home_pinned",
            "ALTER TABLE chat_conversation ADD COLUMN is_home_pinned INTEGER NOT NULL DEFAULT 0");
        // Per-turn metrics (set only on the final assistant message of a turn).
        await EnsureColumnAsync(conn, "chat_message", "tool_calls_count",
            "ALTER TABLE chat_message ADD COLUMN tool_calls_count INTEGER");
        await EnsureColumnAsync(conn, "chat_message", "duration_ms",
            "ALTER TABLE chat_message ADD COLUMN duration_ms INTEGER");
        // content_text routinely carries decrypted vault content (a tool result JSON is a full
        // article body — see ChatToolLoop.SafePersistToolMessage), so it must never sit in the DB
        // as plaintext. New rows are encrypted under the master
        // DEK (AES-256-GCM, see ChatMessageRepository) into these two columns instead, and
        // content_text is left NULL going forward. Existing rows are NOT retroactively
        // re-encrypted (there is no reliable point in startup to do that — the vault may still be
        // locked here); ChatMessageRepository reads content_ciphertext when present and falls back
        // to the legacy plaintext content_text otherwise, exactly like this codebase's other
        // lazy v0→v1 migrations (e.g. node-identity private key, agent KDF version).
        await EnsureColumnAsync(conn, "chat_message", "content_ciphertext",
            "ALTER TABLE chat_message ADD COLUMN content_ciphertext BLOB");
        await EnsureColumnAsync(conn, "chat_message", "content_iv",
            "ALTER TABLE chat_message ADD COLUMN content_iv BLOB");
        // tool_calls_json carries the same class of decrypted vault content (a WRITE tool's
        // arguments ARE the article body being saved) — see ChatMessageRepository's class
        // remarks. Same shape as content_text's
        // pair above: new rows encrypt into these two columns and leave tool_calls_json NULL;
        // ChatMessageRepository reads tool_calls_ciphertext when present and falls back to legacy
        // plaintext tool_calls_json otherwise.
        await EnsureColumnAsync(conn, "chat_message", "tool_calls_ciphertext",
            "ALTER TABLE chat_message ADD COLUMN tool_calls_ciphertext BLOB");
        await EnsureColumnAsync(conn, "chat_message", "tool_calls_iv",
            "ALTER TABLE chat_message ADD COLUMN tool_calls_iv BLOB");
        // chat_attachment.blob must never hold raw image bytes unencrypted. New rows encrypt
        // the blob under the master DEK and record the IV here; NULL iv (legacy rows) means the
        // blob column still holds plaintext bytes, read as-is for backward compatibility.
        await EnsureColumnAsync(conn, "chat_attachment", "iv",
            "ALTER TABLE chat_attachment ADD COLUMN iv BLOB");

        // Key-version markers, one per ciphertext column: 1 = sealed under the node chat key (the
        // 'chat' row of tbl_node_data_key in the main database, carried forward by every DEK
        // rotation), NULL = legacy (sealed directly under the master DEK, or still plaintext),
        // -1 = legacy ciphertext that opened under no available master DEK when migrated. See
        // ChatDataProtector. Rows sealed directly under the master DEK did not survive a DEK
        // rotation, which is why these exist.
        await EnsureColumnAsync(conn, "chat_message", "content_key_v",
            "ALTER TABLE chat_message ADD COLUMN content_key_v INTEGER");
        await EnsureColumnAsync(conn, "chat_message", "tool_calls_key_v",
            "ALTER TABLE chat_message ADD COLUMN tool_calls_key_v INTEGER");
        await EnsureColumnAsync(conn, "chat_attachment", "key_v",
            "ALTER TABLE chat_attachment ADD COLUMN key_v INTEGER");
        await EnsureColumnAsync(conn, "chat_api_key", "key_v",
            "ALTER TABLE chat_api_key ADD COLUMN key_v INTEGER");

        // A conversation title is the first 120 characters of the user's first message, and a
        // provider key's key_prefix is the first 12 characters of the secret itself: both are sealed
        // under the chat key like every other chat.db column (ChatConversationRepository,
        // ChatSettingsRepository), and the plaintext columns are written empty. They stay NOT NULL
        // only because SQLite cannot relax a column constraint without rebuilding the table.
        await EnsureColumnAsync(conn, "chat_conversation", "title_ciphertext",
            "ALTER TABLE chat_conversation ADD COLUMN title_ciphertext BLOB");
        await EnsureColumnAsync(conn, "chat_conversation", "title_iv",
            "ALTER TABLE chat_conversation ADD COLUMN title_iv BLOB");
        await EnsureColumnAsync(conn, "chat_conversation", "title_key_v",
            "ALTER TABLE chat_conversation ADD COLUMN title_key_v INTEGER");
        await EnsureColumnAsync(conn, "chat_api_key", "key_prefix_ciphertext",
            "ALTER TABLE chat_api_key ADD COLUMN key_prefix_ciphertext BLOB");
        await EnsureColumnAsync(conn, "chat_api_key", "key_prefix_iv",
            "ALTER TABLE chat_api_key ADD COLUMN key_prefix_iv BLOB");
        await EnsureColumnAsync(conn, "chat_api_key", "key_prefix_key_v",
            "ALTER TABLE chat_api_key ADD COLUMN key_prefix_key_v INTEGER");

        // Partial indexes backing the legacy-migration scans (ChatMessageRepository /
        // ChatAttachmentRepository.MigrateLegacyBatchAsync). Each index only contains rows still
        // needing migration, so it self-shrinks to empty as rows get migrated and stays empty
        // forever after — a node with nothing legacy left pays an empty-index lookup per scan,
        // never a full table scan, regardless of how large chat.db grows. The first three served
        // the plaintext-only backfill; the *_legacy_key pair now covers that case
        // too and is what the scans use. CREATE INDEX IF NOT EXISTS is unconditionally idempotent
        // (unlike ALTER TABLE ADD COLUMN), so these run every startup with no existence check.
        foreach (var indexDdl in new[]
        {
            "CREATE INDEX IF NOT EXISTS idx_chat_message_legacy_content ON chat_message(id) WHERE content_ciphertext IS NULL AND content_text IS NOT NULL AND content_text != ''",
            "CREATE INDEX IF NOT EXISTS idx_chat_message_legacy_toolcalls ON chat_message(id) WHERE tool_calls_ciphertext IS NULL AND tool_calls_json IS NOT NULL AND tool_calls_json != ''",
            "CREATE INDEX IF NOT EXISTS idx_chat_attachment_legacy_blob ON chat_attachment(id) WHERE iv IS NULL AND blob IS NOT NULL",
            // Same self-emptying shape for the move onto the chat key: each WHERE mirrors the scan
            // in the matching MigrateLegacyBatchAsync exactly (SQLite only uses a partial index when
            // the query's WHERE implies the index's).
            $"CREATE INDEX IF NOT EXISTS idx_chat_message_legacy_key ON chat_message(id) WHERE {ChatMessageRepository.LegacyKeyPredicate}",
            $"CREATE INDEX IF NOT EXISTS idx_chat_attachment_legacy_key ON chat_attachment(id) WHERE {ChatAttachmentRepository.LegacyKeyPredicate}",
            $"CREATE INDEX IF NOT EXISTS idx_chat_conversation_legacy_title ON chat_conversation(id) WHERE {ChatConversationRepository.LegacyTitlePredicate}"
        })
        {
            await using var indexCmd = conn.CreateCommand();
            indexCmd.CommandText = indexDdl;
            await indexCmd.ExecuteNonQueryAsync();
        }

        // Model providers (docs/ai-providers.md): the fallback chain of the default text model, and which provider answered a message.
        await EnsureColumnAsync(conn, "chat_settings", "fallback_text_model_ids",
            "ALTER TABLE chat_settings ADD COLUMN fallback_text_model_ids TEXT");
        await EnsureColumnAsync(conn, "chat_message", "provider",
            "ALTER TABLE chat_message ADD COLUMN provider TEXT");
        // The provider-opaque content of an assistant tool-call turn (Anthropic's thinking blocks, Gemini's thought signatures), sealed
        // under the chat key from its first write (ChatMessageRepository; the offline re-key's ChatColumns re-seals it).
        await EnsureColumnAsync(conn, "chat_message", "provider_state_ciphertext",
            "ALTER TABLE chat_message ADD COLUMN provider_state_ciphertext BLOB");
        await EnsureColumnAsync(conn, "chat_message", "provider_state_iv",
            "ALTER TABLE chat_message ADD COLUMN provider_state_iv BLOB");
        await EnsureColumnAsync(conn, "chat_message", "provider_state_key_v",
            "ALTER TABLE chat_message ADD COLUMN provider_state_key_v INTEGER");

        // A node upgraded from 2.9.0 has keys in chat_api_key or models in chat_model: those are the built-in OpenRouter provider's, and
        // its row is made here, with nobody doing anything (and made again after a rollback to 2.9.0 and back, if that version added
        // rows meanwhile). A node that never used the chat gets no provider. Nothing is copied: the legacy tables stay the built-in
        // provider's own (see ChatSettingsRepository).
        // The id is bound the way the repositories bind it (Dapper, a Guid), so the check and the insert see the same text.
        var needsBuiltIn = await Dapper.SqlMapper.ExecuteScalarAsync<long>(conn,
            "SELECT (EXISTS (SELECT 1 FROM chat_api_key) OR EXISTS (SELECT 1 FROM chat_model)) " +
            "AND NOT EXISTS (SELECT 1 FROM chat_provider WHERE id = @id)",
            new { id = Providers.BuiltInOpenRouter.Id });
        if (needsBuiltIn == 1)
        {
            await Dapper.SqlMapper.ExecuteAsync(conn, Providers.ChatProviderRepository.EnsureBuiltInSql,
                Providers.ChatProviderRepository.BuiltInParameters());
            _logger.LogInformation("chat.db: the keys and models of 2.9.0 belong to the built-in OpenRouter provider");
        }

        // The Dream tables (plan 3.2) live in chat.db for the same reasons the chat does: local to the node, never in a
        // snapshot or a sync event, sealed under the chat key. Their DDL is the feature's own (DreamSchema).
        await BeeMemoryBank.Api.Dream.DreamSchema.EnsureAsync(conn);

        _logger.LogInformation("chat.db schema initialized");
    }

    /// <summary>Adds a column to a table only if it doesn't already exist. Idempotent — uses
    /// PRAGMA table_info to check before issuing the ALTER, so re-running on an already-migrated
    /// DB is a no-op (never throws "duplicate column name").</summary>
    private static async Task EnsureColumnAsync(SqliteConnection conn, string table, string column, string alterSql)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = $"PRAGMA table_info({table})";
            await using var reader = await checkCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (reader["name"] is string name && !string.IsNullOrEmpty(name))
                    existing.Add(name);
            }
        }

        if (existing.Contains(column))
            return;

        await using var alterCmd = conn.CreateCommand();
        alterCmd.CommandText = alterSql;
        await alterCmd.ExecuteNonQueryAsync();
    }

    // chat_api_key stores only (ciphertext, iv) plus its key-version marker —
    // ArticleEncryptor.Encrypt(secret, chatKey, aad) yields exactly those two artifacts (AES-256-GCM
    // with a constant AAD), so there is no salt/kdf_version. The ciphertext/key_v columns of every
    // table below are covered by the key-version comment in InitializeAsync.
    private static readonly string[] SchemaStatements =
    [
        """
        CREATE TABLE IF NOT EXISTS chat_conversation (
            id              TEXT PRIMARY KEY,
            user_id         INTEGER NOT NULL,
            title           TEXT NOT NULL,
            created_at      TEXT NOT NULL,
            updated_at      TEXT NOT NULL,
            is_home_pinned  INTEGER NOT NULL DEFAULT 0
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS chat_message (
            id                    TEXT PRIMARY KEY,
            conversation_id       TEXT NOT NULL,
            role                  TEXT NOT NULL,
            -- content_text/tool_calls_json are legacy plaintext, kept for backward-compat reads of
            -- rows written before the H3/H3b encryption fixes. New rows leave them NULL and
            -- populate the ciphertext/iv column pairs below instead (AES-256-GCM under the node
            -- chat key, one independent AAD-bound pair per column, *_key_v = 1) — see
            -- ChatMessageRepository and ChatDataProtector. A fresh database gets these columns here
            -- directly and never needs the additive ALTER path below.
            content_text          TEXT,
            content_ciphertext    BLOB,
            content_iv            BLOB,
            content_key_v         INTEGER,
            tool_calls_json       TEXT,
            tool_calls_ciphertext BLOB,
            tool_calls_iv         BLOB,
            tool_calls_key_v      INTEGER,
            tool_call_id          TEXT,
            model                 TEXT,
            provider              TEXT,
            tokens_in             INTEGER,
            tokens_out            INTEGER,
            tool_calls_count      INTEGER,
            duration_ms           INTEGER,
            created_at            TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS chat_attachment (
            id          TEXT PRIMARY KEY,
            message_id  TEXT NOT NULL,
            kind        TEXT NOT NULL,
            mime        TEXT NOT NULL,
            -- blob holds ciphertext when iv is set (under the node chat key when key_v = 1, under
            -- the master DEK for a legacy row); a NULL iv means a legacy row written before the H3
            -- encryption fix, whose blob is still plaintext bytes — see ChatAttachmentRepository.
            blob        BLOB,
            iv          BLOB,
            key_v       INTEGER,
            created_at  TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS chat_api_key (
            id              TEXT PRIMARY KEY,
            label           TEXT NOT NULL,
            key_prefix      TEXT NOT NULL,
            ciphertext      BLOB NOT NULL,
            iv              BLOB NOT NULL,
            key_v           INTEGER,
            enabled         INTEGER NOT NULL DEFAULT 1,
            priority        INTEGER NOT NULL DEFAULT 0,
            disabled_until  TEXT,
            last_error      TEXT,
            last_used_at    TEXT,
            created_at      TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS chat_model (
            id                    TEXT PRIMARY KEY,
            model_id              TEXT NOT NULL,
            label                 TEXT NOT NULL,
            category              TEXT NOT NULL,
            default_for_category  INTEGER NOT NULL DEFAULT 0,
            enabled               INTEGER NOT NULL DEFAULT 1,
            is_text               INTEGER NOT NULL DEFAULT 0,
            is_vision             INTEGER NOT NULL DEFAULT 0,
            is_image_gen          INTEGER NOT NULL DEFAULT 0,
            context_window        INTEGER,
            created_at            TEXT
        );
        """,
        // Model providers (docs/ai-providers.md). A provider is a name, a protocol, a base URL, a local flag, wire options and an
        // approval: approval_* is the stamp sealed under the chat key that says which protocol, base URL and local flag a superadmin
        // approved (ChatProviderRepository); approved_host/by/at are for display only. The built-in OpenRouter provider of 2.9.0 keeps
        // its keys in chat_api_key and its models in chat_model; every other provider's are in the two tables below, which 2.9.0
        // never reads. A chat_provider_key secret is sealed with its id in the AAD and its provider's id inside the sealed text.
        """
        CREATE TABLE IF NOT EXISTS chat_provider (
            id                   TEXT PRIMARY KEY,
            name                 TEXT NOT NULL,
            preset_id            TEXT,
            protocol             TEXT NOT NULL,
            base_url             TEXT NOT NULL,
            is_local             INTEGER NOT NULL DEFAULT 0,
            options_json         TEXT,
            approval_ciphertext  BLOB,
            approval_iv          BLOB,
            approval_key_v       INTEGER,
            approved_host        TEXT,
            approved_by          INTEGER,
            approved_at          TEXT,
            created_at           TEXT NOT NULL,
            updated_at           TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS chat_provider_key (
            id                     TEXT PRIMARY KEY,
            provider_id            TEXT NOT NULL,
            label                  TEXT NOT NULL,
            key_prefix_ciphertext  BLOB,
            key_prefix_iv          BLOB,
            key_prefix_key_v       INTEGER,
            ciphertext             BLOB NOT NULL,
            iv                     BLOB NOT NULL,
            key_v                  INTEGER,
            enabled                INTEGER NOT NULL DEFAULT 1,
            priority               INTEGER NOT NULL DEFAULT 0,
            disabled_until         TEXT,
            last_error             TEXT,
            last_used_at           TEXT,
            created_at             TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS chat_provider_model (
            id              TEXT PRIMARY KEY,
            provider_id     TEXT NOT NULL,
            model_id        TEXT NOT NULL,
            label           TEXT NOT NULL,
            is_text         INTEGER NOT NULL DEFAULT 0,
            is_vision       INTEGER NOT NULL DEFAULT 0,
            is_image_gen    INTEGER NOT NULL DEFAULT 0,
            context_window  INTEGER,
            created_at      TEXT
        );
        """,
        // Single-row settings table (id is always 1). Holds the three pinned default-model ids
        // (nullable GUIDs referencing chat_model.id; null = "use the oldest model with the
        // matching property") and the node-wide chat_globally_enabled kill switch. Node-local like
        // the rest of chat.db; never synced. The old category/enabled/default_for_category columns
        // on chat_model are kept for backward compatibility but are no longer read or written by
        // application code — same for auto_approve_writes here (see chat_user_settings below):
        // auto-approval must be a per-user choice; a single node-global toggle removed the human
        // confirm gate for EVERY user at once and required superadmin to touch it on everyone's
        // behalf. The column is kept only so an upgrade from an older chat.db doesn't need a
        // destructive migration, never read or written by current code.
        """
        CREATE TABLE IF NOT EXISTS chat_settings (
            id                         INTEGER PRIMARY KEY CHECK (id = 1),
            auto_approve_writes        INTEGER NOT NULL DEFAULT 0,
            chat_globally_enabled      INTEGER NOT NULL DEFAULT 1,
            default_text_model_id      TEXT,
            default_vision_model_id    TEXT,
            default_image_gen_model_id TEXT,
            fallback_text_model_ids    TEXT
        );
        """,
        // Auto-approve-writes is per-user, not a single node-global toggle — each user
        // controls only their OWN confirm-gate bypass for their OWN chat writes (still fully ACL
        // + destructive-cap + audit-tag gated regardless; this only skips the human Allow/Deny
        // click). No row for a user means "off" (see ChatSettingsRepository.GetAutoApproveWritesAsync's
        // COALESCE), so a brand-new user needs no seed row here.
        """
        CREATE TABLE IF NOT EXISTS chat_user_settings (
            user_id             INTEGER PRIMARY KEY,
            auto_approve_writes INTEGER NOT NULL DEFAULT 0
        );
        """,
        // chat_globally_enabled is intentionally NOT seeded here: on an existing chat.db this
        // statement runs BEFORE EnsureColumnAsync below adds the column (CREATE TABLE IF NOT
        // EXISTS above is a no-op for a pre-existing table), so referencing it here would throw
        // "no such column" on upgrade. The column's own DEFAULT 1 (both in the CREATE TABLE for
        // fresh DBs and in the ALTER for existing ones) already gives every row the correct value.
        "INSERT OR IGNORE INTO chat_settings (id, auto_approve_writes) VALUES (1, 0);"
    ];
}
