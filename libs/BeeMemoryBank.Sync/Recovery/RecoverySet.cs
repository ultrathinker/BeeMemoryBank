using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Crypto;
using Dapper;

namespace BeeMemoryBank.Sync.Recovery;

public sealed record RecoverySetBox(
    [property: JsonPropertyName("box_id")] string BoxId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("author_node_id")] string AuthorNodeId,
    [property: JsonPropertyName("dek_fingerprint")] string DekFingerprint,
    [property: JsonPropertyName("epoch_hint")] long EpochHint,
    [property: JsonPropertyName("kdf_preset")] string KdfPreset,
    [property: JsonPropertyName("salt")] string Salt,
    [property: JsonPropertyName("wrapped")] string Wrapped,
    [property: JsonPropertyName("iv")] string Iv,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("lamport_ts")] long LamportTs,
    [property: JsonPropertyName("source_node_id")] string? SourceNodeId);

public sealed record RecoverySetLink(
    [property: JsonPropertyName("commit_id")] string CommitId,
    [property: JsonPropertyName("author_node_id")] string AuthorNodeId,
    [property: JsonPropertyName("old_fingerprint")] string OldFingerprint,
    [property: JsonPropertyName("new_fingerprint")] string NewFingerprint,
    [property: JsonPropertyName("wrapped")] string Wrapped,
    [property: JsonPropertyName("iv")] string Iv,
    [property: JsonPropertyName("created_at")] string CreatedAt);

public sealed record RecoverySetAnchor(
    [property: JsonPropertyName("anchor_id")] string AnchorId,
    [property: JsonPropertyName("author_node_id")] string AuthorNodeId,
    [property: JsonPropertyName("dek_fingerprint")] string DekFingerprint,
    [property: JsonPropertyName("position_vector")] Dictionary<string, long> PositionVector,
    [property: JsonPropertyName("digest")] string Digest,
    [property: JsonPropertyName("hmac")] string Hmac,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("lamport_ts")] long LamportTs);

public sealed record RecoverySetSecret(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("dek_fingerprint")] string DekFingerprint,
    [property: JsonPropertyName("wrapped")] string Wrapped,
    [property: JsonPropertyName("iv")] string Iv,
    [property: JsonPropertyName("updated_at")] string UpdatedAt);

/// <summary>
/// The open recovery set kept NEXT TO every backup (CONTRACTS §2, plan 6.8): everything a restore needs
/// before it can open the backup — boxes, chain links, anchors, sealed secrets (the backup's own password
/// sealed under the DEK). All of it is sealed already, so lying in the open costs nothing a copy of the
/// backup would not. Only ACTIVE boxes go in: a superseded device box may hold an old, weaker password,
/// and a retired one is covered by a strong box with the same key.
/// </summary>
public sealed record RecoverySet(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("boxes")] List<RecoverySetBox> Boxes,
    [property: JsonPropertyName("links")] List<RecoverySetLink> Links,
    [property: JsonPropertyName("anchors")] List<RecoverySetAnchor> Anchors,
    [property: JsonPropertyName("sealed_secrets")] List<RecoverySetSecret> SealedSecrets,
    [property: JsonPropertyName("created_at")] string CreatedAt)
{
    public const string FormatV1 = "bmb-recovery-set-v1";

    // A recovery set arrives from a folder, a bucket or a file header anyone could have replaced.
    // These bounds keep a crafted one from exhausting a restoring PC before a single box is tried.
    private const int MaxBytes = 16 * 1024 * 1024;
    private const int MaxBoxes = 1024, MaxLinks = 4096, MaxAnchors = 4096, MaxSecrets = 1024;

    public string ToJson() => JsonSerializer.Serialize(this);

    public static RecoverySet Parse(string json)
    {
        if (json.Length > MaxBytes)
            throw new InvalidDataException("Recovery set is too large.");
        var set = JsonSerializer.Deserialize<RecoverySet>(json)
            ?? throw new InvalidDataException("Empty recovery set.");
        if (set.Format != FormatV1)
            throw new InvalidDataException($"Unknown recovery set format '{set.Format}'.");
        if (set.Boxes is not { Count: <= MaxBoxes } || set.Links is not { Count: <= MaxLinks }
            || set.Anchors is not { Count: <= MaxAnchors } || set.SealedSecrets is not { Count: <= MaxSecrets })
            throw new InvalidDataException("Recovery set exceeds its limits.");
        return set;
    }
}

/// <summary>
/// Builds the recovery set from this node's tables (CONTRACTS §2). Needs no DEK: a blind node's backup
/// job calls it (IRecoverySetSource, <c>&lt;repo&gt;.recovery-set.json</c> next to the restic repository).
/// </summary>
public class RecoverySetBuilder(IDbConnectionFactory connFactory)
{
    public async Task<RecoverySet> BuildAsync()
    {
        using var conn = connFactory.CreateConnection();
        return await BuildAsync(conn);
    }

    public async Task<string> BuildJsonAsync() => (await BuildAsync()).ToJson();

    public static async Task<RecoverySet> BuildAsync(IDbConnection conn)
    {
        var boxes = (await conn.QueryAsync<(string BoxId, string Kind, string Author, string Fp, long Epoch, string Preset,
                byte[] Salt, byte[] Wrapped, byte[] Iv, string CreatedAt, long Lamport, string? Source)>(
            @"SELECT box_id, kind, author_node_id, dek_fingerprint, epoch_hint, kdf_preset, salt, wrapped, iv,
                     created_at, lamport_ts, source_node_id
              FROM tbl_recovery_box WHERE status = 'A' ORDER BY box_id"))
            .Select(b => new RecoverySetBox(b.BoxId, b.Kind, b.Author, b.Fp, b.Epoch, b.Preset,
                B64(b.Salt), B64(b.Wrapped), B64(b.Iv), b.CreatedAt, b.Lamport, b.Source))
            .ToList();

        var links = (await conn.QueryAsync<(string Commit, string Author, string Old, string New, byte[] Wrapped, byte[] Iv, string CreatedAt)>(
            @"SELECT commit_id, author_node_id, old_fingerprint, new_fingerprint, wrapped, iv, created_at
              FROM tbl_dek_retired_link ORDER BY commit_id, author_node_id"))
            .Select(l => new RecoverySetLink(l.Commit, l.Author, l.Old, l.New, B64(l.Wrapped), B64(l.Iv), l.CreatedAt))
            .ToList();

        var anchors = (await conn.QueryAsync<(string Id, string Author, string Fp, string Vector, string Digest, string Hmac, string CreatedAt, long Lamport)>(
            @"SELECT anchor_id, author_node_id, dek_fingerprint, position_vector, digest, hmac, created_at, lamport_ts
              FROM tbl_state_anchor ORDER BY created_at"))
            .Select(a => new RecoverySetAnchor(a.Id, a.Author, a.Fp,
                JsonSerializer.Deserialize<Dictionary<string, long>>(a.Vector) ?? [], a.Digest, a.Hmac, a.CreatedAt, a.Lamport))
            .ToList();

        var secrets = (await conn.QueryAsync<(string Name, string Fp, byte[] Wrapped, byte[] Iv, string UpdatedAt)>(
            "SELECT name, dek_fingerprint, wrapped, iv, updated_at FROM tbl_sealed_secret WHERE status = 'A' ORDER BY name"))
            .Select(s => new RecoverySetSecret(s.Name, s.Fp, B64(s.Wrapped), B64(s.Iv), s.UpdatedAt))
            .ToList();

        return new RecoverySet(RecoverySet.FormatV1, boxes, links, anchors, secrets, DateTime.UtcNow.ToString("O"));
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes);
}
