using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Review S3-r2 #4: every check the phone makes on a downloaded replica before it installs it, one
/// test each. The package is crafted here and signed by a test key standing in for the serving node
/// (the call code carries that key); the handshake still runs against the real blind host. A control
/// test installs a correct package, so each refusal below differs from it by exactly one defect.
/// </summary>
public sealed class BlindPhoneReplicaVerificationTests : IDisposable
{
    private static readonly byte[] SignatureDomain = "BMB-MANIFEST-FILE-V1\0"u8.ToArray();

    private readonly BlindNodeFactory _blind = new();

    public void Dispose() => _blind.Dispose();

    [Fact]
    public async Task Control_ACorrectlyCraftedPackage_Installs()
    {
        var phone = await CreatePhoneAsync();

        await phone.InstallAsync(await Craft(phone));

        (await new SyncPositionRepository(phone.Factory).GetAsync(phone.Target.NodeId)).Should().NotBeNull(
            "the install seeds the pull position of the node it came from");
    }

    [Fact]
    public async Task Refuses_ASignatureThatIsValidButMadeByADifferentKey()
    {
        var phone = await CreatePhoneAsync();
        var forger = Ed25519Signer.GenerateKeyPair();

        var package = await Craft(phone, new CraftOptions { SigningSeed = forger.privateKey });

        await phone.ExpectRefusedAsync(package, "detached signature does not verify");
    }

    [Fact]
    public async Task Refuses_AProducerNodeIdThatIsNotTheCallCodeNode()
    {
        var phone = await CreatePhoneAsync();

        var package = await Craft(phone, new CraftOptions { ProducerNodeId = Guid.NewGuid() });

        await phone.ExpectRefusedAsync(package, "producer does not match the call-code node");
    }

    [Fact]
    public async Task Refuses_APackageThatDoesNotWhitelistItsProducer()
    {
        var phone = await CreatePhoneAsync();

        var package = await Craft(phone, new CraftOptions { WhitelistedNodeId = Guid.NewGuid() });

        await phone.ExpectRefusedAsync(package, "does not whitelist its producer");
    }

    [Fact]
    public async Task Refuses_AProducerKeyThatDiffersFromTheCallCodeKey()
    {
        var phone = await CreatePhoneAsync();

        var package = await Craft(phone, new CraftOptions { WhitelistedKey = Ed25519Signer.GenerateKeyPair().publicKey });

        await phone.ExpectRefusedAsync(package, "producer key does not match the call-code key");
    }

    [Fact]
    public async Task Refuses_ATarEntryThatEscapesTheStagingDirectoryWithDotDot()
    {
        var phone = await CreatePhoneAsync();

        var package = await Craft(phone, new CraftOptions { ExtraEntries = [("../escaped-by-dotdot.txt", [1])] });

        await phone.ExpectRefusedAsync(package, "escapes its staging directory");
        File.Exists(Path.Combine(phone.WorkDirectory, "escaped-by-dotdot.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Refuses_ATarEntryWithAnAbsolutePath()
    {
        var phone = await CreatePhoneAsync();
        var outside = Path.Combine(phone.DataPath, "outside-the-staging-tree");
        var absoluteName = Path.Combine(outside, "absolute-escape.txt");

        var package = await Craft(phone, new CraftOptions { ExtraEntries = [(absoluteName, [1])] });

        await phone.ExpectRefusedAsync(package, "escapes its staging directory");
        File.Exists(absoluteName).Should().BeFalse("an absolute entry name must not be written where it points");
    }

    [Fact]
    public async Task Refuses_AManifestFileHashThatDoesNotMatchTheFile()
    {
        var phone = await CreatePhoneAsync();

        var package = await Craft(phone, new CraftOptions { ListedHashOverrides = new() { ["beememorybank.db"] = new string('0', 64) } });

        await phone.ExpectRefusedAsync(package, "manifest hash mismatch");
    }

    [Fact]
    public async Task Refuses_AManifestThatListsAFileTheArchiveDoesNotContain()
    {
        var phone = await CreatePhoneAsync();

        var package = await Craft(phone, new CraftOptions { ListedHashOverrides = new() { ["listed-but-absent.bin"] = new string('1', 64) } });

        await phone.ExpectRefusedAsync(package, "manifest file is missing");
    }

    [Theory]
    [InlineData("garbage-blind-manifest")]
    [InlineData("key-that-is-not-base64")]
    public async Task AFailureOfAnyTypeAfterTheArchiveVerified_ClearsThePartialAndReportsInvalidData(string defect)
    {
        var phone = await CreatePhoneAsync();
        var package = await Craft(phone, defect == "garbage-blind-manifest"
            ? new CraftOptions { BlindManifestBytes = "{ not json"u8.ToArray() }
            : new CraftOptions { WhitelistedKeyB64 = "***not base64***" });

        var act = () => phone.InstallAsync(package);

        var thrown = (await act.Should().ThrowAsync<InvalidDataException>()).Which;
        thrown.InnerException.Should().NotBeNull("the cause stays attached for the log");
        File.Exists(Path.Combine(phone.WorkDirectory, "replica.part")).Should().BeFalse(
            "a complete archive cannot be resumed, so keeping it only holds the disk");
        File.Exists(Path.Combine(phone.WorkDirectory, "replica.part.json")).Should().BeFalse();
    }

    [Fact]
    public async Task AnIoFailureWhileInstalling_KeepsItsType_ButAlsoClearsThePartial()
    {
        var phone = await CreatePhoneAsync(copyMediaFile: (_, _, _) => Task.FromException(new IOException("simulated full storage")));
        var package = await Craft(phone, new CraftOptions { ExtraEntries = [("media/blob.enc", [1, 2, 3])] });

        var act = () => phone.InstallAsync(package);

        await act.Should().ThrowAsync<IOException>();
        File.Exists(Path.Combine(phone.WorkDirectory, "replica.part")).Should().BeFalse();
        File.Exists(Path.Combine(phone.WorkDirectory, "replica.part.json")).Should().BeFalse();
        File.Exists(Path.Combine(phone.WorkDirectory, "replica.install-failed.json")).Should().BeTrue();
    }

    [Fact]
    public async Task AFailureAfterTheArchiveVerified_BacksOffInsteadOfDownloadingTheSamePackageAgain()
    {
        var time = new AdjustableTime(DateTimeOffset.UtcNow);
        var phone = await CreatePhoneAsync(time: time);
        var bad = await Craft(phone, new CraftOptions { BlindManifestBytes = "{ not json"u8.ToArray() });
        (await FluentActions.Awaiting(() => phone.InstallAsync(bad)).Should().ThrowAsync<InvalidDataException>())
            .Which.Message.Should().NotContain("next attempt after", "the first failure is the real one");

        // An hour later is the first moment another attempt may start, and a good package then installs.
        var good = await Craft(phone);
        (await FluentActions.Awaiting(() => phone.InstallAsync(good)).Should().ThrowAsync<InvalidDataException>())
            .Which.Message.Should().Contain("next attempt after");
        phone.RequestsOfLastInstall.Should().Be(0, "a backing-off phone must not even contact the node");
        time.Advance(TimeSpan.FromMinutes(59));
        await FluentActions.Awaiting(() => phone.InstallAsync(good)).Should().ThrowAsync<InvalidDataException>();
        phone.RequestsOfLastInstall.Should().Be(0);

        // A second failure doubles the wait.
        time.Advance(TimeSpan.FromMinutes(2));
        await FluentActions.Awaiting(() => phone.InstallAsync(bad)).Should().ThrowAsync<InvalidDataException>();
        time.Advance(TimeSpan.FromMinutes(119));
        await FluentActions.Awaiting(() => phone.InstallAsync(good)).Should().ThrowAsync<InvalidDataException>();
        phone.RequestsOfLastInstall.Should().Be(0);

        time.Advance(TimeSpan.FromMinutes(2));
        await phone.InstallAsync(good);
        (await new SyncPositionRepository(phone.Factory).GetAsync(phone.Target.NodeId)).Should().NotBeNull();
        File.Exists(Path.Combine(phone.WorkDirectory, "replica.install-failed.json")).Should().BeFalse(
            "a successful install forgets the failures");
    }

    [Fact]
    public async Task ABackOffRecordedForAnotherNode_DoesNotBlockThisOne()
    {
        var time = new AdjustableTime(DateTimeOffset.UtcNow);
        var phone = await CreatePhoneAsync(time: time);
        var bad = await Craft(phone, new CraftOptions { BlindManifestBytes = "{ not json"u8.ToArray() });
        await FluentActions.Awaiting(() => phone.InstallAsync(bad)).Should().ThrowAsync<InvalidDataException>();
        var recordPath = Path.Combine(phone.WorkDirectory, "replica.install-failed.json");
        await File.WriteAllTextAsync(recordPath, (await File.ReadAllTextAsync(recordPath))
            .Replace(phone.Target.NodeId.ToString(), Guid.NewGuid().ToString(), StringComparison.OrdinalIgnoreCase));

        await phone.InstallAsync(await Craft(phone));

        (await new SyncPositionRepository(phone.Factory).GetAsync(phone.Target.NodeId)).Should().NotBeNull();
    }

    [Fact]
    public async Task Refuses_AnIdentityThatIsNotV2_WithoutAnyRequest()
    {
        var phone = await CreatePhoneAsync(identityKeyVersion: NodeIdentityCrypto.ExternalKeyVersion + 1);
        var package = await Craft(phone);
        var counting = new CraftedReplicaHandler(_blind.Server.CreateHandler(), package);
        using var http = new HttpClient(counting);

        var act = () => phone.Client.FetchAndInstallAsync(http, phone.Target, phone.WorkDirectory, progress: null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*v=2 external-key identity*");
        counting.Requests.Should().Be(0, "a non-v2 identity must be refused before the phone contacts anyone");
    }

    private async Task<PhoneSetup> CreatePhoneAsync(
        int identityKeyVersion = -1, TimeProvider? time = null,
        Func<string, string, CancellationToken, Task>? copyMediaFile = null)
    {
        var phoneData = Path.Combine(_blind.DataPath, "phone");
        var phoneDb = Path.Combine(phoneData, "beememorybank.db");
        var factory = new DbConnectionFactory(phoneDb);
        await new MigrationRunner(factory).RunMigrationsAsync();
        var (phonePublic, phoneSeed) = Ed25519Signer.GenerateKeyPair();
        var phoneId = BlindNodeId.NewId();
        var identity = new NodeIdentity
        {
            NodeId = phoneId,
            DisplayName = "phone",
            Ed25519PublicKey = phonePublic,
            Ed25519PrivateKey = [],
            Ed25519PrivateKeyV = identityKeyVersion >= 0 ? identityKeyVersion : NodeIdentityCrypto.ExternalKeyVersion,
            CanGenerateEmbeddings = false,
            CreatedAt = DateTime.UtcNow
        };
        await new NodeIdentityRepository(factory).CreateAsync(identity);
        await _blind.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = phoneId,
            DisplayName = "phone",
            Ed25519PublicKey = phonePublic,
            Status = WhitelistStatuses.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        // The real host answers the handshake; the packages it would serve are replaced by crafted ones,
        // signed by this key, which the call code names as the serving node's key.
        var servingKey = Ed25519Signer.GenerateKeyPair();
        var server = (await _blind.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var target = BlindCallCode.Create(BlindNodeFactory.PublicAddress, server.NodeId,
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", servingKey.publicKey, new byte[32]);
        var client = new BlindPhoneReplicaClient(factory, new NodeIdentityRepository(factory), new SeedSigner(phoneSeed),
            phoneData, phoneDb, NullLogger<BlindPhoneReplicaClient>.Instance, copyMediaFile, time);
        return new PhoneSetup(this, phoneData, factory, identity, target, servingKey.privateKey, client);
    }

    private static async Task<CraftedPackage> Craft(PhoneSetup phone, CraftOptions? options = null)
    {
        options ??= new CraftOptions();
        var database = await EmptyMigratedDatabaseAsync(phone.DataPath);
        var blindManifest = options.BlindManifestBytes ?? new BlindManifest(BlindManifest.CurrentFormat, Guid.NewGuid(),
            options.ProducerNodeId ?? phone.Target.NodeId, CpSequence: 1, IncludesUpTo: null, DateTime.UtcNow,
            [new BlindManifestPeer(options.WhitelistedNodeId ?? phone.Target.NodeId, "serving node",
                options.WhitelistedKeyB64 ?? Convert.ToBase64String(options.WhitelistedKey ?? phone.Target.PublicKey),
                null, false, null, 0, null)],
            [], null).ToBytes();

        var files = new Dictionary<string, string>
        {
            ["beememorybank.db"] = Hex(database),
            [BlindManifest.FileName] = Hex(blindManifest)
        };
        foreach (var (name, hash) in options.ListedHashOverrides) files[name] = hash;
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new { files });

        var archive = new MemoryStream();
        using (var gzip = new GZipStream(archive, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            Write(tar, "beememorybank.db", database);
            Write(tar, BlindManifest.FileName, blindManifest);
            foreach (var (name, bytes) in options.ExtraEntries) Write(tar, name, bytes);
            Write(tar, "manifest.json", manifest);
        }

        var bytesOfArchive = archive.ToArray();
        using var payload = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        payload.AppendData(SignatureDomain);
        payload.AppendData(manifest);
        payload.AppendData(bytesOfArchive);
        var signature = Ed25519Signer.Sign(options.SigningSeed ?? phone.ServingSeed, payload.GetHashAndReset());
        return new CraftedPackage(bytesOfArchive, Hex(bytesOfArchive), Convert.ToBase64String(signature));
    }

    private static void Write(TarWriter tar, string name, byte[] bytes) =>
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(bytes) });

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task<byte[]> EmptyMigratedDatabaseAsync(string directory)
    {
        var path = Path.Combine(directory, $"crafted-{Guid.NewGuid():N}.db");
        using (var factory = new DbConnectionFactory(path))
            await new MigrationRunner(factory).RunMigrationsAsync();
        SqliteConnection.ClearAllPools();
        var bytes = await File.ReadAllBytesAsync(path);
        File.Delete(path);
        return bytes;
    }

    private sealed class CraftOptions
    {
        public byte[]? SigningSeed { get; init; }
        public Guid? ProducerNodeId { get; init; }
        public Guid? WhitelistedNodeId { get; init; }
        public byte[]? WhitelistedKey { get; init; }
        public string? WhitelistedKeyB64 { get; init; }
        public byte[]? BlindManifestBytes { get; init; }
        public IReadOnlyList<(string Name, byte[] Bytes)> ExtraEntries { get; init; } = [];
        public Dictionary<string, string> ListedHashOverrides { get; init; } = [];
    }

    private sealed record CraftedPackage(byte[] Archive, string Sha256, string SignatureB64);

    private sealed class PhoneSetup(
        BlindPhoneReplicaVerificationTests owner, string dataPath, DbConnectionFactory factory, NodeIdentity identity,
        BlindCallCode target, byte[] servingSeed, BlindPhoneReplicaClient client)
    {
        public string DataPath { get; } = dataPath;
        public string WorkDirectory { get; } = Path.Combine(dataPath, "replica");
        public DbConnectionFactory Factory { get; } = factory;
        public NodeIdentity Identity { get; } = identity;
        public BlindCallCode Target { get; } = target;
        public byte[] ServingSeed { get; } = servingSeed;
        public BlindPhoneReplicaClient Client { get; } = client;

        public int RequestsOfLastInstall { get; private set; }

        public async Task InstallAsync(CraftedPackage package)
        {
            var handler = new CraftedReplicaHandler(owner._blind.Server.CreateHandler(), package);
            using var http = new HttpClient(handler);
            try
            {
                await Client.FetchAndInstallAsync(http, Target, WorkDirectory, progress: null, CancellationToken.None);
            }
            finally
            {
                RequestsOfLastInstall = handler.Requests;
            }
        }

        /// <summary>The package is refused for the named reason and leaves nothing behind that could be reused.</summary>
        public async Task ExpectRefusedAsync(CraftedPackage package, string reason)
        {
            var act = () => InstallAsync(package);

            (await act.Should().ThrowAsync<InvalidDataException>()).Which.Message.Should().Contain(reason);
            (await new SyncPositionRepository(Factory).GetAsync(Target.NodeId)).Should().BeNull(
                "a refused package must not reach the live database");
            File.Exists(Path.Combine(WorkDirectory, "replica.part")).Should().BeFalse("a refused archive is not resumable state");
            File.Exists(Path.Combine(WorkDirectory, "replica.part.json")).Should().BeFalse();
            Directory.GetDirectories(WorkDirectory, "verified-*").Should().BeEmpty();
        }
    }

    /// <summary>Answers the replica request with a crafted package; everything else goes to the real host.</summary>
    private sealed class CraftedReplicaHandler(HttpMessageHandler inner, CraftedPackage package) : DelegatingHandler(inner)
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            if (request.RequestUri?.AbsolutePath != "/api/blind/replica")
                return base.SendAsync(request, ct);

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package.Archive) };
            response.Headers.TryAddWithoutValidation("X-BMB-Package-Sha256", package.Sha256);
            response.Headers.TryAddWithoutValidation("X-BMB-Snapshot-Signature", package.SignatureB64);
            return Task.FromResult(response);
        }
    }

    private sealed class AdjustableTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class SeedSigner(byte[] seed) : INodeAuthSigner
    {
        public byte[] SignChallenge(NodeIdentity identity, byte[] challengePayload) => Ed25519Signer.Sign(seed, challengePayload);
    }
}
