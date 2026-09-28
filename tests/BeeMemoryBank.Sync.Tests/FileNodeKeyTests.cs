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
