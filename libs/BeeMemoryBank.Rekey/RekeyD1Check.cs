using System.Buffers.Binary;
using System.Security.Cryptography;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Rekey.Steps;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Rekey;

/// <summary>What an attacker holding the old material knows, gathered from the live vault before the re-key.</summary>
public sealed class RekeyOldMaterial
{
    /// <summary>Every old master key: D2 and every retired DEK it opens.</summary>
    public required IReadOnlyList<byte[]> Masters { get; init; }
    /// <summary>Every entity key the old wrappers open (bodies, versions, conflicts, media).</summary>
    public required IReadOnlyList<byte[]> EntityKeys { get; init; }
    /// <summary>Every node data key and index key the old masters open (the chat key among them).</summary>
    public required IReadOnlyList<(string Name, byte[] Key)> DataKeys { get; init; }
    /// <summary>The first 16 bytes of every old ciphertext and wrapper, for the byte search.</summary>
    public required IReadOnlySet<Int128> CiphertextPrefixes { get; init; }
}

/// <summary>
/// The D1-attacker check of verify (rekey-offline.md §2 step 4, §4 "old-key access"): every old master, every entity
/// key the old wrappers open and every old node data key is tried against every sealed value of the copy, and nothing
/// may open. After the scrub, no 16-byte prefix of any old ciphertext or wrapper may appear anywhere in the copy's
/// files. It complements each step's own verify: this one knows nothing of the steps and fails on anything they miss.
/// </summary>
public static class RekeyD1Check
{
    private const int Prefix = 16;

    /// <summary>The old ciphertext columns of the main database, as (table, column).</summary>
    private static readonly (string Table, string Column)[] MainCiphertexts =
    [
        ("tbl_blob", "data"), ("tbl_conflict_version", "ciphertext"), ("tbl_comment", "ciphertext"),
        ("tbl_article_body", "encrypted_dek"), ("tbl_article_version", "encrypted_dek"),
        ("tbl_conflict_version", "encrypted_dek"), ("tbl_media", "encrypted_dek"),
        ("tbl_sealed_secret", "wrapped"), ("tbl_remote_account", "encrypted_token"),
        ("tbl_node_data_key", "wrapped_key"), ("tbl_search_index_key", "wrapped_key"),
        ("tbl_key_slot", "encrypted_master_dek"), ("tbl_agent", "encrypted_dek"),
        ("tbl_projection_matrix", "encrypted_matrix"), ("tbl_recovery_box", "wrapped"), ("tbl_dek_retired_link", "wrapped"),
    ];

    public static async Task<RekeyOldMaterial> GatherAsync(SqliteConnection live, SqliteConnection? liveChat, RekeyKeys keys)
    {
        var masters = keys.OldCandidates.Select(k => (byte[])k.Clone()).ToList();
        var entity = new List<byte[]>();
        foreach (var (table, framing, idCol) in new[]
                 {
                     ("tbl_article_body", EnvelopeFraming.Article, "article_id"), ("tbl_article_version", EnvelopeFraming.Article, "article_id"),
                     ("tbl_conflict_version", EnvelopeFraming.Article, "article_id"), ("tbl_media", EnvelopeFraming.Media, "id"),
                 })
            foreach (var r in await live.QueryAsync<(string Id, byte[]? W, byte[]? Iv)>($"SELECT {idCol}, encrypted_dek, dek_iv FROM {table}"))
                if (Guid.TryParse(r.Id, out var id) && RowResealStep.OpenWrapper(framing, id, r.W, r.Iv, masters) is { } k) entity.Add(k);

        var data = new List<(string, byte[])>();
        foreach (var r in await live.QueryAsync<(string Name, byte[] W, byte[] Iv)>("SELECT key_name, wrapped_key, iv FROM tbl_node_data_key"))
            foreach (var m in masters)
                if (NodeDataKeyEnvelope.TryUnwrap(r.Name, r.W, r.Iv, m) is { } k) { data.Add((r.Name, k)); break; }

        var prefixes = new HashSet<Int128>();
        void Add(byte[]? b)
        {
            if (b is { Length: >= Prefix }) prefixes.Add(BinaryPrimitives.ReadInt128LittleEndian(b));
        }
        var tables = (await live.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'table'")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (table, column) in MainCiphertexts.Where(c => tables.Contains(c.Table)))
            foreach (var b in await live.QueryAsync<byte[]?>($"SELECT {column} FROM {table} WHERE {column} IS NOT NULL")) Add(b);
        var identity = await live.QuerySingleOrDefaultAsync<(byte[]? Pk, long V)>(
            "SELECT ed25519_private_key, ed25519_private_key_v FROM tbl_node_identity LIMIT 1");
        if (identity.V == 1) Add(identity.Pk);
        if (liveChat != null)
            foreach (var table in await liveChat.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'"))
                foreach (var column in (await liveChat.QueryAsync<(long Cid, string Name, string Type)>($"PRAGMA table_info([{table}])"))
                         .Where(c => c.Type.Equals("BLOB", StringComparison.OrdinalIgnoreCase)))
                    foreach (var b in await liveChat.QueryAsync<byte[]?>($"SELECT [{column.Name}] FROM [{table}] WHERE [{column.Name}] IS NOT NULL")) Add(b);

        return new RekeyOldMaterial { Masters = masters, EntityKeys = entity, DataKeys = data, CiphertextPrefixes = prefixes };
    }

    /// <summary>Every sealed value of the copy against every old key: nothing may open.</summary>
    public static async Task<IReadOnlyList<RekeyProblem>> CheckRowsAsync(RekeyContext ctx, RekeyOldMaterial old)
    {
        var db = ctx.Main;
        var problems = new List<RekeyProblem>();
        void Opened(string table, string key, string what) => problems.Add(new(table, key, $"opens under an old {what} (D1)"));
        static bool Opens(Func<object?> open)
        {
            try { return open() != null; }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException) { return false; }
        }

        foreach (var (table, framing, idCol) in new[]
                 {
                     ("tbl_article_body", EnvelopeFraming.Article, "article_id"), ("tbl_article_version", EnvelopeFraming.Article, "article_id"),
                     ("tbl_conflict_version", EnvelopeFraming.Article, "article_id"), ("tbl_media", EnvelopeFraming.Media, "id"),
                 })
            foreach (var r in await db.QueryAsync<(string Id, byte[]? W, byte[]? Iv)>($"SELECT {idCol}, encrypted_dek, dek_iv FROM {table}"))
                if (Guid.TryParse(r.Id, out var id) && RowResealStep.OpenWrapper(framing, id, r.W, r.Iv, old.Masters) != null)
                    Opened(table, r.Id, "master key");

        // Ciphertexts under an old entity key: a row re-sealed under a new wrapper but an old entity key.
        foreach (var b in await db.QueryAsync<(string Id, byte[] W, byte[]? Iv, byte[]? Ct)>(
                     "SELECT b.article_id, b.encrypted_dek, b.iv, bl.data FROM tbl_article_body b LEFT JOIN tbl_blob bl ON bl.hash = b.ciphertext_hash"))
            if (Guid.TryParse(b.Id, out var a) && b.Ct != null && b.Iv != null
                && old.EntityKeys.Any(k => Opens(() => EnvelopeFraming.Article.DecryptBody(a, b.W, k, b.Ct, b.Iv))))
                Opened("tbl_article_body", b.Id, "article key");
        foreach (var c in await db.QueryAsync<(long Id, string ArticleId, string? CommentId, byte[]? Ct, byte[]? Iv)>(
                     "SELECT id, article_id, comment_id, ciphertext, iv FROM tbl_comment WHERE ciphertext IS NOT NULL"))
            if (Guid.TryParse(c.ArticleId, out var a) && c.Iv != null
                && old.EntityKeys.Any(k => RowResealStep.TryOpenComment(a, c.CommentId, c.Ct!, c.Iv, k) != null))
                Opened("tbl_comment", c.Id.ToString(), "article key");

        foreach (var s in await db.QueryAsync<(string Name, byte[]? W, byte[]? Iv)>("SELECT name, wrapped, iv FROM tbl_sealed_secret"))
            if (old.Masters.Any(k => SealedSecretCrypto.TryOpen(s.Name, s.W, s.Iv, k) != null)) Opened("tbl_sealed_secret", s.Name, "master key");
        foreach (var r in await db.QueryAsync<(string Id, byte[]? T, byte[]? Iv)>("SELECT id, encrypted_token, token_iv FROM tbl_remote_account"))
            if (old.Masters.Any(k => RemoteAccountService.TryOpenToken(r.T, r.Iv, k) != null)) Opened("tbl_remote_account", r.Id, "master key");
        foreach (var r in await db.QueryAsync<(string Name, byte[] W, byte[] Iv)>("SELECT key_name, wrapped_key, iv FROM tbl_node_data_key"))
        {
            if (old.Masters.Any(k => NodeDataKeyEnvelope.TryUnwrap(r.Name, r.W, r.Iv, k) != null)) Opened("tbl_node_data_key", r.Name, "master key");
            // A data key carried over unchanged (under the new master) is the old key still in use.
            if (NodeDataKeyEnvelope.TryUnwrap(r.Name, r.W, r.Iv, ctx.Keys.CampaignDek) is { } now
                && old.DataKeys.Any(o => o.Key.AsSpan().SequenceEqual(now)))
                problems.Add(new("tbl_node_data_key", r.Name, "still holds the old data key (D1)"));
        }
        foreach (var r in await db.QueryAsync<(long Id, byte[] W, byte[] Iv)>("SELECT slot_id, encrypted_master_dek, iv FROM tbl_key_slot"))
            if (old.Masters.Any(k => Opens(() => DekManager.UnwrapVersioned(r.W, r.Iv, k)))) Opened("tbl_key_slot", r.Id.ToString(), "master key");
        foreach (var r in await db.QueryAsync<(long Id, byte[] W, byte[] Iv)>("SELECT id, encrypted_dek, dek_iv FROM tbl_agent WHERE encrypted_dek IS NOT NULL"))
            problems.Add(new("tbl_agent", r.Id.ToString(), "an agent still carries a wrapped master key"));
        var identity = await db.QuerySingleOrDefaultAsync<(string NodeId, byte[] Pk, byte[]? Iv, long V)>(
            "SELECT node_id, ed25519_private_key, ed25519_private_key_iv, ed25519_private_key_v FROM tbl_node_identity LIMIT 1");
        if (identity.V == 1 && Guid.TryParse(identity.NodeId, out var nodeId)
            && old.Masters.Any(k => Opens(() => NodeIdentityCrypto.GetDecryptedPrivateKey(identity.Pk, identity.Iv, 1, nodeId, k))))
            Opened("tbl_node_identity", "ed25519_private_key", "master key");
        var sentinel = await db.ExecuteScalarAsync<byte[]?>("SELECT sentinel_value FROM tbl_node_identity LIMIT 1");
        if (sentinel == null || !MasterKeyManager.VerifySentinel(sentinel, ctx.Keys.CampaignDek))
            problems.Add(new("tbl_node_identity", "sentinel_value", "the sentinel does not name the new key"));
        foreach (var table in new[] { "tbl_dek_retired_link", "tbl_recovery_box" })
            if (await db.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table}") is var n and > 0)
                problems.Add(new(table, "*", $"{n} row(s) of old-key recovery material left"));
        if (await db.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM tbl_dek_rotation_state WHERE chain_encrypted_new_dek IS NOT NULL OR chain_iv IS NOT NULL") is var chains and > 0)
            problems.Add(new("tbl_dek_rotation_state", "*", $"{chains} rotation(s) keep chain material"));
        return problems;
    }

    /// <summary>
    /// No 16-byte prefix of any old ciphertext or wrapper in any file of the new vault. Run after the scrub, with the
    /// connections closed. A single pass per file: every offset is looked up in the prefix set.
    /// </summary>
    public static IReadOnlyList<RekeyProblem> CheckBytes(string newDir, RekeyOldMaterial old)
    {
        var problems = new List<RekeyProblem>();
        if (old.CiphertextPrefixes.Count == 0) return problems;
        foreach (var file in Directory.EnumerateFiles(newDir, "*", NoFollow.Walk))
        {
            var bytes = File.ReadAllBytes(file);
            for (var i = 0; i + Prefix <= bytes.Length; i++)
            {
                if (!old.CiphertextPrefixes.Contains(BinaryPrimitives.ReadInt128LittleEndian(bytes.AsSpan(i, Prefix)))) continue;
                problems.Add(new(Path.GetRelativePath(newDir, file), $"offset {i}", "holds old ciphertext bytes (D1)"));
                break;
            }
        }
        return problems;
    }
}
