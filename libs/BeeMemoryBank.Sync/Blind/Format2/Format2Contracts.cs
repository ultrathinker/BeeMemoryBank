using System.Text.Json;
using System.Text.Json.Serialization;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Metadata;

namespace BeeMemoryBank.Sync.Blind.Format2;

public static class BlindPackageKinds
{
    public const string Full = "full";
    public const string Baseline = "baseline";
}

public sealed record BlindActivationRef(
    [property: JsonPropertyName("activation_id")] Guid ActivationId,
    [property: JsonPropertyName("key_id")] string KeyId);

/// <summary>blind-manifest.json of a format-2 package (spec 8.3). ProducerNodeId is the SERVING node.</summary>
public sealed record BlindManifestV2(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("seed_id")] Guid SeedId,
    [property: JsonPropertyName("producer_node_id")] Guid ProducerNodeId,
    [property: JsonPropertyName("cp_sequence")] long CpSequence,
    [property: JsonPropertyName("includes_up_to")] long? IncludesUpTo,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("whitelist")] IReadOnlyList<BlindManifestPeer> Whitelist,
    [property: JsonPropertyName("positions")] IReadOnlyList<BlindManifestPosition> Positions,
    [property: JsonPropertyName("baseline_id")] Guid BaselineId,
    [property: JsonPropertyName("cut_in_server_log")] long CutInServerLog,
    [property: JsonPropertyName("delta_count")] int DeltaCount,
    [property: JsonPropertyName("contains_blobs")] bool ContainsBlobs,
    [property: JsonPropertyName("activation")] BlindActivationRef? Activation,
    [property: JsonPropertyName("standing")] IReadOnlyList<SyncEvent>? Standing = null)
{
    public const string FileName = "blind-manifest.json";
    public const string CurrentFormat = "bmb-blind-package-v2";
    public const string ControlDbFileName = "control.db";
    public const string DeltasFileName = "deltas.jsonl";
    public const string BaselineDirectory = "baseline";
    public const string BlobsDirectory = "blobs";

    /// <summary>
    /// The exclusive Lamport ceiling used by restore (<c>RecoveryRestoreService.MaxRestoredLamport</c>): 2^53 events, beyond any real
    /// mesh and far from the type's overflow edge. A first-seed grant is one above the highest time it brings, so the package leaves
    /// one value for that grant and still keeps it below restore's exclusive ceiling.
    /// </summary>
    public const long RestoreLamportExclusive = 1L << 53;

    /// <summary>The highest Lamport time a format-2 package may carry; the next value is reserved for a first-seed grant.</summary>
    public const long MaximumLamport = RestoreLamportExclusive - 2;

    /// <summary>The highest Lamport time a first-seed grant may use and still be accepted by restore.</summary>
    public const long MaximumFirstSeedGrant = RestoreLamportExclusive - 1;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    public static BlindManifestV2 Parse(byte[] bytes)
    {
        var manifest = JsonSerializer.Deserialize<BlindManifestV2>(bytes)
            ?? throw new InvalidDataException("Empty blind manifest.");
        if (manifest.Format != CurrentFormat)
            throw new InvalidDataException($"Unsupported blind package format '{manifest.Format}'.");
        if (manifest.Kind is not (BlindPackageKinds.Full or BlindPackageKinds.Baseline))
            throw new InvalidDataException($"Unsupported blind package kind '{manifest.Kind}'.");
        manifest.Validate();
        return manifest;
    }

    /// <summary>
    /// Everything a receiver reads from the manifest after it has adopted the baseline - the whitelist rows, the positions, the
    /// standing events, the activation - is checked here, before anything is adopted: the manifest is signed, which makes it
    /// authentic, not well-formed, and a field that fails halfway through a seed leaves the node half-applied (step-a-spec 8.6).
    /// </summary>
    private void Validate()
    {
        if (ProducerNodeId == Guid.Empty) throw Invalid("names no serving node");
        if (BaselineId == Guid.Empty) throw Invalid("names no baseline");
        if (CpSequence < 0 || CutInServerLog < 0 || DeltaCount < 0 || IncludesUpTo is < 0)
            throw Invalid("has a negative position or count");

        if (Whitelist is null) throw Invalid("has no whitelist");
        var peers = new HashSet<Guid>();
        foreach (var peer in Whitelist)
        {
            if (peer is null || peer.NodeId == Guid.Empty) throw Invalid("lists a row without a node id");
            if (!peers.Add(peer.NodeId)) throw Invalid("lists a node twice");
            if (peer.DisplayName is null) throw Invalid($"lists node {peer.NodeId} without a display name");
            if (!IsPublicKey(peer.PublicKeyB64)) throw Invalid($"lists node {peer.NodeId} with a public key that is not {CryptoConstants.Ed25519PublicKeySize} bytes of base64");
            if (peer.LamportTs < 0 || peer.LamportTs > MaximumLamport) throw Invalid($"lists node {peer.NodeId} with a Lamport time that is negative or past the limit");
        }

        if (Positions is null) throw Invalid("has no positions");
        var positions = new HashSet<Guid>();
        foreach (var position in Positions)
        {
            if (position is null || position.RemoteNodeId == Guid.Empty) throw Invalid("has a position without a node id");
            if (position.LastSequence < 0) throw Invalid($"has a negative position for node {position.RemoteNodeId}");
            if (!positions.Add(position.RemoteNodeId)) throw Invalid($"lists a position twice for node {position.RemoteNodeId}");
        }

        if (Activation is not null && (Activation.ActivationId == Guid.Empty || !SealedEnvelope.IsKeyId(Activation.KeyId)))
            throw Invalid("has an activation without a valid id or key id");

        // The standing events of a first seed go through the applier one by one (a refused one is skipped), but their
        // Lamport times set the grant time, and a whitelist_add is read for its key before it is applied.
        foreach (var evt in Standing ?? [])
        {
            if (evt is null || evt.EventId == Guid.Empty || string.IsNullOrEmpty(evt.EventType) || evt.Payload is null)
                throw Invalid("carries a standing event that is not a complete event");
            if (evt.LamportTs < 0 || evt.LamportTs > MaximumLamport)
                throw Invalid("carries a standing event whose Lamport time cannot be used");
            if (evt.EventType == EventTypes.WhitelistAdd && !ReadsAsWhitelistAdd(evt.Payload))
                throw Invalid("carries a standing whitelist_add event whose payload cannot be read");
        }
    }

    private static InvalidDataException Invalid(string what) => new($"The blind manifest {what}.");

    private static bool IsPublicKey(string? base64)
    {
        if (base64 is null) return false;
        Span<byte> key = stackalloc byte[CryptoConstants.Ed25519PublicKeySize];
        return Convert.TryFromBase64String(base64, key, out var written) && written == key.Length;
    }

    private static bool ReadsAsWhitelistAdd(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<WhitelistAddPayload>(payload) is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>A baseline on disk: the directory holds manifest.json, manifest.json.sig, blobs.txt and baseline.bmbch.</summary>
public sealed record BaselineFiles(string Directory, BaselineManifest Manifest, byte[] ManifestBytes, byte[] Signature);

/// <summary>The baseline a node can serve and the position of ITS OWN log the baseline covers (spec 8.3, 9.7).</summary>
public sealed record ServableBaseline(BaselineFiles Files, long CutInLocalLog);

/// <summary>Full node: newest produced baseline (track 3). Blind host: current adopted one (track 2).</summary>
public interface IBaselineSource
{
    Task<ServableBaseline?> GetServableAsync(CancellationToken ct = default);
}

public sealed record BlindPackageV2Request(
    string Kind, Guid SeedId, ServableBaseline Baseline, long? IncludesUpTo, bool IncludeBlobs, string? OutputDirectory = null);

public sealed record BlindPackageV2(string FilePath, string Sha256, long Length, byte[] DetachedSignature, BlindManifestV2 Manifest);

/// <summary>Assembles and signs a format-2 package from a servable baseline and this node's own log, control rows and blobs.</summary>
public interface IBlindPackageV2Writer
{
    Task<BlindPackageV2> WriteAsync(BlindPackageV2Request request, CancellationToken ct = default);
}

/// <summary>A package after the checks of spec 8.6, steps 1-4. Deltas are still to be stored through the applier.</summary>
public sealed record VerifiedPackageV2(
    string Directory, BlindManifestV2 Manifest, BaselineFiles Baseline,
    string? ControlDbPath, string? DeltasPath, string? BlobsDirectory);

/// <param name="trustedProducer">Public key and superadmin flag of a baseline producer as the RECEIVER trusts it; null = unknown.</param>
public interface IBlindPackageV2Reader
{
    Task<VerifiedPackageV2> ExtractVerifiedAsync(
        string archivePath, byte[] detachedSignature, byte[] servingNodePublicKey,
        Func<Guid, (byte[] PublicKey, bool IsSuperadmin)?> trustedProducer,
        string targetDirectory, CancellationToken ct = default);
}

public sealed record AdoptedBaseline(BaselineFiles Files, long LocalCut, DateTime AdoptedAt);

public enum AdoptOutcome { Adopted, AlreadyCurrent, Refused }

public sealed record AdoptResult(AdoptOutcome Outcome, string? Reason, int TrimmedEvents, int CollectedBlobs);

/// <summary>A blind host's baseline store (spec 9). Never opens a baseline.</summary>
public interface IBaselineStore
{
    Task<AdoptedBaseline?> GetCurrentAsync(CancellationToken ct = default);

    /// <param name="localCut">Position in this node's own log the baseline covers (spec 9.2).</param>
    /// <param name="producerAuthorized">The producer check of spec 9.2 step 1, made by the caller who knows the authority (pair code or whitelist).</param>
    Task<AdoptResult> AdoptAsync(BaselineFiles files, long localCut, bool producerAuthorized, CancellationToken ct = default);

    Task<int> TrimAsync(CancellationToken ct = default);
    Task<int> CollectGarbageAsync(CancellationToken ct = default);

    /// <summary>Spec 9.3; idempotent.</summary>
    Task PurgeLegacyContentAsync(CancellationToken ct = default);
}
