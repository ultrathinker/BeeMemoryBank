using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Rekey.Steps;

/// <summary>
/// The pre-flight of an offline re-key (rekey-offline.md §2 step 1): read-only on the LIVE vault, fail closed.
/// Nothing is created before it passes, so every refusal leaves the vault byte for byte as it was.
///
/// <para><b>Every row the re-key will touch must open with a key it holds</b> (<see cref="RekeyKeys.OldCandidates"/>).
/// The inventory is the tables themselves, unscoped — deleted articles, soft-deleted comments and media, purged
/// bodies with retained versions, every event: none of the product's retention filters, since a row nobody looks at
/// is a row the re-key would carry under the old key. Each row is opened the way the product reads it: an entity
/// key under its framing and AAD, then the ciphertext under that key. A row that does not open is a blocking problem
/// of its own, listed with its table and key.</para>
///
/// <para>Also blocking: a DEK rotation proposed or committing; a restore pending (the restore state, a pending
/// upload, a standalone restore's staging file); less free space on the vault's volume than 2.5 × (database +
/// chat.db + media) (§7).</para>
///
/// <para>Listed, never acted on (warnings): a copy of the vault in the OS temp folder, a blind node's folders in a
/// full node's data directory, ciphertext that is already gone (a body whose blob is missing: nothing to re-key, and
/// nothing the re-key could lose). The pre-flight deletes nothing.</para>
/// </summary>
public sealed class RekeyPreflight : IRekeyPreflight
{
    /// <summary>§7: the copy, the old vault kept until it is deleted, and room for the rebuild.</summary>
    public const double SpaceFactor = 2.5;

    private static readonly byte[] SqliteMagic = "SQLite format 3\0"u8.ToArray();
    private static readonly string[] BlindDirs = ["blind", "blind-tmp", "blind-cutover", "restic-cache"];
    private static readonly string[] OsTempVaultDirPrefixes = ["bmb-filter-", "bmb-join-restore-", "bmb-network-restore-", "bmb-restore-", "bmb-upload-"];

    /// <summary>The OS temp folder to look for vault copies in (tests point it elsewhere).</summary>
    public string OsTempDir { get; init; } = Path.GetTempPath();

    /// <summary>The free bytes of the vault's volume (tests replace it); null when it cannot be measured.</summary>
    public Func<string, long?> FreeBytes { get; init; } = MeasureFreeBytes;

    public Task<RekeyPreflightReport> RunAsync(string sourceDir, SqliteConnection liveMain, SqliteConnection? liveChat,
        RekeyKeys keys, CancellationToken ct)
    {
        var blocking = new List<RekeyProblem>();
        var warnings = new List<string>();

        CheckNothingInFlight(sourceDir, liveMain, blocking);
        CheckArticles(liveMain, keys, blocking, warnings, ct);
        CheckMedia(sourceDir, liveMain, keys, blocking, warnings, ct);
        CheckSealedSecrets(liveMain, keys, blocking, warnings);
        CheckRemoteTokens(liveMain, keys, blocking);
        if (liveChat != null)
        {
            CheckChat(liveMain, liveChat, keys, blocking, ct);
            NameChatPlaintext(liveChat, warnings);
        }
        ListLeftovers(sourceDir, warnings);

        var needed = BytesNeeded(sourceDir);
        var free = FreeBytes(sourceDir);
        if (free is null)
            blocking.Add(new RekeyProblem("(volume)", sourceDir, "the free space of the vault's volume cannot be measured"));
        else if (free < needed)
            blocking.Add(new RekeyProblem("(volume)", sourceDir,
                $"not enough free space: {needed / (1024 * 1024)} MB needed ({SpaceFactor} × database, chat.db and media), {free / (1024 * 1024)} MB free"));

        return Task.FromResult(new RekeyPreflightReport(blocking, warnings, needed));
    }

    // ─── Nothing in flight ──────────────────────────────────────────────────

    private static void CheckNothingInFlight(string sourceDir, SqliteConnection main, List<RekeyProblem> blocking)
    {
        if (HasTable(main, "tbl_dek_rotation_state"))
            foreach (var (id, state) in Query(main, "SELECT event_id, state FROM tbl_dek_rotation_state WHERE UPPER(state) IN ('PROPOSED','COMMITTING')",
                         r => (r.GetString(0), r.GetString(1))))
                blocking.Add(new RekeyProblem("tbl_dek_rotation_state", id, $"a key rotation is {state.ToLowerInvariant()}; finish or cancel it first"));

        if (HasTable(main, "tbl_restore_event_state"))
            foreach (var (id, state) in Query(main, "SELECT event_id, state FROM tbl_restore_event_state WHERE UPPER(state) IN ('PENDING','DOWNLOADING','APPLYING')",
                         r => (r.GetString(0), r.GetString(1))))
                blocking.Add(new RekeyProblem("tbl_restore_event_state", id, $"a restore is {state.ToLowerInvariant()}; finish or cancel it first"));

        var pending = Path.Combine(sourceDir, "snapshots", "restore-pending");
        if (Directory.Exists(pending) && Directory.EnumerateFileSystemEntries(pending).Any())
            blocking.Add(new RekeyProblem("(file)", pending, "a restore upload is pending; finish or cancel it first"));
        var standalone = Path.Combine(sourceDir, "beememorybank.db.standalone-staging");
        if (File.Exists(standalone))
            blocking.Add(new RekeyProblem("(file)", standalone, "a standalone restore did not finish; start the node once to recover it"));
    }

    // ─── Articles: bodies, versions, conflicts, events, comments ────────────

    private sealed record Wrapper(string Table, string Key, byte[] Wrapped, byte[] Iv);

    private static void CheckArticles(SqliteConnection main, RekeyKeys keys, List<RekeyProblem> blocking, List<string> warnings,
        CancellationToken ct)
    {
        var wrappers = new Dictionary<Guid, List<Wrapper>>();
        void Add(Guid article, Wrapper w)
        {
            if (!wrappers.TryGetValue(article, out var list)) wrappers[article] = list = [];
            list.Add(w);
        }

        // Every wrapper the re-seal will rewrite, unscoped. The key of one article is the same in all of them.
        foreach (var (id, wrapped, iv) in Query(main, "SELECT article_id, encrypted_dek, dek_iv FROM tbl_article_body",
                     r => (Id(r, 0), Bytes(r, 1), Bytes(r, 2))))
            Add(id, new Wrapper("tbl_article_body", id.ToString(), wrapped, iv));
        foreach (var (id, article, wrapped, iv) in Query(main, "SELECT id, article_id, encrypted_dek, dek_iv FROM tbl_article_version",
                     r => (r.GetString(0), Id(r, 1), Bytes(r, 2), Bytes(r, 3))))
            Add(article, new Wrapper("tbl_article_version", id, wrapped, iv));
        foreach (var (id, article, wrapped, iv) in Query(main, "SELECT id, article_id, encrypted_dek, dek_iv FROM tbl_conflict_version",
                     r => (r.GetString(0), Id(r, 1), Bytes(r, 2), Bytes(r, 3))))
            Add(article, new Wrapper("tbl_conflict_version", id, wrapped, iv));
        // The event log is cleared on the copy (EventLogResetStep), so an event's wrapper is never re-sealed and never
        // blocks. It is still a source of an article's key: the comments of an article whose body was purged have no
        // other one.
        var fallback = new Dictionary<Guid, List<Wrapper>>();
        foreach (var (seq, article, payload) in Query(main,
                     "SELECT sequence_num, article_id, payload FROM tbl_event WHERE article_id IS NOT NULL AND event_type IN ('article_create','article_update')",
                     r => (r.GetInt64(0), Id(r, 1), r.GetString(2))))
            if (EventWrapper(payload) is { } w)
            {
                if (!fallback.TryGetValue(article, out var list)) fallback[article] = list = [];
                list.Add(new Wrapper("tbl_event", seq.ToString(), w.Wrapped, w.Iv));
            }

        var comments = Query(main, "SELECT id, comment_id, article_id, ciphertext, iv FROM tbl_comment WHERE encrypted = 1",
                r => (Row: r.GetInt64(0), Id: r.IsDBNull(1) ? (Guid?)null : Guid.Parse(r.GetString(1)), Article: Id(r, 2),
                    Cipher: r.IsDBNull(3) ? null : Bytes(r, 3), Iv: r.IsDBNull(4) ? null : Bytes(r, 4)))
            .GroupBy(c => c.Article).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var article in wrappers.Keys.Concat(comments.Keys).Distinct())
        {
            ct.ThrowIfCancellationRequested();
            var opened = new List<byte[]>();
            try
            {
                foreach (var w in wrappers.GetValueOrDefault(article) ?? [])
                    CheckArticleRow(main, article, w, keys, opened, blocking, warnings);

                var articleComments = comments.GetValueOrDefault(article) ?? [];
                if (articleComments.Count == 0) continue;
                if (opened.Count == 0)
                    foreach (var w in fallback.GetValueOrDefault(article) ?? [])
                        if (OpenEntityKey(EnvelopeFraming.Article, article, w.Wrapped, w.Iv, keys) is { } key)
                            opened.Add(key);
                // A purged body: the product no longer shows its comments (CommentService reads the body's key only),
                // and the re-seal drops the sealed ones from the copy (RowResealStep, option C); the old vault keeps
                // them. A warning, so the report names them; a comment no key opens still blocks below (only the
                // pre-flight can prove it was openable).
                if (!(wrappers.GetValueOrDefault(article) ?? []).Any(w => w.Table == "tbl_article_body"))
                    warnings.Add($"article {article}: its body is purged; its {articleComments.Count} sealed comment(s) are dropped from the re-keyed vault; they remain only in the old vault folder, and deleting that folder deletes them");

                // Every comment, not only the first: each is opened the way CommentService reads it.
                foreach (var c in articleComments)
                {
                    var rowKey = c.Id?.ToString() ?? $"row {c.Row}";
                    if (opened.Count == 0)
                        blocking.Add(new RekeyProblem("tbl_comment", rowKey, $"no key of article {article} opens, so the comment cannot be re-sealed"));
                    else if (c.Cipher is not { Length: > 0 } || c.Iv is not { Length: > 0 } || c.Id is null
                             || !opened.Any(k => CommentOpens(article, c.Id.Value, c.Cipher, c.Iv, k)))
                        blocking.Add(new RekeyProblem("tbl_comment", rowKey, $"the comment does not open under any key of article {article}"));
                }
            }
            finally
            {
                foreach (var key in opened) Array.Clear(key);
            }
        }
    }

    /// <summary>A body, version or conflict: its wrapper opens, and its ciphertext opens under the key it gives. A row
    /// whose ciphertext is gone protects nothing, so it is a warning whatever its wrapper does.</summary>
    private static void CheckArticleRow(SqliteConnection main, Guid article, Wrapper w, RekeyKeys keys, List<byte[]> opened,
        List<RekeyProblem> blocking, List<string> warnings)
    {
        var (cipher, iv) = w.Table switch
        {
            "tbl_article_body" => Ciphertext(main, "tbl_article_body", "article_id", w.Key),
            "tbl_article_version" => Ciphertext(main, "tbl_article_version", "id", w.Key),
            _ => Ciphertext(main, "tbl_conflict_version", "id", w.Key),
        };
        var entity = OpenEntityKey(EnvelopeFraming.Article, article, w.Wrapped, w.Iv, keys);
        if (entity is not null) opened.Add(entity);
        if (cipher is null)
        {
            warnings.Add($"{w.Table} {w.Key}: its ciphertext is gone (nothing to re-key)");
            return;
        }
        if (entity is null)
        {
            blocking.Add(new RekeyProblem(w.Table, w.Key, $"the key of article {article} does not open"));
            return;
        }
        if (iv is not { Length: > 0 } || !Opens(() => Array.Clear(EnvelopeFraming.Article.DecryptBody(article, w.Wrapped, entity, cipher, iv))))
            blocking.Add(new RekeyProblem(w.Table, w.Key, $"the content of article {article} does not open under its key"));
    }

    /// <summary>A row's ciphertext: in tbl_blob for bodies and versions since the blob store (017 dropped their inline
    /// column), inline for conflicts.</summary>
    private static (byte[]? Cipher, byte[]? Iv) Ciphertext(SqliteConnection main, string table, string idCol, string id)
    {
        var inline = HasColumn(main, table, "ciphertext") ? "t.ciphertext" : "NULL";
        var sql = HasColumn(main, table, "ciphertext_hash")
            ? $"SELECT {inline}, t.iv, b.data, t.ciphertext_hash FROM {table} t LEFT JOIN tbl_blob b ON b.hash = t.ciphertext_hash WHERE t.{idCol} = $id COLLATE NOCASE"
            : $"SELECT {inline}, t.iv, NULL, NULL FROM {table} t WHERE t.{idCol} = $id COLLATE NOCASE";
        using var cmd = main.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (null, null);
        var iv = r.IsDBNull(1) ? null : Bytes(r, 1);
        if (!r.IsDBNull(2)) return (Bytes(r, 2), iv);
        if (!r.IsDBNull(3)) return (null, iv); // a hash with no blob: the ciphertext is gone
        var bytes = r.IsDBNull(0) ? null : Bytes(r, 0);
        return (bytes is { Length: > 0 } ? bytes : null, iv);
    }

    // ─── Media ──────────────────────────────────────────────────────────────

    private static void CheckMedia(string sourceDir, SqliteConnection main, RekeyKeys keys, List<RekeyProblem> blocking,
        List<string> warnings, CancellationToken ct)
    {
        var hasHash = HasColumn(main, "tbl_media", "ciphertext_sha256");
        var sql = hasHash
            ? "SELECT m.id, m.encrypted_dek, m.dek_iv, m.iv, b.data FROM tbl_media m LEFT JOIN tbl_blob b ON b.hash = m.ciphertext_sha256"
            : "SELECT m.id, m.encrypted_dek, m.dek_iv, m.iv, NULL FROM tbl_media m";
        foreach (var (id, wrapped, dekIv, iv, blob) in Query(main, sql,
                     r => (Id(r, 0), Bytes(r, 1), Bytes(r, 2), Bytes(r, 3), r.IsDBNull(4) ? null : Bytes(r, 4))))
        {
            ct.ThrowIfCancellationRequested();
            var entity = OpenEntityKey(EnvelopeFraming.Media, id, wrapped, dekIv, keys);
            if (entity is null)
            {
                blocking.Add(new RekeyProblem("tbl_media", id.ToString(), "the media key does not open"));
                continue;
            }
            try
            {
                var cipher = blob ?? MediaFile(sourceDir, id);
                if (cipher is null)
                {
                    warnings.Add($"tbl_media {id}: its ciphertext is gone (no blob, no media file; nothing to re-key)");
                    continue;
                }
                try
                {
                    Array.Clear(EnvelopeFraming.Media.DecryptBody(id, wrapped, entity, cipher, iv));
                }
                catch (CryptographicException)
                {
                    blocking.Add(new RekeyProblem("tbl_media", id.ToString(), "the media content does not open under its key"));
                }
            }
            finally
            {
                Array.Clear(entity);
            }
        }
    }

    private static byte[]? MediaFile(string sourceDir, Guid id)
    {
        var dir = Path.Combine(sourceDir, "media");
        foreach (var name in new[] { $"{id}.enc", $"{id.ToString().ToUpperInvariant()}.enc" })
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path)) return File.ReadAllBytes(path);
        }
        return null;
    }

    // ─── Sealed secrets, remote tokens ──────────────────────────────────────

    private static void CheckSealedSecrets(SqliteConnection main, RekeyKeys keys, List<RekeyProblem> blocking, List<string> warnings)
    {
        if (!HasTable(main, "tbl_sealed_secret")) return;
        foreach (var (name, wrapped, iv, status) in Query(main, "SELECT name, wrapped, iv, status FROM tbl_sealed_secret",
                     r => (r.GetString(0), Bytes(r, 1), Bytes(r, 2), r.GetString(3))))
        {
            var opened = keys.OldCandidates.Select(k => SealedSecretCrypto.TryOpen(name, wrapped, iv, k)).FirstOrDefault(v => v is not null);
            if (opened is not null) { Array.Clear(opened); continue; }
            if (status == "A") blocking.Add(new RekeyProblem("tbl_sealed_secret", name, "the sealed secret does not open"));
            else warnings.Add($"tbl_sealed_secret {name}: retired and does not open");
        }
    }

    private static readonly byte[] RemoteTokenAad = "bmb-remote-token"u8.ToArray();

    private static void CheckRemoteTokens(SqliteConnection main, RekeyKeys keys, List<RekeyProblem> blocking)
    {
        if (!HasTable(main, "tbl_remote_account")) return;
        foreach (var (id, token, iv) in Query(main, "SELECT id, encrypted_token, token_iv FROM tbl_remote_account",
                     r => (r.GetString(0), Bytes(r, 1), Bytes(r, 2))))
            if (!keys.OldCandidates.Any(k => Opens(() => ArticleEncryptor.Decrypt(token, iv, k, RemoteTokenAad))))
                blocking.Add(new RekeyProblem("tbl_remote_account", id, "the remote account's token does not open"));
    }

    // ─── chat.db ────────────────────────────────────────────────────────────

    private static void CheckChat(SqliteConnection main, SqliteConnection chat, RekeyKeys keys, List<RekeyProblem> blocking, CancellationToken ct)
    {
        byte[]? chatKey = null;
        var row = Query(main, $"SELECT wrapped_key, iv FROM {NodeDataKeyEnvelope.TableName} WHERE key_name = '{ChatDataKeyEnvelope.KeyName}'",
            r => (Wrapped: Bytes(r, 0), Iv: Bytes(r, 1))).FirstOrDefault();
        if (row.Wrapped is not null)
        {
            chatKey = keys.OldCandidates.Select(k => ChatDataKeyEnvelope.TryUnwrap(row.Wrapped, row.Iv, k)).FirstOrDefault(v => v is not null);
            if (chatKey is null)
                blocking.Add(new RekeyProblem(NodeDataKeyEnvelope.TableName, ChatDataKeyEnvelope.KeyName, "the chat key does not open"));
        }
        try
        {
            foreach (var column in ChatColumns.All)
            {
                if (!HasColumn(chat, column.Table, column.Cipher) || !HasColumn(chat, column.Table, column.Version)) continue;
                var rows = Query(chat,
                    $"SELECT id, {column.Cipher}, {column.Iv}, {column.Version} FROM {column.Table} WHERE {column.Cipher} IS NOT NULL",
                    r => (Id: r.GetString(0), Cipher: Bytes(r, 1), Iv: r.IsDBNull(2) ? null : Bytes(r, 2), Version: r.IsDBNull(3) ? (long?)null : r.GetInt64(3)));
                foreach (var r in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    var where = column.Name;
                    if (column.Bytes && r.Iv is null && r.Version is null) continue; // a legacy attachment's plaintext bytes
                    if (r.Version == ChatColumns.LegacyUnreadable)
                    {
                        blocking.Add(new RekeyProblem(where, r.Id, "marked unreadable: sealed under a key this node no longer has"));
                        continue;
                    }
                    if (r.Iv is not { Length: > 0 })
                    {
                        blocking.Add(new RekeyProblem(where, r.Id, "ciphertext without its IV"));
                        continue;
                    }
                    var aad = column.Aad(r.Id);
                    bool opened = r.Version switch
                    {
                        ChatColumns.ChatKeyVersion => chatKey is not null && Opens(() => Decrypt(column.Bytes, r.Cipher, r.Iv, chatKey, aad)),
                        null => keys.OldCandidates.Any(k => Opens(() => Decrypt(column.Bytes, r.Cipher, r.Iv, k, aad))),
                        _ => false,
                    };
                    if (!opened)
                        blocking.Add(new RekeyProblem(where, r.Id, r.Version is null or ChatColumns.ChatKeyVersion
                            ? "does not open under its key"
                            : $"is at chat key version {r.Version}, which this node does not know"));
                }
            }
        }
        finally
        {
            if (chatKey != null) Array.Clear(chatKey);
        }
    }

    /// <summary>
    /// Legacy plaintext in chat.db, by column: a message, conversation title or provider-key prefix from before it was
    /// sealed that the hosted backfill has not reached. Not a refusal (the chat step seals it under the new chat key and
    /// clears the column), but the owner is told it was there.
    /// </summary>
    private static void NameChatPlaintext(SqliteConnection chat, List<string> warnings)
    {
        foreach (var column in ChatColumns.All.Where(c => c.HasSeparatePlain))
        {
            if (!HasColumn(chat, column.Table, column.Cipher) || !HasColumn(chat, column.Table, column.Plain!)) continue;
            var n = Query(chat,
                $"SELECT COUNT(*) FROM {column.Table} WHERE {column.Plain} IS NOT NULL AND {column.Plain} != ''",
                r => r.GetInt64(0)).Single();
            if (n > 0)
                warnings.Add($"{column.Name}: {n} row(s) of legacy plaintext in {column.Table}.{column.Plain}; the re-key seals them under the new chat key and clears the column");
        }
    }

    private static void Decrypt(bool bytes, byte[] cipher, byte[] iv, byte[] key, byte[] aad)
    {
        if (bytes) Array.Clear(MediaEncryptor.Decrypt(cipher, iv, key, aad));
        else ArticleEncryptor.Decrypt(cipher, iv, key, aad);
    }

    // ─── Warnings: what the re-key leaves as it is ──────────────────────────

    private void ListLeftovers(string sourceDir, List<string> warnings)
    {
        foreach (var dir in BlindDirs)
            if (Directory.Exists(Path.Combine(sourceDir, dir)))
                warnings.Add($"{Path.Combine(sourceDir, dir)}: a blind node's folder in a full node's data directory; it is left behind");

        if (!Directory.Exists(OsTempDir)) return;
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(OsTempDir).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        foreach (var entry in entries)
        {
            try
            {
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                var name = Path.GetFileName(entry);
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (OsTempVaultDirPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                        warnings.Add($"{entry}: a folder a snapshot, restore or upload left in the OS temp folder; it may hold a copy of the vault under the old key — delete it yourself");
                }
                else if (IsVaultCopy(entry))
                    warnings.Add($"{entry}: a copy of a vault in the OS temp folder, under the old key — delete it yourself");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not ours to judge */ }
        }
    }

    /// <summary>A SQLite file, whatever its extension (a raw snapshot copy is a <c>.tmp</c>), with the vault's tables.</summary>
    private static bool IsVaultCopy(string path)
    {
        // Only a regular file can be a copy, and it is opened only once that is known. Opening a FIFO blocks until
        // a writer comes, forever: on Linux every running .NET process keeps clr-debug-pipe-* FIFOs in /tmp, the
        // verb's own included. A SQLite database is at least one 512-byte page, and stat() gives a FIFO, a socket or
        // a device a length of 0.
        if (new FileInfo(path).Length < 512) return false;
        var header = new byte[SqliteMagic.Length];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            if (fs.Read(header, 0, header.Length) != header.Length || !header.AsSpan().SequenceEqual(SqliteMagic)) return false;
        // A file another process holds locked is not waited on for long: it is a warning, not a reason to stall.
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 };
        try
        {
            using var conn = new SqliteConnection(builder.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('tbl_article_body','tbl_key_slot')";
            return Convert.ToInt64(cmd.ExecuteScalar()) == 2;
        }
        catch (SqliteException) { return false; }
    }

    // ─── Space ──────────────────────────────────────────────────────────────

    private static long BytesNeeded(string sourceDir)
    {
        long Size(string file) => File.Exists(file) ? new FileInfo(file).Length : 0;
        long total = 0;
        foreach (var db in new[] { "beememorybank.db", "chat.db" })
            foreach (var suffix in new[] { "", "-wal" })
                total += Size(Path.Combine(sourceDir, db + suffix));
        var media = Path.Combine(sourceDir, "media");
        if (Directory.Exists(media))
            total += Directory.EnumerateFiles(media, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        return (long)Math.Ceiling(total * SpaceFactor);
    }

    private static long? MeasureFreeBytes(string dir)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static byte[]? OpenEntityKey(EnvelopeFraming framing, Guid id, byte[] wrapped, byte[] iv, RekeyKeys keys)
    {
        foreach (var master in keys.OldCandidates)
        {
            try { return framing.UnwrapDek(id, wrapped, iv, master); }
            catch (CryptographicException) { /* not this key */ }
        }
        return null;
    }

    // The product's reader (CommentService.DecryptCommentText): the comment AAD, then the legacy one without an AAD.
    private static bool CommentOpens(Guid article, Guid comment, byte[] cipher, byte[] iv, byte[] key)
    {
        var aad = "bmb-comment"u8.ToArray().Concat(article.ToByteArray()).Concat(comment.ToByteArray()).ToArray();
        return Opens(() => ArticleEncryptor.Decrypt(cipher, iv, key, aad)) || Opens(() => ArticleEncryptor.Decrypt(cipher, iv, key, aad: null));
    }

    private static (byte[] Wrapped, byte[] Iv)? EventWrapper(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("encrypted_dek", out var dek) && dek.ValueKind == JsonValueKind.String
                && dek.GetString() is { Length: > 0 } d
                && doc.RootElement.TryGetProperty("dek_iv", out var iv) && iv.ValueKind == JsonValueKind.String
                && iv.GetString() is { Length: > 0 } i)
                return (Convert.FromBase64String(d), Convert.FromBase64String(i));
        }
        catch (Exception ex) when (ex is JsonException or FormatException) { /* not a key source */ }
        return null;
    }

    private static bool Opens(Action decrypt)
    {
        try { decrypt(); return true; }
        catch (CryptographicException) { return false; }
    }

    private static List<T> Query<T>(SqliteConnection conn, string sql, Func<SqliteDataReader, T> map)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    private static bool HasTable(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $t";
        cmd.Parameters.AddWithValue("$t", table);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static bool HasColumn(SqliteConnection conn, string table, string column)
    {
        if (!HasTable(conn, table)) return false;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $c";
        cmd.Parameters.AddWithValue("$c", column);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static Guid Id(SqliteDataReader r, int i) => Guid.Parse(r.GetString(i));
    private static byte[] Bytes(SqliteDataReader r, int i) => (byte[])r.GetValue(i);
}
