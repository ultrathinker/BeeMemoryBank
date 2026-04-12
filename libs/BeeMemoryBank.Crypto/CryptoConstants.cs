namespace BeeMemoryBank.Crypto;

public static class CryptoConstants
{
    public const int KeySize = 32;       // 256-bit AES key
    public const int IvSize = 12;        // 96-bit GCM nonce (recommended)
    public const int TagSize = 16;       // 128-bit GCM auth tag
    public const int SaltSize = 32;      // Argon2id salt

    public const int Ed25519PublicKeySize = 32;
    public const int Ed25519PrivateKeySize = 32; // NSec stores seed (32 bytes)

    // Default Argon2id parameters (OWASP recommendations)
    public const int DefaultArgonMemory = 65536;      // 64 MB
    public const int DefaultArgonIterations = 3;
    public const int DefaultArgonParallelism = 4;
}
