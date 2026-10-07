using System.Text.Json;
using System.Text.Json.Serialization;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync.Blind;

/// <summary>
/// <c>blind-manifest.json</c> inside a blind package (CONTRACTS §2, plan 4.2, 5.2, 10): what the
/// receiving blind node needs besides the peer snapshot itself. Listed and hashed in the signed
/// snapshot manifest, so it is exactly as trustworthy as the package signature.
/// </summary>
/// <param name="SeedId">Chosen by the sender; one seed per id, and the blind node's 409 names it.</param>
/// <param name="ProducerNodeId">Whose key signed the package.</param>
/// <param name="CpSequence">The producer's own log head when the package was cut: the producer's
/// push position for the receiver afterwards (plan 4.2 step 3).</param>
/// <param name="IncludesUpTo">Reseed only (plan 5.2): the package already contains everything the
/// blind node had up to this position of ITS log; the blind node re-applies its own events after
/// it. Null for a first seed and a replica.</param>
/// <param name="Whitelist">Who the receiver trusts afterwards, the producer included, with the
/// superadmin flag and the TLS pin of each row.</param>
/// <param name="Positions">The producer's pull positions: its content is the receiver's content now,
/// so it has pulled from each of those peers exactly as far.</param>
public sealed record BlindManifest(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("seed_id")] Guid SeedId,
    [property: JsonPropertyName("producer_node_id")] Guid ProducerNodeId,
    [property: JsonPropertyName("cp_sequence")] long CpSequence,
    [property: JsonPropertyName("includes_up_to")] long? IncludesUpTo,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("whitelist")] IReadOnlyList<BlindManifestPeer> Whitelist,
    [property: JsonPropertyName("positions")] IReadOnlyList<BlindManifestPosition> Positions,
    [property: JsonPropertyName("standing")] IReadOnlyList<SyncEvent>? Standing = null)
{
    public const string FileName = "blind-manifest.json";
    public const string CurrentFormat = "bmb-blind-package-v1";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    public static BlindManifest Parse(byte[] bytes)
    {
        var manifest = JsonSerializer.Deserialize<BlindManifest>(bytes)
            ?? throw new InvalidDataException("Empty blind manifest.");
        if (manifest.Format != CurrentFormat)
            throw new InvalidDataException($"Unsupported blind package format '{manifest.Format}'.");
        return manifest;
    }
}

public sealed record BlindManifestPeer(
    [property: JsonPropertyName("node_id")] Guid NodeId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("public_key")] string PublicKeyB64,
    [property: JsonPropertyName("api_address")] string? ApiAddress,
    [property: JsonPropertyName("is_superadmin")] bool IsSuperadmin,
    [property: JsonPropertyName("tls_spki")] string? TlsSpki,
    [property: JsonPropertyName("lamport_ts")] long LamportTs,
    [property: JsonPropertyName("source_node_id")] Guid? SourceNodeId,
    // How blind copies trust this peer's TLS endpoint (BlindTrust, ADR 0007). Absent from an older package; an
    // older reader ignores it, which is all it needs: a pin is a pin there.
    [property: JsonPropertyName("tls_trust")] string? TlsTrust = null);

public sealed record BlindManifestPosition(
    [property: JsonPropertyName("remote_node_id")] Guid RemoteNodeId,
    [property: JsonPropertyName("last_sequence")] long LastSequence);
