namespace BeeMemoryBank.Crypto;

public static class CryptoConstants
{
    public const int KeySize = 32;       // 256-bit AES key
    public const int IvSize = 12;        // 96-bit GCM nonce (recommended)
    public const int TagSize = 16;       // 128-bit GCM auth tag
    public const int SaltSize = 32;      // Argon2id salt

    public const int Ed25519PublicKeySize = 32;
    public const int Ed25519PrivateKeySize = 32; // NSec stores seed (32 bytes)

    // Default Argon2id parameters.
    // NOTE: these values are part of the password-hash format stored in tbl_user.password_hash:
    // the legacy `$argon2id$<salt>$<hash>` PHC variant does NOT encode m/t/p — verification
    // uses whatever DeriveKek currently defaults to. Changing these values WILL invalidate
    // every existing user's password until tbl_user.password_hash is re-hashed.
    // 64 MiB / t=3 / p=4 are the values used to bootstrap existing prod deployments.
    // Bumping requires (a) embedding params into the PHC string, or (b) a lazy re-hash
    // pass on next successful login. Tracked as audit finding claude-A2 #1 / claude-A2 #7.
    public const int DefaultArgonMemory = 65536;       // 64 MB
    public const int DefaultArgonIterations = 3;
    public const int DefaultArgonParallelism = 4;
}
