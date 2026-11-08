namespace BeeMemoryBank.Mdns;

/// <summary>
/// A peer BeeMemoryBank node discovered on the local network via mDNS, with its
/// TXT-record metadata and a resolved <c>host:port</c> suitable for the join wizard.
/// </summary>
/// <param name="NodeId">The peer's node GUID (parsed from the <c>nodeId</c> TXT key). <see cref="Guid.Empty"/> if absent/unparseable.</param>
/// <param name="Name">Human-readable node name (<c>name</c> TXT key).</param>
/// <param name="Version">Software version (<c>ver</c> TXT key).</param>
/// <param name="Https">Whether the peer exposes HTTPS (<c>https</c> TXT key).</param>
/// <param name="Host">Resolved host — an IP address when A/AAAA records were present, else the mDNS target hostname.</param>
/// <param name="Port">TCP port from the SRV record.</param>
/// <param name="Surface">The TXT <c>surface</c> key: "sync", "web" or "sync,web"; null in a record of a build before LAN sync (its whole front).</param>
public sealed record MdnsNodeRecord(
    Guid NodeId,
    string Name,
    string Version,
    bool Https,
    string Host,
    int Port,
    string? Surface = null)
{
    /// <summary>The resolvable base URL for this node (scheme chosen from <see cref="Https"/>; an IPv6 host in brackets).</summary>
    public string Url => $"{(Https ? "https" : "http")}://{(Host.Contains(':') ? $"[{Host}]" : Host)}:{Port}";

    /// <summary>True when the record says the node's sync routes answer at <see cref="Url"/> (a record of a build before LAN sync served its whole front there).</summary>
    public bool ServesSync => Surface is null || Surface.Split(',').Contains("sync") || Surface.Split(',').Contains("web");
}
