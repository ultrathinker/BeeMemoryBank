using System.Text.Json;

namespace BeeMemoryBank.Infrastructure.Network;

/// <summary>
/// The per-profile network settings of a desktop node (Admin &gt; Devices on my network).
/// <list type="bullet">
/// <item><see cref="DevicesOnMyNetwork"/>, off by default: the node also serves its WHOLE front (web UI, API, MCP) over HTTPS on the LAN port and
///   announces its name by mDNS, so another computer can find it and a phone can open it.</item>
/// <item><see cref="LanSync"/>, ON by default (owner decision D9, docs/design/lan-peer-sync.md): the node answers its already paired devices directly on
///   the LAN port, but only the node-key-authenticated sync routes (never the web UI), and only on networks the system or the person trusts.</item>
/// <item><see cref="LanPort"/>: the one TCP port every LAN surface shares (the sync surface, the join door of "Connect a device", the web surface).
///   Null means <see cref="LanPorts.Default"/> (5311). <see cref="LanPorts.EnvironmentVariable"/> overrides it for a run.</item>
/// <item><see cref="LanSyncNoticeSeen"/>: the one-time notice "Direct sync with your devices on this network is on" was dismissed.</item>
/// </list>
/// A file written by an older build has only <c>devicesOnMyNetwork</c>; the other members then take their defaults.
/// </summary>
public sealed record NodeNetworkSettings(
    bool DevicesOnMyNetwork = false,
    bool LanSync = true,
    int? LanPort = null,
    bool LanSyncNoticeSeen = false);

/// <summary>Where the answer to "is this node open to the network?" came from.</summary>
public enum NetworkExposureSource
{
    /// <summary>Nothing asked for it: the node answers this computer only.</summary>
    Off,

    /// <summary>The Admin setting is on.</summary>
    Setting,

    /// <summary><c>BMB_HTTPS_ENABLED=1</c> is set for this run: the older, hidden way, kept as an override.</summary>
    Environment,
}

/// <summary>The node's decision about the WEB surface, made once at start and again whenever the setting is changed.</summary>
public readonly record struct NetworkExposure(bool Enabled, NetworkExposureSource Source)
{
    /// <summary>The variable that predates the setting. It still switches the listener on, and the setting cannot switch it off.</summary>
    public const string EnvironmentVariable = "BMB_HTTPS_ENABLED";

    public static NetworkExposure Resolve(NodeNetworkSettings settings, string? environmentValue)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (environmentValue == "1") return new(true, NetworkExposureSource.Environment);
        return settings.DevicesOnMyNetwork ? new(true, NetworkExposureSource.Setting) : new(false, NetworkExposureSource.Off);
    }
}

/// <summary>
/// The LAN port of a desktop node: one TCP port for the sync surface, the join door and the web surface (decision D6, made configurable by the owner).
/// What is stored is the setting; what a run uses is <see cref="Resolve"/>: <c>BMB_LAN_PORT</c> when it is a valid port, else the setting, else 5311.
/// <para>A changed port does not move the addresses peers already store (the replicated <c>api_address</c> and the join code's address name the old port):
/// an updated peer learns the new port from this node's own announcement (<c>node_endpoints</c>) and from its mDNS record; a 2.7.5 peer keeps dialling
/// the old port (connection refused, which on a LAN is answered at once) until it is updated or its address for this node is changed by hand.</para>
/// </summary>
public static class LanPorts
{
    public const int Default = 5311;

    /// <summary>Overrides the setting for one run: two profiles on one computer, a Windows service, a test.</summary>
    public const string EnvironmentVariable = "BMB_LAN_PORT";

    /// <summary>Ports a LAN surface may not take: the loopback front's own preferred port, and the standalone/Docker ports of the Api and Web.</summary>
    private static readonly int[] Reserved = [5300, 5301, 5310];

    /// <summary>True for a port a person may choose: 1024..65535 and not one the node itself uses on loopback.</summary>
    public static bool IsAllowed(int port) => port is >= 1024 and <= 65535 && !Reserved.Contains(port);

    /// <summary>The refusal of a port that <see cref="IsAllowed"/> rejects, in a sentence a person can act on.</summary>
    public const string Refusal = "Choose a port between 1024 and 65535 that is not 5300, 5301 or 5310 (this node uses those itself).";

    /// <summary>The port of this run: <paramref name="environmentValue"/> when it is an allowed port, else the setting when it is, else <see cref="Default"/>.</summary>
    public static int Resolve(NodeNetworkSettings settings, string? environmentValue)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (int.TryParse(environmentValue, out var fromEnvironment) && IsAllowed(fromEnvironment)) return fromEnvironment;
        if (settings.LanPort is { } fromSetting && IsAllowed(fromSetting)) return fromSetting;
        return Default;
    }

    /// <summary>True when <c>BMB_LAN_PORT</c> decides the port for this run (the setting cannot change it then).</summary>
    public static bool IsFromEnvironment(string? environmentValue) =>
        int.TryParse(environmentValue, out var port) && IsAllowed(port);
}

/// <summary>
/// Loads and saves <see cref="NodeNetworkSettings"/> as one small JSON file in the profile's data folder. Every read goes
/// to disk: the node (which opens the listener), the Api (which announces) and the Web (which shows the card) are three
/// processes and none of them may act on a stale copy. Writes are atomic (temp file, then move), so a crash in the middle
/// keeps the previous value.
///
/// <para>It fails toward "closed": a missing, unreadable or damaged file reads as the defaults (the web surface off; LAN sync, which exposes only the
/// authenticated sync routes on trusted networks, on). The file is also in <c>VaultFiles</c>' list of files that are never copied with a profile, so a
/// vault carried to another computer starts closed there, whatever the old one chose.</para>
/// </summary>
public class NodeNetworkSettingsStore(string dataPath)
{
    /// <summary>The file in the data folder. Keep in step with <c>VaultFiles</c> in AppPaths (a test compares them).</summary>
    public const string FileName = "network.settings.json";

    private static readonly object WriteGate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private string FilePath => Path.Combine(dataPath, FileName);

    public NodeNetworkSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new NodeNetworkSettings();
            return JsonSerializer.Deserialize<NodeNetworkSettings>(File.ReadAllText(FilePath), Json) ?? new NodeNetworkSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new NodeNetworkSettings();
        }
    }

    public virtual void Save(NodeNetworkSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (WriteGate)
        {
            Directory.CreateDirectory(dataPath);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
    }

    /// <summary>Reads, changes and saves in one step (one process at a time; the three processes write different members).</summary>
    public virtual NodeNetworkSettings Update(Func<NodeNetworkSettings, NodeNetworkSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (WriteGate)
        {
            var next = change(Load());
            Save(next);
            return next;
        }
    }

    /// <summary>The decision for now about the web surface: the setting, overridden by <see cref="NetworkExposure.EnvironmentVariable"/>.</summary>
    public NetworkExposure Current() =>
        NetworkExposure.Resolve(Load(), Environment.GetEnvironmentVariable(NetworkExposure.EnvironmentVariable));

    /// <summary>The LAN port of this run (<see cref="LanPorts.Resolve"/>).</summary>
    public int CurrentPort() => LanPorts.Resolve(Load(), Environment.GetEnvironmentVariable(LanPorts.EnvironmentVariable));
}
