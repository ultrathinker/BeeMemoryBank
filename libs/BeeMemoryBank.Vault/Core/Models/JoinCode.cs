using System.Diagnostics.CodeAnalysis;

namespace BeeMemoryBank.Core.Models;

/// <summary>
/// The code a listening Windows node shows on its Connect page (as text and as a QR) so a phone can
/// join it without a hub (plan section 10): where to call, the one-time token that opens the join for
/// this session, and the SPKI pin of the node's TLS key. The phone refuses to send the master password
/// anywhere the pin does not match.
///
/// <para>Text form: <c>bmb-join:?a=&lt;https address&gt;&amp;t=&lt;token&gt;&amp;s=&lt;pin&gt;</c>, values
/// URI-escaped. The token is absent for a node whose LAN listener is permanently on ("Devices on my network" is switched on, or it
/// was started with <c>BMB_HTTPS_ENABLED=1</c>) and does not gate joins by token. Who shows it: the Windows app, the Mac app and the
/// Windows service (Admin &gt; Connect a device); who reads it: the phone, the Setup page of another computer, and <c>bmb join --code</c>.</para>
/// </summary>
public sealed record JoinCode(string Address, string? Token, string SpkiPin)
{
    private const string Prefix = "bmb-join:?";

    /// <summary>Request header that carries <see cref="Token"/> on the phone's <c>POST /api/join</c>.</summary>
    public const string TokenHeader = "X-BMB-Join-Token";

    /// <summary>Bytes of a join token (base64url in the code); the LAN listener mints exactly this many.</summary>
    public const int TokenSize = 16;

    /// <summary>What every screen that takes a code says when it cannot be read (Setup, <c>bmb join</c>).</summary>
    public const string NotValidMessage =
        "That join code is not valid. Copy all of it again from Connect a device on the other computer; it starts with bmb-join:";

    /// <summary>The other computer's key is not the one the code pins, so nothing was sent to it.</summary>
    public const string WrongComputerMessage =
        "The computer at this address is not the one the join code belongs to (its key does not match), so the password was not sent. " +
        "Open Connect a device on the other computer again and use the new code.";

    /// <summary>Nothing answered at the code's address: its Connect a device window closed (it stays open 15 minutes) or it is not reachable.</summary>
    public const string NotAnsweringMessage =
        "The other computer did not answer. Connect a device stays open for 15 minutes: open it there again and use the new code, " +
        "and check that both computers are on the same network.";

    /// <summary>
    /// The sentence for a failed connection made with a code. The code pins the key, so a handshake that fails means the
    /// server is not the one that produced the code; anything else is "nobody home". The exception text is not passed on.
    /// </summary>
    public static string DescribeConnectionFailure(HttpRequestException ex) =>
        ex.HttpRequestError == HttpRequestError.SecureConnectionError ? WrongComputerMessage : NotAnsweringMessage;

    /// <summary>The other computer's join door refused the code: it is not valid, or it has been used (the door answers 403 for both).</summary>
    public const string DoorRefusedMessage =
        "The other computer did not accept this join code: it is not valid, or it has already been used. Open Connect a device on that computer again and use the new code.";

    /// <summary>The other computer's join door is busy with another device that joins with the same code (the door answers 409).</summary>
    public const string DoorBusyMessage = "Another device is joining with this code right now. Try again in a moment.";

    /// <summary>
    /// The sentence for a refusal of the other computer's join door, from the STATUS of its answer and nothing else: what the body says is that computer's own text
    /// and is never shown (not capped, not stripped - not shown). Null for a status the door does not use, which is told as the status alone.
    /// </summary>
    public static string? DescribeDoorAnswer(int status) => status switch
    {
        403 => DoorRefusedMessage,
        409 => DoorBusyMessage,
        _ => null,
    };

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
