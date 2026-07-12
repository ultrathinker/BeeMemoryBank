using System.Text.Json.Serialization;

namespace BeeMemoryBank.Api.Services;

// ── Manifest DTO (mirrors superplan §6.1 releases.json shape) ────────────────

/// <summary>
/// Root DTO for the releases.json manifest file.
/// A detached Ed25519 signature over the raw manifest bytes is verified
/// against the two embedded release public keys before any version check.
/// </summary>
public sealed class ReleasesManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("channels")]
    public ReleasesChannels Channels { get; set; } = new();
}

public sealed class ReleasesChannels
{
    [JsonPropertyName("stable")]
    public ReleaseChannelInfo Stable { get; set; } = new();
}

public sealed class ReleaseChannelInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; }

    [JsonPropertyName("artifacts")]
    public List<ArtifactDescriptor> Artifacts { get; set; } = [];
}

public sealed class ArtifactDescriptor
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

// ── Update state machine enums ────────────────────────────────────────────────

public enum UpdateFlowStep
{
    Idle,
    Checking,
    UpdateAvailable,
    Downloading,
    ReadyToApply,
    Applying,
    Completed,
    Failed
}

// ── Progress response (mirrors DekRotationProgressResponse shape) ─────────────

public sealed record UpdateProgressResponse(
    UpdateFlowStep CurrentStep,
    string? AvailableVersion,
    int PercentageComplete,
    string? StatusMessage,
    string? ErrorMessage,
    IReadOnlyList<string>? BlockedGates
);

// ── Check request ─────────────────────────────────────────────────────────────

/// <summary>
/// Body for POST /node/update/check — caller supplies the manifest JSON and
/// its detached base64 Ed25519 signature so the service can verify authenticity
/// before comparing versions.
/// </summary>
public sealed class UpdateCheckRequest
{
    /// <summary>Raw UTF-8 JSON of the releases.json manifest.</summary>
    [JsonPropertyName("manifestJson")]
    public string ManifestJson { get; set; } = "";

    /// <summary>Base64-encoded detached Ed25519 signature over the raw manifest bytes.</summary>
    [JsonPropertyName("manifestSignatureBase64")]
    public string ManifestSignatureBase64 { get; set; } = "";
}
