using System.Security.Cryptography;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Rekey.Steps;

/// <summary>A step found rows it cannot re-seal. The attempt is thrown away and the old vault stays in use.</summary>
public sealed class RekeyRowsUnopenableException(string step, IReadOnlyList<RekeyProblem> problems)
    : InvalidOperationException($"{step}: {problems.Count} row(s) cannot be re-sealed: "
                                + string.Join("; ", problems.Take(20).Select(p => $"{p.Table}:{p.RowKey} {p.Problem}")))
{
    public IReadOnlyList<RekeyProblem> Problems { get; } = problems;
}

/// <summary>
/// The re-seal of every sealed row of the copy (rekey-offline.md §2 step 3, R1; the offline port of WI-7).
/// <list type="bullet">
/// <item>Bodies (<c>tbl_article_body</c>): a fresh article key wrapped under D_c. The article's comments are sealed
///   under that key (a comment has no wrapper of its own), soft-deleted and formerly plaintext ones included; a
///   formerly plaintext comment's <c>text</c> is emptied.</item>
/// <item>Versions and conflicts: a fresh key each, Article framing with the article-id AAD.</item>
/// <item>Media: a fresh key; the ciphertext comes from its blob, or from <c>media/{id}.enc</c> in the source directory
///   (read only), and lands in a new blob.</item>
/// <item>Sealed secrets and remote-account tokens, sealed directly under the master DEK: re-sealed under D_c.</item>
/// <item>The blobs of the old ciphertexts are removed: every blob left is referenced by a re-sealed row.</item>
/// </list>
/// Nothing is filtered by status or <c>deleted_at</c>. A row whose stored form does not open under any old key — a
/// wrapper no candidate opens, a ciphertext that fails, a sealed comment with no ciphertext or IV, media with neither
/// blob nor file, a comment without a body — fails the whole attempt with its list: nothing is ever re-sealed empty
/// or skipped. The pre-flight is meant to have listed such rows first. One transaction; the copy is thrown away on
/// failure, so there is no resume.
/// </summary>
public sealed class RowResealStep : IRekeyStep
{
    public string Name => "RowReseal";

    /// <summary>The note of a sealed comment dropped because its article's body was purged:
    /// <c>dropped-comment:&lt;comment id&gt; article:&lt;article id&gt; created:&lt;created_at&gt;</c>. No content.</summary>
    public const string DroppedCommentNote = "dropped-comment:";

    public const string Body = "tbl_article_body", Comment = "tbl_comment", Version = "tbl_article_version",
        Conflict = "tbl_conflict_version", Media = "tbl_media", Blob = "tbl_blob", Secret = "tbl_sealed_secret",
        Remote = "tbl_remote_account";

    public async Task<RekeyStepResult> RunAsync(RekeyContext ctx)
    {
        var run = new Run(ctx, Name);
        using (var tx = ctx.Main.BeginTransaction())
        {
            await run.BodiesAsync(tx);
            await run.RowsAsync(tx, Version);
            await run.RowsAsync(tx, Conflict);
            await run.MediaAsync(tx);
            await run.SecretsAsync(tx);
            await run.RemoteTokensAsync(tx);
            if (run.Problems.Count > 0) throw new RekeyRowsUnopenableException(Name, run.Problems);
            run.Counts["old_blobs_removed"] = await ctx.Main.ExecuteAsync(
                $"DELETE FROM tbl_blob WHERE hash NOT IN ({ReferencedBlobs})", transaction: tx);
            tx.Commit();
        }
        return new RekeyStepResult(Name, run.Counts, run.Notes);
    }

    private const string ReferencedBlobs =
        @"SELECT ciphertext_hash FROM tbl_article_body WHERE ciphertext_hash IS NOT NULL
          UNION SELECT ciphertext_hash FROM tbl_article_version WHERE ciphertext_hash IS NOT NULL
          UNION SELECT ciphertext_sha256 FROM tbl_media WHERE ciphertext_sha256 IS NOT NULL";

    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>AAD a comment is sealed under (CommentService): <c>"bmb-comment" || articleId || commentId</c>.</summary>
    public static byte[]? CommentAad(Guid articleId, string? commentId) =>
        Guid.TryParse(commentId, out var c)
            ? "bmb-comment"u8.ToArray().Concat(articleId.ToByteArray()).Concat(c.ToByteArray()).ToArray()
            : null;

    /// <summary>A comment as CommentService opens it: with its AAD, else as a legacy comment sealed without one.</summary>
    public static string? TryOpenComment(Guid articleId, string? commentId, byte[] ct, byte[] iv, byte[] articleKey)
    {
        try { return ArticleEncryptor.Decrypt(ct, iv, articleKey, CommentAad(articleId, commentId)); }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException) { }
        try { return ArticleEncryptor.Decrypt(ct, iv, articleKey, aad: null); }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException) { return null; }
    }

    public static T? Try<T>(Func<T> open) where T : class
    {
        try { return open(); }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException) { return null; }
    }

    /// <summary>The entity key of a wrapper under the first of <paramref name="masters"/> that opens it.</summary>
    public static byte[]? OpenWrapper(EnvelopeFraming framing, Guid id, byte[]? wrapped, byte[]? iv, IEnumerable<byte[]> masters)
    {
        if (wrapped == null || iv == null) return null;
        foreach (var m in masters)
            if (Try(() => framing.UnwrapDek(id, wrapped, iv, m)) is { } key) return key;
        return null;
    }

    public static string CommentKey(long rowId, string? commentId) => commentId ?? "row:" + rowId;

    private static async Task<string> StoreBlobAsync(SqliteConnection conn, SqliteTransaction tx, byte[] data)
    {
        var hash = BlobHash.Compute(data);
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_blob (hash, data, size, created_at) VALUES (@hash, @data, @size, @now)
              ON CONFLICT (hash) DO UPDATE SET created_at = excluded.created_at",
            new { hash, data, size = data.LongLength, now = DateTime.UtcNow.ToString("O") }, tx);
        return hash;
    }

    private static async Task<byte[]?> BlobAsync(SqliteConnection conn, SqliteTransaction? tx, string? hash) =>
        string.IsNullOrEmpty(hash) ? null
            : await conn.ExecuteScalarAsync<byte[]?>("SELECT data FROM tbl_blob WHERE hash = @hash", new { hash }, tx);

    private static byte[] FreshKeyUnlike(byte[] old)
    {
        while (true)
        {
            var k = MasterKeyManager.GenerateMasterDek();
            if (!k.AsSpan().SequenceEqual(old)) return k;
        }
    }

    /// <summary>The legacy media file of <paramref name="id"/> in the source directory, named as MediaService names it.</summary>
    public static string? MediaFile(string sourceDir, string id) =>
        Guid.TryParse(id, out var g) ? Path.Combine(sourceDir, "media", $"{g}.enc") : null;

    private sealed class Run(RekeyContext ctx, string name)
    {
        public readonly List<RekeyProblem> Problems = [];
        public readonly Dictionary<string, long> Counts = new();
        public readonly List<string> Notes = [];
        private SqliteConnection Db => ctx.Main;
        private byte[] Dc => ctx.Keys.CampaignDek;
        private IReadOnlyList<byte[]> Old => ctx.Keys.OldCandidates;

        private void Count(string table) => Counts[table] = Counts.GetValueOrDefault(table) + 1;

        private void Report(string table, long done, long total)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            ctx.Progress.Report(name, done, total, table);
        }

        public async Task BodiesAsync(SqliteTransaction tx)
        {
            var bodies = (await Db.QueryAsync<(string Id, string? Hash, byte[]? Iv, byte[]? Wrapped, byte[]? DekIv)>(
                "SELECT article_id, ciphertext_hash, iv, encrypted_dek, dek_iv FROM tbl_article_body ORDER BY article_id", transaction: tx)).ToList();
            var comments = (await Db.QueryAsync<CommentRow>(
                    @"SELECT id AS RowId, article_id AS ArticleId, comment_id AS CommentId, encrypted AS Encrypted, text AS Text,
                             ciphertext AS Ct, iv AS Iv, created_at AS CreatedAt FROM tbl_comment", transaction: tx))
                .ToLookup(c => c.ArticleId, StringComparer.OrdinalIgnoreCase);
            // Comments of an article whose body was purged (the cleanup purges a body 30 days after a soft delete and
            // leaves its comments). A sealed one is already unreadable in the product ("article key unavailable"): its key
            // survives only in the event log, which the re-key clears. Re-sealing it under a key stored nowhere would
            // destroy it anyway, and keeping it under the old key would leave it to the old keys (D1). It is dropped
            // from the copy and listed, without content, in the report; the old vault keeps it until the owner deletes
            // it. A plaintext one is readable and holds no old-key material: it stays as it is.
            var withBody = bodies.Select(b => b.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var orphans in comments.Where(g => !withBody.Contains(g.Key)))
                foreach (var c in orphans)
                {
                    var key = CommentKey(c.RowId, c.CommentId);
                    if (c.Encrypted == 0)
                    {
                        Notes.Add($"kept-plaintext-comment:{key} article:{c.ArticleId}");
                        continue;
                    }
                    await Db.ExecuteAsync("DELETE FROM tbl_comment WHERE id = @RowId", new { c.RowId }, tx);
                    Notes.Add($"{DroppedCommentNote}{key} article:{c.ArticleId} created:{c.CreatedAt}");
                    Count("comments_dropped_body_purged");
                }

            var done = 0;
            foreach (var b in bodies)
            {
                Report(Body, ++done, bodies.Count);
                if (!Guid.TryParse(b.Id, out var articleId)) { Problems.Add(new(Body, b.Id, "not a GUID")); continue; }
                var oldCt = await BlobAsync(Db, tx, b.Hash);
                if (oldCt == null || b.Iv == null) { Problems.Add(new(Body, b.Id, "its ciphertext blob is missing")); continue; }
                var oldKey = OpenWrapper(EnvelopeFraming.Article, articleId, b.Wrapped, b.DekIv, Old);
                if (oldKey == null) { Problems.Add(new(Body, b.Id, "no old key opens its wrapper")); continue; }
                var newKey = FreshKeyUnlike(oldKey);
                try
                {
                    var plain = Try(() => EnvelopeFraming.Article.DecryptBody(articleId, b.Wrapped!, oldKey, oldCt, b.Iv));
                    if (plain == null) { Problems.Add(new(Body, b.Id, "its ciphertext does not open under its key")); continue; }

                    // Every comment is opened before anything of the article is written.
                    var texts = new List<(CommentRow Row, string Text)>();
                    foreach (var c in comments[b.Id])
                    {
                        string? text = c.Encrypted == 0 ? c.Text
                            : c.Ct == null || c.Iv == null ? null
                            : TryOpenComment(articleId, c.CommentId, c.Ct, c.Iv, oldKey);
                        if (text == null)
                            Problems.Add(new(Comment, CommentKey(c.RowId, c.CommentId), c.Encrypted != 0 && (c.Ct == null || c.Iv == null)
                                ? "sealed, but its ciphertext or IV is missing"
                                : "its ciphertext does not open under its article's key"));
                        else texts.Add((c, text));
                    }
                    if (Problems.Count > 0) continue; // the attempt fails; no point writing

                    var env = EnvelopeFraming.Article.Seal(articleId, plain, newKey, Dc);
                    var hash = await StoreBlobAsync(Db, tx, env.Ciphertext);
                    await Db.ExecuteAsync(
                        "UPDATE tbl_article_body SET ciphertext_hash = @hash, iv = @Iv, encrypted_dek = @WrappedDek, dek_iv = @DekIv WHERE article_id = @Id",
                        new { hash, env.Iv, env.WrappedDek, env.DekIv, b.Id }, tx);
                    Count(Body);
                    foreach (var (c, text) in texts)
                    {
                        var (ct, iv) = ArticleEncryptor.Encrypt(text, newKey, CommentAad(articleId, c.CommentId));
                        await Db.ExecuteAsync("UPDATE tbl_comment SET encrypted = 1, text = '', ciphertext = @ct, iv = @iv WHERE id = @RowId",
                            new { ct, iv, c.RowId }, tx);
                        Count(Comment);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(oldKey);
                    CryptographicOperations.ZeroMemory(newKey);
                }
            }
        }

        public async Task RowsAsync(SqliteTransaction tx, string table)
        {
            var sql = table == Version
                ? "SELECT id AS Id, article_id AS ArticleId, iv AS Iv, encrypted_dek AS Wrapped, dek_iv AS DekIv, ciphertext_hash AS Hash, NULL AS Ct FROM tbl_article_version ORDER BY id"
                : "SELECT id AS Id, article_id AS ArticleId, iv AS Iv, encrypted_dek AS Wrapped, dek_iv AS DekIv, NULL AS Hash, ciphertext AS Ct FROM tbl_conflict_version ORDER BY id";
            var rows = (await Db.QueryAsync<ArticleScopedRow>(sql, transaction: tx)).ToList();
            var done = 0;
            foreach (var r in rows)
            {
                Report(table, ++done, rows.Count);
                if (!Guid.TryParse(r.ArticleId, out var articleId)) { Problems.Add(new(table, r.Id, "its article id is not a GUID")); continue; }
                var oldCt = table == Version ? await BlobAsync(Db, tx, r.Hash) : r.Ct;
                if (oldCt == null || r.Iv == null) { Problems.Add(new(table, r.Id, "its ciphertext is missing")); continue; }
                var oldKey = OpenWrapper(EnvelopeFraming.Article, articleId, r.Wrapped, r.DekIv, Old);
                if (oldKey == null) { Problems.Add(new(table, r.Id, "no old key opens its wrapper")); continue; }
                var newKey = FreshKeyUnlike(oldKey);
                try
                {
                    var plain = Try(() => EnvelopeFraming.Article.DecryptBody(articleId, r.Wrapped!, oldKey, oldCt, r.Iv));
                    if (plain == null) { Problems.Add(new(table, r.Id, "its ciphertext does not open under its key")); continue; }
                    var env = EnvelopeFraming.Article.Seal(articleId, plain, newKey, Dc);
                    if (table == Version)
                    {
                        var hash = await StoreBlobAsync(Db, tx, env.Ciphertext);
                        await Db.ExecuteAsync(
                            "UPDATE tbl_article_version SET ciphertext_hash = @hash, iv = @Iv, encrypted_dek = @WrappedDek, dek_iv = @DekIv WHERE id = @Id",
                            new { hash, env.Iv, env.WrappedDek, env.DekIv, r.Id }, tx);
                    }
                    else
                    {
                        await Db.ExecuteAsync(
                            "UPDATE tbl_conflict_version SET ciphertext = @Ciphertext, iv = @Iv, encrypted_dek = @WrappedDek, dek_iv = @DekIv WHERE id = @Id",
                            new { env.Ciphertext, env.Iv, env.WrappedDek, env.DekIv, r.Id }, tx);
                    }
                    Count(table);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(oldKey);
                    CryptographicOperations.ZeroMemory(newKey);
                }
            }
        }

        public async Task MediaAsync(SqliteTransaction tx)
        {
            var rows = (await Db.QueryAsync<(string Id, string? Hash, byte[]? Iv, byte[]? Wrapped, byte[]? DekIv)>(
                "SELECT id, ciphertext_sha256, iv, encrypted_dek, dek_iv FROM tbl_media ORDER BY id", transaction: tx)).ToList();
            var done = 0;
            foreach (var m in rows)
            {
                Report(Media, ++done, rows.Count);
                if (!Guid.TryParse(m.Id, out var mediaId)) { Problems.Add(new(Media, m.Id, "not a GUID")); continue; }
                var oldCt = await BlobAsync(Db, tx, m.Hash);
                var fromFile = false;
                if (oldCt == null && MediaFile(ctx.SourceDir, m.Id) is { } path && File.Exists(path))
                {
                    oldCt = await File.ReadAllBytesAsync(path, ctx.Ct);
                    fromFile = true;
                }
                if (oldCt == null || m.Iv == null) { Problems.Add(new(Media, m.Id, "neither its blob nor its .enc file exists")); continue; }
                var oldKey = OpenWrapper(EnvelopeFraming.Media, mediaId, m.Wrapped, m.DekIv, Old);
                if (oldKey == null) { Problems.Add(new(Media, m.Id, "no old key opens its wrapper")); continue; }
                var newKey = FreshKeyUnlike(oldKey);
                try
                {
                    var plain = Try(() => EnvelopeFraming.Media.DecryptBody(mediaId, m.Wrapped!, oldKey, oldCt, m.Iv));
                    if (plain == null) { Problems.Add(new(Media, m.Id, "its ciphertext does not open under its key")); continue; }
                    var env = EnvelopeFraming.Media.Seal(mediaId, plain, newKey, Dc);
                    var hash = await StoreBlobAsync(Db, tx, env.Ciphertext);
                    await Db.ExecuteAsync(
                        "UPDATE tbl_media SET ciphertext_sha256 = @hash, iv = @Iv, encrypted_dek = @WrappedDek, dek_iv = @DekIv WHERE id = @Id",
                        new { hash, env.Iv, env.WrappedDek, env.DekIv, m.Id }, tx);
                    Count(Media);
                    if (fromFile) Count("media_imported_from_enc");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(oldKey);
                    CryptographicOperations.ZeroMemory(newKey);
                }
            }
        }

        public async Task SecretsAsync(SqliteTransaction tx)
        {
            var dcFp = DekFingerprint.Of(Dc);
            foreach (var s in await Db.QueryAsync<(string Name, byte[]? Wrapped, byte[]? Iv)>(
                         "SELECT name, wrapped, iv FROM tbl_sealed_secret ORDER BY name", transaction: tx))
            {
                byte[]? secret = null;
                foreach (var k in Old)
                    if ((secret = SealedSecretCrypto.TryOpen(s.Name, s.Wrapped, s.Iv, k)) != null) break;
                if (secret == null) { Problems.Add(new(Secret, s.Name, "no old key opens it")); continue; }
                try
                {
                    var (wrapped, iv) = SealedSecretCrypto.Seal(s.Name, secret, Dc);
                    await Db.ExecuteAsync("UPDATE tbl_sealed_secret SET wrapped = @wrapped, iv = @iv, dek_fingerprint = @dcFp WHERE name = @Name",
                        new { wrapped, iv, dcFp, s.Name }, tx);
                    Count(Secret);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(secret);
                }
            }
        }

        public async Task RemoteTokensAsync(SqliteTransaction tx)
        {
            foreach (var r in await Db.QueryAsync<(string Id, byte[]? Token, byte[]? Iv)>(
                         "SELECT id, encrypted_token, token_iv FROM tbl_remote_account ORDER BY id", transaction: tx))
            {
                string? token = null;
                foreach (var k in Old)
                    if ((token = RemoteAccountService.TryOpenToken(r.Token, r.Iv, k)) != null) break;
                if (token == null) { Problems.Add(new(Remote, r.Id, "no old key opens its token")); continue; }
                var (cipher, iv) = RemoteAccountService.SealToken(token, Dc);
                await Db.ExecuteAsync("UPDATE tbl_remote_account SET encrypted_token = @cipher, token_iv = @iv WHERE id = @Id",
                    new { cipher, iv, r.Id }, tx);
                Count(Remote);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Every row this step owns opens under D_c to a readable plaintext, and none of its wrappers (or directly sealed
    /// values) opens under any old key; every comment is sealed with no text left; every blob is referenced by a
    /// re-sealed row; no media row depends on a file.
    /// </summary>
    public async Task<IReadOnlyList<RekeyProblem>> VerifyAsync(RekeyContext ctx)
    {
        var problems = new List<RekeyProblem>();
        var db = ctx.Main;
        var dc = ctx.Keys.CampaignDek;
        var old = ctx.Keys.OldCandidates;

        byte[]? CheckWrapper(string table, string key, EnvelopeFraming framing, Guid id, byte[]? wrapped, byte[]? iv)
        {
            if (OpenWrapper(framing, id, wrapped, iv, old) is { } leaked)
            {
                CryptographicOperations.ZeroMemory(leaked);
                problems.Add(new(table, key, "its wrapper opens under an old key"));
            }
            var k = OpenWrapper(framing, id, wrapped, iv, [dc]);
            if (k == null) problems.Add(new(table, key, "its wrapper does not open under the new key"));
            return k;
        }

        var comments = (await db.QueryAsync<CommentRow>(
                @"SELECT id AS RowId, article_id AS ArticleId, comment_id AS CommentId, encrypted AS Encrypted, text AS Text,
                         ciphertext AS Ct, iv AS Iv, created_at AS CreatedAt FROM tbl_comment"))
            .ToLookup(c => c.ArticleId, StringComparer.OrdinalIgnoreCase);
        var bodyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in await db.QueryAsync<(string Id, string? Hash, byte[]? Iv, byte[]? Wrapped, byte[]? DekIv)>(
                     "SELECT article_id, ciphertext_hash, iv, encrypted_dek, dek_iv FROM tbl_article_body"))
        {
            bodyIds.Add(b.Id);
            if (!Guid.TryParse(b.Id, out var a)) { problems.Add(new(Body, b.Id, "not a GUID")); continue; }
            var k = CheckWrapper(Body, b.Id, EnvelopeFraming.Article, a, b.Wrapped, b.DekIv);
            if (k == null) continue;
            try
            {
                var ct = await BlobAsync(db, null, b.Hash);
                if (ct == null || b.Iv == null || Try(() => EnvelopeFraming.Article.DecryptBody(a, b.Wrapped!, k, ct, b.Iv)) == null)
                    problems.Add(new(Body, b.Id, "its ciphertext does not open under its new key"));
                foreach (var c in comments[b.Id])
                    if (c.Encrypted == 0 || c.Text.Length > 0 || c.Ct == null || c.Iv == null
                        || Try(() => ArticleEncryptor.Decrypt(c.Ct, c.Iv, k, CommentAad(a, c.CommentId))) == null)
                        problems.Add(new(Comment, CommentKey(c.RowId, c.CommentId), "not sealed under its article's new key"));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(k);
            }
        }
        foreach (var g in comments.Where(g => !bodyIds.Contains(g.Key)))
            foreach (var c in g.Where(c => c.Encrypted != 0))
                problems.Add(new(Comment, CommentKey(c.RowId, c.CommentId), "a sealed comment of a purged article is left under its old key"));

        foreach (var (table, sql) in new[]
                 {
                     (Version, "SELECT id AS Id, article_id AS ArticleId, iv AS Iv, encrypted_dek AS Wrapped, dek_iv AS DekIv, ciphertext_hash AS Hash, NULL AS Ct FROM tbl_article_version"),
                     (Conflict, "SELECT id AS Id, article_id AS ArticleId, iv AS Iv, encrypted_dek AS Wrapped, dek_iv AS DekIv, NULL AS Hash, ciphertext AS Ct FROM tbl_conflict_version"),
                 })
        {
            foreach (var r in await db.QueryAsync<ArticleScopedRow>(sql))
            {
                if (!Guid.TryParse(r.ArticleId, out var a)) { problems.Add(new(table, r.Id, "its article id is not a GUID")); continue; }
                var k = CheckWrapper(table, r.Id, EnvelopeFraming.Article, a, r.Wrapped, r.DekIv);
                if (k == null) continue;
                var ct = table == Version ? await BlobAsync(db, null, r.Hash) : r.Ct;
                if (ct == null || r.Iv == null || Try(() => EnvelopeFraming.Article.DecryptBody(a, r.Wrapped!, k, ct, r.Iv)) == null)
                    problems.Add(new(table, r.Id, "its ciphertext does not open under its new key"));
                CryptographicOperations.ZeroMemory(k);
            }
        }

        foreach (var m in await db.QueryAsync<(string Id, string? Hash, byte[]? Iv, byte[]? Wrapped, byte[]? DekIv)>(
                     "SELECT id, ciphertext_sha256, iv, encrypted_dek, dek_iv FROM tbl_media"))
        {
            if (!Guid.TryParse(m.Id, out var id)) { problems.Add(new(Media, m.Id, "not a GUID")); continue; }
            var k = CheckWrapper(Media, m.Id, EnvelopeFraming.Media, id, m.Wrapped, m.DekIv);
            if (k == null) continue;
            var ct = await BlobAsync(db, null, m.Hash);
            if (ct == null || m.Iv == null || Try(() => EnvelopeFraming.Media.DecryptBody(id, m.Wrapped!, k, ct, m.Iv)) == null)
                problems.Add(new(Media, m.Id, "does not read through its blob under the new key"));
            CryptographicOperations.ZeroMemory(k);
        }

        foreach (var s in await db.QueryAsync<(string Name, byte[]? Wrapped, byte[]? Iv)>("SELECT name, wrapped, iv FROM tbl_sealed_secret"))
        {
            if (old.Any(k => SealedSecretCrypto.TryOpen(s.Name, s.Wrapped, s.Iv, k) is { } x && Wipe(x)))
                problems.Add(new(Secret, s.Name, "opens under an old key"));
            if (SealedSecretCrypto.TryOpen(s.Name, s.Wrapped, s.Iv, dc) is { } v) Wipe(v);
            else problems.Add(new(Secret, s.Name, "does not open under the new key"));
        }

        foreach (var r in await db.QueryAsync<(string Id, byte[]? Token, byte[]? Iv)>("SELECT id, encrypted_token, token_iv FROM tbl_remote_account"))
        {
            if (old.Any(k => RemoteAccountService.TryOpenToken(r.Token, r.Iv, k) != null))
                problems.Add(new(Remote, r.Id, "its token opens under an old key"));
            if (RemoteAccountService.TryOpenToken(r.Token, r.Iv, dc) == null)
                problems.Add(new(Remote, r.Id, "its token does not open under the new key"));
        }

        foreach (var hash in await db.QueryAsync<string>($"SELECT hash FROM tbl_blob WHERE hash NOT IN ({ReferencedBlobs})"))
            problems.Add(new(Blob, hash, "not referenced by a re-sealed row (an old ciphertext left behind)"));
        return problems;
    }

    private static bool Wipe(byte[] b)
    {
        CryptographicOperations.ZeroMemory(b);
        return true;
    }

    private sealed class CommentRow
    {
        public long RowId { get; set; }
        public string ArticleId { get; set; } = "";
        public string? CommentId { get; set; }
        public long Encrypted { get; set; }
        public string Text { get; set; } = "";
        public byte[]? Ct { get; set; }
        public byte[]? Iv { get; set; }
        public string? CreatedAt { get; set; }
    }

    private sealed class ArticleScopedRow
    {
        public string Id { get; set; } = "";
        public string ArticleId { get; set; } = "";
        public byte[]? Iv { get; set; }
        public byte[]? Wrapped { get; set; }
        public byte[]? DekIv { get; set; }
        public string? Hash { get; set; }
        public byte[]? Ct { get; set; }
    }
}
