using System.Net;
using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Sync;

/// <summary>
/// The one policy for the address of a peer (BMB-307 fix round): what may be typed, in what form it is stored and sent to the other nodes, and what a person is
/// told when it is refused. Every writer of the address - the node's "Change node address", the phone's "Edit URL" - goes through <see cref="Canonicalize"/>
/// (inside <see cref="PeerAddressService"/>), so the rules cannot differ between a screen and the API, or between the node and the phone.
/// <para>
/// A sync address is only where a device is: <c>http(s)://host[:port][/prefix]</c>. Refused: another scheme, a user name or password in it (it would be stored,
/// replicated to every node, logged and shown), a query or a fragment (a token rides there; and the address is extended with <c>/api/sync/...</c>), a host that
/// is empty, an unspecified address (0.0.0.0, ::), a link-local one (169.254.0.0/16, fe80::/10 - where the cloud metadata services live) and the names of
/// those services. NOT refused: loopback and private LAN addresses - this product's nodes live on LANs, and the tests of a setup use loopback.
/// </para>
/// <para>The refusals are fixed sentences: nothing of what was typed is echoed back (a pasted address may carry a secret).</para>
/// </summary>
public static class PeerAddressPolicy
{
    public const int MaxLength = 2048;

    /// <param name="Address">The canonical address, or null when it was refused.</param>
    /// <param name="Refusal">Why it was refused, in a sentence that does not quote the input; null when it was accepted.</param>
    public sealed record Result(string? Address, string? Refusal)
    {
        public bool Ok => Address is not null;
    }

    /// <summary>
    /// What an address that arrives from OUTSIDE - a join request, a pair code, an import, a replicated event, the answer of a host to a join - may become when it
    /// is stored: its canonical form, or null when the policy refuses it or none was given (the row then keeps what it had, or has none). One rule at every boundary
    /// that writes a peer's address, so no row holds a form or a target the node's own screens would have refused. Nothing is probed or sent here.
    /// </summary>
    public static string? ForStorage(string? incoming) => string.IsNullOrWhiteSpace(incoming) ? null : Canonicalize(incoming).Address;

    /// <summary>
    /// True when a connection to <paramref name="ip"/> is one this node may make to a peer: not an unspecified address, not link-local (where the cloud metadata
    /// services live), not those services' fixed addresses and not multicast or broadcast. Loopback and private LAN ranges are allowed. The same rule judges an
    /// address literal typed in an address (<see cref="Canonicalize"/>) and every address a peer's name resolves to when the socket is opened
    /// (<see cref="PeerSocketConnector"/>).
    /// </summary>
    public static bool IsAllowedAddress(IPAddress ip) => IpRefusal(ip) is null;

    private static string? IpRefusal(IPAddress ip) => PeerNetworkRules.Refusal(ip);

    /// <summary>
    /// Trims, adds <c>https://</c> to an address typed without a scheme (what every screen always did), and returns the one form that is stored: lower-case
    /// scheme and host, no default port, no trailing slash. The same place always gives the same text.
    /// </summary>
    public static Result Canonicalize(string? raw)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text)) return Refuse("Give the address of the device.");
        if (text.Length > MaxLength) return Refuse("The address is too long.");
        if (text.Any(char.IsControl) || text.Any(char.IsWhiteSpace)) return Refuse("The address must not contain spaces or control characters.");
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return Refuse("That is not an address (for example https://example.com or http://192.168.1.20:5300).");
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return Refuse("Only http:// and https:// addresses are used for sync.");
        if (!string.IsNullOrEmpty(uri.UserInfo) || AuthorityHasUserInfo(text))
            return Refuse("The address must not contain a user name or a password: it is only where the device is.");
        if (text.Contains('?') || text.Contains('#'))
            return Refuse("The address must not contain a ? or # part: it is only where the device is.");

        if (HostRefusal(uri) is { } why) return Refuse(why);

        var path = uri.AbsolutePath.TrimEnd('/');
        return new Result(uri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + path, null);
    }

    /// <summary>
    /// A short stand-in for an address in the audit trail and the logs: it tells two addresses apart without carrying the host, the port or the path. 12
    /// hexadecimal characters of the hash of <c>host:port</c>; "none" for no address.
    /// </summary>
    public static string Fingerprint(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || !Uri.TryCreate(address.Contains("://") ? address.Trim() : "https://" + address.Trim(), UriKind.Absolute, out var uri))
            return "none";
        var authority = $"{uri.Host.ToLowerInvariant()}:{uri.Port}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(authority)))[..12].ToLowerInvariant();
    }

    private static Result Refuse(string why) => new(null, why);

    /// <summary>A user name or password in the authority part, before the Uri class gets to see it (it would take "http://a@b" for a host b).</summary>
    private static bool AuthorityHasUserInfo(string text)
    {
        var start = text.IndexOf("://", StringComparison.Ordinal) + 3;
        var end = text.IndexOf('/', start);
        var authority = end < 0 ? text[start..] : text[start..end];
        return authority.Contains('@');
    }

    private static string? HostRefusal(Uri uri) => PeerNetworkRules.HostRefusal(uri.DnsSafeHost);
}
