namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Configuration for <see cref="MdnsAnnouncer"/>.
/// </summary>
/// <remarks>
/// The node's identity (<c>nodeId</c>/<c>name</c>) is read LIVE from
/// <c>INodeIdentityRepository</c> on every refresh cycle, so the announcement always reflects
/// the initialised node (and only starts once one exists). <see cref="Port"/> and
/// <see cref="Https"/> are deployment facts the host supplies: the HTTPS flag's real wiring
/// (Ярус-1 local CA) is a later task — see TASK_BRIEF §5 Этап 5. For now it is a settable
/// property with a sensible default, as the brief requires.
/// </remarks>
public sealed class MdnsAnnouncerOptions
{
    /// <summary>
    /// TCP port a peer would connect to (the node's reachable UI/API surface that the join
    /// flow targets). Default <c>5301</c> — the Web process's default port (see docker-compose).
    /// </summary>
    public int Port { get; set; } = 5301;

    /// <summary>
    /// Whether this node exposes HTTPS via the Ярус-1 local CA. Default <c>false</c>; flipped to
    /// <c>true</c> by a later task once local-CA HTTPS is wired in.
    /// </summary>
    public bool Https { get; set; } = false;

    /// <summary>
    /// How often to re-evaluate the announce/withdraw decision (identity + invisible mode).
    /// Polling is the agreed approach — see TASK_BRIEF: no change-notification plumbing is added
    /// to <see cref="InvisibleModeService"/>. Default 60s.
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Optional explicit version string override. When null, the announcer reads
    /// <c>AssemblyInformationalVersion</c> (compiled from the repo-root VERSION file) at runtime.
    /// </summary>
    public string? Version { get; set; }
}
