using System.Diagnostics.CodeAnalysis;

namespace BeeMemoryBank.Core.Models;

/// <summary>
/// The code a listening Windows node shows on its Connect page (as text and as a QR) so a phone can
/// join it without a hub (plan section 10): where to call, the one-time token that opens the join for
/// this session, and the SPKI pin of the node's TLS key. The phone refuses to send the master password
/// anywhere the pin does not match.
///
/// <para>Text form: <c>bmb-join:?a=&lt;https address&gt;&amp;t=&lt;token&gt;&amp;s=&lt;pin&gt;</c>, values
/// URI-escaped. The token is absent for a node whose LAN listener is permanently on (it was started with
/// <c>BMB_HTTPS_ENABLED=1</c> and does not gate joins by token).</para>
/// </summary>
public sealed record JoinCode(string Address, string? Token, string SpkiPin)
{
    private const string Prefix = "bmb-join:?";

    /// <summary>Request header that carries <see cref="Token"/> on the phone's <c>POST /api/join</c>.</summary>
    public const string TokenHeader = "X-BMB-Join-Token";

    /// <summary>Bytes of a join token (base64url in the code); the LAN listener mints exactly this many.</summary>
    public const int TokenSize = 16;

    /// <summary>True if <paramref name="text"/> is meant as a join code, well-formed or not.</summary>
    public static bool LooksLikeJoinCode(string? text) =>
        text?.TrimStart().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) == true;

    public override string ToString() =>
        CodeText.Format(Prefix, [("a", Address), ("t", Token), ("s", SpkiPin)]);

    /// <summary>
    /// Parses a code the user pasted or scanned. Only an https address with a host and nothing else
    /// is accepted — the pin means nothing over plain HTTP, and a path would let a code point the
    /// join at some other endpoint. The pin must be a base64url SHA-256 (32 bytes) and the token, if
    /// any, a base64url <see cref="TokenSize"/>-byte value: a damaged code is refused here, on the
    /// screen where it was pasted, not later as a puzzling TLS failure.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out JoinCode? code)
    {
        code = null;
        var fields = CodeText.Parse(text, Prefix);
        if (fields == null) return false;

        if (CodeText.HttpsOrigin(fields.GetValueOrDefault("a")) is not { } address) return false;
        if (fields.GetValueOrDefault("s") is not { } pin || CodeText.Base64UrlOfLength(pin, 32) == null) return false;

        fields.TryGetValue("t", out var token);
        if (token != null && CodeText.Base64UrlOfLength(token, TokenSize) == null) return false;

        code = new JoinCode(address, token, pin);
        return true;
    }
}
