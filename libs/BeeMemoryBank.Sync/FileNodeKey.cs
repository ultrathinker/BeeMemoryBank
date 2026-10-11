using System.Security.Cryptography;
using BeeMemoryBank.Core.IO;
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
    private const int ReplaceSharingViolationAttempts = 10;

    // Anything beyond owner read/write means another account on the box can read the key.
    private const UnixFileMode NotOwnerOnly =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    // How long a start waits for another start that is replacing the torn file. That one generates a key, writes,
    // flushes and renames: milliseconds, seconds on a slow disk. Past this the start fails and the next one retries.
    private static readonly TimeSpan DefaultReplaceLockWait = TimeSpan.FromSeconds(30);

    public string Path { get; } = path;

    /// <summary>The file held (never written) while a torn key file is replaced, beside the key: see <see cref="LoadOrCreate"/>.</summary>
    internal string ReplaceLockPath => Path + ".lock";

    /// <summary>Test seam: how long <see cref="LoadOrCreate"/> waits for the replace lock.</summary>
    internal TimeSpan ReplaceLockWait { get; init; } = DefaultReplaceLockWait;

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
    ///
    /// <para>Two starts that both find no file (review A2-d) do not both win: the new seed is
    /// renamed into place without replacing anything, in one atomic step on every platform
    /// (<see cref="OwnerOnlyFile.MoveNoReplace"/>), and the start whose rename finds the other's
    /// seed there takes that one.</para>
    ///
    /// <para>Only a file that exists but holds no seed is replaced, and that cannot be one atomic
    /// step: no system call replaces a file only if it is still the torn one, so two starts that
    /// both saw it torn both renamed their seed over it, and the first went on with an identity
    /// that was no longer in the file (BMB-190). Such a start therefore takes an exclusive lock
    /// on a file beside the key (<see cref="ReplaceLockPath"/>), looks at the key again, and only
    /// if it is still torn replaces it; a start that waited finds the winner's seed and takes it.
    /// The lock goes with the process, so a crash never leaves a key file that cannot be
    /// repaired.</para>
    /// </summary>
    public byte[] LoadOrCreate(out bool created)
    {
        if (TryReadSeed(out var seed))
        {
            created = false;
            return PublicKeyOfAndClear(seed);
        }
        created = true;
        if (File.Exists(Path))
        {
            using var replaceLock = HoldReplaceLock();
            // Looked at again with the lock held: the start that had it first has replaced the torn file by now.
            if (TryReadSeed(out var current))
            {
                created = false;
                return PublicKeyOfAndClear(current);
            }
            if (File.Exists(Path))
                return WriteNewSeed(replaceExisting: true);
            // Gone while this start waited (removed by hand): create it like a start that found no file.
        }
        try
        {
            return WriteNewSeed(replaceExisting: false);
        }
        catch (IOException)
        {
            // Another start renamed its seed into place first: that one is the node's identity.
            if (!TryReadWinner(out var winner)) throw;
            created = false;
            return PublicKeyOfAndClear(winner);
        }
    }

    /// <summary>
    /// Takes the exclusive hold on <see cref="ReplaceLockPath"/> and keeps it until disposed. The file is only ever
    /// held, never written, and stays beside the key afterwards: deleting a lock file while others wait for it would
    /// let a late start lock a new one next to the old. A second holder is refused with a sharing violation on
    /// Windows (write access with FileShare.Read, as <c>.instance.lock</c> does) and with an exclusive flock on
    /// Linux and macOS (.NET turns FileShare.None into one; flock belongs to the open file, so two starts in one
    /// process exclude each other too, which fcntl byte-range locks would not).
    /// </summary>
    /// <exception cref="IOException">The lock stayed held for <see cref="ReplaceLockWait"/>: the torn file was left as it is.</exception>
    internal FileStream HoldReplaceLock()
    {
        OwnerOnlyFile.RefuseLink(ReplaceLockPath);
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate, Access = FileAccess.Write, BufferSize = 1,
            Share = OperatingSystem.IsWindows() ? FileShare.Read : FileShare.None
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var started = Environment.TickCount64;
        while (true)
        {
            try
            {
                return new FileStream(ReplaceLockPath, options);
            }
            catch (IOException ex) when (ex is not DirectoryNotFoundException)
            {
                if (Environment.TickCount64 - started >= ReplaceLockWait.TotalMilliseconds)
                    throw new IOException(
                        $"Node identity key {Path} is held by another start that is replacing it ({ReplaceLockPath} " +
                        $"was locked for {(int)ReplaceLockWait.TotalSeconds} s); the key was left as it is. Start again.", ex);
            }
            Thread.Sleep(Random.Shared.Next(10, 40));
        }
    }

    /// <summary>
    /// The seed another start has just renamed into place. A brand-new file can be held for a moment
    /// by a scanner on Windows (a sharing violation on the read), so a few short retries first.
    /// </summary>
    private bool TryReadWinner(out byte[] seed)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return TryReadSeed(out seed);
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static byte[] PublicKeyOfAndClear(byte[] seed)
    {
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
    /// The seed, or false when the file is not there or does not hold a <see cref="SeedLength"/>-byte
    /// seed — the shapes that mean "no identity here yet". Anything else that goes wrong (a file
    /// others can read, a read error, a damaged volume) throws: none of those may be answered by
    /// quietly minting a new identity over a key that may still be a perfectly good one.
    /// </summary>
    public bool TryReadSeed(out byte[] seed)
    {
        seed = [];
        if (!File.Exists(Path)) return false;
        RefuseIfLink();
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
    ///
    /// <para>The temp name is random and created with <see cref="FileMode.CreateNew"/>, and the
    /// final path is refused when it is a link (review release-a2 sec#9). A fixed
    /// <c>name.tmp</c> beside the key is a path anything else on the box can pre-create — as a
    /// symlink to somewhere else, in which case <see cref="FileMode.Create"/> would follow it and
    /// write the seed through the link, outside the data volume and with whatever permissions the
    /// target has; and two starts at once would write the same temp file. CreateNew refuses a path
    /// that already exists — a symlink included: POSIX open(O_CREAT|O_EXCL) fails on one rather than
    /// following it — and an unpredictable name has nothing to pre-create.</para>
    /// </summary>
    private byte[] WriteNewSeed(bool replaceExisting)
    {
        RefuseIfLink();
        var (publicKey, seed) = Ed25519Signer.GenerateKeyPair();
        var temp = Path + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant() + ".tmp";
        var tempCreated = false;
        try
        {
            // Owner-only at creation, not chmod-ed afterwards: there is no moment the seed is on disk
            // with the umask's default permissions (0600), or with the folder's inherited ACL on
            // Windows (OwnerOnlyFile).
            using (var file = OwnerOnlyFile.CreateNew(temp))
            {
                tempCreated = true;
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

            // Not File.Move(overwrite: false): on Linux and macOS that checks and then renames, which replaces, so two
            // first starts both "won" and one kept an identity that was not in the file (BMB-188).
            if (replaceExisting) MoveReplacingTornKeyWithRetry(temp);
            else OwnerOnlyFile.MoveNoReplace(temp, Path);
        }
        catch when (tempCreated)
        {
            // Our own temp file, never renamed: a seed nobody will use.
            try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
        finally
        {
            Array.Clear(seed);
        }
        return publicKey;
    }

    /// <summary>
    /// Windows refuses to replace a file while another process has a read handle that did not share delete access.
    /// A competing start can leave that handle open briefly between finding the torn key and taking the replace lock.
    /// Keep the lock while retrying the transient denial, so no second start can choose a different replacement.
    /// </summary>
    private void MoveReplacingTornKeyWithRetry(string temp)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, Path, overwrite: true);
                return;
            }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows() && attempt < ReplaceSharingViolationAttempts)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException ex) when (OperatingSystem.IsWindows())
            {
                throw new IOException(
                    $"Node identity key {Path} could not be replaced because another process kept it open; " +
                    "the torn key was left as it is. Start again.", ex);
            }
        }
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
    /// A link is not this node's key file. Reading through one would adopt a seed from wherever it
    /// points — outside the data volume, on a filesystem with different permissions — and a rename
    /// onto one replaces the link while whatever it pointed at keeps the old contents, so the node
    /// would come back with the identity it thought it had replaced. Refused, not resolved: a key
    /// file is one file in the data volume, and a link there is either an attack or a mistake.
    /// </summary>
    private void RefuseIfLink()
    {
        if (new FileInfo(Path).LinkTarget is not null)
            throw new InvalidOperationException(
                $"Node identity key {Path} is a symlink or junction; refusing to use it. " +
                "The key belongs in the node's data volume as a regular file.");
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
