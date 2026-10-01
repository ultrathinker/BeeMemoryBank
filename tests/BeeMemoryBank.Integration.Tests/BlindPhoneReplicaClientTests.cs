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

    private async Task<PhoneSetup> CreatePhoneAsync()
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
            phoneData, phoneDb, NullLogger<BlindPhoneReplicaClient>.Instance);
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

    private sealed class SeedSigner(byte[] seed) : INodeAuthSigner
    {
        public byte[] SignChallenge(NodeIdentity identity, byte[] challengePayload) => Ed25519Signer.Sign(seed, challengePayload);
    }
}
