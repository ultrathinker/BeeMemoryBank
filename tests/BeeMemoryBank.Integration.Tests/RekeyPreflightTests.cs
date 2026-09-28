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
/// The pre-flight of the offline re-key (rekey-offline.md §2 step 1), against a real vault: every row the re-key
/// touches must open with a key it holds, or the verb stops before it creates anything. The vault is built by the
/// product's own services, then frozen into a directory of its own (the node is stopped during a real re-key; here
/// the running test host would otherwise write under the test's feet), and the pre-flight runs on it read-only.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class RekeyPreflightTests : IAsyncLifetime
{
    private const string Password = "rekeyPreflightPw1!";
    private readonly BmbWebApplicationFactory _factory = new();
    private string _vault = null!;
    private string _osTemp = null!;
    private Fixture _data = null!;

    public async Task InitializeAsync()
    {
        await _factory.InitializeNodeAsync(password: Password);
        (await Session.UnlockAsync(Password)).Should().BeTrue();
        _vault = _factory.DataPath + "-preflight";
        _osTemp = _factory.DataPath + "-ostemp";
        Directory.CreateDirectory(_osTemp);
        _data = await BuildAsync();
        Freeze();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        _factory.Dispose();
        foreach (var dir in new[] { _vault, _osTemp })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AVaultWithEveryKindOfRow_Passes()
    {
        CountRows().Should().OnlyContain(kv => kv.Value > 0, "the fixture must hold every kind of row it claims to");

        var report = await RunAsync();

        report.Blocking.Should().BeEmpty();
        report.BytesNeeded.Should().BeGreaterThan(0);
    }

    /// <summary>The floor: one key that does not open is listed with its row, the verb refuses, and the vault is left
    /// byte for byte as it was.</summary>
    [Fact]
    public async Task OneKeyThatDoesNotOpen_IsListed_AndTheVaultIsUntouched()
    {
        Mutate($"UPDATE tbl_article_version SET encrypted_dek = randomblob(length(encrypted_dek)) WHERE id = '{_data.Version}'");
        var before = Fingerprint();

        var report = await RunAsync();

        report.Blocking.Should().ContainSingle(p => p.Table == "tbl_article_version" && p.RowKey.Equals(_data.Version, StringComparison.OrdinalIgnoreCase));
        Fingerprint().Should().Equal(before, "the pre-flight writes nothing");
    }

    [Theory]
    [InlineData("tbl_article_body", "encrypted_dek")]
    [InlineData("tbl_conflict_version", "ciphertext")]
    [InlineData("tbl_media", "encrypted_dek")]
    [InlineData("tbl_remote_account", "encrypted_token")]
    [InlineData("tbl_sealed_secret", "wrapped")]
    public async Task EachKindOfRow_ThatDoesNotOpen_Blocks(string table, string column)
    {
        Mutate($"UPDATE {table} SET {column} = randomblob(length({column}))");

        var report = await RunAsync();

        report.Blocking.Should().Contain(p => p.Table == table);
    }

    [Fact]
    public async Task EveryComment_IsOpened_NotOnlyTheFirst()
    {
        Mutate($"UPDATE tbl_comment SET ciphertext = randomblob(length(ciphertext)) WHERE comment_id = '{_data.LastComment}'");

        var report = await RunAsync();

        report.Blocking.Should().ContainSingle(p => p.Table == "tbl_comment" && p.RowKey.Equals(_data.LastComment.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A purged article keeps its comments; its key survives only in the event log, which the pre-flight
    /// reads as a key source. Without it the comment cannot be re-sealed, and that blocks.</summary>
    [Fact]
    public async Task ACommentOfAPurgedArticle_OpensThroughItsEvent_AndBlocksWithoutIt()
    {
        Mutate($"DELETE FROM tbl_article_version WHERE article_id = '{Upper(_data.Purged)}' COLLATE NOCASE",
               $"DELETE FROM tbl_article_body WHERE article_id = '{Upper(_data.Purged)}' COLLATE NOCASE");
        (await RunAsync()).Blocking.Should().BeEmpty("the article's key is still in its events");

        Mutate($"DELETE FROM tbl_event WHERE article_id = '{Upper(_data.Purged)}' COLLATE NOCASE");
        (await RunAsync()).Blocking.Should().Contain(p => p.Table == "tbl_comment");
    }

    [Fact]
    public async Task SoftDeletedRows_AreInventoriedToo()
    {
        Mutate($"UPDATE tbl_comment SET ciphertext = randomblob(length(ciphertext)) WHERE comment_id = '{_data.DeletedComment}'",
               $"UPDATE tbl_media SET encrypted_dek = randomblob(length(encrypted_dek)) WHERE id = '{Upper(_data.DeletedMedia)}'");

        var report = await RunAsync();

        report.Blocking.Should().Contain(p => p.Table == "tbl_comment" && p.RowKey.Equals(_data.DeletedComment.ToString(), StringComparison.OrdinalIgnoreCase));
        report.Blocking.Should().Contain(p => p.Table == "tbl_media" && p.RowKey.Equals(_data.DeletedMedia.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MediaOnlyInItsEncFile_IsOpenedFromTheFile()
    {
        var media = Upper(_data.Media);
        byte[] blob;
        using (var conn = Open(Main, readOnly: true))
            blob = conn.ExecuteScalar<byte[]>($"SELECT b.data FROM tbl_media m JOIN tbl_blob b ON b.hash = m.ciphertext_sha256 WHERE m.id = '{media}'")!;
        Mutate($"UPDATE tbl_media SET ciphertext_sha256 = NULL WHERE id = '{media}'");
        Directory.CreateDirectory(Path.Combine(_vault, "media"));
        var file = Path.Combine(_vault, "media", $"{media}.enc");
        File.WriteAllBytes(file, blob);
        (await RunAsync()).Blocking.Should().BeEmpty();

        blob[^1] ^= 0xFF;
        File.WriteAllBytes(file, blob);
        (await RunAsync()).Blocking.Should().ContainSingle(p => p.Table == "tbl_media");
    }

    [Fact]
    public async Task CiphertextThatIsAlreadyGone_IsAWarning_NotABlock()
    {
        Mutate($"DELETE FROM tbl_blob WHERE hash = (SELECT ciphertext_hash FROM tbl_article_version WHERE id = '{_data.Version}')");

        var report = await RunAsync();

        report.Blocking.Should().BeEmpty();
        report.Warnings.Should().Contain(w => w.Contains(_data.Version, StringComparison.OrdinalIgnoreCase) && w.Contains("gone"));
    }

    [Fact]
    public async Task ChatRows_ThatDoNotOpen_Block()
    {
        MutateIn(Chat, $"UPDATE chat_message SET content_ciphertext = randomblob(length(content_ciphertext)) WHERE id = '{Upper(_data.ChatMessage)}'",
                     $"UPDATE chat_message SET content_key_v = -1 WHERE id = '{Upper(_data.LegacyDekMessage)}'");

        var report = await RunAsync();

        report.Blocking.Should().Contain(p => p.Table == "chat_message.content_ciphertext" && p.RowKey.Equals(_data.ChatMessage.ToString(), StringComparison.OrdinalIgnoreCase));
        report.Blocking.Should().Contain(p => p.RowKey.Equals(_data.LegacyDekMessage.ToString(), StringComparison.OrdinalIgnoreCase) && p.Problem.Contains("unreadable"));
    }

    [Fact]
    public async Task AChatKeyThatDoesNotOpen_Blocks()
    {
        Mutate($"UPDATE tbl_node_data_key SET wrapped_key = randomblob(length(wrapped_key)) WHERE key_name = 'chat'");

        var report = await RunAsync();

        report.Blocking.Should().Contain(p => p.Table == "tbl_node_data_key" && p.RowKey == "chat");
    }

    [Theory]
    [InlineData("PROPOSED")]
    [InlineData("COMMITTING")]
    public async Task ARotationInFlight_Refuses(string state)
    {
        Mutate($"INSERT INTO tbl_dek_rotation_state (event_id, state, rotation_ts, created_at, updated_at) VALUES ('r1', '{state}', 'now', 'now', 'now')");

        (await RunAsync()).Blocking.Should().ContainSingle(p => p.Table == "tbl_dek_rotation_state");
    }

    [Fact]
    public async Task AFinishedRotation_DoesNotRefuse()
    {
        Mutate("INSERT INTO tbl_dek_rotation_state (event_id, state, rotation_ts, created_at, updated_at) VALUES ('r1', 'APPLIED', 'now', 'now', 'now')");

        (await RunAsync()).Blocking.Should().BeEmpty();
    }

    [Fact]
    public async Task APendingRestore_Refuses_InEachOfItsForms()
    {
        Mutate("INSERT INTO tbl_restore_event_state (event_id, state, created_at, updated_at) VALUES ('e1', 'PENDING', 'now', 'now')");
        (await RunAsync()).Blocking.Should().ContainSingle(p => p.Table == "tbl_restore_event_state");
        Mutate("DELETE FROM tbl_restore_event_state");

        Directory.CreateDirectory(Path.Combine(_vault, "snapshots", "restore-pending"));
        File.WriteAllText(Path.Combine(_vault, "snapshots", "restore-pending", "upload.bmbsnap"), "x");
        (await RunAsync()).Blocking.Should().ContainSingle(p => p.RowKey.EndsWith("restore-pending"));
        Directory.Delete(Path.Combine(_vault, "snapshots", "restore-pending"), recursive: true);

        File.WriteAllText(Path.Combine(_vault, "beememorybank.db.standalone-staging"), "x");
        (await RunAsync()).Blocking.Should().ContainSingle(p => p.RowKey.EndsWith("standalone-staging"));
    }

    [Fact]
    public async Task NotEnoughSpace_Refuses_AndSoDoesSpaceThatCannotBeMeasured()
    {
        var needed = (await RunAsync()).BytesNeeded;
        var sizes = new[] { "beememorybank.db", "chat.db" }.Sum(f => new FileInfo(Path.Combine(_vault, f)).Length)
                    + Directory.EnumerateFiles(Path.Combine(_vault, "media"), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        needed.Should().Be((long)Math.Ceiling(sizes * RekeyPreflight.SpaceFactor));

        (await RunAsync(free: needed - 1)).Blocking.Should().ContainSingle(p => p.Table == "(volume)" && p.Problem.Contains("not enough"));
        (await RunAsync(free: needed)).Blocking.Should().BeEmpty();
        (await RunAsync(unmeasurable: true)).Blocking.Should().ContainSingle(p => p.Table == "(volume)" && p.Problem.Contains("cannot be measured"));
    }

    /// <summary>A vault copy left in the OS temp folder is found whatever its extension, and only listed: the
    /// pre-flight deletes nothing.</summary>
    [Fact]
    public async Task VaultCopiesInTheOsTempFolder_AndBlindFolders_AreListed_NeverDeleted()
    {
        var copy = Path.Combine(_osTemp, "tmp5A3F.tmp");
        File.Copy(Path.Combine(_vault, "beememorybank.db"), copy);
        var notAVault = Path.Combine(_osTemp, "other.tmp");
        File.WriteAllText(notAVault, "not sqlite");
        var restoreDir = Directory.CreateDirectory(Path.Combine(_osTemp, "bmb-restore-1234")).FullName;
        var blind = Directory.CreateDirectory(Path.Combine(_vault, "blind-tmp")).FullName;

        var report = await RunAsync();

        report.Blocking.Should().BeEmpty();
        report.Warnings.Should().Contain(w => w.StartsWith(copy));
        report.Warnings.Should().Contain(w => w.StartsWith(restoreDir));
        report.Warnings.Should().Contain(w => w.StartsWith(blind));
        report.Warnings.Should().NotContain(w => w.StartsWith(notAVault));
        File.Exists(copy).Should().BeTrue();
        Directory.Exists(restoreDir).Should().BeTrue();
        Directory.Exists(blind).Should().BeTrue();
    }

    /// <summary>The keys belong to the verb; the pre-flight neither clears nor keeps any of them.</summary>
    [Fact]
    public async Task TheCandidateKeys_AreLeftAsTheyWere()
    {
        var dek = Session.GetMasterDek();
        var retired = RandomNumberGenerator.GetBytes(32);
        using var keys = new RekeyKeys((byte[])dek.Clone(), [(byte[])retired.Clone()], new byte[32], new byte[32]);

        using (var main = Open(Main, readOnly: true))
        using (var chat = Open(Chat, readOnly: true))
            await new RekeyPreflight { OsTempDir = _osTemp, FreeBytes = _ => long.MaxValue }
                .RunAsync(_vault, main, chat, keys, CancellationToken.None);

        keys.OldCandidates[0].Should().Equal(dek);
        keys.OldCandidates[1].Should().Equal(retired);
    }

    [Fact]
    public async Task AKeyThatOnlyARetiredCandidateOpens_Passes()
    {
        var report = await RunAsync(retiredFirst: true);

        report.Blocking.Should().BeEmpty("every candidate is tried, not only the predecessor");
    }

    // ─── Fixture ────────────────────────────────────────────────────────────

    private sealed record Fixture(string Version, Guid LastComment, Guid DeletedComment, Guid Purged, Guid Media, Guid DeletedMedia,
        Guid ChatMessage, Guid LegacyDekMessage);

    /// <summary>Two articles with versions, several comments (one soft-deleted, one legacy-AAD), a conflict, media (one
    /// soft-deleted), a sealed secret, a remote token, and chat rows of every kind this schema has.</summary>
    private async Task<Fixture> BuildAsync()
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
            Id = Guid.NewGuid(), ConversationId = conversation, Role = "assistant", ContentText = "chat content",
            ToolCallsJson = "[{\"note\":\"tool\"}]", CreatedAt = DateTime.UtcNow,
        };
        await sp.GetRequiredService<ChatMessageRepository>().CreateAsync(message, Session);
        await sp.GetRequiredService<ChatAttachmentRepository>().CreateAsync(new ChatAttachment
            { Id = Guid.NewGuid(), MessageId = message.Id, Kind = "user-upload", Mime = "image/png", Blob = RandomNumberGenerator.GetBytes(64) }, Session);
        var settings = sp.GetRequiredService<ChatSettingsRepository>();
        var apiKey = new ChatApiKey { Id = Guid.NewGuid(), Label = "k", KeyPrefix = "sk-or-v1-abc", CreatedAt = DateTime.UtcNow };
        await settings.SealSecretAsync(apiKey, "sk-or-v1-abcdef-provider-key");
        await settings.CreateAsync(apiKey);

        // Legacy chat rows: plaintext from before chat encryption, and a message sealed under the master DEK.
        var legacyDekMessage = Guid.NewGuid();
        var (legacyChatCt, legacyChatIv) = ArticleEncryptor.Encrypt("legacy dek message", dek, "bmb-chat-message-content-v1"u8.ToArray());
        using (var chat = (SqliteConnection)sp.GetRequiredService<ChatDbConnectionFactory>().CreateConnection())
        {
            await chat.ExecuteAsync(
                "INSERT INTO chat_message (id, conversation_id, role, content_text, created_at) VALUES (@id, @c, 'user', 'legacy plain', @now)",
                new { id = Guid.NewGuid(), c = conversation, now = DateTime.UtcNow.ToString("o") });
            await chat.ExecuteAsync(
                "INSERT INTO chat_message (id, conversation_id, role, content_ciphertext, content_iv, created_at) VALUES (@id, @c, 'user', @ct, @iv, @now)",
                new { id = legacyDekMessage, c = conversation, ct = legacyChatCt, iv = legacyChatIv, now = DateTime.UtcNow.ToString("o") });
        }
        Array.Clear(dek);

        string version;
        using (var conn = Db.CreateConnection())
            version = await conn.ExecuteScalarAsync<string>(
                "SELECT id FROM tbl_article_version WHERE article_id = @id COLLATE NOCASE ORDER BY version_number LIMIT 1", new { id = first.Id.ToString() }) ?? "";
        return new Fixture(version, last.CommentId, deleted.CommentId, purged.Id, media.Id, deletedMedia.Id, message.Id, legacyDekMessage);
    }

    /// <summary>The vault as the stopped node leaves it: both databases whole (no WAL) and the media folder.</summary>
    private void Freeze()
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

    private async Task<RekeyPreflightReport> RunAsync(long? free = long.MaxValue, bool unmeasurable = false, bool retiredFirst = false)
    {
        var dek = Session.GetMasterDek();
        using var keys = retiredFirst
            ? new RekeyKeys(RandomNumberGenerator.GetBytes(32), [dek], new byte[32], new byte[32])
            : new RekeyKeys(dek, [], new byte[32], new byte[32]);
        using var main = Open(Main, readOnly: true);
        using var chat = Open(Chat, readOnly: true);
        return await new RekeyPreflight { OsTempDir = _osTemp, FreeBytes = _ => unmeasurable ? null : free }
            .RunAsync(_vault, main, chat, keys, CancellationToken.None);
    }

    private Dictionary<string, long> CountRows()
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
            ["chat api keys"] = Count(chat, "SELECT COUNT(*) FROM chat_api_key WHERE ciphertext IS NOT NULL"),
        };
    }

    private void Mutate(params string[] sql) => MutateIn(Main, sql);

    private void MutateIn(string db, params string[] sql)
    {
        using var conn = Open(db, readOnly: false);
        foreach (var statement in sql) conn.Execute(statement);
    }

    /// <summary>Every file of the vault's directory, with its bytes' hash.</summary>
    private List<string> Fingerprint()
    {
        SqliteConnection.ClearAllPools();
        return Directory.EnumerateFiles(_vault, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(f => $"{Path.GetRelativePath(_vault, f)} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))}")
            .ToList();
    }

    private static SqliteConnection Open(string path, bool readOnly)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        conn.Open();
        return conn;
    }

    private static string Upper(Guid id) => id.ToString().ToUpperInvariant();

    private string Main => Path.Combine(_vault, "beememorybank.db");
    private string Chat => Path.Combine(_vault, "chat.db");
    private SessionService Session => _factory.Services.GetRequiredService<SessionService>();
    private DbConnectionFactory Db => _factory.Services.GetRequiredService<DbConnectionFactory>();
}
