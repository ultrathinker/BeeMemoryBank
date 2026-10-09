using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.TestSupport;

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

    /// <summary>
    /// Review release-a #7: on Windows the seed used to take over the data folder's inherited ACL, so in a folder
    /// outside the user's profile every account of the computer could read the node's identity.
    /// </summary>
    [WindowsAclFact]
    public void Create_InAFolderEveryAccountCanRead_TheSeedIsOwnerOnly()
    {
        if (!OperatingSystem.IsWindows()) return;
        WindowsAcl.MakeReadableByAllUsers(_dir);
        var key = NewKey();

        key.Create();

        WindowsAcl.UsersCanRead(key.Path).Should().BeFalse();
        WindowsAcl.OwnerHasFullControl(key.Path).Should().BeTrue();
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

    /// <summary>
    /// Review A2-d: two first starts on one volume at the same instant. Both used to see "no seed", both wrote one and
    /// renamed it over the other's, so one returned a public key that was no longer in the file - its identity row then
    /// failed "does not belong to node" at every later start. Now the second rename does not replace, and the loser takes
    /// the winner's key.
    /// </summary>
    [Fact]
    public void TwoFirstStartsAtOnce_BothGetTheKeyThatIsInTheFile()
    {
        const int rounds = 200;
        for (var round = 0; round < rounds; round++)
        {
            var key = new FileNodeKey(Path.Combine(_dir, $"race-{round}.key"));
            using var barrier = new Barrier(2);
            var results = new (byte[] PublicKey, bool Created)[2];

            var tasks = Enumerable.Range(0, 2).Select(i => Task.Factory.StartNew(() =>
            {
                barrier.SignalAndWait();
                var pub = new FileNodeKey(key.Path).LoadOrCreate(out var created);
                results[i] = (pub, created);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            Task.WaitAll(tasks, TimeSpan.FromSeconds(30)).Should().BeTrue();

            results[0].PublicKey.Should().Equal(results[1].PublicKey, $"round {round}: both starts must end up with one identity");
            key.Matches(results[0].PublicKey).Should().BeTrue($"round {round}: the key returned is the key in the file");
            results.Count(r => r.Created).Should().Be(1, $"round {round}: exactly one start created the key");
        }

        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty("the loser's temp file is not left behind");
    }

    /// <summary>
    /// BMB-188: the test above failed on Linux in CI. There <c>File.Move(overwrite: false)</c> checks that the target
    /// is missing and then renames, which replaces: two starts that both passed the check both renamed, the second over
    /// the first, and the first went on with an identity that was not in the file. More starts at once than two make
    /// that window easy to hit; every one of them must end up with the key in the file.
    /// </summary>
    [Fact]
    public void ManyFirstStartsAtOnce_AllGetTheKeyThatIsInTheFile()
    {
        const int rounds = 100, starts = 4;
        for (var round = 0; round < rounds; round++)
        {
            var key = new FileNodeKey(Path.Combine(_dir, $"crowd-{round}.key"));
            using var barrier = new Barrier(starts);
            var results = new (byte[] PublicKey, bool Created)[starts];

            var tasks = Enumerable.Range(0, starts).Select(i => Task.Factory.StartNew(() =>
            {
                barrier.SignalAndWait();
                var pub = new FileNodeKey(key.Path).LoadOrCreate(out var created);
                results[i] = (pub, created);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            Task.WaitAll(tasks, TimeSpan.FromSeconds(30)).Should().BeTrue();

            foreach (var result in results)
                key.Matches(result.PublicKey).Should().BeTrue($"round {round}: every start returns the key that is in the file");
            results.Count(r => r.Created).Should().Be(1, $"round {round}: exactly one start created the key");
        }

        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty("no loser's temp file is left behind");
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

    /// <summary>
    /// A key path that is a link is refused rather than followed: reading through one adopts a seed
    /// from outside the data volume, and a rename onto one replaces the link while its target keeps
    /// the old contents — the node would come back with the identity it thought it had replaced
    /// (review release-a2 sec#9).
    /// </summary>
    [Fact]
    public void AKeyPathThatIsALink_IsRefused_NotFollowed()
    {
        if (OperatingSystem.IsWindows()) return; // symlinks need privileges there; the code path is the same

        var elsewhere = Path.Combine(_dir, "elsewhere.key");
        var real = NewKey();
        real.Create();
        Directory.CreateDirectory(Path.Combine(_dir, "targets"));
        var victim = Path.Combine(_dir, "targets", "victim.key");
        File.WriteAllBytes(victim, File.ReadAllBytes(real.Path));
        File.Delete(real.Path);
        File.CreateSymbolicLink(real.Path, victim);
        File.Delete(elsewhere);

        var act = () => real.ReadSeed();

        act.Should().Throw<InvalidOperationException>().WithMessage("*symlink*");
        var replace = () => real.LoadOrCreate(out _);
        replace.Should().Throw<InvalidOperationException>("regenerating over a link would replace the link, not the key");
        File.ReadAllBytes(victim).Should().HaveCount(32, "nothing wrote through the link");
    }

    /// <summary>
    /// The temporary file is created with an unpredictable name and CreateNew, so a pre-created
    /// <c>.tmp</c> — a symlink to somewhere else, say — is neither followed nor overwritten. The
    /// old fixed name beside the key was exactly that invitation.
    /// </summary>
    [Fact]
    public void TheTempFileIsRandom_AndAPreCreatedOneIsNotUsed()
    {
        var key = NewKey();
        // The name an attacker would guess, made a directory so a write through it would fail
        // loudly rather than silently landing somewhere.
        var decoy = key.Path + ".tmp";
        Directory.CreateDirectory(decoy);

        var pub = key.Create();

        Directory.Exists(decoy).Should().BeTrue("the decoy was not touched");
        key.Matches(pub).Should().BeTrue();
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty("the temp file is renamed, not left behind");
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
