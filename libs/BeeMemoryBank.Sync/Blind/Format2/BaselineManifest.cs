using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeeMemoryBank.Sync.Blind.Format2;

/// <summary>The signed description of a baseline (spec 7.4). The exact bytes of manifest.json are what is signed.</summary>
public sealed record BaselineManifest(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("baseline_id")] Guid BaselineId,
    [property: JsonPropertyName("producer_node_id")] Guid ProducerNodeId,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("producer_log_head")] long ProducerLogHead,
    [property: JsonPropertyName("lamport_at_cut")] long LamportAtCut,
    [property: JsonPropertyName("pull_positions")] IReadOnlyList<BlindManifestPosition> PullPositions,
    [property: JsonPropertyName("key_id")] string KeyId,
    [property: JsonPropertyName("key_generation")] int KeyGeneration,
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("container_sha256")] string ContainerSha256,
    [property: JsonPropertyName("container_bytes")] long ContainerBytes,
    [property: JsonPropertyName("blob_list_sha256")] string BlobListSha256,
    [property: JsonPropertyName("blob_count")] int BlobCount,
    [property: JsonPropertyName("blob_bytes")] long BlobBytes)
{
    public const string CurrentFormat = "bmb-baseline-v1";
    public const string FileName = "manifest.json";
    public const string SignatureFileName = "manifest.json.sig";
    public const string BlobListFileName = "blobs.txt";
    public const string ContainerFileName = "baseline.bmbch";

    private static readonly byte[] SignatureDomain = "BMB-BASELINE-V1\0"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    public static BaselineManifest Parse(byte[] bytes)
    {
        var manifest = JsonSerializer.Deserialize<BaselineManifest>(bytes)
            ?? throw new InvalidDataException("Empty baseline manifest.");
        if (manifest.Format != CurrentFormat)
            throw new InvalidDataException($"Unsupported baseline format '{manifest.Format}'.");
        manifest.Validate();
        return manifest;
    }

    /// <summary>
    /// A producer's signature makes the manifest authentic, not complete: the collections and strings every receiver reads
    /// (<see cref="PullPositionOf"/>, the hash comparisons) must be there and in range, including the format-2 Lamport bound,
    /// or reading fails later, after a baseline was already taken. Whether the values are right is the receiver's business (the
    /// hashes, the key, the cut).
    /// </summary>
    private void Validate()
    {
        if (KeyId is null || ContainerSha256 is null || BlobListSha256 is null)
            throw new InvalidDataException("The baseline manifest lacks its key id or one of its hashes.");
        if (ProducerLogHead < 0 || ContainerBytes < 0 || BlobCount < 0 || BlobBytes < 0)
            throw new InvalidDataException("The baseline manifest holds a negative position, size or count.");
        if (LamportAtCut < 0 || LamportAtCut > BlindManifestV2.MaximumLamport)
            throw new InvalidDataException("The baseline manifest holds a Lamport time outside the format-2 limit.");
        if (PullPositions is null)
            throw new InvalidDataException("The baseline manifest lacks its pull positions.");
        var seen = new HashSet<Guid>();
        foreach (var position in PullPositions)
        {
            if (position is null || position.RemoteNodeId == Guid.Empty)
                throw new InvalidDataException("The baseline manifest holds a pull position without a node id.");
            if (position.LastSequence < 0)
                throw new InvalidDataException("The baseline manifest holds a negative pull position.");
            if (!seen.Add(position.RemoteNodeId))
                throw new InvalidDataException("The baseline manifest lists a pull position twice.");
        }
    }

    /// <summary>What the producer signs and every receiver verifies: domain tag, then the manifest bytes as stored.</summary>
    public static byte[] SignaturePayload(byte[] manifestBytes)
    {
        var payload = new byte[SignatureDomain.Length + manifestBytes.Length];
        SignatureDomain.CopyTo(payload, 0);
        manifestBytes.CopyTo(payload, SignatureDomain.Length);
        return payload;
    }

    /// <summary>The producer's pull position on <paramref name="node"/>, or 0 when it never pulled from it.</summary>
    public long PullPositionOf(Guid node) =>
        PullPositions.FirstOrDefault(p => p.RemoteNodeId == node)?.LastSequence ?? 0;
}
