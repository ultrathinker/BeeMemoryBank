namespace BeeMemoryBank.Core.Models;

/// <summary>
/// How a blind copy decides to trust the TLS endpoint of a node it is told to call (ADR 0007). Stored on the
/// node's whitelist row (<c>tls_trust</c>), replicated in <c>whitelist_add</c> / <c>whitelist_update</c>
/// (<c>tls_trust</c>) and carried by the call code.
/// </summary>
public static class BlindTrust
{
    /// <summary>The node's TLS key is pinned (SPKI SHA-256): a Docker blind node, or a full node with its own certificate.</summary>
    public const string Pin = "pin";

    /// <summary>The certificate must validate through the operating system's chain, name included; nothing is pinned.</summary>
    public const string PublicCa = "public-ca";

    /// <summary>A <c>whitelist_update</c> value only: take the mode (and the pin) away. Never stored.</summary>
    public const string None = "none";

    /// <summary>True for a mode this build can act on.</summary>
    public static bool IsKnown(string? trust) => trust is Pin or PublicCa;

    /// <summary>
    /// The mode a row stands in: what it says, or <see cref="Pin"/> for a pin with no mode (a row written by an
    /// older build, or a snapshot from one), or null. A value this build does not know is returned as it is, so
    /// that nothing downstream mistakes it for a mode it understands.
    /// </summary>
    public static string? Effective(string? trust, string? tlsSpki) =>
        !string.IsNullOrEmpty(trust) ? trust : string.IsNullOrEmpty(tlsSpki) ? null : Pin;

    /// <summary>
    /// The pin a row (or a manifest peer, or a restore record) holds, as far as it counts: only a row in <see cref="Pin"/> mode has one.
    /// The mode is the authority, so a pin next to <c>public-ca</c>, <c>none</c> or a mode this build does not know is a leftover and
    /// reads as no pin — it can neither be required of a certificate nor accepted in its place.
    /// </summary>
    public static string? PinOf(string? trust, string? tlsSpki) =>
        !string.IsNullOrEmpty(tlsSpki) && Effective(trust, tlsSpki) == Pin ? tlsSpki : null;
}
