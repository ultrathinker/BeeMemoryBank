using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The blind node's identity seed in a file (plan 3.5): owner-only from the moment it exists, never
/// silently replaced, and refused when others can read it.
/// </summary>
public sealed class FileNodeKeyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_nodekey_" + Guid.NewGuid().ToString("N"));

    public FileNodeKeyTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private FileNodeKey NewKey() => new(Path.Combine(_dir, FileNodeKey.FileName));

    [Fact]
    public void Create_WritesAnOwnerOnlySeed_ThatMatchesTheReturnedPublicKey()
    {
        var key = NewKey();
        var pub = key.Create();

        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(key.Path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        key.ReadSeed().Should().HaveCount(32);
        key.Matches(pub).Should().BeTrue();
        key.Matches(Ed25519Signer.GenerateKeyPair().publicKey).Should().BeFalse();
    }

    [Fact]
    public void Create_NeverReplacesAnExistingKey()
    {
        var key = NewKey();
        key.Create();
        var before = File.ReadAllBytes(key.Path);

        var act = () => key.Create();

        act.Should().Throw<IOException>();
        File.ReadAllBytes(key.Path).Should().Equal(before);
    }

    /// <summary>
    /// The write is atomic: the seed goes to a temp file beside the final name, is flushed, read
    /// back and checked, and is only then renamed onto it. Written straight to the final path, a
    /// crash or a full volume in that window left a file that <c>Exists</c> reports and
    /// <c>ReadSeed</c> refuses — and the node never started again, because its v=2 identity row
    /// points at a key file with nothing in it.
    /// </summary>
    [Fact]
    public void AFileThatHoldsNoSeed_IsRegenerated_AndNoTempIsLeftBehind()
    {
        var key = NewKey();
        // What a torn write left — and with the permissions that write would have left it with
        // (0600). Created world-readable, the node refuses the file instead of regenerating over a
        // key another account may be able to read, which is its own test.
        File.WriteAllBytes(key.Path, []);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(key.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var pub = key.LoadOrCreate(out var created);

        created.Should().BeTrue();
        key.ReadSeed().Should().HaveCount(32);
        key.Matches(pub).Should().BeTrue();
        File.Exists(key.Path + ".tmp").Should().BeFalse(
            "the temp file is renamed onto the final name — a leftover would be the half-written key moved aside");
    }

    [Fact]
    public void LoadOrCreate_KeepsAValidSeed_WhateverElseIsWrong()
    {
        var key = NewKey();
        var first = key.LoadOrCreate(out var created);
        created.Should().BeTrue();
        var onDisk = File.ReadAllBytes(key.Path);

        var again = key.LoadOrCreate(out var createdAgain);

        createdAgain.Should().BeFalse();
        again.Should().Equal(first, "a node that already has a key must never be handed another one");
        File.ReadAllBytes(key.Path).Should().Equal(onDisk);
    }

    [Fact]
    public void Create_LeavesTheSeedOnlyUnderTheFinalName()
    {
        var key = NewKey();
        var pub = key.Create();

        // Only the final name: the temp file of the atomic write is renamed, never left beside it.
        Directory.GetFiles(_dir).Should().Equal(key.Path);
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
        key.Matches(pub).Should().BeTrue();
    }

    [Fact]
    public void ReadSeed_RefusesAKeyOthersCanRead()
    {
        if (OperatingSystem.IsWindows()) return; // POSIX permissions only
        var key = NewKey();
        key.Create();
        File.SetUnixFileMode(key.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        var act = () => key.ReadSeed();

        act.Should().Throw<InvalidOperationException>().WithMessage("*other users*");
    }

    [Fact]
    public void ReadSeed_RefusesAFileThatIsNotASeed()
    {
        var key = NewKey();
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var f = new FileStream(key.Path, options)) f.Write(new byte[31]);

        var act = () => key.ReadSeed();

        act.Should().Throw<InvalidDataException>();
    }

    /// <summary>The sync handshake signer takes the v=2 seed from the file, with the vault locked.</summary>
    [Fact]
    public void SessionNodeAuthSigner_SignsAV2IdentityWithTheFile_WhileLocked()
    {
        var key = NewKey();
        var pub = key.Create();
        using var db = DbConnectionFactory.CreateInMemory($"bmb_nodekey_{Guid.NewGuid():N}");
        var signer = new SessionNodeAuthSigner(new SessionService(new KeySlotRepository(db)), key);
        var identity = new NodeIdentity
        {
            NodeId = BlindNodeId.NewId(),
            Ed25519PublicKey = pub,
            Ed25519PrivateKey = [],
            Ed25519PrivateKeyV = NodeIdentityCrypto.ExternalKeyVersion
        };
        var challenge = "challenge"u8.ToArray();

        Ed25519Signer.Verify(pub, challenge, signer.SignChallenge(identity, challenge)).Should().BeTrue();
    }
}
