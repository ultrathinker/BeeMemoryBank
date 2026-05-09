using System.Security.Cryptography;

namespace BeeMemoryBank.Crypto;

public static class SecureRandom
{
    public static byte[] GetBytes(int count) => RandomNumberGenerator.GetBytes(count);
}
