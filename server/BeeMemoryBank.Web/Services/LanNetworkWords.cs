namespace BeeMemoryBank.Web.Services;

/// <summary>
/// The words for the networks of this computer (docs/design/lan-peer-sync.md 6 and 15.1): why a network is trusted or not, what a person can do about it, and the
/// line "On this network: ..." of the Devices page. One place, so the Devices page and Network settings say the same.
/// </summary>
public static class LanNetworkWords
{
    /// <summary>Why a network is or is not trusted, in a clause. <paramref name="reason"/> is the node's: windows-private, windows-domain, windows-public, trusted, refused, unknown, pending, vpn-off, virtual.</summary>
    public static string Why(string reason) => reason switch
    {
        "windows-private" => "Windows calls this network Private",
        "windows-domain" => "a domain network",
        "windows-public" => "Windows calls this network Public: direct sync is off here",
        "trusted" => "trusted",
        "refused" => "you said: not this network",
        "unknown" => "not asked yet",
        "pending" => "looking at this network",
        "vpn-off" => "a VPN: not used for sync unless you switch it on",
        "virtual" => "a virtual adapter: never used",
        _ => reason,
    };

    /// <summary>How to change the category of a Public network in Windows, for a network that is Public; null for any other.</summary>
    public static string? HowToChange(NodeLanClient.LanNetwork network) => network.Reason == "windows-public"
        ? "If it is your own network, set it to Private in Windows: Settings > Network & internet > Wi-Fi (or Ethernet) > this network > Network profile type > Private network. " +
          "This list updates by itself."
        : null;

    /// <summary>The networks the computer really uses: a virtual adapter is never offered.</summary>
    public static IEnumerable<NodeLanClient.LanNetwork> Used(NodeLanClient.LanSyncStatus lan) => lan.Networks.Where(n => n.Kind != "virtual");

    /// <summary>"Home Wi-Fi (trusted), Office (not asked yet)".</summary>
    public static string Networks(NodeLanClient.LanSyncStatus lan)
    {
        var used = Used(lan).ToList();
        return used.Count == 0
            ? "no network found"
            : string.Join(", ", used.Select(n => $"{n.Label} ({(n.Trusted ? "trusted" : n.Kind == "vpn" ? "VPN, not used for sync" : n.Reason == "unknown" ? "not asked yet" : "not trusted")})"));
    }

    /// <summary>Where this computer listens for the person's devices, in a sentence (no full stop).</summary>
    public static string Listening(NodeLanClient.LanSyncStatus lan) => lan.Setting switch
    {
        "environment" => "this node's front answers every network, sync included",
        "on" when lan.BindError is not null => $"could not open port {lan.Port}",
        "on" when lan.Serving.Count > 0 => "listening for your devices on " + string.Join(", ", lan.Serving.Select(a => a + ":" + lan.Port)),
        "on" => "not listening now: no trusted network, so your devices sync through your server or blind node",
        _ => "not listening for your devices: direct sync is off",
    };
}
