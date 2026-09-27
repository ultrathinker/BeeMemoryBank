using System.Security.Cryptography;

namespace BeeMemoryBank.Core.Models;

/// <summary>
/// The mark a blind node carries in its own NodeId (BMB-43, plan section 3.1). A blind node stores and
/// relays the mesh's ciphertext but never holds the master DEK, and every node must be able to tell
/// one apart from the id alone — a flag in a whitelist row can be lost by an older node or flipped by
/// an update event, an id cannot.
///
/// <para>Format: an RFC 9562 version-8 UUID whose canonical string starts with "b11d":
/// <c>b11dxxxx-xxxx-8xxx-{8,9,a,b}xxx-xxxxxxxxxxxx</c>. Random v4 and time-ordered v7 ids (what every
/// other node gets) never have version 8, so they can never be mistaken for a blind node.</para>
///
/// <para>Checks go through the canonical string, never <see cref="Guid.ToByteArray"/>: .NET stores
/// the first three fields little-endian, so the version nibble sits in byte 7 of that array, not
/// byte 6 as in RFC 9562 — the classic trap. The database keeps NodeIds uppercase; the check ignores
/// case.</para>
/// </summary>
public static class BlindNodeId
{
    private const string Prefix = "b11d";

    /// <summary>A fresh random id with the blind-node mark.</summary>
    public static Guid NewId()
    {
        Span<byte> random = stackalloc byte[16];
        RandomNumberGenerator.Fill(random);
        var hex = Convert.ToHexString(random).ToLowerInvariant().ToCharArray();

        // Positions in the 32 hex digits (no dashes): 0-3 prefix, 12 version, 16 variant.
        for (var i = 0; i < Prefix.Length; i++) hex[i] = Prefix[i];
        hex[12] = '8';
        hex[16] = "89ab"[Convert.ToInt32(hex[16].ToString(), 16) & 0x3];

        return Guid.ParseExact(new string(hex), "N");
    }

    /// <summary>True if <paramref name="nodeId"/> carries the blind-node mark.</summary>
    public static bool IsBlind(Guid nodeId)
    {
        var s = nodeId.ToString("D"); // lowercase, xxxxxxxx-xxxx-Vxxx-Nxxx-xxxxxxxxxxxx
        return s.StartsWith(Prefix, StringComparison.Ordinal)
            && s[14] == '8'
            && s[19] is '8' or '9' or 'a' or 'b';
    }

    /// <summary>String form (any case, braces not allowed) — for ids read from payloads and rows.</summary>
    public static bool IsBlind(string? nodeId) =>
        Guid.TryParseExact(nodeId?.Trim(), "D", out var g) && IsBlind(g);
}
