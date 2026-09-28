using System.Security.Cryptography;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Opening an Android blind node's backup file on Windows (plan 6.8, section 10): the body opens only
/// under the key sealed for the phone the header names, and a damaged, cut, spliced or foreign file never
/// yields a package. The key side (master password → boxes → DEKs → sealed secret) is the restore
/// pipeline's and comes in as the candidate keys; here "wrong password" is that side refusing.
/// </summary>
public sealed class AndroidBackupRestoreTests : IDisposable
{
    private const int Chunk = 1024;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_abr_" + Guid.NewGuid().ToString("N"));

    public AndroidBackupRestoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task OpensUnderTheSealedKey_EvenAmongOthers_AndGivesTheRecoverySet()
    {
        var phone = await BackupAsync();

        var opened = await AndroidBackupRestore.OpenAsync(phone.File,
            Keys(RandomNumberGenerator.GetBytes(32), phone.Key), PackagePath());

        opened.NodeId.Should().Be(phone.NodeId);
        File.ReadAllBytes(opened.PackagePath).Should().Equal(phone.Content);
        opened.RecoverySetJson.Should().Contain("bmb-recovery-set-v1");
    }

    [Fact]
    public async Task AWrongMasterPassword_IsRefusedByTheKeySide_AndLeavesNothing()
    {
        var phone = await BackupAsync();
        var package = PackagePath();

        var open = () => AndroidBackupRestore.OpenAsync(phone.File,
            (_, _) => throw new UnauthorizedAccessException("The master password opens none of the recovery boxes."), package);

        await open.Should().ThrowAsync<UnauthorizedAccessException>();
        File.Exists(package).Should().BeFalse();
    }

    [Fact]
    public async Task ARecoverySetWithoutThePhonesSealedKey_IsNamedAsSuch()
    {
        var phone = await BackupAsync();
        var open = () => AndroidBackupRestore.OpenAsync(phone.File, Keys(), PackagePath());
        (await open.Should().ThrowAsync<InvalidDataException>()).WithMessage("*no sealed key for this phone*");
    }

    [Fact]
    public async Task AWrongSealedKey_DoesNotOpen_AndLeavesNothing()
    {
        var phone = await BackupAsync();
        var package = PackagePath();

        var open = () => AndroidBackupRestore.OpenAsync(phone.File, Keys(RandomNumberGenerator.GetBytes(32)), package);

        await open.Should().ThrowAsync<InvalidDataException>();
        File.Exists(package).Should().BeFalse("a half-decrypted package must never be left to import");
    }

    [Theory]
    [InlineData("truncate")]
    [InlineData("flip")]
    public async Task ADamagedBody_DoesNotOpen_AndLeavesNothing(string damage)
    {
        var phone = await BackupAsync();
        var bytes = File.ReadAllBytes(phone.File);
        if (damage == "truncate") bytes = bytes[..^(Chunk / 2)];
        else bytes[^40] ^= 0x10;
        File.WriteAllBytes(phone.File, bytes);
        var package = PackagePath();

        var open = () => AndroidBackupRestore.OpenAsync(phone.File, Keys(phone.Key), package);

        await open.Should().ThrowAsync<InvalidDataException>();
        File.Exists(package).Should().BeFalse();
    }

    [Fact]
    public async Task AHeaderFromAnotherPhone_OnThisBody_OpensUnderNeitherKey()
    {
        var a = await BackupAsync();
        var b = await BackupAsync();
        var spliced = Splice(headerOf: b.File, bodyOf: a.File);

        var open = () => AndroidBackupRestore.OpenAsync(spliced, Keys(a.Key, b.Key), PackagePath());

        await open.Should().ThrowAsync<InvalidDataException>(
            "the header is bound to every chunk: another phone's header cannot front this body");
    }

    /// <summary>
    /// The file is replaced while the keys are being resolved — by another phone's genuine backup, whose
    /// key happens to be among the candidates. What was checked (phone A's header) and what is decrypted
    /// must be the same file: phone B's body never comes out under phone A's header. The one handle may
    /// still hold phone A's bytes (a small file sits whole in its read buffer) — then A's own backup opens,
    /// consistently; whatever it reads of B is refused (the checked preamble, and every chunk bound to it).
    /// </summary>
    [Theory]
    [InlineData(3)]  // the whole file fits the handle's read buffer
    [InlineData(20)] // most of it is read from disk after the swap
    public async Task AFileReplacedWhileTheKeysAreResolved_NeverYieldsTheOtherPhonesBody(int chunks)
    {
        var a = await BackupAsync(chunks: chunks);
        var b = await BackupAsync(chunks: chunks);
        var package = PackagePath();

        AndroidBackupOpened? opened = null;
        try
        {
            opened = await AndroidBackupRestore.OpenAsync(a.File, (header, _) =>
            {
                File.WriteAllBytes(a.File, File.ReadAllBytes(b.File)); // same path, other content
                return Task.FromResult<IReadOnlyList<byte[]>>([a.Key.ToArray(), b.Key.ToArray()]);
            }, package);
        }
        catch (InvalidDataException)
        {
            File.Exists(package).Should().BeFalse("a refused file leaves nothing to import");
        }

        if (opened != null)
        {
            opened.NodeId.Should().Be(a.NodeId);
            File.ReadAllBytes(opened.PackagePath).Should().Equal(a.Content,
                "phone B's body must never come out under phone A's checked header");
        }
        if (chunks > 3) opened.Should().BeNull("past the read buffer the handle sees the other file, and that is refused");
    }

    [Fact]
    public async Task AHeaderNamingAnotherPhonesKey_IsRefusedBeforeAnyKeyIsLookedUp()
    {
        var phone = await BackupAsync(keyName: $"android-backup:{BlindNodeId.NewId()}");
        var asked = false;

        var open = () => AndroidBackupRestore.OpenAsync(phone.File, (_, _) =>
        {
            asked = true;
            return Task.FromResult<IReadOnlyList<byte[]>>([phone.Key]);
        }, PackagePath());

        await open.Should().ThrowAsync<InvalidDataException>();
        asked.Should().BeFalse("a header must not choose which phone's sealed secret gets opened");
    }

    [Fact]
    public async Task AHeaderOfANonBlindNode_IsRefused()
    {
        var phone = await BackupAsync(nodeId: Guid.NewGuid());
        var open = () => AndroidBackupRestore.OpenAsync(phone.File, Keys(phone.Key), PackagePath());
        await open.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task LooksLikeBackup_ByItsMagic()
    {
        var phone = await BackupAsync();
        var other = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(other, "hello there, not a backup");

        AndroidBackupRestore.LooksLikeBackup(phone.File).Should().BeTrue();
        AndroidBackupRestore.LooksLikeBackup(other).Should().BeFalse();
        AndroidBackupRestore.LooksLikeBackup(Path.Combine(_dir, "missing")).Should().BeFalse();
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private sealed record Phone(Guid NodeId, byte[] Key, byte[] Content, string File);

    private async Task<Phone> BackupAsync(Guid? nodeId = null, string? keyName = null, int chunks = 3)
    {
        var id = nodeId ?? BlindNodeId.NewId();
        var key = RandomNumberGenerator.GetBytes(32);
        var content = RandomNumberGenerator.GetBytes(Chunk * chunks + 100);
        var source = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".src");
        File.WriteAllBytes(source, content);
        var file = Path.Combine(_dir, Guid.NewGuid().ToString("N") + AndroidBackupRestore.Extension);
        var set = $"{{\"format\":\"bmb-recovery-set-v1\",\"boxes\":[],\"links\":[],\"anchors\":[],\"sealed_secrets\":[],\"created_at\":\"{id}\"}}";
        await AndroidBackupWriter.WriteAsync(source, file, key, id, keyName ?? $"android-backup:{id}", set, Chunk);
        return new Phone(id, key, content, file);
    }

    private string Splice(string headerOf, string bodyOf)
    {
        static int BodyStart(byte[] f) => 8 + 4 + (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(8, 4));
        var h = File.ReadAllBytes(headerOf);
        var b = File.ReadAllBytes(bodyOf);
        var path = Path.Combine(_dir, "spliced" + AndroidBackupRestore.Extension);
        File.WriteAllBytes(path, [.. h[..BodyStart(h)], .. b[BodyStart(b)..]]);
        return path;
    }

    private string PackagePath() => Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".package");

    private static Func<AndroidBackupHeader, CancellationToken, Task<IReadOnlyList<byte[]>>> Keys(params byte[][] keys) =>
        (_, _) => Task.FromResult<IReadOnlyList<byte[]>>(keys);
}
