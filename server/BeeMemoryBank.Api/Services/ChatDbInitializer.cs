using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Creates the chat.db schema once at startup. Idempotent (<c>CREATE TABLE IF NOT EXISTS</c>).
///
/// This is deliberately NOT <c>MigrationRunner</c>, does NOT live under
/// <c>Storage/Migrations/*.sql</c> (that folder is glob-embedded into the Storage assembly and
/// Ghost-Hunter-managed), and is NOT registered via <c>AddStorage()</c>. It is invoked from a
/// dedicated <c>using</c> scope block in <c>Api/Program.cs</c> placed AFTER the beedb migration
/// blocks. See plan §1 ("Chat DB") + §3 (schema) + §4 (guardrails).
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

        _logger.LogInformation("chat.db schema initialized");
    }

    // Schema per plan §3. chat_api_key stores only (ciphertext, iv) — the
    // ArticleEncryptor.Encrypt(secret, masterDek, aad) path yields exactly those two artifacts
    // (AES-256-GCM under the master DEK with a constant AAD), so there is no salt/kdf_version.
    private static readonly string[] SchemaStatements =
    [
        """
        CREATE TABLE IF NOT EXISTS chat_conversation (
            id           TEXT PRIMARY KEY,
            user_id      INTEGER NOT NULL,
            title        TEXT NOT NULL,
            created_at   TEXT NOT NULL,
            updated_at   TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS chat_message (
            id              TEXT PRIMARY KEY,
            conversation_id TEXT NOT NULL,
            role            TEXT NOT NULL,
            content_text    TEXT,
            tool_calls_json TEXT,
            tool_call_id    TEXT,
            model           TEXT,
            tokens_in       INTEGER,
            tokens_out      INTEGER,
            created_at      TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS chat_attachment (
            id          TEXT PRIMARY KEY,
            message_id  TEXT NOT NULL,
            kind        TEXT NOT NULL,
            mime        TEXT NOT NULL,
            blob        BLOB,
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
            enabled               INTEGER NOT NULL DEFAULT 1
        );
        """,
        // Single-row settings table (id is always 1). Currently holds only auto_approve_writes —
        // a superadmin-only opt-in that skips the human-in-the-loop confirm gate for write tools
        // (the user has explicit article history/restore as a safety net). Node-local like the
        // rest of chat.db; never synced.
        """
        CREATE TABLE IF NOT EXISTS chat_settings (
            id                    INTEGER PRIMARY KEY CHECK (id = 1),
            auto_approve_writes   INTEGER NOT NULL DEFAULT 0
        );
        """,
        "INSERT OR IGNORE INTO chat_settings (id, auto_approve_writes) VALUES (1, 0);"
    ];
}
