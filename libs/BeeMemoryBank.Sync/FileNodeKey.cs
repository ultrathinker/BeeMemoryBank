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
    /// an existing file: a node that silently got a new key would no longer match its whitelist
    /// rows anywhere in the mesh.
    /// </summary>
    public byte[] Create()
    {
        var publicKey = WriteNewSeed(replaceExisting: false);
        return publicKey;
    }

    /// <summary>
    /// What a start asks for: the public key of the seed in the file, or a freshly written one when
    /// the file holds no seed at all — missing, empty, or truncated by a start that died mid-write
    /// on a build that wrote straight to this path. <paramref name="created"/> says which happened,
    /// so a caller can log it honestly.
    ///
    /// <para>A file that DOES hold a valid seed is never replaced, whatever else is wrong with the
    /// node: a silently re-minted identity no longer matches the whitelist rows the whole mesh
    /// keeps for this node, and nothing an operator can do afterwards repairs that.</para>
    /// </summary>
    public byte[] LoadOrCreate(out bool created)
    {
        if (TryReadSeed(out var seed))
        {
            try
            {
                created = false;
                return NodeIdentityCrypto.PublicKeyOf(seed);
            }
            finally
            {
                Array.Clear(seed);
            }
        }
        created = true;
        return WriteNewSeed(replaceExisting: true);
    }

    /// <summary>
    /// The seed, or false when the file is not there or does not hold a <see cref="SeedLength"/>-byte
    /// seed — the shapes that mean "no identity here yet". Anything else that goes wrong (a file
    /// others can read, a read error, a damaged volume) throws: none of those may be answered by
    /// quietly minting a new identity over a key that may still be a perfectly good one.
    /// </summary>
    public bool TryReadSeed(out byte[] seed)
    {
        seed = [];
        if (!File.Exists(Path)) return false;
        RefuseIfOthersCanRead();
        var bytes = File.ReadAllBytes(Path);
        if (bytes.Length != SeedLength)
        {
            Array.Clear(bytes);
            return false;
        }
        seed = bytes;
        return true;
    }

    /// <summary>
    /// Writes a new seed so that the final name never exists half-written: the bytes go to a temp
    /// file beside it, are flushed to the disk, are read back and checked, and only then is the
    /// temp renamed onto the final name — one atomic step within the directory.
    ///
    /// <para>Before this, the seed was written straight to the final path: a crash, a full volume or
    /// a power cut in that window left a file that <see cref="Exists"/> reports and
    /// <see cref="ReadSeed"/> refuses, and the node never started again — its identity row (v=2)
    /// points at a key that is not there. The rename is what makes the file appear only complete.</para>
    /// </summary>
    private byte[] WriteNewSeed(bool replaceExisting)
    {
        var (publicKey, seed) = Ed25519Signer.GenerateKeyPair();
        var temp = Path + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            // Set at creation, not chmod-ed afterwards: there is no moment the seed is on disk
            // with the umask's default permissions.
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temp, options))
            {
                file.Write(seed);
                // On the platter before the rename, deliberately: a rename that survives a power
                // cut while the bytes are still in the page cache is the empty final file this
                // whole method exists to make impossible.
                file.Flush(flushToDisk: true);
            }

            // Read back before it can become the node's identity: a short write (a full volume)
            // is caught here, while the temp file is still the only thing to lose.
            var written = File.ReadAllBytes(temp);
            try
            {
                if (!written.AsSpan().SequenceEqual(seed))
                    throw new IOException(
                        $"Node identity key {temp} does not hold what was written — the node was not given a key.");
            }
            finally
            {
                Array.Clear(written);
            }

            if (replaceExisting && TryReadSeed(out _))
                throw new IOException(
                    $"Node identity key {Path} appeared while a new one was being written; keeping the one on disk.");

            File.Move(temp, Path, overwrite: replaceExisting);
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
        if (TryReadSeed(out var seed)) return seed;
        throw new InvalidDataException(File.Exists(Path)
            ? $"Node identity key {Path} does not hold a {SeedLength}-byte Ed25519 seed (it is empty or truncated)."
            : $"Node identity key {Path} is missing.");
    }

    /// <summary>
    /// Same stance as ssh with a private key: a key others can read is refused rather than used, so
    /// a mistake in the volume's permissions surfaces instead of quietly leaking the identity.
    /// </summary>
    private void RefuseIfOthersCanRead()
    {
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(Path) & NotOwnerOnly) != 0)
            throw new InvalidOperationException(
                $"Node identity key {Path} is accessible to other users; restrict it to the owner (chmod 600).");
    }
}
