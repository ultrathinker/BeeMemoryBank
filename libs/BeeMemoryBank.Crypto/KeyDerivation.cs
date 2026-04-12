using Konscious.Security.Cryptography;
using System.Text;

namespace BeeMemoryBank.Crypto;

/// <summary>
/// Key Encryption Key (KEK) derivation from password via Argon2id.
/// </summary>
public static class KeyDerivation
{
    /// <summary>
    /// Derives a KEK (Key Encryption Key) from password and salt.
    /// </summary>
    public static byte[] DeriveKek(
        string password,
        byte[] salt,
        int memory = CryptoConstants.DefaultArgonMemory,
        int iterations = CryptoConstants.DefaultArgonIterations,
        int parallelism = CryptoConstants.DefaultArgonParallelism)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);

        using var argon2 = new Argon2id(passwordBytes);
        argon2.Salt = salt;
        argon2.MemorySize = memory;
        argon2.Iterations = iterations;
        argon2.DegreeOfParallelism = parallelism;

        return argon2.GetBytes(CryptoConstants.KeySize);
    }

    public static byte[] GenerateSalt() => SecureRandom.GetBytes(CryptoConstants.SaltSize);
}
