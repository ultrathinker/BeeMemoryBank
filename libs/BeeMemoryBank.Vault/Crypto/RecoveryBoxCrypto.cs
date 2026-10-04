using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace BeeMemoryBank.Crypto;

/// <summary>The stored part of a recovery box: what a blind node keeps and cannot open.</summary>
public sealed record RecoveryBoxSeal(string KdfPreset, byte[] Salt, byte[] Wrapped, byte[] Iv);

/// <summary>
/// A recovery box is the master DEK wrapped under a key derived from a master password with one of
/// the <see cref="RecoveryBoxKdf"/> presets (BMB-43, plan 6.2). The wrap is exactly the one a key slot
/// uses (<see cref="MasterKeyManager.WrapMasterDek"/>), so a device box can be a byte copy of the
/// device's slot and a restore opens both kinds the same way.
/// </summary>
public static class RecoveryBoxCrypto
{
    /// <summary>
    /// Seals <paramref name="dek"/> under <paramref name="password"/>. A heavy preset must be called
    /// from <see cref="HeavyDerivationQueue"/>.
    /// </summary>
    public static RecoveryBoxSeal Wrap(byte[] dek, string password, string preset)
    {
        ArgumentNullException.ThrowIfNull(dek);
        ArgumentNullException.ThrowIfNull(password);
        var p = RecoveryBoxKdf.Resolve(preset);

        var salt = KeyDerivation.GenerateSalt();
        var kek = DeriveKek(password, salt, p);
        try
        {
            var (wrapped, iv) = MasterKeyManager.WrapMasterDek(dek, kek);
            return new RecoveryBoxSeal(p.Name, salt, wrapped, iv);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>
    /// Opens a box. The preset and the material's shape are checked before any memory is committed;
    /// a wrong password throws <see cref="CryptographicException"/>. A heavy preset must be called
    /// from <see cref="HeavyDerivationQueue"/>.
    /// </summary>
    public static byte[] Unwrap(string password, string preset, byte[] salt, byte[] wrapped, byte[] iv)
    {
        ArgumentNullException.ThrowIfNull(password);
        var p = RecoveryBoxKdf.Resolve(preset);
        if (!RecoveryBoxKdf.IsWellFormed(p.IsHeavy ? RecoveryBoxKdf.KindStrong : RecoveryBoxKdf.KindDevice, salt, wrapped, iv))
            throw new CryptographicException("Malformed recovery box.");

        var kek = DeriveKek(password, salt, p);
        try
        {
            return MasterKeyManager.UnwrapMasterDek(wrapped, iv, kek);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary><see cref="Unwrap"/> that answers "does this password open it" instead of throwing.</summary>
    public static byte[]? TryUnwrap(string password, string preset, byte[] salt, byte[] wrapped, byte[] iv)
    {
        try { return Unwrap(password, preset, salt, wrapped, iv); }
        catch (CryptographicException) { return null; }
    }

    private static byte[] DeriveKek(string password, byte[] salt, RecoveryBoxPreset p)
    {
        // The light preset is an ordinary slot derivation and goes through the shared, memory-
        // budgeted gate like every other one.
        if (!p.IsHeavy)
            return KeyDerivation.DeriveKek(password, salt, p.MemoryKiB, p.Iterations, p.Parallelism);

        if (!HeavyDerivationQueue.IsOnWorker)
            throw new InvalidOperationException(
                $"Recovery box preset {p.Name} may only be derived on the heavy-derivation queue.");

        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            using var argon2 = new Argon2id(passwordBytes);
            argon2.Salt = salt;
            argon2.MemorySize = p.MemoryKiB;
            argon2.Iterations = p.Iterations;
            argon2.DegreeOfParallelism = p.Parallelism;
            return argon2.GetBytes(CryptoConstants.KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }
}
