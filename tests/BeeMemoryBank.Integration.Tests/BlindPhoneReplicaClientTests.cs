using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>Real API host + real blind package reader, with a locally-created v2 phone identity.</summary>
public sealed class BlindPhoneReplicaClientTests : IDisposable
{
    private readonly BlindNodeFactory _blind = new();

    public void Dispose() => _blind.Dispose();

    [Fact]
    public async Task Download_VerifiesAndAtomicallyInstallsTheServingBlindReplica()
    {
        var phone = await CreatePhoneAsync();
        var workDirectory = Path.Combine(phone.DataPath, "replica");
        using var http = new HttpClient(_blind.Server.CreateHandler());

        await phone.Client.FetchAndInstallAsync(http, phone.Target, workDirectory, progress: null, CancellationToken.None);

        var installed = await new NodeIdentityRepository(phone.Factory).GetAsync();
        installed!.NodeId.Should().Be(phone.Identity.NodeId, "the v2 phone identity survives the database cutover");
        var position = await new SyncPositionRepository(phone.Factory).GetAsync(phone.Target.NodeId);
        position.Should().NotBeNull("the next pull must start at the replica checkpoint");
        File.Exists(Path.Combine(workDirectory, "replica.part")).Should().BeFalse("a completed package must not block a later reseed");
        File.Exists(Path.Combine(workDirectory, "replica.part.json")).Should().BeFalse("the completed package metadata is no longer resumable state");
        Directory.GetDirectories(workDirectory, "verified-*").Should().BeEmpty("verified extraction staging is no longer needed after cutover");
    }

    [Fact]
    public async Task Download_DiscardsAnEmptyPartialAndStartsANewPackage()
    {
        var phone = await CreatePhoneAsync();
        var workDirectory = Path.Combine(phone.DataPath, "replica");
        Directory.CreateDirectory(workDirectory);
        await File.WriteAllBytesAsync(Path.Combine(workDirectory, "replica.part"), []);
        using var http = new HttpClient(_blind.Server.CreateHandler());

        await phone.Client.FetchAndInstallAsync(http, phone.Target, workDirectory, progress: null, CancellationToken.None);

        (await new NodeIdentityRepository(phone.Factory).GetAsync())!.NodeId.Should().Be(phone.Identity.NodeId);
        File.Exists(Path.Combine(workDirectory, "replica.part")).Should().BeFalse();
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData(null)]
    public async Task Download_RecoversFromMalformedPersistedPartialMetadata(string? signatureB64)
    {
        var phone = await CreatePhoneAsync();
        var workDirectory = Path.Combine(phone.DataPath, "replica");
        Directory.CreateDirectory(workDirectory);
        var package = await _blind.Services.GetRequiredService<BlindReplicaPackageCache>()
            .GetAsync(producerIsSuperadmin: false, CancellationToken.None);
        await File.WriteAllBytesAsync(Path.Combine(workDirectory, "replica.part"), [0]);
        await File.WriteAllTextAsync(Path.Combine(workDirectory, "replica.part.json"), JsonSerializer.Serialize(new
        {
            sha256 = package.Sha256,
            signatureB64,
            length = package.Length
        }));
        using var http = new HttpClient(_blind.Server.CreateHandler());

        await phone.Client.FetchAndInstallAsync(http, phone.Target, workDirectory, progress: null, CancellationToken.None);

        (await new NodeIdentityRepository(phone.Factory).GetAsync())!.NodeId.Should().Be(phone.Identity.NodeId);
        File.Exists(Path.Combine(workDirectory, "replica.part")).Should().BeFalse();
        File.Exists(Path.Combine(workDirectory, "replica.part.json")).Should().BeFalse();
    }

    [Fact]
    public async Task Download_ReplacesAStaleCandidateDatabaseBeforeRetryingTheSamePackage()
    {
        var phone = await CreatePhoneAsync();
        var package = await _blind.Services.GetRequiredService<BlindReplicaPackageCache>()
            .GetAsync(producerIsSuperadmin: false, CancellationToken.None);
        var candidatePath = Path.Combine(phone.DataPath, $"beememorybank.replica-{package.Manifest.SeedId:N}.db");
        var candidate = new DbConnectionFactory(candidatePath);
        await new MigrationRunner(candidate).RunMigrationsAsync();
        await new NodeIdentityRepository(candidate).CreateAsync(phone.Identity);
        using var http = new HttpClient(_blind.Server.CreateHandler());

        await phone.Client.FetchAndInstallAsync(http, phone.Target, Path.Combine(phone.DataPath, "replica"), progress: null, CancellationToken.None);

        (await new NodeIdentityRepository(phone.Factory).GetAsync())!.NodeId.Should().Be(phone.Identity.NodeId);
        File.Exists(candidatePath).Should().BeFalse("the candidate is consumed by the successful cutover");
    }

    [Fact]
    public async Task Download_MediaCopyFailureKeepsTheLiveDatabase()
    {
        var sourceMediaDirectory = Path.Combine(_blind.DataPath, "media");
        Directory.CreateDirectory(sourceMediaDirectory);
        await File.WriteAllBytesAsync(Path.Combine(sourceMediaDirectory, "copy-failure.enc"), [1, 2, 3]);
        var phone = await CreatePhoneAsync((_, _, _) => Task.FromException(new IOException("simulated full storage")));
        using var http = new HttpClient(_blind.Server.CreateHandler());

        var act = () => phone.Client.FetchAndInstallAsync(http, phone.Target,
            Path.Combine(phone.DataPath, "replica"), progress: null, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
        (await new SyncPositionRepository(phone.Factory).GetAsync(phone.Target.NodeId)).Should().BeNull(
            "the database switch follows media copy and must not occur after a media-copy failure");
        (await new NodeIdentityRepository(phone.Factory).GetAsync())!.NodeId.Should().Be(phone.Identity.NodeId);
        Directory.GetDirectories(Path.Combine(phone.DataPath, "replica"), "verified-*").Should().BeEmpty(
            "failed extraction staging must not remain after a media-copy failure");
    }

    [Fact]
    public async Task Download_KeepsOnlyTheMostRecentReplicaRollbackDatabase()
    {
        var phone = await CreatePhoneAsync();
        var directory = phone.DataPath;
        const string databaseName = "beememorybank.db";
        await File.WriteAllBytesAsync(Path.Combine(directory, databaseName + ".before-replica-" + Guid.NewGuid().ToString("N")), [1]);
        await File.WriteAllBytesAsync(Path.Combine(directory, databaseName + ".before-replica-" + Guid.NewGuid().ToString("N")), [2]);
        using var http = new HttpClient(_blind.Server.CreateHandler());

        await phone.Client.FetchAndInstallAsync(http, phone.Target, Path.Combine(phone.DataPath, "replica"), progress: null, CancellationToken.None);

        Directory.GetFiles(directory, databaseName + ".before-replica-*").Should().ContainSingle(
            "only one previous live database may remain after a successful replica switch");
    }

    [Fact]
    public async Task Download_RestartsFromZeroWhenTheServerRejectsAResumeRange()
    {
        var phone = await CreatePhoneAsync();
        var workDirectory = Path.Combine(phone.DataPath, "replica");
        Directory.CreateDirectory(workDirectory);
        using (var source = new HttpClient(_blind.Server.CreateHandler()))
        {
            var token = await PeerAuthenticator.AuthenticateAsync(phone.Signer, source, phone.Target.Address,
                phone.Identity, phone.Target.NodeId, CancellationToken.None);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{phone.Target.Address}/api/blind/replica");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await source.SendAsync(request);
            var package = await response.Content.ReadAsByteArrayAsync();
            await File.WriteAllBytesAsync(Path.Combine(workDirectory, "replica.part"), package[..1]);
            await File.WriteAllTextAsync(Path.Combine(workDirectory, "replica.part.json"), JsonSerializer.Serialize(new
            {
                sha256 = response.Headers.GetValues("X-BMB-Package-Sha256").Single(),
                signatureB64 = response.Headers.GetValues("X-BMB-Snapshot-Signature").Single(),
                length = package.LongLength
            }));
        }

        using var handler = new RejectFirstRangeHandler(_blind.Server.CreateHandler());
        using var http = new HttpClient(handler);

        await phone.Client.FetchAndInstallAsync(http, phone.Target, workDirectory, progress: null, CancellationToken.None);

        handler.RangeRequests.Should().Be(1);
        handler.FreshReplicaRequests.Should().Be(1, "the rejected range must retry a new package from byte zero");
    }

    [Fact]
    public async Task Download_InterruptedResponseRetainsAuthenticatedPartialForTheNextAttempt()
    {
        var phone = await CreatePhoneAsync();
        var package = await _blind.Services.GetRequiredService<BlindReplicaPackageCache>()
            .GetAsync(producerIsSuperadmin: false, CancellationToken.None);
        var archive = await File.ReadAllBytesAsync(package.FilePath);
        var signature = Convert.ToBase64String(await File.ReadAllBytesAsync(package.FilePath + ".sig"));
        var workDirectory = Path.Combine(phone.DataPath, "replica");
        using var http = new HttpClient(new InterruptedReplicaResponseHandler(_blind.Server.CreateHandler(), archive,
            package.Sha256, signature));

        var act = () => phone.Client.FetchAndInstallAsync(http, phone.Target, workDirectory, progress: null, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
        new FileInfo(Path.Combine(workDirectory, "replica.part")).Length.Should().BeGreaterThan(0);
        File.Exists(Path.Combine(workDirectory, "replica.part.json")).Should().BeTrue(
            "the interrupted bytes can resume only when their authenticated metadata remains beside them");
    }

    [Fact]
    public async Task Download_MissingPackageHashHeader_ThrowsInvalidDataException()
    {
        var phone = await CreatePhoneAsync();
        using var http = new HttpClient(new MissingPackageHashHeaderHandler(_blind.Server.CreateHandler()));

        var act = () => phone.Client.FetchAndInstallAsync(http, phone.Target,
            Path.Combine(phone.DataPath, "replica"), progress: null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData("X-BMB-Package-Sha256", "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("X-BMB-Snapshot-Signature", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==")]
    public async Task Download_TamperedPackageHeaders_AreRefusedAndDoNotRemainAsResumeState(string header, string value)
    {
        var phone = await CreatePhoneAsync();
        var workDirectory = Path.Combine(phone.DataPath, "replica");
        using var http = new HttpClient(new RewrittenReplicaHeaderHandler(_blind.Server.CreateHandler(), header, value));

        var act = () => phone.Client.FetchAndInstallAsync(http, phone.Target, workDirectory, progress: null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(workDirectory, "replica.part")).Should().BeFalse();
        File.Exists(Path.Combine(workDirectory, "replica.part.json")).Should().BeFalse();
    }

    private async Task<PhoneSetup> CreatePhoneAsync(Func<string, string, CancellationToken, Task>? copyMediaFile = null)
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
            Ed25519PrivateKeyV = NodeIdentityCrypto.ExternalKeyVersion,
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
        var server = (await _blind.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var target = BlindCallCode.Create(BlindNodeFactory.PublicAddress, server.NodeId,
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", server.Ed25519PublicKey, new byte[32]);
        var signer = new SeedSigner(phoneSeed);
        var client = new BlindPhoneReplicaClient(factory, new NodeIdentityRepository(factory), signer,
            phoneData, phoneDb, NullLogger<BlindPhoneReplicaClient>.Instance, copyMediaFile);
        return new PhoneSetup(phoneData, factory, identity, target, signer, client);
    }

    private sealed record PhoneSetup(
        string DataPath,
        DbConnectionFactory Factory,
        NodeIdentity Identity,
        BlindCallCode Target,
        INodeAuthSigner Signer,
        BlindPhoneReplicaClient Client);

    private sealed class RejectFirstRangeHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private bool _rejected;

        public int RangeRequests { get; private set; }
        public int FreshReplicaRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri?.AbsolutePath != "/api/blind/replica")
                return base.SendAsync(request, ct);
            if (request.Headers.Range is not null && !_rejected)
            {
                _rejected = true;
                RangeRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
            }

            FreshReplicaRequests++;
            return base.SendAsync(request, ct);
        }
    }

    private sealed class MissingPackageHashHeaderHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (request.RequestUri?.AbsolutePath == "/api/blind/replica")
                response.Headers.Remove("X-BMB-Package-Sha256");
            return response;
        }
    }

    private sealed class RewrittenReplicaHeaderHandler(HttpMessageHandler inner, string header, string value) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (request.RequestUri?.AbsolutePath == "/api/blind/replica")
            {
                response.Headers.Remove(header);
                response.Headers.TryAddWithoutValidation(header, value);
            }
            return response;
        }
    }

    private sealed class InterruptedReplicaResponseHandler(
        HttpMessageHandler inner, byte[] archive, string sha256, string signatureB64) : DelegatingHandler(inner)
    {
        private bool _interrupted;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (_interrupted || request.RequestUri?.AbsolutePath != "/api/blind/replica")
                return base.SendAsync(request, ct);

            _interrupted = true;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ThrowAfterBytesStream(new MemoryStream(archive, writable: false), 1024))
            };
            response.Headers.TryAddWithoutValidation("X-BMB-Package-Sha256", sha256);
            response.Headers.TryAddWithoutValidation("X-BMB-Snapshot-Signature", signatureB64);
            response.Content.Headers.ContentLength = archive.LongLength;
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowAfterBytesStream(Stream inner, long beforeFailure) : Stream
    {
        private long _remaining = beforeFailure;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => ReadCore(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) => ReadCore(buffer);

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            await ReadAsync(buffer.AsMemory(offset, count), ct);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_remaining <= 0) throw new IOException("simulated killed response body");
            var count = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], ct);
            _remaining -= count;
            return count;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        private int ReadCore(Span<byte> buffer)
        {
            if (_remaining <= 0) throw new IOException("simulated killed response body");
            var count = inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
            _remaining -= count;
            return count;
        }
    }

    private sealed class SeedSigner(byte[] seed) : INodeAuthSigner
    {
        public byte[] SignChallenge(NodeIdentity identity, byte[] challengePayload) => Ed25519Signer.Sign(seed, challengePayload);
    }
}
