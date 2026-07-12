namespace BeeMemoryBank.Core.Services;

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
public sealed record MdnsNodeRecord(
    Guid NodeId,
    string Name,
    string Version,
    bool Https,
    string Host,
    int Port)
{
    /// <summary>The resolvable base URL for this node (scheme chosen from <see cref="Https"/>).</summary>
    public string Url => $"{(Https ? "https" : "http")}://{Host}:{Port}";
}
