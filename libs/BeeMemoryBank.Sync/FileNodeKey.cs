using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Sync;

/// <summary>
/// The blind node's identity seed in a file in its data volume (plan 3.5): 32 raw bytes, owner
/// read/write only. The row in tbl_node_identity is v=2 and holds only the public key.
///
/// <para>
/// Honest about what this protects: whoever reads the file can authenticate as the blind node to
/// any listening full node and download the event log — the same metadata and ciphertext the blind
/// node already stores. It can never author an event (EventApplier refuses blind originators) or
/// receive the DEK (rotation skips blind recipients). A stolen key means revoking the node.
/// </para>
/// </summary>
public sealed class FileNodeKey(string path) : IExternalNodeKey
{
    public const string FileName = "node-identity.key";

    private const int SeedLength = 32;

    // Anything beyond owner read/write means another account on the box can read the key.
    private const UnixFileMode NotOwnerOnly =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public string Path { get; } = path;

    public bool Exists => File.Exists(Path);

    /// <summary>
    /// Generates a fresh key pair, writes the seed and returns the public key. Refuses to replace
    /// an existing file (<see cref="FileMode.CreateNew"/>): a node that silently got a new key would
    /// no longer match its whitelist rows anywhere in the mesh.
    /// </summary>
    public byte[] Create()
    {
        var (publicKey, seed) = Ed25519Signer.GenerateKeyPair();
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            // Set at creation, not chmod-ed afterwards: there is no moment the seed is on disk
            // with the umask's default permissions.
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var file = new FileStream(Path, options);
            file.Write(seed);
            file.Flush(flushToDisk: true);
        }
        finally
        {
            Array.Clear(seed);
        }
        return publicKey;
    }

    /// <summary>The public half of the seed in the file.</summary>
    public byte[] ReadPublicKey()
    {
        var seed = ReadSeed();
        try
        {
            return NodeIdentityCrypto.PublicKeyOf(seed);
        }
        finally
        {
            Array.Clear(seed);
        }
    }

    /// <summary>
    /// True if the file's seed is the private half of <paramref name="publicKey"/>. Checked at
    /// startup: a key file from another volume would otherwise surface only as every peer
    /// refusing to authenticate us.
    /// </summary>
    public bool Matches(byte[] publicKey)
    {
        var probe = "bmb-node-key-probe"u8.ToArray();
        var seed = ReadSeed();
        try
        {
            return Ed25519Signer.Verify(publicKey, probe, Ed25519Signer.Sign(seed, probe));
        }
        finally
        {
            Array.Clear(seed);
        }
    }

    public byte[] ReadSeed()
    {
        // Same stance as ssh with a private key: a key others can read is refused rather than used,
        // so a mistake in the volume's permissions surfaces instead of quietly leaking the identity.
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(Path) & NotOwnerOnly) != 0)
            throw new InvalidOperationException(
                $"Node identity key {Path} is accessible to other users; restrict it to the owner (chmod 600).");

        var seed = File.ReadAllBytes(Path);
        if (seed.Length != SeedLength)
        {
            Array.Clear(seed);
            throw new InvalidDataException(
                $"Node identity key {Path} is {seed.Length} bytes; expected a {SeedLength}-byte Ed25519 seed.");
        }
        return seed;
    }
}
