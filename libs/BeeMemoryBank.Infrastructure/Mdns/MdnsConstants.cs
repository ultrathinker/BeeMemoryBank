namespace BeeMemoryBank.Infrastructure.Mdns;

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

    // ── TXT record keys ───────────────────────────────────────────────────────
    public const string TxtNodeId = "nodeId";
    public const string TxtVersion = "ver";
    public const string TxtName = "name";

    /// <summary>
    /// Prefix of a TXT <c>name</c> value that carries a percent-encoded UTF-8 name. DNS TXT strings
    /// are written as ASCII by the mDNS library, so a non-ASCII display name is sent as
    /// <c>u8:</c> + <see cref="Uri.EscapeDataString(string)"/>; ASCII names are sent unchanged.
    /// </summary>
    public const string TxtUtf8Prefix = "u8:";

    /// <summary>Encodes a display name for the TXT <c>name</c> key (see <see cref="TxtUtf8Prefix"/>).</summary>
    public static string EncodeTxtName(string name) =>
        name.All(c => c < 128) ? name : TxtUtf8Prefix + Uri.EscapeDataString(name);

    /// <summary>Reverses <see cref="EncodeTxtName"/>; a malformed encoding is returned as is.</summary>
    public static string DecodeTxtName(string value)
    {
        if (!value.StartsWith(TxtUtf8Prefix, StringComparison.Ordinal)) return value;
        try { return Uri.UnescapeDataString(value[TxtUtf8Prefix.Length..]); }
        catch (UriFormatException) { return value; }
    }
    public const string TxtHttps = "https";
}
