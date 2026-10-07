using System.Text.Json;

namespace BeeMemoryBank.Infrastructure.Network;

/// <summary>
/// The per-profile setting "Devices on my network" (Admin &gt; Nodes). Off by default: a node that has never been
/// asked serves its front on this computer only (127.0.0.1:5310) and announces nothing. On: the node also serves the
/// whole front over HTTPS on <c>0.0.0.0:5311</c> (certificate from the node's own local CA) and announces itself on
/// the local network by mDNS, so another computer can find it and a phone can open it.
/// </summary>
public sealed record NodeNetworkSettings(bool DevicesOnMyNetwork = false);

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

/// <summary>The node's decision, made once at start and again whenever the setting is changed.</summary>
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
/// Loads and saves <see cref="NodeNetworkSettings"/> as one small JSON file in the profile's data folder. Every read goes
/// to disk: the node (which opens the listener), the Api (which announces) and the Web (which shows the card) are three
/// processes and none of them may act on a stale copy. Writes are atomic (temp file, then move), so a crash in the middle
/// keeps the previous value.
///
/// <para>It fails toward "off": a missing, unreadable or damaged file reads as the default, which exposes nothing. The
/// file is also in <c>VaultFiles</c>' list of files that are never copied with a profile, so a vault carried to another
/// computer starts closed there, whatever the old one chose.</para>
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

    /// <summary>The decision for now: the setting, overridden by <see cref="NetworkExposure.EnvironmentVariable"/>.</summary>
    public NetworkExposure Current() =>
        NetworkExposure.Resolve(Load(), Environment.GetEnvironmentVariable(NetworkExposure.EnvironmentVariable));
}
