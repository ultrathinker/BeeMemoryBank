using System.Text.Json.Serialization;

namespace BeeMemoryBank.Sync;

// Payloads of the recovery events that feed blind nodes (BMB-43, plan sections 5.5 and 6). Binary
// fields are base64; fingerprints are lowercase hex of SHA256("bmb-dek-verify" || DEK). The tables
// they land in are described in migration 027_blind_recovery.sql.

/// <summary>
/// recovery_box_set — a node publishes ITS OWN box (the signer must equal <see cref="AuthorNodeId"/>).
/// <see cref="Kind"/> is "strong" or "device"; <see cref="KdfPreset"/> is one of the fixed presets
/// ("d64t3" for device boxes, "s512t6" / "s1024t4" for strong ones) — anything else is rejected, so
/// a crafted box cannot make a restoring PC run an arbitrarily expensive derivation.
/// </summary>
public record RecoveryBoxSetPayload(
    [property: JsonPropertyName("box_id")]          string BoxId,
    [property: JsonPropertyName("kind")]            string Kind,
    [property: JsonPropertyName("author_node_id")]  string AuthorNodeId,
    [property: JsonPropertyName("dek_fingerprint")] string DekFingerprint,
    [property: JsonPropertyName("epoch_hint")]      int EpochHint,
    [property: JsonPropertyName("kdf_preset")]      string KdfPreset,
    [property: JsonPropertyName("salt")]            string Salt,
    [property: JsonPropertyName("wrapped")]         string Wrapped,
    [property: JsonPropertyName("iv")]              string Iv
);

/// <summary>
/// recovery_box_retire — superadmin only. Retires other nodes' boxes that the named strong box covers
/// (same password, same <c>dek_fingerprint</c>). A receiver applies it only while the covering box is
/// active there; if the covering box has not arrived yet, the retire is deferred, never dropped.
/// </summary>
public record RecoveryBoxRetirePayload(
    [property: JsonPropertyName("box_ids")]          IReadOnlyList<string> BoxIds,
    [property: JsonPropertyName("covering_box_id")]  string CoveringBoxId
);

/// <summary>
/// retired_link_set — any full node that holds the link publishes it (not only the rotation
/// initiator). Stored under (commit_id, author node); a forged link is harmless because a restore
/// keeps only links that open under a verified newer DEK and yield the claimed old fingerprint.
/// </summary>
public record RetiredLinkSetPayload(
    [property: JsonPropertyName("commit_id")]       string CommitId,
    [property: JsonPropertyName("old_fingerprint")] string OldFingerprint,
    [property: JsonPropertyName("new_fingerprint")] string NewFingerprint,
    [property: JsonPropertyName("wrapped")]         string Wrapped,
    [property: JsonPropertyName("iv")]              string Iv
);

/// <summary>
/// state_anchor — superadmin only. <see cref="PositionVector"/>: source node id (UPPERCASE) → highest
/// position the digest includes. <see cref="Digest"/> carries its encoding: <c>"sd&lt;format&gt;:" + hex</c>
/// (Recovery/StateDigest.cs), so an anchor of another format is recognised, not silently mismatched. <see cref="Hmac"/> = HMAC-SHA256(HKDF(DEK, "bmb-blind-state-v1"),
/// canonical(anchor_id, dek_fingerprint, position_vector, digest, created_at)), hex.
/// </summary>
public record StateAnchorPayload(
    [property: JsonPropertyName("anchor_id")]       string AnchorId,
    [property: JsonPropertyName("dek_fingerprint")] string DekFingerprint,
    [property: JsonPropertyName("position_vector")] IReadOnlyDictionary<string, long> PositionVector,
    [property: JsonPropertyName("digest")]          string Digest,
    [property: JsonPropertyName("hmac")]            string Hmac,
    [property: JsonPropertyName("created_at")]      string CreatedAt
);

/// <summary>
/// sealed_secret_set — any full node. A small secret a restore needs before it can open a backup
/// ("restic:&lt;blind node id&gt;", "android-backup:&lt;node id&gt;"), sealed under the DEK named by
/// <see cref="DekFingerprint"/>; re-sealed by any full node after a rotation.
/// </summary>
public record SealedSecretSetPayload(
    [property: JsonPropertyName("name")]            string Name,
    [property: JsonPropertyName("dek_fingerprint")] string DekFingerprint,
    [property: JsonPropertyName("wrapped")]         string Wrapped,
    [property: JsonPropertyName("iv")]              string Iv
);
