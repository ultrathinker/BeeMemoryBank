using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Sync;

/// <summary>Closed vocabulary for a node's runtime environment reported in <c>node_state</c>.</summary>
public static class NodeRuntimeKinds
{
    public const string Blind = "blind";
    public const string Container = "container";
    public const string Mobile = "mobile";
    public const string Desktop = "desktop";
    public const string Server = "server";

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        Blind, Container, Mobile, Desktop, Server,
    };

    public static string Normalize(string? kind)
    {
        var value = kind?.Trim().ToLowerInvariant();
        return value is not null && Known.Contains(value) ? value : "";
    }

    public static string DetectCurrent(Guid nodeId)
    {
        if (BlindNodeId.IsBlind(nodeId)) return Blind;
        if (Metadata.NodePlatform.InContainer) return Container;
        if (Metadata.NodePlatform.Current is Metadata.NodePlatform.Android or Metadata.NodePlatform.Ios) return Mobile;
        if (Metadata.NodePlatform.Current == Metadata.NodePlatform.Linux) return Server;
        return Desktop;
    }

    public static bool IsInfrastructure(string? runtimeKind) =>
        Normalize(runtimeKind) is Blind or Container or Server;
}

/// <summary>Mutable <c>node_state</c> snapshot assembled across <see cref="INodeStateContributor"/> instances.</summary>
public sealed class NodeStateDraft
{
    public string AppVersion { get; set; } = "";
    public string? Platform { get; set; }
    public string RuntimeKind { get; set; } = "";
    public string IdentityKeyFp { get; set; } = "";
    public string? DekFp { get; set; }
    public List<string> AttentionFlags { get; } = [];
    public Dictionary<string, string> Extra { get; } = new(StringComparer.Ordinal);

    public static string IdentityFingerprint(byte[] ed25519PublicKey) =>
        Convert.ToHexStringLower(SHA256.HashData(ed25519PublicKey));

    public string ComputeDigest() => DigestOf(
        AppVersion,
        Platform,
        RuntimeKind,
        IdentityKeyFp,
        DekFp,
        AttentionFlags,
        Extra);

    public static string DigestOf(NodeStateRecord row) => DigestOf(
        row.AppVersion,
        row.Platform,
        row.RuntimeKind,
        row.IdentityKeyFp,
        row.DekFp,
        row.AttentionFlags,
        row.Extra);

    private static string DigestOf(
        string? appVersion,
        string? platform,
        string? runtimeKind,
        string? identityKeyFp,
        string? dekFp,
        IEnumerable<string>? flags,
        IReadOnlyDictionary<string, string>? extra)
    {
        var flagPart = flags is null
            ? ""
            : string.Join(",", flags.Where(f => !string.IsNullOrWhiteSpace(f)).Order(StringComparer.Ordinal));
        var extraPart = extra is null || extra.Count == 0
            ? ""
            : string.Join("&", extra.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
        return string.Join("|",
            appVersion ?? "",
            Metadata.NodePlatform.Normalize(platform) ?? "",
            NodeRuntimeKinds.Normalize(runtimeKind),
            identityKeyFp ?? "",
            dekFp ?? "",
            flagPart,
            extraPart);
    }
}

/// <summary>Keys of <see cref="NodeStateDraft.Extra"/> that a reader of another node's report looks for.</summary>
public static class NodeStateExtraKeys
{
    /// <summary>How many v1 doors (key slots no rotation can re-seal) the node has, in invariant digits (release 2.12).</summary>
    public const string V1Doors = "v1_doors";

    /// <summary>"1" while the node keeps the chain material of a rotation (the bridge from an old master key), "0" when it keeps none, and
    /// <see cref="Unknown"/> when the node could not read it (release 2.12).</summary>
    public const string DekBridge = "dek_bridge";

    /// <summary>The value of a field the node could not determine.</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// Why this node cannot give itself a new identity key when a removal asks (release 2.12.0, step S1): <see cref="RekeyExternal"/> (the seed is
    /// kept outside the database: it never can), or the code of the last refusal (<see cref="RekeyDevices"/>, <see cref="RekeyBusy"/>,
    /// <see cref="RekeyEvents"/>). Absent when there is nothing to say.
    /// </summary>
    public const string IdentityRekey = "identity_rekey";
    public const string RekeyExternal = "external";
    public const string RekeyDevices = "devices";
    public const string RekeyBusy = "busy";
    public const string RekeyEvents = "events";
}

/// <summary>Contributes fields to the node's own <c>node_state</c> control event.</summary>
public interface INodeStateContributor
{
    int Order => 0;
    Task ContributeAsync(NodeStateDraft draft, CancellationToken ct = default);
}

/// <summary>Materialised row from <c>tbl_node_state</c>.</summary>
public sealed record NodeStateRecord(
    Guid NodeId,
    string AppVersion,
    string? Platform,
    string RuntimeKind,
    string IdentityKeyFp,
    string? DekFp,
    IReadOnlyList<string> AttentionFlags,
    IReadOnlyDictionary<string, string>? Extra,
    long LamportTs,
    string? EventId,
    DateTime UpdatedAt);

/// <summary>Base contributor supplying version, platform, runtime kind, and identity key fingerprint.</summary>
public sealed class DefaultNodeStateContributor(INodeIdentityRepository nodeRepo) : INodeStateContributor
{
    public int Order => -100;

    public async Task ContributeAsync(NodeStateDraft draft, CancellationToken ct = default)
    {
        var identity = await nodeRepo.GetAsync();
        if (identity is null) return;
        draft.AppVersion = Metadata.BuildVersion.Current;
        draft.Platform = Metadata.NodePlatform.Current;
        draft.RuntimeKind = NodeRuntimeKinds.DetectCurrent(identity.NodeId);
        draft.IdentityKeyFp = NodeStateDraft.IdentityFingerprint(identity.Ed25519PublicKey);
    }
}

/// <summary>Reads materialised <c>tbl_node_state</c> rows written by <see cref="EventApplier"/>.</summary>
public sealed class NodeStateStore(IDbConnectionFactory db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<NodeStateRecord?> GetAsync(Guid nodeId)
    {
        using var conn = db.CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync<Row>(
            @"SELECT node_id AS NodeId, app_version AS AppVersion, platform AS Platform, runtime_kind AS RuntimeKind,
                     identity_key_fp AS IdentityKeyFp, dek_fp AS DekFp, attention_flags_json AS AttentionFlagsJson,
                     extra_json AS ExtraJson, lamport_ts AS LamportTs, event_id AS EventId, updated_at AS UpdatedAt
              FROM tbl_node_state WHERE node_id = @Id COLLATE NOCASE",
            new { Id = nodeId.ToString("D") });
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<NodeStateRecord>> GetAllAsync()
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<Row>(
            @"SELECT node_id AS NodeId, app_version AS AppVersion, platform AS Platform, runtime_kind AS RuntimeKind,
                     identity_key_fp AS IdentityKeyFp, dek_fp AS DekFp, attention_flags_json AS AttentionFlagsJson,
                     extra_json AS ExtraJson, lamport_ts AS LamportTs, event_id AS EventId, updated_at AS UpdatedAt
              FROM tbl_node_state");
        return rows.Select(Map).Where(r => r is not null).ToList()!;
    }

    private static NodeStateRecord? Map(Row r)
    {
        if (!Guid.TryParseExact(r.NodeId, "D", out var id)) return null;
        IReadOnlyList<string> flags;
        try
        {
            flags = string.IsNullOrWhiteSpace(r.AttentionFlagsJson)
                ? []
                : JsonSerializer.Deserialize<List<string>>(r.AttentionFlagsJson, Json) ?? [];
        }
        catch (JsonException)
        {
            flags = [];
        }

        IReadOnlyDictionary<string, string>? extra = null;
        if (!string.IsNullOrWhiteSpace(r.ExtraJson))
        {
            try { extra = JsonSerializer.Deserialize<Dictionary<string, string>>(r.ExtraJson, Json); }
            catch (JsonException) { extra = null; }
        }

        var updatedAt = DateTime.TryParse(r.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToUniversalTime()
            : DateTime.UnixEpoch;

        return new NodeStateRecord(
            id,
            r.AppVersion ?? "",
            string.IsNullOrWhiteSpace(r.Platform) ? null : r.Platform,
            r.RuntimeKind ?? "",
            r.IdentityKeyFp ?? "",
            string.IsNullOrWhiteSpace(r.DekFp) ? null : r.DekFp,
            flags,
            extra,
            r.LamportTs,
            r.EventId,
            updatedAt);
    }

    private sealed class Row
    {
        public string NodeId { get; set; } = "";
        public string? AppVersion { get; set; }
        public string? Platform { get; set; }
        public string? RuntimeKind { get; set; }
        public string? IdentityKeyFp { get; set; }
        public string? DekFp { get; set; }
        public string? AttentionFlagsJson { get; set; }
        public string? ExtraJson { get; set; }
        public long LamportTs { get; set; }
        public string? EventId { get; set; }
        public string? UpdatedAt { get; set; }
    }
}
