using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Rekey;
using BeeMemoryBank.Rekey.Steps;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// A real vault for the offline re-key's tests: built by the product's own services with every kind of row the
/// re-key touches, then frozen into a directory of its own, as the stopped node leaves it (the running test host
/// would otherwise write under the test's feet). The tests work on the frozen copy.
/// </summary>
public abstract class RekeyVaultTestBase : IAsyncLifetime
{
    private const string Password = "rekeyVaultPw1!";
    protected readonly BmbWebApplicationFactory _factory = new();
    protected string _vault = null!;
    protected Fixture _data = null!;

    public virtual async Task InitializeAsync()
    {
        await _factory.InitializeNodeAsync(password: Password);
        (await Session.UnlockAsync(Password)).Should().BeTrue();
        _vault = _factory.DataPath + "-rekey";
        _data = await BuildAsync();
        Freeze();
    }

    public virtual Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        _factory.Dispose();
        if (Directory.Exists(_vault)) Directory.Delete(_vault, recursive: true);
        return Task.CompletedTask;
    }

    // ─── Fixture ────────────────────────────────────────────────────────────

    protected sealed record Fixture(string Version, Guid LastComment, Guid DeletedComment, Guid Purged, Guid Media, Guid DeletedMedia,
        Guid ChatMessage, Guid LegacyDekMessage);

    /// <summary>Two articles with versions, several comments (one soft-deleted, one legacy-AAD), a conflict, media (one
    /// soft-deleted), a sealed secret, a remote token, and chat rows of every kind this schema has.</summary>
    protected async Task<Fixture> BuildAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var articles = sp.GetRequiredService<ArticleService>();
        var comments = sp.GetRequiredService<CommentService>();
        var mediaService = sp.GetRequiredService<MediaService>();
        var dek = Session.GetMasterDek();

        var first = await articles.CreateAsync("First", "/Rekey", [], "first body");
        await articles.UpdateAsync(first.Id, plaintext: "first body, edited");
        await articles.UpdateAsync(first.Id, plaintext: "first body, edited twice");
        var purged = await articles.CreateAsync("Purged", "/Rekey", [], "purged body");
        await articles.UpdateAsync(purged.Id, plaintext: "purged body, edited");

        await comments.CreateAsync(first.Id, "comment one");
        var deleted = await comments.CreateAsync(first.Id, "comment deleted");
        var last = await comments.CreateAsync(first.Id, "comment last");
        await comments.CreateAsync(purged.Id, "comment of the purged");

        var media = await mediaService.CreateAsync("a.bin", "application/octet-stream", RandomNumberGenerator.GetBytes(512), first.Id, isAttachment: true);
        var deletedMedia = await mediaService.CreateAsync("b.bin", "application/octet-stream", RandomNumberGenerator.GetBytes(256), first.Id, isAttachment: true);

        using (var conn = Db.CreateConnection())
        {
            // A comment an older build sealed without an AAD (CommentService still reads it).
            var body = await conn.QuerySingleAsync<(byte[] Dek, byte[] Iv)>(
                "SELECT encrypted_dek, dek_iv FROM tbl_article_body WHERE article_id = @id COLLATE NOCASE", new { id = first.Id.ToString() });
            var articleKey = EnvelopeFraming.Article.UnwrapDek(first.Id, body.Dek, body.Iv, dek);
            var (legacyCt, legacyIv) = ArticleEncryptor.Encrypt("legacy comment", articleKey);
            await conn.ExecuteAsync(
                "INSERT INTO tbl_comment (article_id, text, created_at, comment_id, encrypted, ciphertext, iv) VALUES (@a, '', 'now', @c, 1, @ct, @iv)",
                new { a = Upper(first.Id), c = Guid.NewGuid().ToString(), ct = legacyCt, iv = legacyIv });

            // A conflict: a sibling version sealed under the article's key, as the sync applier stores one.
            var conflict = EnvelopeFraming.Article.Seal(first.Id, Encoding.UTF8.GetBytes("conflicting body"), articleKey, dek);
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_conflict_version (id, article_id, source_node_id, lamport_ts, ciphertext, iv, encrypted_dek, dek_iv, created_at, expires_at)
                  VALUES (@id, @a, 'peer', 1, @ct, @iv, @d, @di, 'now', 'later')",
                new { id = Upper(Guid.NewGuid()), a = Upper(first.Id), ct = conflict.Ciphertext, iv = conflict.Iv, d = conflict.WrappedDek, di = conflict.DekIv });
            Array.Clear(articleKey);

            var (secret, secretIv) = SealedSecretCrypto.Seal("rekey-test-secret", RandomNumberGenerator.GetBytes(32), dek);
            await conn.ExecuteAsync(
                "INSERT INTO tbl_sealed_secret (name, dek_fingerprint, wrapped, iv, updated_at) VALUES ('rekey-test-secret', 'fp', @w, @iv, 'now')",
                new { w = secret, iv = secretIv });

            var (token, tokenIv) = ArticleEncryptor.Encrypt("remote-bearer-token", dek, "bmb-remote-token"u8.ToArray());
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_remote_account (id, display_name, base_url, remote_username, encrypted_token, token_iv, created_at, updated_at)
                  VALUES (@id, 'r', 'https://remote.example', 'u', @t, @iv, 'now', 'now')",
                new { id = Guid.NewGuid().ToString(), t = token, iv = tokenIv });

            await conn.ExecuteAsync("UPDATE tbl_media SET status = 'D', deleted_at = 'now' WHERE id = @id", new { id = Upper(deletedMedia.Id) });
            await conn.ExecuteAsync("UPDATE tbl_comment SET deleted_at = 'now' WHERE id = @id", new { id = deleted.Id });
        }

        var conversation = Guid.NewGuid();
        await sp.GetRequiredService<ChatConversationRepository>().CreateAsync(new ChatConversation
            { Id = conversation, UserId = 1, Title = "t", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        var message = new ChatMessage
        {
            Id = Guid.NewGuid(), ConversationId = conversation, Role = "assistant", ContentText = "CHAT-SENTINEL-content",
            ToolCallsJson = "[{\"note\":\"CHAT-SENTINEL-tool\"}]", CreatedAt = DateTime.UtcNow,
        };
        await sp.GetRequiredService<ChatMessageRepository>().CreateAsync(message, Session);
        await sp.GetRequiredService<ChatAttachmentRepository>().CreateAsync(new ChatAttachment
            { Id = Guid.NewGuid(), MessageId = message.Id, Kind = "user-upload", Mime = "image/png", Blob = Encoding.UTF8.GetBytes("CHAT-SENTINEL-attachment") }, Session);
        var settings = sp.GetRequiredService<ChatSettingsRepository>();
        var apiKey = new ChatApiKey { Id = Guid.NewGuid(), Label = "k", KeyPrefix = "sk-or-v1-abc", CreatedAt = DateTime.UtcNow };
        await settings.SealSecretAsync(apiKey, "sk-or-v1-CHAT-SENTINEL-provider-key");
        await settings.CreateAsync(apiKey);

        // Legacy chat rows: plaintext from before chat encryption, and a message sealed under the master DEK.
        var legacyDekMessage = Guid.NewGuid();
        var (legacyChatCt, legacyChatIv) = ArticleEncryptor.Encrypt("CHAT-SENTINEL-legacy-dek", dek, "bmb-chat-message-content-v1"u8.ToArray());
        using (var chat = (SqliteConnection)sp.GetRequiredService<ChatDbConnectionFactory>().CreateConnection())
        {
            await chat.ExecuteAsync(
                "INSERT INTO chat_message (id, conversation_id, role, content_text, created_at) VALUES (@id, @c, 'user', 'CHAT-SENTINEL-legacy-plain', @now)",
                new { id = Guid.NewGuid(), c = conversation, now = DateTime.UtcNow.ToString("o") });
            await chat.ExecuteAsync(
                "INSERT INTO chat_message (id, conversation_id, role, content_ciphertext, content_iv, created_at) VALUES (@id, @c, 'user', @ct, @iv, @now)",
                new { id = legacyDekMessage, c = conversation, ct = legacyChatCt, iv = legacyChatIv, now = DateTime.UtcNow.ToString("o") });
            // An attachment from before chat encryption: plaintext bytes in the blob column, no IV.
            await chat.ExecuteAsync(
                "INSERT INTO chat_attachment (id, message_id, kind, mime, blob, created_at) VALUES (@id, @m, 'user-upload', 'image/png', @blob, @now)",
                new { id = Guid.NewGuid(), m = message.Id, blob = Encoding.UTF8.GetBytes("CHAT-SENTINEL-legacy-attachment"), now = DateTime.UtcNow.ToString("o") });
        }
        Array.Clear(dek);

        string version;
        using (var conn = Db.CreateConnection())
            version = await conn.ExecuteScalarAsync<string>(
                "SELECT id FROM tbl_article_version WHERE article_id = @id COLLATE NOCASE ORDER BY version_number LIMIT 1", new { id = first.Id.ToString() }) ?? "";
        return new Fixture(version, last.CommentId, deleted.CommentId, purged.Id, media.Id, deletedMedia.Id, message.Id, legacyDekMessage);
    }

    /// <summary>The vault as the stopped node leaves it: both databases whole (no WAL) and the media folder.</summary>
    protected void Freeze()
    {
        Directory.CreateDirectory(_vault);
        using (var conn = (SqliteConnection)Db.CreateConnection())
            conn.Execute("VACUUM INTO $path", new { path = Main });
        using (var scope = _factory.Services.CreateScope())
        using (var chat = (SqliteConnection)scope.ServiceProvider.GetRequiredService<ChatDbConnectionFactory>().CreateConnection())
            chat.Execute("VACUUM INTO $path", new { path = Chat });
        foreach (var db in new[] { Main, Chat })
            using (var conn = Open(db, readOnly: false))
                conn.Execute("PRAGMA journal_mode = DELETE");
        var media = Path.Combine(_factory.DataPath, "media");
        Directory.CreateDirectory(Path.Combine(_vault, "media"));
        if (Directory.Exists(media))
            foreach (var file in Directory.EnumerateFiles(media))
                File.Copy(file, Path.Combine(_vault, "media", Path.GetFileName(file)));
    }

    protected Dictionary<string, long> CountRows()
    {
        using var main = Open(Main, readOnly: true);
        using var chat = Open(Chat, readOnly: true);
        long Count(SqliteConnection c, string sql) => c.ExecuteScalar<long>(sql);
        return new()
        {
            ["articles"] = Count(main, "SELECT COUNT(*) FROM tbl_article_body"),
            ["versions"] = Count(main, "SELECT COUNT(*) FROM tbl_article_version"),
            ["conflicts"] = Count(main, "SELECT COUNT(*) FROM tbl_conflict_version"),
            ["comments"] = Count(main, "SELECT COUNT(*) FROM tbl_comment WHERE encrypted = 1"),
            ["deleted comments"] = Count(main, "SELECT COUNT(*) FROM tbl_comment WHERE deleted_at IS NOT NULL"),
            ["media"] = Count(main, "SELECT COUNT(*) FROM tbl_media"),
            ["deleted media"] = Count(main, "SELECT COUNT(*) FROM tbl_media WHERE deleted_at IS NOT NULL"),
            ["sealed secrets"] = Count(main, "SELECT COUNT(*) FROM tbl_sealed_secret"),
            ["remote tokens"] = Count(main, "SELECT COUNT(*) FROM tbl_remote_account"),
            ["chat key"] = Count(main, "SELECT COUNT(*) FROM tbl_node_data_key WHERE key_name = 'chat'"),
            ["chat content"] = Count(chat, "SELECT COUNT(*) FROM chat_message WHERE content_key_v = 1"),
            ["chat tool calls"] = Count(chat, "SELECT COUNT(*) FROM chat_message WHERE tool_calls_key_v = 1"),
            ["chat legacy dek"] = Count(chat, "SELECT COUNT(*) FROM chat_message WHERE content_ciphertext IS NOT NULL AND content_key_v IS NULL"),
            ["chat attachments"] = Count(chat, "SELECT COUNT(*) FROM chat_attachment WHERE iv IS NOT NULL"),
            ["chat legacy attachments"] = Count(chat, "SELECT COUNT(*) FROM chat_attachment WHERE iv IS NULL"),
            ["chat legacy plaintext"] = Count(chat, "SELECT COUNT(*) FROM chat_message WHERE content_text != ''"),
            ["chat api keys"] = Count(chat, "SELECT COUNT(*) FROM chat_api_key WHERE ciphertext IS NOT NULL"),
        };
    }

    protected void Mutate(params string[] sql) => MutateIn(Main, sql);

    protected void MutateIn(string db, params string[] sql)
    {
        using var conn = Open(db, readOnly: false);
        foreach (var statement in sql)
            conn.Execute(statement).Should().BeGreaterThan(0, $"the test's premise is that '{statement}' changes a row");
    }

    /// <summary>Every file of the vault's directory, with its bytes' hash.</summary>
    protected List<string> Fingerprint()
    {
        SqliteConnection.ClearAllPools();
        return Directory.EnumerateFiles(_vault, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(f => $"{Path.GetRelativePath(_vault, f)} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))}")
            .ToList();
    }

    protected static SqliteConnection Open(string path, bool readOnly)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        conn.Open();
        return conn;
    }

    protected static string Upper(Guid id) => id.ToString().ToUpperInvariant();

    protected string Main => Path.Combine(_vault, "beememorybank.db");
    protected string Chat => Path.Combine(_vault, "chat.db");
    protected SessionService Session => _factory.Services.GetRequiredService<SessionService>();
    protected DbConnectionFactory Db => _factory.Services.GetRequiredService<DbConnectionFactory>();
}
