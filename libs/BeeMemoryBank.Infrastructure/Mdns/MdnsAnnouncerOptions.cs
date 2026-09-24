namespace BeeMemoryBank.Infrastructure.Mdns;

/// <summary>
/// Configuration for <see cref="MdnsAnnouncer"/>.
/// </summary>
/// <remarks>
/// The node's identity (<c>nodeId</c>/<c>name</c>) is read LIVE from
/// <c>INodeIdentityRepository</c> on every refresh cycle, so the announcement always reflects
/// the initialised node (and only starts once one exists). <see cref="Port"/> and
/// <see cref="Https"/> are deployment facts the host supplies; the HTTPS flag is not yet derived
/// automatically from the Tier-1 local CA, so it is a plain settable property with a safe default.
/// </remarks>
public sealed class MdnsAnnouncerOptions
{
    /// <summary>
    /// TCP port a peer would connect to (the node's reachable UI/API surface that the join
    /// flow targets). Default <c>5301</c> — the Web process's default port (see docker-compose).
    /// </summary>
    public int Port { get; set; } = 5301;

    /// <summary>
    /// Whether this node exposes HTTPS via the Tier-1 local CA. Default <c>false</c>; the host
    /// sets it explicitly (it is not detected from the local-CA configuration).
    /// </summary>
    public bool Https { get; set; } = false;

    /// <summary>
    /// How often to re-evaluate the announce/withdraw decision (identity + invisible mode).
    /// Polling is deliberate: <see cref="InvisibleModeService"/> has no change-notification
    /// plumbing. Default 60s.
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Optional explicit version string override. When null, the announcer reads
    /// <c>AssemblyInformationalVersion</c> (compiled from the repo-root VERSION file) at runtime.
    /// </summary>
    public string? Version { get; set; }
}
