using System.Text.Json.Serialization;

namespace BeeMemoryBank.Sync.Metadata;

// Payloads of the two control events of the metadata key (docs/design/step-a-spec.md 4.3, 5.4). Snake case like their
// neighbours in RecoveryPayloads.cs; binary fields are standard base64. Both travel in clear and hold no human-authored
// metadata: a key id, a generation, a DEK fingerprint, ciphertext a blind host cannot open, and timestamps.

/// <summary>
/// metadata_key_set: one generation of the MEK, wrapped under the master DEK named by <see cref="DekFingerprint"/>
/// (AES-256-GCM, AAD <c>"bmb-mek-v1:" + key_id + ":" + generation + ":" + dek_fingerprint</c>). Superadmin only.
/// </summary>
public sealed record MetadataKeySetPayload(
    [property: JsonPropertyName("key_id")]          string KeyId,
    [property: JsonPropertyName("generation")]      int Generation,
    [property: JsonPropertyName("dek_fingerprint")] string DekFingerprint,
    [property: JsonPropertyName("epoch_hint")]      int EpochHint,
    [property: JsonPropertyName("wrapped")]         string Wrapped,
    [property: JsonPropertyName("iv")]              string Iv,
    [property: JsonPropertyName("created_at")]      string CreatedAt
);

/// <summary>metadata_activation: the owner's one-way switch (spec 5.4), naming the key generation it starts with.</summary>
public sealed record MetadataActivationPayload(
    [property: JsonPropertyName("activation_id")] string ActivationId,
    [property: JsonPropertyName("key_id")]        string KeyId,
    [property: JsonPropertyName("generation")]    int Generation,
    [property: JsonPropertyName("activated_at")]  string ActivatedAt
);

/// <summary>
/// A metadata_activation names a key whose metadata_key_set has not been applied here yet. The two travel together (the key
/// just before the activation), so this is a missing precondition, retried like a whitelist_add still in flight.
/// </summary>
public sealed class MetadataKeyMissingException(string keyId)
    : InvalidOperationException($"Metadata key {keyId} is not known here yet."), IDeferrableSyncFailure
{
    public string KeyId { get; } = keyId;
}
