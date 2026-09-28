using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using Dapper;

namespace BeeMemoryBank.Sync.Recovery;

/// <summary>One digest entry: what it is, and the version (Lamport time, source node) that wrote it.</summary>
public sealed record StateDigestEntry(string Type, string Id, long LamportTs, string Source);

/// <summary>
/// The digest of a state and the position vector it covers; for a digest taken at a cut, also the entries
/// past it, the buckets they fall into, and the covered entries sharing those buckets (which a comparison
/// with an anchor cannot check one by one). <see cref="History"/> lists the entries of the history section.
/// </summary>
public sealed record StateDigestResult(
    string Digest,
    IReadOnlyDictionary<string, long> PositionVector,
    IReadOnlyList<StateDigestEntry> BeyondCut,
    IReadOnlySet<int> AffectedBuckets,
    IReadOnlyList<StateDigestEntry> Unchecked,
    IReadOnlyList<StateDigestEntry>? History = null,
    IReadOnlyList<StateDigestEntry>? Trust = null)
{
    /// <summary>The trust entries with their encoded bytes (<see cref="StateDigest.CompareTrust"/>).</summary>
    internal IReadOnlyList<(StateDigestEntry Entry, byte[] Bytes)> TrustRows { get; init; } = [];
}

/// <summary>
/// The trust section compared with an anchor's: the peer entries present in a differing bucket are not
/// vouched for by the anchor (an entry the anchor had and the state lacks cannot be named — it only makes
/// its bucket differ).
/// </summary>
/// <param name="DifferingBuckets">How many row hashes the two trust sets do not share (the name kept from the bucketed
/// form; 0 means the same set).</param>
public sealed record StateDigestTrustCheck(int DifferingBuckets, IReadOnlyList<StateDigestEntry> Unconfirmed)
{
    public bool Matches => DifferingBuckets == 0;
}

/// <summary>
/// The history section compared with an anchor's: how many of its buckets differ (a removed entry shows only
/// here) and the history entries present in those buckets, which the anchor does not vouch for.
/// </summary>
public sealed record StateDigestHistoryCheck(int DifferingBuckets, IReadOnlyList<StateDigestEntry> Unverified)
{
    public bool Matches => DifferingBuckets == 0;
}

/// <summary>
/// Digest of the replicated state for integrity anchors (plan 5.5). Computable WITHOUT the DEK, so a
/// blind node, the node that anchors and a restoring node compute the same value from the same rows.
///
/// <para><b>Two kinds of entries.</b> A <i>content</i> entry is versioned by the row's content version
/// (<c>lamport_ts, source_node_id</c>). A <i>key</i> entry holds the bytes a content re-key (BMB-49,
/// BMB-56 re-key design §2.3, §3.2) replaces without touching the content version — IV and ciphertext — and is
/// versioned by the row's key version (<c>key_lamport_ts, key_source_node_id</c>) plus its key
/// generation. Both kinds take part in the cut and the position vector alike, so after a re-key the key
/// entry lies beyond an older anchor's cut while the content entry is still covered by it.</para>
///
/// <para><b>Where the key version comes from.</b> Today nothing re-keys: a body's key bytes change only
/// with an edit, so the key version is the content version and the key generation is "0". BMB-57 adds
/// <c>key_gen</c>, <c>key_lamport_ts</c> and <c>key_source_node_id</c> to <c>tbl_article_body</c>,
/// <c>tbl_media</c> and <c>tbl_comment</c> (BMB-56 re-key design §6.1); where those columns exist this reads
/// them, falling back to the content version for rows that carry none.</para>
///
/// <para><b>Covered</b> — every field comes from the signed events that wrote the row:</para>
/// <list type="bullet">
/// <item><c>article</c> (<c>tbl_article</c>): status, title, tree path, protection flag and hint, concept
/// tag names (<c>tbl_article_concept_tag</c> → <c>tbl_concept_tag</c>).
/// <c>article_key</c> (<c>tbl_article_body</c>): key generation, body IV, SHA-256 of the actual body bytes
/// in <c>tbl_blob</c>.</item>
/// <item><c>folder</c> (<c>tbl_folder</c>): status, path, name, parent path.</item>
/// <item><c>media</c> (<c>tbl_media</c>): status, article, file name, content type, size, kind.
/// <c>media_key</c>: key generation, IV, SHA-256 of the actual bytes in <c>tbl_blob</c>.</item>
/// <item><c>comment</c> (<c>tbl_comment</c>): article, plaintext text of an unencrypted comment, encrypted
/// flag, deletion version. <c>comment_key</c> (encrypted comments): key generation, ciphertext, IV.</item>
/// <item><c>tombstone</c> (<c>tbl_tombstone</c>): article id and version.</item>
/// </list>
/// <para><b>History section</b> — a second, separate set of buckets over the rows a restore imports but no
/// event replicates:</para>
/// <list type="bullet">
/// <item><c>article_version</c> (<c>tbl_article_version</c>): article, version number, title, tree path,
/// creation time, author. <c>article_version_key</c>: key generation, IV, SHA-256 of the actual bytes in
/// <c>tbl_blob</c>.</item>
/// <item><c>conflict_version</c> (<c>tbl_conflict_version</c>): article, the losing version (Lamport time,
/// source), its title and path (<c>metadata_json</c>), creation and expiry time.
/// <c>conflict_version_key</c>: key generation, IV, SHA-256 of the inline ciphertext.</item>
/// </list>
/// <para>The key generation (<c>key_gen</c>, BMB-56 v2 §3.2; <c>'0'</c> where the column does not exist yet or is
/// null) lets a comparison tell a history row re-sealed by a re-key campaign — its key entry differs and names
/// the campaign — from one altered in place.</para>
/// <para>Why separate: these rows are written by each node for itself — an article version by the node the
/// edit was made on (ArticleService; the event applier writes none), a conflict copy by every node that
/// applies an overwriting update, with its own id and clock, expiring on its own schedule — so the anchoring
/// node's history and a blind node's legitimately differ. In the state buckets they would make almost every
/// real anchor mismatch. They carry no replicated version either, so they take no part in the cut. Compared
/// on their own (<see cref="CompareHistory"/>) an altered or removed row is still detected, and reported
/// as such, at the granularity of a bucket.</para>
/// <para><b>Not covered</b>, on purpose: the wrapped entity DEKs (<c>encrypted_dek</c>/<c>dek_iv</c>) and
/// <c>tbl_projection_matrix</c>, which every node re-wraps with its own random IV at each rotation;
/// <c>tbl_concept_tag_edge</c>, embeddings and
/// <c>folder_id</c>, derived locally; the recovery tables (<c>tbl_recovery_box</c>,
/// <c>tbl_dek_retired_link</c>, <c>tbl_sealed_secret</c>, sealed and verified on their own at restore;
/// <c>tbl_state_anchor</c>, which cannot cover itself). Blob rows that no covered row references are not
/// state.</para>
///
/// <para><b>Encoding</b> (format <see cref="FormatVersion"/>): each entry is a sequence of
/// length-prefixed fields (int32 big-endian length, -1 for null, then the bytes) starting with type, id,
/// version and source, so no field value can be read as a boundary. Entries fall into
/// <see cref="Buckets"/> buckets by SHA-256 of (type, id); a bucket's hash is SHA-256 over its
/// length-prefixed entries sorted by (type, id), truncated to <see cref="BucketHashBytes"/> bytes. The
/// digest string is <c>"sd&lt;format&gt;:"</c> followed by the hex of every state bucket hash in order, then
/// <c>"/"</c> and the history buckets, <c>"/"</c> and the trust set: the format travels inside the anchor payload,
/// covered by its MAC and signature, so an anchor of another encoding is recognised as such instead of
/// silently mismatching.</para>
///
/// <para><b>Trust section</b> — not buckets but a SET, one 16-byte hash per row, sorted: each row, and the anchor's
/// signer, is then checked on its own — no other row, changed or added, can void it (a whitelist is small: tens of
/// rows). Over who is in the mesh: one <c>peer</c> entry per active
/// whitelist row (node id, Ed25519 key, superadmin flag), plus — only on the node that publishes the anchor,
/// <c>includeSelfAsSuperadmin</c> — that node itself as a superadmin, since no node has a row of its own. Every
/// node's whitelist lists every other node, so a node's rows plus its own record are the same set everywhere;
/// a restored node's rows (the blind node's, with the blind node's own row added from its package) are that set
/// again. The anchor's MAC under the master DEK covers this section, so a blind node — which never has the DEK
/// — cannot make a row of its own making verify: a restore keeps active only the rows this section vouches for.
/// Row versions and TLS pins stay out: the anchoring node knows neither for its own record, and a pin only
/// pins transport, the node key still authenticates the peer.</para>
///
/// <para><b>Why buckets.</b> An anchor commits to entries at their versions then. When an entry is later
/// edited or re-keyed its old version is gone, so a single hash over everything could never match again.
/// Compared bucket by bucket (<see cref="Matches"/>), only the buckets holding a newer entry are left out;
/// every other bucket must match exactly — an older anchor stays valid for what it covered, at the
/// granularity of a bucket, and the covered entries that share a bucket with a newer one are listed as
/// <see cref="StateDigestResult.Unchecked"/>.</para>
/// </summary>
public static class StateDigest
{
    /// <summary>
    /// 7: the trust section is a per-row hash set, not buckets. 6: the trust section covers every active row (node id, key, superadmin flag), pins out. 5: trust section
    /// over superadmins. 4: key generation in the history key entries. 3: the history section added. 2: content
    /// and key entries split for re-keying. (1 was the unsplit encoding of ab38b428. No anchor of 1-5 was ever
    /// persisted outside tests.)
    /// </summary>
    public const int FormatVersion = 7;
    public static readonly string FormatPrefix = $"sd{FormatVersion}:";

    /// <summary>
    /// 64 buckets of 16 bytes: a 2 KB digest per anchor; a change after the anchor leaves 1/64 of the state
    /// unchecked individually rather than all of it. 128 bits per bucket keep a tampered bucket from
    /// passing (second preimage), which is what the comparison needs.
    /// </summary>
    public const int Buckets = 64;
    public const int BucketHashBytes = 16;

    /// <summary>
    /// Does the state taken at an anchor's cut match the anchor's digest? Every bucket without a newer
    /// entry must be identical; the others are not compared. False for a digest of another format.
    /// </summary>
    public static bool Matches(string anchorDigest, StateDigestResult atCut)
    {
        if (!TrySplit(anchorDigest, out var anchored, out _, out _) || !TrySplit(atCut.Digest, out var current, out _, out _)) return false;
        for (var b = 0; b < Buckets; b++)
        {
            if (atCut.AffectedBuckets.Contains(b)) continue;
            if (!Bucket(anchored, b).SequenceEqual(Bucket(current, b))) return false;
        }
        return true;
    }

    /// <summary>
    /// The history section against an anchor's, bucket by bucket; null for a digest of another format. There
    /// is no cut to leave buckets out by — history rows carry no replicated version — so every difference
    /// counts, and every history entry in a differing bucket is unverified.
    /// </summary>
    public static StateDigestHistoryCheck? CompareHistory(string anchorDigest, StateDigestResult current)
    {
        if (!TrySplit(anchorDigest, out _, out var anchored, out _) || !TrySplit(current.Digest, out _, out var now, out _)) return null;
        var differing = Enumerable.Range(0, Buckets).Where(b => !Bucket(anchored, b).SequenceEqual(Bucket(now, b))).ToHashSet();
        return new StateDigestHistoryCheck(differing.Count,
            (current.History ?? []).Where(e => differing.Contains(BucketOf(e))).ToList());
    }

    private const int BucketHex = BucketHashBytes * 2;

    private static ReadOnlySpan<char> Bucket(string section, int b) => section.AsSpan(b * BucketHex, BucketHex);

    /// <summary>
    /// Did the anchor's trust set hold the row (<paramref name="nodeId"/>, <paramref name="publicKey"/>,
    /// <paramref name="superadmin"/>) at its cut? A membership test on that row's own hash: nothing else in the
    /// set, or in the state now, bears on the answer.
    /// </summary>
    public static bool TrustProves(string anchorDigest, Guid nodeId, byte[] publicKey, bool superadmin) =>
        TrySplit(anchorDigest, out _, out _, out var anchored)
        && TrustSet(anchored).Contains(TrustHash(TrustRow(nodeId.ToString(), publicKey, superadmin).Bytes));

    /// <summary>
    /// The trust set against an anchor's; null for a digest of another format. The peer entries of
    /// <paramref name="current"/> whose row hash the anchor does not hold are the ones it does not vouch for —
    /// row by row, so a change to one row leaves every other row vouched for.
    /// </summary>
    public static StateDigestTrustCheck? CompareTrust(string anchorDigest, StateDigestResult current)
    {
        if (!TrySplit(anchorDigest, out _, out _, out var anchored) || !TrySplit(current.Digest, out _, out _, out var now)) return null;
        var anchorSet = TrustSet(anchored);
        var unconfirmed = current.TrustRows.Where(r => !anchorSet.Contains(TrustHash(r.Bytes))).Select(r => r.Entry).ToList();
        var nowSet = TrustSet(now);
        return new StateDigestTrustCheck(anchorSet.Count(h => !nowSet.Contains(h)) + nowSet.Count(h => !anchorSet.Contains(h)), unconfirmed);
    }

    private const int TrustHashHex = 32;

    private static string TrustHash(byte[] rowBytes) => Convert.ToHexStringLower(SHA256.HashData(rowBytes).AsSpan(0, TrustHashHex / 2));

    private static HashSet<string> TrustSet(string section) =>
        Enumerable.Range(0, section.Length / TrustHashHex).Select(i => section.Substring(i * TrustHashHex, TrustHashHex)).ToHashSet(StringComparer.Ordinal);

    private static bool TrySplit(string digest, out string state, out string history, out string trust)
    {
        state = history = trust = "";
        if (FormatOf(digest) != FormatVersion) return false;
        var parts = digest[FormatPrefix.Length..].Split('/');
        if (parts.Length != 3 || parts[0].Length != Buckets * BucketHex || parts[1].Length != Buckets * BucketHex
            || parts[2].Length % TrustHashHex != 0 || parts[2].Any(c => !char.IsAsciiHexDigitLower(c) && !char.IsAsciiDigit(c)))
            return false;
        (state, history, trust) = (parts[0], parts[1], parts[2]);
        return true;
    }

    public static int BucketOf(StateDigestEntry e) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(e.Type + "\0" + e.Id))[0] % Buckets;

    /// <summary>The format an anchor's digest string declares, or null when it declares none.</summary>
    public static int? FormatOf(string? digest)
    {
        if (digest == null || !digest.StartsWith("sd", StringComparison.Ordinal)) return null;
        var colon = digest.IndexOf(':');
        return colon > 2 && int.TryParse(digest.AsSpan(2, colon - 2), out var v) ? v : null;
    }

    /// <param name="cut">
    /// When given (a restore checking an anchor), only entries at or below the cut for their source go into
    /// the digest; the rest come back in <see cref="StateDigestResult.BeyondCut"/>.
    /// </param>
    /// <param name="includeSelfAsSuperadmin">
    /// Only when this node publishes an anchor (it is a superadmin then): its own identity joins the trust section.
    /// </param>
    public static async Task<StateDigestResult> ComputeAsync(
        IDbConnection conn, IReadOnlyDictionary<string, long>? cut = null, bool includeSelfAsSuperadmin = false)
    {
        var tags = (await conn.QueryAsync<(string ArticleId, string Name)>(
                @"SELECT act.article_id, t.name FROM tbl_article_concept_tag act
                  JOIN tbl_concept_tag t ON t.id = act.concept_tag_id"))
            .GroupBy(t => t.ArticleId.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList());

        var entries = new List<(StateDigestEntry Entry, byte[] Bytes)>();

        await ReadAsync(conn,
            @"SELECT id, lamport_ts, source_node_id, status, title, tree_path, protected, protection_hint FROM tbl_article",
            r =>
            {
                var w = Begin("article", r, out var entry);
                w.Str(r, 3); w.Str(r, 4); w.Str(r, 5); w.Long(r, 6); w.Str(r, 7);
                var names = tags.GetValueOrDefault(entry.Id) ?? [];
                w.Long(names.Count);
                foreach (var n in names) w.Str(n);
                entries.Add((entry, w.ToArray()));
            });

        var body = await KeyColumnsAsync(conn, "tbl_article_body", "b", "a");
        await ReadAsync(conn,
            $@"SELECT a.id, {body.Lamport}, {body.Source}, {body.Gen}, b.iv,
                      (SELECT data FROM tbl_blob WHERE hash = b.ciphertext_hash)
               FROM tbl_article_body b JOIN tbl_article a ON a.id = b.article_id",
            r =>
            {
                var w = Begin("article_key", r, out var entry);
                w.Str(r, 3); w.Bytes(r, 4); w.Sha(r, 5);
                entries.Add((entry, w.ToArray()));
            });

        await ReadAsync(conn,
            "SELECT id, lamport_ts, source_node_id, status, path, name, parent_path FROM tbl_folder",
            r =>
            {
                var w = Begin("folder", r, out var entry);
                w.Str(r, 3); w.Str(r, 4); w.Str(r, 5); w.Str(r, 6);
                entries.Add((entry, w.ToArray()));
            });

        await ReadAsync(conn,
            "SELECT id, lamport_ts, source_node_id, status, article_id, file_name, content_type, file_size, kind FROM tbl_media",
            r =>
            {
                var w = Begin("media", r, out var entry);
                w.Str(r, 3); w.Str(r, 4); w.Str(r, 5); w.Str(r, 6); w.Long(r, 7); w.Str(r, 8);
                entries.Add((entry, w.ToArray()));
            });

        var media = await KeyColumnsAsync(conn, "tbl_media", "m", "m");
        await ReadAsync(conn,
            $@"SELECT m.id, {media.Lamport}, {media.Source}, {media.Gen}, m.iv,
                      (SELECT data FROM tbl_blob WHERE hash = m.ciphertext_sha256)
               FROM tbl_media m",
            r =>
            {
                var w = Begin("media_key", r, out var entry);
                w.Str(r, 3); w.Bytes(r, 4); w.Sha(r, 5);
                entries.Add((entry, w.ToArray()));
            });

        await ReadAsync(conn,
            @"SELECT comment_id, lamport_ts, source_node_id, article_id, CASE WHEN encrypted = 1 THEN NULL ELSE text END,
                     encrypted, delete_lamport_ts, delete_node_id
              FROM tbl_comment",
            r =>
            {
                var w = Begin("comment", r, out var entry);
                w.Str(r, 3); w.Str(r, 4); w.Long(r, 5); w.Long(r, 6); w.Str(r, 7);
                entries.Add((entry, w.ToArray()));
            });

        var comment = await KeyColumnsAsync(conn, "tbl_comment", "c", "c");
        await ReadAsync(conn,
            $@"SELECT c.comment_id, {comment.Lamport}, {comment.Source}, {comment.Gen}, c.ciphertext, c.iv
               FROM tbl_comment c WHERE c.ciphertext IS NOT NULL",
            r =>
            {
                var w = Begin("comment_key", r, out var entry);
                w.Str(r, 3); w.Bytes(r, 4); w.Bytes(r, 5);
                entries.Add((entry, w.ToArray()));
            });

        await ReadAsync(conn,
            "SELECT article_id, lamport_ts, source_node_id FROM tbl_tombstone",
            r =>
            {
                var w = Begin("tombstone", r, out var entry);
                entries.Add((entry, w.ToArray()));
            });

        // History: no replicated version, so an entry's version fields are 0 / the empty node, except a
        // conflict copy's, which record the version that lost (stable wherever the copy is kept).
        var history = new List<(StateDigestEntry Entry, byte[] Bytes)>();
        await ReadAsync(conn,
            "SELECT id, 0, NULL, article_id, version_number, title, tree_path, created_at, updated_by FROM tbl_article_version",
            r =>
            {
                var w = Begin("article_version", r, out var entry);
                w.Str(r, 3); w.Long(r, 4); w.Str(r, 5); w.Str(r, 6); w.Str(r, 7); w.Str(r, 8);
                history.Add((entry, w.ToArray()));
            });
        var versionGen = await KeyGenColumnAsync(conn, "tbl_article_version", "v");
        await ReadAsync(conn,
            $"SELECT v.id, 0, NULL, {versionGen}, v.iv, (SELECT data FROM tbl_blob WHERE hash = v.ciphertext_hash) FROM tbl_article_version v",
            r =>
            {
                var w = Begin("article_version_key", r, out var entry);
                w.Str(r, 3); w.Bytes(r, 4); w.Sha(r, 5);
                history.Add((entry, w.ToArray()));
            });
        await ReadAsync(conn,
            "SELECT id, lamport_ts, source_node_id, article_id, metadata_json, created_at, expires_at FROM tbl_conflict_version",
            r =>
            {
                var w = Begin("conflict_version", r, out var entry);
                w.Str(r, 3); w.Str(r, 4); w.Str(r, 5); w.Str(r, 6);
                history.Add((entry, w.ToArray()));
            });
        var conflictGen = await KeyGenColumnAsync(conn, "tbl_conflict_version", "c");
        await ReadAsync(conn,
            $"SELECT c.id, c.lamport_ts, c.source_node_id, {conflictGen}, c.iv, c.ciphertext FROM tbl_conflict_version c",
            r =>
            {
                var w = Begin("conflict_version_key", r, out var entry);
                w.Str(r, 3); w.Bytes(r, 4); w.Sha(r, 5);
                history.Add((entry, w.ToArray()));
            });

        // Trust: every active row, and the publisher's own record (see the class remarks).
        var trust = new List<(StateDigestEntry Entry, byte[] Bytes)>();
        var selfId = await conn.QuerySingleOrDefaultAsync<string?>("SELECT node_id FROM tbl_node_identity LIMIT 1");
        foreach (var (id, key, super) in await conn.QueryAsync<(string Id, byte[] Key, long Super)>(
                     "SELECT node_id, ed25519_public_key, is_superadmin FROM tbl_whitelist WHERE status = 'A'"))
        {
            if (selfId != null && string.Equals(id, selfId, StringComparison.OrdinalIgnoreCase)) continue;
            trust.Add(TrustRow(id, key, super != 0));
        }
        if (includeSelfAsSuperadmin
            && await conn.QuerySingleOrDefaultAsync<(string Id, byte[] Key)?>("SELECT node_id, ed25519_public_key FROM tbl_node_identity LIMIT 1") is { } self)
            trust.Add(TrustRow(self.Id, self.Key, superadmin: true));

        var included = new List<(StateDigestEntry Entry, byte[] Bytes)>();
        var beyond = new List<StateDigestEntry>();
        var vector = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var item in entries)
        {
            var e = item.Entry;
            if (cut != null && !(cut.TryGetValue(e.Source, out var max) && e.LamportTs <= max))
            {
                beyond.Add(e);
                continue;
            }
            included.Add(item);
            vector[e.Source] = Math.Max(vector.GetValueOrDefault(e.Source), e.LamportTs);
        }

        var digest = new StringBuilder(FormatPrefix, FormatPrefix.Length + 3 * Buckets * BucketHex + 2);
        AppendBuckets(digest, included);
        digest.Append('/');
        AppendBuckets(digest, history);
        digest.Append('/');
        foreach (var hash in trust.Select(t => TrustHash(t.Bytes)).Distinct().Order(StringComparer.Ordinal))
            digest.Append(hash);

        var affected = beyond.Select(BucketOf).ToHashSet();
        var unchecked_ = included.Select(i => i.Entry).Where(e => affected.Contains(BucketOf(e))).ToList();
        return new StateDigestResult(digest.ToString(), vector, beyond, affected, unchecked_,
            history.Select(h => h.Entry).ToList(), trust.Select(t => t.Entry).ToList()) { TrustRows = trust };
    }

    private static void AppendBuckets(StringBuilder digest, List<(StateDigestEntry Entry, byte[] Bytes)> entries)
    {
        var byBucket = entries.ToLookup(e => BucketOf(e.Entry));
        for (var b = 0; b < Buckets; b++)
            digest.Append(HashBucket(byBucket[b]));
    }

    // One bucket: SHA-256 over its entries sorted by (type, id), each length-prefixed, truncated.
    private static string HashBucket(IEnumerable<(StateDigestEntry Entry, byte[] Bytes)> entries)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var (_, bytes) in entries.OrderBy(e => e.Entry.Type, StringComparer.Ordinal).ThenBy(e => e.Entry.Id, StringComparer.Ordinal))
        {
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            sha.AppendData(length);
            sha.AppendData(bytes);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset().AsSpan(0, BucketHashBytes));
    }

    // A trust entry: no version of its own (lamport 0, the empty node), then the key and the superadmin flag.
    private static (StateDigestEntry Entry, byte[] Bytes) TrustRow(string nodeId, byte[] key, bool superadmin)
    {
        var id = nodeId.ToLowerInvariant();
        var source = NormalizeSource(null);
        var w = new RowWriter();
        w.Str("peer"); w.Str(id); w.Long(0); w.Str(source);
        w.Bytes(key); w.Long(superadmin ? 1 : 0);
        return (new StateDigestEntry("peer", id, 0, source), w.ToArray());
    }

    /// <summary>SQL for a row's key version and generation: BMB-57's columns where they exist, else the content version.</summary>
    private static async Task<(string Lamport, string Source, string Gen)> KeyColumnsAsync(
        IDbConnection conn, string table, string alias, string contentAlias)
    {
        var columns = (await conn.QueryAsync<string>($"SELECT name FROM pragma_table_info('{table}')"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return columns.Contains("key_lamport_ts") && columns.Contains("key_source_node_id") && columns.Contains("key_gen")
            ? ($"COALESCE({alias}.key_lamport_ts, {contentAlias}.lamport_ts)",
               $"COALESCE({alias}.key_source_node_id, {contentAlias}.source_node_id)",
               $"COALESCE({alias}.key_gen, '0')")
            : ($"{contentAlias}.lamport_ts", $"{contentAlias}.source_node_id", "'0'");
    }

    /// <summary>SQL for a history row's key generation: its <c>key_gen</c> where the column exists, else '0'.</summary>
    private static async Task<string> KeyGenColumnAsync(IDbConnection conn, string table, string alias) =>
        (await conn.QueryAsync<string>($"SELECT name FROM pragma_table_info('{table}')"))
            .Contains("key_gen", StringComparer.OrdinalIgnoreCase)
            ? $"COALESCE({alias}.key_gen, '0')"
            : "'0'";

    // Entry by entry: blob bytes are hashed as they are read, never all held at once.
    private static async Task ReadAsync(IDbConnection conn, string sql, Action<IDataRecord> onRow)
    {
        using var reader = await conn.ExecuteReaderAsync(sql);
        while (reader.Read()) onRow(reader);
    }

    // Every entry starts with its type, id, version and source: columns 0-2 of every query.
    private static RowWriter Begin(string type, IDataRecord r, out StateDigestEntry entry)
    {
        var id = r.GetString(0).ToLowerInvariant();
        var lamport = r.IsDBNull(1) ? 0 : r.GetInt64(1);
        var source = NormalizeSource(r.IsDBNull(2) ? null : r.GetValue(2)?.ToString());
        entry = new StateDigestEntry(type, id, lamport, source);
        var w = new RowWriter();
        w.Str(type); w.Str(id); w.Long(lamport); w.Str(source);
        return w;
    }

    // A row written before sources were recorded counts as the empty node, like everywhere else.
    private static string NormalizeSource(string? source) =>
        (Guid.TryParse(source, out var g) ? g : Guid.Empty).ToString("D").ToUpperInvariant();

    private sealed class RowWriter
    {
        private readonly MemoryStream _ms = new();

        public void Str(string? value) => Field(value == null ? null : Encoding.UTF8.GetBytes(value));
        public void Long(long value) => Field(BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(value)));
        public void Str(IDataRecord r, int i) => Str(r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture));
        public void Long(IDataRecord r, int i) => Field(r.IsDBNull(i) ? null : BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(Convert.ToInt64(r.GetValue(i)))));
        public void Bytes(IDataRecord r, int i) => Field(r.IsDBNull(i) ? null : (byte[])r.GetValue(i));
        public void Bytes(byte[]? value) => Field(value);
        // The SHA-256 of the actual blob bytes, never a stored hash column that could lie about them.
        public void Sha(IDataRecord r, int i) => Field(r.IsDBNull(i) ? null : SHA256.HashData((byte[])r.GetValue(i)));

        public byte[] ToArray() => _ms.ToArray();

        private void Field(byte[]? bytes)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes?.Length ?? -1);
            _ms.Write(length);
            if (bytes != null) _ms.Write(bytes);
        }
    }
}
