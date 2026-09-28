using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.Api.Services.Recovery;

/// <summary>
/// The TLS pin a restore trusts the blind node by: <see cref="Spki"/>'s form (base64url, no padding, of
/// SHA-256 over the SubjectPublicKeyInfo). It comes from what the user typed or scanned — the code the
/// blind node's own console shows — never from anything the endpoint says about itself. The pinning
/// itself is <see cref="SpkiPinRegistry"/>'s, through the request's own pin
/// (<see cref="SpkiPinRegistry.ExplicitPin"/>), as when a PC adds a blind node.
/// </summary>
public static class RestoreTlsPin
{
    /// <summary>A pin <see cref="Spki.Equal"/> can compare: 32 bytes of base64url.</summary>
    public static bool IsWellFormed(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin)) return false;
        // Base64Url's "Try" still throws on a character outside its alphabet.
        try { return Spki.Equal(pin.Trim(), pin.Trim()); }
        catch (FormatException) { return false; }
    }
}
