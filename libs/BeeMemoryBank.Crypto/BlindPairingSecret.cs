using System.Security.Cryptography;

namespace BeeMemoryBank.Crypto;

/// <summary>
/// The one-time secret an Android blind node shows in its pairing code (phone → Windows, plan
/// section 10). Windows authenticates its answer — the "where to call" code — with
/// <see cref="CallCodeMac"/>, so the phone never pins a server someone else typed in. The phone spends
/// the secret on the first answer it accepts; the backup key is a separate random key, so re-pairing
/// never changes what opens earlier backups.
/// </summary>
public static class BlindPairingSecret
{
    public const int Size = 32;

    private static readonly byte[] CallCodeKeyInfo = "bmb-android-call-code-v1"u8.ToArray();

    public static byte[] New() => RandomNumberGenerator.GetBytes(Size);

    /// <summary>HMAC-SHA256 of <paramref name="message"/> under the call-code key.</summary>
    public static byte[] CallCodeMac(byte[] secret, byte[] message)
    {
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Checked(secret), 32, info: CallCodeKeyInfo);
        try { return HMACSHA256.HashData(key, message); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Constant-time check of a call-code MAC.</summary>
    public static bool VerifyCallCodeMac(byte[] secret, byte[] message, byte[] mac)
    {
        var expected = CallCodeMac(secret, message);
        return mac.Length == expected.Length && CryptographicOperations.FixedTimeEquals(expected, mac);
    }

    private static byte[] Checked(byte[] secret) =>
        secret is { Length: Size } ? secret : throw new ArgumentException($"A pairing secret is {Size} bytes.", nameof(secret));
}
