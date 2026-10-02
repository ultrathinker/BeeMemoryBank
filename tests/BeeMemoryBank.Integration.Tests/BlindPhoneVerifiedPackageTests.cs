using System.Net;
using System.Net.Http.Headers;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Api.Services;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The package a backup carries is downloaded fresh and verified like a replica, but never installed
/// (stage 4: a restore needs the signed anchor events of the package's anchors, which a phone only
/// holds for anchors that arrived after its own first load). Real API host, real package, real signature.
/// </summary>
public sealed class BlindPhoneVerifiedPackageTests : IDisposable
{
    private readonly BlindNodeFactory _blind = new();

    public void Dispose() => _blind.Dispose();

    [Fact]
    public async Task FetchVerified_ReturnsTheServedArchiveAndItsSignature_AndInstallsNothing()
    {
        var anchorId = await AddAnchorAsync("2026-09-01T00:00:00.0000000Z");
        var phone = await CreatePhoneAsync();
        var work = Path.Combine(phone.DataPath, "replica");
        using var http = new HttpClient(_blind.Server.CreateHandler());

        var fetched = await phone.Client.FetchVerifiedPackageAsync(http, phone.Target, work, progress: null, CancellationToken.None);

        var served = await _blind.Services.GetRequiredService<BlindReplicaPackageCache>()
            .GetAsync(producerIsSuperadmin: false, CancellationToken.None);
        fetched.Sha256.Should().Be(served.Sha256);
        (await File.ReadAllBytesAsync(fetched.ArchivePath)).Should().Equal(await File.ReadAllBytesAsync(served.FilePath),
            "the signature covers exactly these bytes, so a backup must carry them untouched");
        fetched.Signature.Should().Equal(await File.ReadAllBytesAsync(served.FilePath + ".sig"));
        fetched.AnchorIds.Should().Equal([anchorId], "the package's anchor rows are what the backup gate looks for events of");
        (await new SyncPositionRepository(phone.Factory).GetAsync(phone.Target.NodeId)).Should().BeNull(
            "a download for a backup must not move the phone's receive cursor: nothing was installed");
        Directory.GetFiles(phone.DataPath, "beememorybank.db.before-replica-*").Should().BeEmpty();
        Directory.GetDirectories(work, "*verified*").Should().BeEmpty("the extraction used for checking is not kept");
        File.Exists(Path.Combine(work, "backup-package.part")).Should().BeFalse();
        File.Exists(Path.Combine(work, "backup-package.part.json")).Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    public async Task FetchVerified_CountsTheActiveRecoveryBoxesOfThePackage(int active, int expected)
    {
        await AddAnchorAsync("2026-09-01T00:00:00.0000000Z");
        for (var i = 0; i < active; i++) await AddBoxAsync($"box-{i}", "A");
        await AddBoxAsync("box-retired", "R");
        var phone = await CreatePhoneAsync();
        var work = Path.Combine(phone.DataPath, "replica");
        using var http = new HttpClient(_blind.Server.CreateHandler());

        var fetched = await phone.Client.FetchVerifiedPackageAsync(http, phone.Target, work, progress: null, CancellationToken.None);

        fetched.RecoveryBoxes.Should().Be(expected, "a restore opens the package's boxes with the master password; a retired one does not count");
    }

    [Fact]
    public async Task FetchVerified_DoesNotTouchTheInstallPartialOrItsBackOff()
    {
        var phone = await CreatePhoneAsync();
        var work = Path.Combine(phone.DataPath, "replica");
        Directory.CreateDirectory(work);
        await File.WriteAllBytesAsync(Path.Combine(work, "replica.part"), [1, 2, 3]);
        await File.WriteAllTextAsync(Path.Combine(work, "replica.part.json"), "{}");
        using var http = new HttpClient(_blind.Server.CreateHandler());

        await phone.Client.FetchVerifiedPackageAsync(http, phone.Target, work, progress: null, CancellationToken.None);

        (await File.ReadAllBytesAsync(Path.Combine(work, "replica.part"))).Should().Equal(new byte[] { 1, 2, 3 },
            "the first load's resumable state is not the backup's");
        File.ReadAllText(Path.Combine(work, "replica.part.json")).Should().Be("{}");
    }

    [Theory]
    [InlineData("X-BMB-Package-Sha256", "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("X-BMB-Snapshot-Signature", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==")]
    public async Task FetchVerified_ATamperedPackage_IsRefused_LeavesNoPartial_AndBacksOffWithoutAnotherDownload(string header, string value)
    {
        var phone = await CreatePhoneAsync();
        var work = Path.Combine(phone.DataPath, "replica");
        var counting = new CountingHandler(new RewriteHeaderHandler(_blind.Server.CreateHandler(), header, value));
        using var http = new HttpClient(counting);

        var act = () => phone.Client.FetchVerifiedPackageAsync(http, phone.Target, work, progress: null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(work, "backup-package.part")).Should().BeFalse("a complete archive that did not verify is not resumable");
        File.Exists(Path.Combine(work, "backup-package.part.json")).Should().BeFalse();
        var replicaRequests = counting.ReplicaRequests;
        replicaRequests.Should().BeGreaterThan(0);

        var again = () => phone.Client.FetchVerifiedPackageAsync(http, phone.Target, work, progress: null, CancellationToken.None);

        await again.Should().ThrowAsync<InvalidDataException>().WithMessage("*next attempt after*");
        counting.ReplicaRequests.Should().Be(replicaRequests, "a package that failed verification is not downloaded again within the back-off");
        File.Exists(Path.Combine(work, "replica.install-failed.json")).Should().BeFalse(
            "a bad backup download must not block the first load's own back-off record");
    }

    [Fact]
    public async Task FetchVerified_AnInterruptedDownload_ResumesWithARange_FromItsOwnPartial()
    {
        var phone = await CreatePhoneAsync();
        var work = Path.Combine(phone.DataPath, "replica");
        var served = await _blind.Services.GetRequiredService<BlindReplicaPackageCache>()
            .GetAsync(producerIsSuperadmin: false, CancellationToken.None);
        var archive = await File.ReadAllBytesAsync(served.FilePath);
        var signature = Convert.ToBase64String(await File.ReadAllBytesAsync(served.FilePath + ".sig"));
        using (var broken = new HttpClient(new KillAfterBytesHandler(_blind.Server.CreateHandler(), archive, served.Sha256, signature, 1024)))
        {
            var first = () => phone.Client.FetchVerifiedPackageAsync(broken, phone.Target, work, progress: null, CancellationToken.None);
            await first.Should().ThrowAsync<IOException>();
        }
        new FileInfo(Path.Combine(work, "backup-package.part")).Length.Should().BeGreaterThan(0);
        var counting = new CountingHandler(_blind.Server.CreateHandler());
        using var http = new HttpClient(counting);

        var fetched = await phone.Client.FetchVerifiedPackageAsync(http, phone.Target, work, progress: null, CancellationToken.None);

        counting.RangeRequests.Should().Be(1, "the second attempt continues the bytes it already has");
        fetched.Sha256.Should().Be(served.Sha256);
    }

    [Fact]
    public async Task FetchVerified_StopsBeforeAnyByteIsWritten_WhenTheCallerHasNoRoomForThePackage()
    {
        var phone = await CreatePhoneAsync();
        var work = Path.Combine(phone.DataPath, "replica");
        var served = await _blind.Services.GetRequiredService<BlindReplicaPackageCache>()
            .GetAsync(producerIsSuperadmin: false, CancellationToken.None);
        long seen = -1;
        using var http = new HttpClient(_blind.Server.CreateHandler());

        var act = () => phone.Client.FetchVerifiedPackageAsync(http, phone.Target, work, progress: null, CancellationToken.None,
            beforeDownload: length =>
            {
                seen = length;
                throw new IOException("no room");
            });

        await act.Should().ThrowAsync<IOException>().WithMessage("no room");
        seen.Should().Be(served.Length, "the caller is told the package size once the response headers are in");
        File.Exists(Path.Combine(work, "backup-package.part")).Should().BeFalse("nothing was written");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private async Task AddBoxAsync(string id, string status)
    {
        using var conn = _blind.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_recovery_box (box_id, kind, author_node_id, dek_fingerprint, epoch_hint, kdf_preset, salt, wrapped, iv, created_at, status, lamport_ts)
              VALUES (@Id, 'strong', 'a1', 'fp', 1, 's512t6', x'01', x'02', x'03', '2026-09-01T00:00:00Z', @Status, 1)",
            new { Id = id, Status = status });
    }

    private async Task<string> AddAnchorAsync(string createdAt)
    {
        var id = Guid.NewGuid().ToString();
        using var conn = _blind.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_state_anchor (anchor_id, author_node_id, dek_fingerprint, position_vector, digest, hmac, created_at, lamport_ts)
              VALUES (@Id, @Author, 'fp', '{}', 'digest', 'hmac', @At, 1)",
            new { Id = id, Author = Guid.NewGuid().ToString(), At = createdAt });
        return id;
    }

    private async Task<Phone> CreatePhoneAsync()
    {
        var phoneData = Path.Combine(_blind.DataPath, "phone");
        var factory = new DbConnectionFactory(Path.Combine(phoneData, "beememorybank.db"));
        await new MigrationRunner(factory).RunMigrationsAsync();
        var (phonePublic, phoneSeed) = Ed25519Signer.GenerateKeyPair();
        var phoneId = BlindNodeId.NewId();
        var identity = new NodeIdentity
        {
            NodeId = phoneId, DisplayName = "phone", Ed25519PublicKey = phonePublic, Ed25519PrivateKey = [],
            Ed25519PrivateKeyV = NodeIdentityCrypto.ExternalKeyVersion, CanGenerateEmbeddings = false, CreatedAt = DateTime.UtcNow
        };
        await new NodeIdentityRepository(factory).CreateAsync(identity);
        await _blind.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = phoneId, DisplayName = "phone", Ed25519PublicKey = phonePublic, Status = WhitelistStatuses.Active,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        var server = (await _blind.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var target = BlindCallCode.Create(BlindNodeFactory.PublicAddress, server.NodeId,
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", server.Ed25519PublicKey, new byte[32]);
        var client = new BlindPhoneReplicaClient(factory, new NodeIdentityRepository(factory), new SeedSigner(phoneSeed),
            phoneData, Path.Combine(phoneData, "beememorybank.db"), NullLogger<BlindPhoneReplicaClient>.Instance);
        return new Phone(phoneData, factory, target, client);
    }

    private sealed record Phone(string DataPath, DbConnectionFactory Factory, BlindCallCode Target, BlindPhoneReplicaClient Client);

    private sealed class SeedSigner(byte[] seed) : INodeAuthSigner
    {
        public byte[] SignChallenge(NodeIdentity identity, byte[] challengePayload) => Ed25519Signer.Sign(seed, challengePayload);
    }

    private sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public int ReplicaRequests { get; private set; }
        public int RangeRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri?.AbsolutePath == "/api/blind/replica")
            {
                ReplicaRequests++;
                if (request.Headers.Range is not null) RangeRequests++;
            }
            return base.SendAsync(request, ct);
        }
    }

    private sealed class RewriteHeaderHandler(HttpMessageHandler inner, string header, string value) : DelegatingHandler(inner)
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

    private sealed class KillAfterBytesHandler(HttpMessageHandler inner, byte[] archive, string sha256, string signatureB64, long beforeFailure)
        : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri?.AbsolutePath != "/api/blind/replica") return base.SendAsync(request, ct);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ThrowAfterBytesStream(new MemoryStream(archive, writable: false), beforeFailure))
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

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

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
    }
}
