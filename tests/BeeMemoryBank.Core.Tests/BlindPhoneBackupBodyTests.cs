using System.Buffers.Text;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// The two pieces a restore from an Android backup trusts or reads: the sealed pairing record (which node
/// produced the package — strict, or nothing) and the body's three files (by fixed names, nothing else).
/// </summary>
public sealed class BlindPhoneBackupBodyTests : IDisposable
{
    private static readonly string Pin = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_body_" + Guid.NewGuid().ToString("N"));

    public BlindPhoneBackupBodyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void TheSeal_RoundTrips()
    {
        var seal = Seal();

        BlindPhoneBackupSeal.TryDecode(seal.Encode(), out var back).Should().BeTrue();

        back!.BackupKey.Should().Equal(seal.BackupKey);
        back.ProducerNodeId.Should().Be(seal.ProducerNodeId);
        back.ProducerPublicKey.Should().Equal(seal.ProducerPublicKey);
        (back.ProducerAddress, back.ProducerTlsSpki).Should().Be((seal.ProducerAddress, seal.ProducerTlsSpki));
        back.PairedBy.Should().Be(seal.PairedBy);
        back.PairingSignature.Should().Equal(seal.PairingSignature);
    }

    [Fact]
    public void ThePairingStatement_BindsThePhoneAndEveryPartOfTheProducer()
    {
        var seal = Seal();
        var phone = Guid.NewGuid();
        var statement = seal.PairingStatement(phone);

        seal.PairingStatement(Guid.NewGuid()).Should().NotEqual(statement, "a record signed for one phone is not another's");
        (seal with { BackupKey = RandomNumberGenerator.GetBytes(32) }).PairingStatement(phone).Should().NotEqual(statement,
            "the backup key is bound by its digest: swapping only the key voids the signature");
        Encoding.UTF8.GetString(statement).Should().NotContain(Base64Url.EncodeToString(seal.BackupKey), "the key itself stays secret");
        (seal with { ProducerNodeId = Guid.NewGuid() }).PairingStatement(phone).Should().NotEqual(statement);
        (seal with { ProducerPublicKey = RandomNumberGenerator.GetBytes(32) }).PairingStatement(phone).Should().NotEqual(statement);
        (seal with { ProducerAddress = "https://other.test:5300" }).PairingStatement(phone).Should().NotEqual(statement);
        (seal with { ProducerTlsSpki = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)) }).PairingStatement(phone).Should().NotEqual(statement);
    }

    [Theory]
    [InlineData("bare key")]
    [InlineData("not json")]
    [InlineData("short key")]
    [InlineData("short producer key")]
    [InlineData("no producer")]
    [InlineData("address with a path")]
    [InlineData("http address")]
    [InlineData("bad pin")]
    [InlineData("other version")]
    [InlineData("unsigned")]
    [InlineData("short signature")]
    public void AnythingButAWholePairingRecord_IsNoSeal(string damage)
    {
        var s = Seal();
        byte[] value = damage switch
        {
            "bare key" => RandomNumberGenerator.GetBytes(32),
            "not json" => Encoding.UTF8.GetBytes("{not json"),
            "short key" => (s with { BackupKey = new byte[16] }).Encode(),
            "short producer key" => (s with { ProducerPublicKey = new byte[31] }).Encode(),
            "no producer" => (s with { ProducerNodeId = Guid.Empty }).Encode(),
            "address with a path" => (s with { ProducerAddress = "https://hub.test:5300/sync" }).Encode(),
            "http address" => (s with { ProducerAddress = "http://hub.test:5300" }).Encode(),
            "bad pin" => (s with { ProducerTlsSpki = "pin-1" }).Encode(),
            "unsigned" => (s with { PairedBy = Guid.Empty }).Encode(),
            "short signature" => (s with { PairingSignature = new byte[32] }).Encode(),
            _ => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(s.Encode()).Replace("\"v\":2", "\"v\":1")),
        };

        BlindPhoneBackupSeal.TryDecode(value, out var seal).Should().BeFalse();
        seal.Should().BeNull();
    }

    [Fact]
    public async Task TheBody_RoundTrips()
    {
        var (package, events) = (File("package", 5000), File("events", 300));
        var signature = RandomNumberGenerator.GetBytes(64);
        var body = Path.Combine(_dir, "body.tar");

        await BlindPhoneBackupBody.WriteAsync(body, package, signature, events);
        var parts = await BlindPhoneBackupBody.ReadAsync(body, Path.Combine(_dir, "out"), BlindPhoneBackupLimits.Default);

        System.IO.File.ReadAllBytes(parts.PackagePath).Should().Equal(System.IO.File.ReadAllBytes(package));
        System.IO.File.ReadAllBytes(parts.EventsPath).Should().Equal(System.IO.File.ReadAllBytes(events));
        parts.Signature.Should().Equal(signature);
        Path.GetDirectoryName(parts.PackagePath).Should().Be(Path.Combine(_dir, "out"), "written by fixed names only");
    }

    [Theory]
    [InlineData("extra file")]
    [InlineData("missing events")]
    [InlineData("twice")]
    [InlineData("path")]
    [InlineData("link")]
    [InlineData("not a tar")]
    public async Task AnythingButItsThreeFiles_IsRefused(string shape)
    {
        var body = Path.Combine(_dir, "body.tar");
        if (shape == "not a tar")
            await System.IO.File.WriteAllBytesAsync(body, RandomNumberGenerator.GetBytes(2000));
        else
        {
            await using var fs = System.IO.File.Create(body);
            await using var tar = new TarWriter(fs, TarEntryFormat.Pax);
            async Task Entry(string name) => await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, name)
                { DataStream = new MemoryStream(RandomNumberGenerator.GetBytes(64)) });
            await Entry(shape == "path" ? "../" + BlindPhoneBackupBody.PackageEntry : BlindPhoneBackupBody.PackageEntry);
            await Entry(BlindPhoneBackupBody.SignatureEntry);
            if (shape != "missing events") await Entry(BlindPhoneBackupBody.EventsEntry);
            if (shape == "extra file") await Entry("notes.txt");
            if (shape == "twice") await Entry(BlindPhoneBackupBody.EventsEntry);
            if (shape == "link")
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.SymbolicLink, "media") { LinkName = "/etc" });
        }

        var read = () => BlindPhoneBackupBody.ReadAsync(body, Path.Combine(_dir, "out"), BlindPhoneBackupLimits.Default);

        await read.Should().ThrowAsync<InvalidDataException>();
    }

    /// <summary>An entry over its limit is refused by its declared size, before any of it is written.</summary>
    [Theory]
    [InlineData("package")]
    [InlineData("events")]
    public async Task AnEntryOverItsLimit_IsRefused_BeforeAnythingOfItIsWritten(string entry)
    {
        var (package, events) = (File("package", entry == "package" ? 5000 : 100), File("events", entry == "events" ? 5000 : 100));
        var body = Path.Combine(_dir, "body.tar");
        await BlindPhoneBackupBody.WriteAsync(body, package, RandomNumberGenerator.GetBytes(64), events);
        var output = Path.Combine(_dir, "out");

        var read = () => BlindPhoneBackupBody.ReadAsync(body, output, new BlindPhoneBackupLimits(1000, 1000, 10));

        (await read.Should().ThrowAsync<InvalidDataException>()).WithMessage("*larger than a restore takes*");
        System.IO.File.Exists(Path.Combine(output, entry == "package" ? BlindPhoneBackupBody.PackageEntry : BlindPhoneBackupBody.EventsEntry))
            .Should().BeFalse();
    }

    private static BlindPhoneBackupSeal Seal() => new(RandomNumberGenerator.GetBytes(32), Guid.NewGuid(),
        RandomNumberGenerator.GetBytes(32), "https://hub.test:5300", Pin, Guid.NewGuid(), RandomNumberGenerator.GetBytes(64));

    private string File(string name, int size)
    {
        var path = Path.Combine(_dir, name);
        System.IO.File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(size));
        return path;
    }
}
