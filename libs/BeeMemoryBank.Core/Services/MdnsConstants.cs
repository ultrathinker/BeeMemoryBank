namespace BeeMemoryBank.Core.Services;

/// <summary>
/// mDNS / DNS-SD identifiers shared by <see cref="MdnsAnnouncer"/> and <see cref="MdnsBrowser"/>.
/// </summary>
public static class MdnsConstants
{
    /// <summary>
    /// DNS-SD service type (the pair of labels, WITHOUT the trailing <c>.local</c>).
    /// Both the announcer and the browser use this.
    /// </summary>
    public const string ServiceType = "_beememorybank._tcp";

    /// <summary>
    /// Fully-qualified service name on the local mDNS domain (service type + <c>.local</c>).
    /// This is the PTR name queried by browsers.
    /// </summary>
    public const string QualifiedServiceName = "_beememorybank._tcp.local";

    // ── TXT record keys (per TASK_BRIEF) ──────────────────────────────────────
    public const string TxtNodeId = "nodeId";
    public const string TxtVersion = "ver";
    public const string TxtName = "name";
    public const string TxtHttps = "https";
}
