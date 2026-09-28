using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
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
/// The floor tests of R1's part of the offline re-key (rekey-offline.md §4): <see cref="RowResealStep"/> and
/// <see cref="DerivedDataClearStep"/> on a <c>VACUUM INTO</c> copy of a real node's vault holding every entity kind.
/// Data loss: every row reads back identically under the new key, and a row that cannot be re-sealed fails the
/// attempt with the copy untouched. Old-key access: no row of the copy opens under the old key, and no old ciphertext
/// is left in it. The table classification covers every table the migrations create.
/// </summary>
public sealed class RekeyRowResealTests : IAsyncLifetime
{
    private const string Password = "rekeyOfflinePw1";
    private readonly BmbWebApplicationFactory _factory = new();
    private readonly List<string> _copies = [];
    private Guid _live, _deleted, _blobMedia, _encMedia;
    private int _plainComment, _deletedComment, _sealedComment;
    private byte[] _mediaBytes = [];
    private byte[] _secret = [];
    private const string SecretName = "floor-test-secret";
    private const string RemoteId = "remote-1";
    private const string RemoteToken = "bearer-floor-token";

    public async Task InitializeAsync()
    {
        await _factory.InitializeNodeAsync("Owner", Password);
        (await Session.UnlockAsync(Password)).Should().BeTrue();
        using var scope = _factory.Services.CreateScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var comments = scope.ServiceProvider.GetRequiredService<CommentService>();
        var media = scope.ServiceProvider.GetRequiredService<MediaService>();

        _live = (await articles.CreateAsync("Live", "/Notes", [], "live body v1")).Id;
        await articles.UpdateAsync(_live, plaintext: "live body v2"); // a version row
        _sealedComment = (await comments.CreateAsync(_live, "an encrypted comment")).Id;
        _deleted = (await articles.CreateAsync("Gone", "/Notes", [], "deleted body")).Id;
        _deletedComment = (await comments.CreateAsync(_deleted, "a comment later deleted")).Id;
        await comments.DeleteAsync(_deletedComment);
        _mediaBytes = RandomNumberGenerator.GetBytes(4096);
        _blobMedia = (await media.CreateAsync("data.bin", "application/octet-stream", _mediaBytes, _live, isAttachment: true)).Id;
        _encMedia = (await media.CreateAsync("old.bin", "application/octet-stream", _mediaBytes, _deleted, isAttachment: true)).Id;
        await articles.DeleteAsync(_deleted);

        using var conn = Db.CreateConnection();
        // A legacy plaintext comment, as older builds wrote them.
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_comment (article_id, text, created_at, comment_id, encrypted, lamport_ts)
              VALUES ((SELECT id FROM tbl_article WHERE id = @a COLLATE NOCASE), 'plaintext note', @t, @c, 0, 1)",
            new { a = _live.ToString(), t = DateTime.UtcNow.ToString("O"), c = Guid.NewGuid().ToString() });
        _plainComment = await conn.ExecuteScalarAsync<int>("SELECT id FROM tbl_comment WHERE encrypted = 0");
        // A conflict copy, carrying a copy of the body's wrapper like the applier's.
        var body = await conn.QuerySingleAsync<(byte[] Iv, byte[] Wrapped, byte[] DekIv, byte[] Ct)>(
            @"SELECT b.iv, b.encrypted_dek, b.dek_iv, bl.data FROM tbl_article_body b JOIN tbl_blob bl ON bl.hash = b.ciphertext_hash
              WHERE b.article_id = @a COLLATE NOCASE", new { a = _live.ToString() });
        await scope.ServiceProvider.GetRequiredService<IConflictVersionRepository>().CreateAsync(new ConflictVersion
        {
            Id = Guid.NewGuid(), ArticleId = _live, SourceNodeId = Guid.NewGuid(), LamportTs = 3,
            Ciphertext = body.Ct, IV = body.Iv, EncryptedDek = body.Wrapped, DekIV = body.DekIv,
            MetadataJson = "{}", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(7)
        });
        // Media whose ciphertext lives only in media/{id}.enc (a node from before the blob store).
        var encHash = await conn.ExecuteScalarAsync<string>("SELECT ciphertext_sha256 FROM tbl_media WHERE id = @id COLLATE NOCASE", new { id = _encMedia.ToString() });
        var encCt = await conn.ExecuteScalarAsync<byte[]>("SELECT data FROM tbl_blob WHERE hash = @encHash", new { encHash });
        Directory.CreateDirectory(Path.Combine(_factory.DataPath, "media"));
        await File.WriteAllBytesAsync(EncPath, encCt);
        await conn.ExecuteAsync("UPDATE tbl_media SET ciphertext_sha256 = NULL WHERE id = @id COLLATE NOCASE", new { id = _encMedia.ToString() });
        await conn.ExecuteAsync("DELETE FROM tbl_blob WHERE hash = @encHash", new { encHash });

        var dek = Session.GetMasterDek();
        // A sealed secret and a remote-account token, both directly under the master DEK.
        _secret = RandomNumberGenerator.GetBytes(32);
        var (sw, siv) = SealedSecretCrypto.Seal(SecretName, _secret, dek);
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_sealed_secret (name, dek_fingerprint, wrapped, iv, updated_at, status, lamport_ts, source_node_id)
              VALUES (@SecretName, @fp, @sw, @siv, @t, 'A', 1, @n)",
            new { SecretName, fp = DekFingerprint.Of(dek), sw, siv, t = DateTime.UtcNow.ToString("O"), n = Guid.NewGuid().ToString() });
        var (tc, tiv) = RemoteAccountService.SealToken(RemoteToken, dek);
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_remote_account (id, display_name, base_url, remote_username, encrypted_token, token_iv, created_at, updated_at)
              VALUES (@RemoteId, 'Remote', 'https://remote.invalid', 'me', @tc, @tiv, @t, @t)",
            new { RemoteId, tc, tiv, t = DateTime.UtcNow.ToString("O") });
        // Derived data: a tag vector, a chunk embedding, an article projection.
        await conn.ExecuteAsync("INSERT INTO tbl_concept_tag (name, created_at, embedding, embedding_model_version) VALUES ('tag', @t, x'0102', 'm1')",
            new { t = DateTime.UtcNow.ToString("O") });
        await conn.ExecuteAsync("INSERT INTO tbl_article_chunk_embedding (article_id, chunk_index, projection, scale, model_version) VALUES ((SELECT id FROM tbl_article WHERE id = @a COLLATE NOCASE), 0, x'0102', 1.0, 'm1')",
            new { a = _live.ToString() });
        await conn.ExecuteAsync("UPDATE tbl_article SET embedding_projection = x'0102', embedding_pending = 0, index_pending = 0");
        Array.Clear(dek);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var copy in _copies)
            try { Directory.Delete(Path.GetDirectoryName(copy)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private SessionService Session => _factory.Services.GetRequiredService<SessionService>();
    private DbConnectionFactory Db => _factory.Services.GetRequiredService<DbConnectionFactory>();
    private string EncPath => Path.Combine(_factory.DataPath, "media", $"{_encMedia}.enc");

    private sealed class NullProgress : IRekeyProgress
    {
        public void Report(string step, long done, long total, string? note = null) { }
    }

    /// <summary>A VACUUM INTO copy of the live vault, the way the orchestrator makes it, and the keys: D2 from the
    /// session, a fresh D_c.</summary>
    private async Task<RekeyContext> CopyAsync()
    {
        var work = Path.Combine(_factory.DataPath + ".rekey-new-" + _copies.Count);
        Directory.CreateDirectory(work);
        var path = Path.Combine(work, "beememorybank.db");
        _copies.Add(path);
        using (var live = Db.CreateConnection())
            await live.ExecuteAsync($"VACUUM INTO '{path.Replace("'", "''")}'");
        var copy = new SqliteConnection($"Data Source={path};Pooling=False");
        await copy.OpenAsync();
        var candidates = Session.GetCandidateDeks();
        var keys = new RekeyKeys(candidates[0], candidates.Skip(1).ToList(), MasterKeyManager.GenerateMasterDek(), RandomNumberGenerator.GetBytes(32));
        keys.CampaignDek.Should().NotEqual(keys.Predecessor, "a real, distinct new DEK");
        return new RekeyContext(_factory.DataPath, work, copy, null, keys, Guid.NewGuid(), new NullProgress(), CancellationToken.None);
    }

    /// <summary>Every sealed value of <paramref name="conn"/> opened with <paramref name="masters"/>: key → plaintext, null where it does not open.</summary>
    private static async Task<Dictionary<string, string?>> OpenAllAsync(SqliteConnection conn, string sourceDir, IReadOnlyList<byte[]> masters)
    {
        var result = new Dictionary<string, string?>();
        static string? Hex(byte[]? b) => b == null ? null : Convert.ToHexString(b);
        async Task<byte[]?> Blob(string? hash) =>
            hash == null ? null : await conn.ExecuteScalarAsync<byte[]?>("SELECT data FROM tbl_blob WHERE hash = @hash", new { hash });

        var bodyKeys = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in await conn.QueryAsync<(string Id, string? Hash, byte[] Iv, byte[] W, byte[] DekIv)>(
                     "SELECT article_id, ciphertext_hash, iv, encrypted_dek, dek_iv FROM tbl_article_body"))
        {
            var a = Guid.Parse(b.Id);
            var k = RowResealStep.OpenWrapper(EnvelopeFraming.Article, a, b.W, b.DekIv, masters);
            bodyKeys[b.Id] = k;
            var ct = await Blob(b.Hash);
            result[$"body:{a}"] = k == null || ct == null ? null : Hex(RowResealStep.Try(() => EnvelopeFraming.Article.DecryptBody(a, b.W, k, ct, b.Iv)));
        }
        foreach (var c in await conn.QueryAsync<(long Id, string ArticleId, string? CommentId, long Enc, string Text, byte[]? Ct, byte[]? Iv)>(
                     "SELECT id, article_id, comment_id, encrypted, text, ciphertext, iv FROM tbl_comment"))
        {
            var k = bodyKeys.GetValueOrDefault(c.ArticleId);
            result[$"comment:{c.Id}"] = c.Enc == 0 ? (masters.Count == 0 ? null : c.Text) // plaintext needs no key
                : k == null || c.Ct == null || c.Iv == null ? null
                : RowResealStep.TryOpenComment(Guid.Parse(c.ArticleId), c.CommentId, c.Ct, c.Iv, k);
        }
        foreach (var (table, sql) in new[]
                 {
                     ("version", "SELECT v.id, v.article_id, v.iv, v.encrypted_dek, v.dek_iv, b.data FROM tbl_article_version v LEFT JOIN tbl_blob b ON b.hash = v.ciphertext_hash"),
                     ("conflict", "SELECT id, article_id, iv, encrypted_dek, dek_iv, ciphertext FROM tbl_conflict_version"),
                 })
            foreach (var r in await conn.QueryAsync<(string Id, string ArticleId, byte[] Iv, byte[] W, byte[] DekIv, byte[]? Ct)>(sql))
            {
                var a = Guid.Parse(r.ArticleId);
                var k = RowResealStep.OpenWrapper(EnvelopeFraming.Article, a, r.W, r.DekIv, masters);
                result[$"{table}:{r.Id}"] = k == null || r.Ct == null ? null : Hex(RowResealStep.Try(() => EnvelopeFraming.Article.DecryptBody(a, r.W, k, r.Ct, r.Iv)));
            }
        foreach (var m in await conn.QueryAsync<(string Id, string? Hash, byte[] Iv, byte[] W, byte[] DekIv)>(
                     "SELECT id, ciphertext_sha256, iv, encrypted_dek, dek_iv FROM tbl_media"))
        {
            var id = Guid.Parse(m.Id);
            var ct = await Blob(m.Hash);
            if (ct == null && RowResealStep.MediaFile(sourceDir, m.Id) is { } p && File.Exists(p)) ct = await File.ReadAllBytesAsync(p);
            var k = RowResealStep.OpenWrapper(EnvelopeFraming.Media, id, m.W, m.DekIv, masters);
            result[$"media:{id}"] = k == null || ct == null ? null : Hex(RowResealStep.Try(() => EnvelopeFraming.Media.DecryptBody(id, m.W, k, ct, m.Iv)));
        }
        foreach (var s in await conn.QueryAsync<(string Name, byte[] W, byte[] Iv)>("SELECT name, wrapped, iv FROM tbl_sealed_secret"))
            result[$"secret:{s.Name}"] = Hex(masters.Select(k => SealedSecretCrypto.TryOpen(s.Name, s.W, s.Iv, k)).FirstOrDefault(x => x != null));
        foreach (var r in await conn.QueryAsync<(string Id, byte[] T, byte[] Iv)>("SELECT id, encrypted_token, token_iv FROM tbl_remote_account"))
            result[$"remote:{r.Id}"] = masters.Select(k => RemoteAccountService.TryOpenToken(r.T, r.Iv, k)).FirstOrDefault(x => x != null);
        return result;
    }

    private static async Task RunStepsAsync(RekeyContext ctx)
    {
        await new RowResealStep().RunAsync(ctx);
        await new DerivedDataClearStep().RunAsync(ctx);
    }

    [Fact]
    public async Task EveryEntityKind_ReadsBackIdentically_UnderTheNewKeyAlone()
    {
        Dictionary<string, string?> before;
        using (var live = (SqliteConnection)Db.CreateConnection())
            before = await OpenAllAsync(live, _factory.DataPath, Session.GetCandidateDeks());
        before.Values.Should().OnlyContain(v => v != null, "the fixture opens entirely under the old key");
        before.Keys.Should().Contain([$"body:{_deleted}", $"comment:{_deletedComment}", $"comment:{_plainComment}", $"media:{_encMedia}",
            $"secret:{SecretName}", $"remote:{RemoteId}"]);
        before.Keys.Should().Contain(k => k.StartsWith("version:")).And.Contain(k => k.StartsWith("conflict:"));
        var ctx = await CopyAsync();
        using var keys = ctx.Keys;

        await RunStepsAsync(ctx);

        (await new RowResealStep().VerifyAsync(ctx)).Should().BeEmpty();
        (await new DerivedDataClearStep().VerifyAsync(ctx)).Should().BeEmpty();
        var after = await OpenAllAsync(ctx.Main, ctx.WorkDir, [keys.CampaignDek]);
        after.Should().BeEquivalentTo(before, "every row reads back identically under D_c");
        var underOld = await OpenAllAsync(ctx.Main, ctx.WorkDir, keys.OldCandidates);
        underOld.Where(kv => !kv.Key.StartsWith("comment:") || kv.Value != null).Should().OnlyContain(kv => kv.Value == null,
            "no row of the copy opens under the old key");
        (await ctx.Main.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_comment WHERE encrypted = 0 OR text <> ''")).Should().Be(0);
        (await ctx.Main.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_media WHERE ciphertext_sha256 IS NULL")).Should().Be(0,
            "media from .enc files are in blobs now");
        (await ctx.Main.ExecuteScalarAsync<string>("SELECT dek_fingerprint FROM tbl_sealed_secret")).Should().Be(DekFingerprint.Of(keys.CampaignDek));
    }

    [Fact]
    public async Task NoOldCiphertext_IsLeftInTheCopy()
    {
        List<byte[]> oldCiphertexts;
        using (var live = (SqliteConnection)Db.CreateConnection())
        {
            oldCiphertexts = (await live.QueryAsync<byte[]>("SELECT data FROM tbl_blob")).ToList();
            oldCiphertexts.AddRange(await live.QueryAsync<byte[]>("SELECT ciphertext FROM tbl_conflict_version"));
            oldCiphertexts.AddRange(await live.QueryAsync<byte[]>("SELECT ciphertext FROM tbl_comment WHERE ciphertext IS NOT NULL"));
            oldCiphertexts.AddRange(await live.QueryAsync<byte[]>("SELECT encrypted_dek FROM tbl_article_body"));
            oldCiphertexts.AddRange(await live.QueryAsync<byte[]>("SELECT wrapped FROM tbl_sealed_secret"));
            oldCiphertexts.AddRange(await live.QueryAsync<byte[]>("SELECT encrypted_token FROM tbl_remote_account"));
        }
        oldCiphertexts.Add(await File.ReadAllBytesAsync(EncPath));
        var ctx = await CopyAsync();
        using var keys = ctx.Keys;

        await RunStepsAsync(ctx);
        await ctx.Main.ExecuteAsync("VACUUM"); // the orchestrator's scrub (L), so freed pages do not count

        (await new RowResealStep().VerifyAsync(ctx)).Should().NotContain(p => p.Table == RowResealStep.Blob);
        ctx.Main.Close(); // Windows does not let the file be read while the connection holds it
        var file = await File.ReadAllBytesAsync(Path.Combine(ctx.WorkDir, "beememorybank.db"));
        foreach (var old in oldCiphertexts.Where(o => o.Length >= 16))
            file.AsSpan().IndexOf(old.AsSpan(0, 16)).Should().Be(-1, "no old ciphertext survives in the copy");
    }

    [Fact]
    public async Task ASealedCommentWithoutItsCiphertext_FailsTheAttempt_AndIsNeverResealedEmpty()
    {
        using (var conn = Db.CreateConnection())
            await conn.ExecuteAsync("UPDATE tbl_comment SET ciphertext = NULL WHERE id = @_sealedComment", new { _sealedComment });
        var ctx = await CopyAsync();
        using var keys = ctx.Keys;
        var bodyBefore = await ctx.Main.ExecuteScalarAsync<byte[]>("SELECT encrypted_dek FROM tbl_article_body WHERE article_id = @a COLLATE NOCASE", new { a = _live.ToString() });

        var run = () => new RowResealStep().RunAsync(ctx);

        var ex = (await run.Should().ThrowAsync<RekeyRowsUnopenableException>()).Which;
        ex.Problems.Should().Contain(p => p.Table == RowResealStep.Comment && p.Problem.Contains("missing"));
        (await ctx.Main.ExecuteScalarAsync<long>("SELECT encrypted FROM tbl_comment WHERE id = @_sealedComment", new { _sealedComment })).Should().Be(1);
        (await ctx.Main.ExecuteScalarAsync<byte[]>("SELECT encrypted_dek FROM tbl_article_body WHERE article_id = @a COLLATE NOCASE", new { a = _live.ToString() }))
            .Should().Equal(bodyBefore, "the attempt rolled back; nothing in the copy changed");
    }

    [Fact]
    public async Task RowsThatDoNotOpen_FailTheAttemptWithTheirList()
    {
        using (var conn = Db.CreateConnection())
        {
            await conn.ExecuteAsync("UPDATE tbl_conflict_version SET ciphertext = randomblob(64)");
            await conn.ExecuteAsync("UPDATE tbl_comment SET ciphertext = randomblob(64) WHERE id = @_deletedComment", new { _deletedComment });
        }
        var ctx = await CopyAsync();
        using var keys = ctx.Keys;

        var run = () => new RowResealStep().RunAsync(ctx);

        var ex = (await run.Should().ThrowAsync<RekeyRowsUnopenableException>()).Which;
        ex.Problems.Should().Contain(p => p.Table == RowResealStep.Conflict && p.Problem.Contains("does not open"));
        ex.Problems.Should().Contain(p => p.Table == RowResealStep.Comment && p.Problem.Contains("does not open"));
    }

    /// <summary>Media only in a <c>.enc</c> file lands in a blob of the copy; the source file is never touched.</summary>
    [Fact]
    public async Task MediaFromAnEncFile_IsImportedIntoABlob_AndTheSourceFileIsLeftAsItWas()
    {
        var fileBefore = await File.ReadAllBytesAsync(EncPath);
        var ctx = await CopyAsync();
        using var keys = ctx.Keys;

        var result = await new RowResealStep().RunAsync(ctx);

        result.Counts["media_imported_from_enc"].Should().Be(1);
        File.Exists(EncPath).Should().BeTrue();
        (await File.ReadAllBytesAsync(EncPath)).Should().Equal(fileBefore);
        var m = await ctx.Main.QuerySingleAsync<(string Hash, byte[] Iv, byte[] W, byte[] DekIv)>(
            "SELECT ciphertext_sha256, iv, encrypted_dek, dek_iv FROM tbl_media WHERE id = @id COLLATE NOCASE", new { id = _encMedia.ToString() });
        var ct = await ctx.Main.ExecuteScalarAsync<byte[]>("SELECT data FROM tbl_blob WHERE hash = @Hash", new { m.Hash });
        var k = EnvelopeFraming.Media.UnwrapDek(_encMedia, m.W, m.DekIv, keys.CampaignDek);
        EnvelopeFraming.Media.DecryptBody(_encMedia, m.W, k, ct, m.Iv).Should().Equal(_mediaBytes);
    }

    /// <summary>
    /// Option C (orchestrator): a sealed comment whose article body was purged is unreadable today and has its key only
    /// in the event log, which the re-key clears. It is dropped from the copy and listed without content.
    /// </summary>
    [Fact]
    public async Task ASealedCommentOfAPurgedBody_IsDroppedFromTheCopy_AndListedWithoutContent()
    {
        string commentId, createdAt;
        using (var conn = Db.CreateConnection())
        {
            (commentId, createdAt) = await conn.QuerySingleAsync<(string, string)>(
                "SELECT comment_id, created_at FROM tbl_comment WHERE id = @_deletedComment", new { _deletedComment });
            await conn.ExecuteAsync("DELETE FROM tbl_article_body WHERE article_id = @a COLLATE NOCASE", new { a = _deleted.ToString() });
        }
        var ctx = await CopyAsync();
        using var keys = ctx.Keys;

        var result = await new RowResealStep().RunAsync(ctx);
        await new DerivedDataClearStep().RunAsync(ctx);

        (await ctx.Main.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_comment WHERE id = @_deletedComment", new { _deletedComment }))
            .Should().Be(0, "dropped from the copy");
        var note = result.Notes.Should().ContainSingle(n => n.StartsWith(RowResealStep.DroppedCommentNote)).Subject;
        note.Should().Be($"{RowResealStep.DroppedCommentNote}{commentId} article:{await ArticleIdAsync(_deleted)} created:{createdAt}");
        note.Should().NotContain("a comment later deleted", "no content goes into the report");
        result.Counts["comments_dropped_body_purged"].Should().Be(1);
        (await new RowResealStep().VerifyAsync(ctx)).Should().BeEmpty();
        using (var live = Db.CreateConnection())
            (await live.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_comment WHERE id = @_deletedComment", new { _deletedComment }))
                .Should().Be(1, "the old vault keeps it");
    }

    private async Task<string> ArticleIdAsync(Guid id)
    {
        using var conn = Db.CreateConnection();
        return await conn.ExecuteScalarAsync<string>("SELECT id FROM tbl_article WHERE id = @a COLLATE NOCASE", new { a = id.ToString() });
    }

    /// <summary>
    /// Review release-b R1-7: a legacy media/{id}.enc that is a link (here, to a file outside the vault) is never read.
    /// The attempt fails with the row named, instead of importing whatever the link points at.
    /// </summary>
    [Fact]
    public async Task ALegacyMediaFileThatIsALink_IsNotFollowed_AndTheAttemptFails()
    {
        var outside = Path.Combine(Path.GetTempPath(), "bmb-outside-" + Guid.NewGuid().ToString("N") + ".enc");
        File.Copy(EncPath, outside);
        File.Delete(EncPath);
        try { File.CreateSymbolicLink(EncPath, outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.Move(outside, EncPath);
            return; // no right to create links on this machine
        }
        try
        {
            var ctx = await CopyAsync();
            using var keys = ctx.Keys;

            var run = () => new RowResealStep().RunAsync(ctx);

            (await run.Should().ThrowAsync<RekeyRowsUnopenableException>()).Which.Problems
                .Should().Contain(pr => pr.Table == RowResealStep.Media && pr.Problem.Contains("link"));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task MediaWithNeitherBlobNorFile_FailsTheAttempt()
    {
        File.Delete(EncPath);
        var ctx = await CopyAsync();
        using var keys = ctx.Keys;

        var run = () => new RowResealStep().RunAsync(ctx);

        (await run.Should().ThrowAsync<RekeyRowsUnopenableException>()).Which.Problems
            .Should().Contain(p => p.Table == RowResealStep.Media && p.RowKey.Equals(_encMedia.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Verify is not vacuous: an untouched copy, or one with a wrapper put back under the old key, fails it.</summary>
    [Fact]
    public async Task Verify_RefusesACopyWithAnOldKeyWrapper()
    {
        var ctx = await CopyAsync();
        using var keys = ctx.Keys;
        (await new RowResealStep().VerifyAsync(ctx)).Should().Contain(p => p.Problem.Contains("old key"));
        var oldWrapper = await ctx.Main.QuerySingleAsync<(byte[] W, byte[] Iv)>(
            "SELECT encrypted_dek, dek_iv FROM tbl_article_body WHERE article_id = @a COLLATE NOCASE", new { a = _live.ToString() });

        await RunStepsAsync(ctx);
        await ctx.Main.ExecuteAsync("UPDATE tbl_article_body SET encrypted_dek = @W, dek_iv = @Iv WHERE article_id = @a COLLATE NOCASE",
            new { oldWrapper.W, oldWrapper.Iv, a = _live.ToString() });

        (await new RowResealStep().VerifyAsync(ctx)).Should().Contain(p => p.Table == RowResealStep.Body && p.Problem.Contains("old key"));
    }

    /// <summary>§8.3: every table the migrations create has a fate, and every listed table exists.</summary>
    [Fact]
    public async Task EveryTableOfTheVault_IsClassified()
    {
        using var conn = Db.CreateConnection();
        var tables = (await conn.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT IN ('sqlite_stat1', 'sqlite_stat4')")).ToList();

        tables.Where(t => !RekeyTables.Main.ContainsKey(t)).Should().BeEmpty("every table needs a fate");
        RekeyTables.Main.Keys.Where(t => !tables.Contains(t, StringComparer.OrdinalIgnoreCase)).Should().BeEmpty("no stale entries");
    }
}
