using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.Sync;

/// <summary>
/// How a peer is reached over TLS, as the join hands it from the host to the joiner: the host's whitelist row for each
/// other peer says <c>pin</c> (with the key's pin) or <c>public-ca</c>, and the joiner records the same, so that it checks
/// that peer exactly as the host does (<see cref="SpkiPinRegistry"/>). One rule for both sides and for all three joins
/// (<c>/api/join</c> and its callers: <c>bmb join</c>, the Setup page, the phone).
///
/// <para>Both fields are optional on the wire. A host of an older version sends neither; the joiner then records no mode
/// and no pin, as it always did. A joiner of an older version ignores them.</para>
/// </summary>
public static class JoinTls
{
    /// <summary>
    /// The mode and pin to send, or to record, for a row's <paramref name="trust"/> and <paramref name="tlsSpki"/>. Only what this
    /// build can act on survives: <c>pin</c> with a well-formed pin, or <c>public-ca</c>; a leftover pin next to another mode, an
    /// unknown mode and "nothing" all come out as (null, null). False for a <c>pin</c> mode whose pin is not usable (damaged or
    /// foreign): the caller must not take that peer in, since recording it without the pin would check it through the public
    /// CAs instead of the key the host requires.
    /// </summary>
    public static bool TryInherit(string? trust, string? tlsSpki, out string? inheritedTrust, out string? inheritedSpki)
    {
        inheritedTrust = null;
        inheritedSpki = null;
        switch (BlindTrust.Effective(trust, tlsSpki))
        {
            case BlindTrust.Pin:
                if (!Spki.IsWellFormed(tlsSpki)) return false;
                inheritedTrust = BlindTrust.Pin;
                inheritedSpki = tlsSpki;
                return true;
            case BlindTrust.PublicCa:
                inheritedTrust = BlindTrust.PublicCa;
                return true;
            default:
                return true;
        }
    }
}
