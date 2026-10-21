namespace BeeMemoryBank.Core.Services.BlindPhone;

/// <summary>
/// The exact shapes of the <see cref="BlindPhoneLog.PairingKind"/> lines this code writes, from its own data (never from an exception). The kind of a stored line is not proof of where the line
/// came from, so a stamped line of that kind is shown only when it is, character for character, one of these: a fixed sentence, or a sentence with a node id (a GUID) and, for the paired line, the address
/// of the computer this copy calls, which must parse as such an address (<c>https://host[:port]</c>: no user, no path, no query, no fragment - the form a call code holds).
/// </summary>
internal static class BlindPairingLine
{
    private const string Created = "Blind identity created: ";
    private const string Recovered = "Recovered existing blind identity: ";
    private const string Paired = "Paired: calls ";
    private const string PairedNode = " (node ";

    /// <summary>The sentences that carry no data.</summary>
    private static readonly string[] Fixed =
    [
        "Re-pairing started: a new phone code was made.",
        "Cleaned up pairing secret committed in previous session.",
    ];

    public static bool IsOwn(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        if (Array.IndexOf(Fixed, message) >= 0) return true;
        if (message.StartsWith(Created, StringComparison.Ordinal)) return IsNodeId(message[Created.Length..]);
        if (message.StartsWith(Recovered, StringComparison.Ordinal)) return IsNodeId(message[Recovered.Length..]);
        if (!message.StartsWith(Paired, StringComparison.Ordinal) || !message.EndsWith(").", StringComparison.Ordinal)) return false;

        var body = message[Paired.Length..^2];
        var split = body.LastIndexOf(PairedNode, StringComparison.Ordinal);
        return split > 0 && IsNodeAddress(body[..split]) && IsNodeId(body[(split + PairedNode.Length)..]);
    }

    /// <summary>A node id as this code writes it: a GUID in the "D" form.</summary>
    private static bool IsNodeId(string text) => text.Length == 36 && Guid.TryParseExact(text, "D", out _);

    /// <summary>An address that is, as written, the origin of an https node: what a call code carries as the address of the node it calls.</summary>
    private static bool IsNodeAddress(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrEmpty(uri.Host)
        && uri.PathAndQuery == "/"
        && string.IsNullOrEmpty(uri.Fragment)
        && string.IsNullOrEmpty(uri.UserInfo)
        && text == uri.GetLeftPart(UriPartial.Authority);
}
