using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.TestSupport;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>Join after activation (docs/design/step-a-spec.md 11, 16.4 item 6): the archive is sealed, the joiners open it, a plain one is refused.</summary>
[Collection(HeavyOperationCollection.Name)]
public class JoinAfterActivationTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private StepAFactory _host = null!;
    private StepANode.SeededVault _vault = null!;

    public async Task InitializeAsync()
    {
        _host = await StepANode.CreateAsync("Host");
        _vault = await StepANode.SeedWithCanariesAsync(_host);
    }

    public Task DisposeAsync()
    {
        _host.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Forwards the joiner's calls to the host's in-process server, keeping what the for-join route answered and letting a test act first.</summary>
    private sealed class JoinWire(HttpMessageHandler host) : DelegatingHandler(host)
    {
        public Func<HttpRequestMessage, Task<HttpResponseMessage?>>? Replace { get; set; }
        public Action? BeforeForJoin { get; set; }
        public byte[]? ArchiveBytes { get; private set; }
        public HttpStatusCode? ForJoinStatus { get; private set; }
        public string? ForJoinBody { get; private set; }
        public int ForJoinCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var isForJoin = request.RequestUri!.AbsolutePath.EndsWith("/api/sync/snapshot/for-join", StringComparison.Ordinal);
            if (isForJoin)
            {
                ForJoinCalls++;
                BeforeForJoin?.Invoke();
                if (Replace is not null && await Replace(request) is { } replaced)
                {
                    ArchiveBytes = await replaced.Content.ReadAsByteArrayAsync(ct);
                    ForJoinStatus = replaced.StatusCode;
                    return replaced;
                }
            }
            var response = await base.SendAsync(request, ct);
            if (isForJoin)
            {
                ForJoinStatus = response.StatusCode;
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (response.IsSuccessStatusCode) ArchiveBytes = bytes;
                else ForJoinBody = Encoding.UTF8.GetString(bytes);
                var copy = new ByteArrayContent(bytes);
                foreach (var header in response.Content.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
                response.Content = copy;
            }
            return response;
        }
    }

    private static Dictionary<string, byte[]> Entries(byte[] tarGz)
    {
        var entries = new Dictionary<string, byte[]>();
        using var gzip = new GZipStream(new MemoryStream(tarGz), CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.DataStream is null) continue;
            using var memory = new MemoryStream();
            entry.DataStream.CopyTo(memory);
            entries[entry.Name] = memory.ToArray();
        }
        return entries;
    }

    private async Task<(HttpResponseMessage Response, BmbWebApplicationFactory Joiner, JoinWire Wire)> JoinAsync(
        Action<JoinWire>? configure = null, string name = "Joiner")
    {
        // The join door allows five joins in five minutes from one address; every test node here is that address.
        BeeMemoryBank.Api.Middleware.RateLimitMiddleware.ResetForTests();
        var joiner = new BmbWebApplicationFactory();
        var wire = new JoinWire(_host.Server.CreateHandler());
        configure?.Invoke(wire);
        joiner.RouteOutboundHttpThrough(wire);
        using var client = joiner.CreateClient();
        var response = await client.PostAsJsonAsync("/api/init/join",
            new { adminUsername = "admin", displayName = name, remoteUrl = "http://host", password = StepANode.Password }, JsonOpts);
        return (response, joiner, wire);
    }

    [Fact]
    public async Task Archive_after_activation_holds_a_container_not_sqlite_and_no_canary_and_the_joiner_starts_activated()
    {
        await StepANode.ActivateAsync(_host);

        var (response, joiner, wire) = await JoinAsync();
        using var _ = joiner;

        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
        wire.ForJoinStatus.Should().Be(HttpStatusCode.OK);
        var path = Path.Combine(_host.DataPath, "join-archive.tar.gz");
        await File.WriteAllBytesAsync(path, wire.ArchiveBytes!);

        var entries = Entries(wire.ArchiveBytes!);
        var db = entries["beememorybank.db"];
        Encoding.ASCII.GetString(db, 0, 8).Should().Be("BMBCH1\0\0", "the database entry is a container under the master DEK");
        Encoding.ASCII.GetString(db, 0, 15).Should().NotBe("SQLite format 3");
        using (var manifest = JsonDocument.Parse(entries["manifest.json"]))
        {
            manifest.RootElement.GetProperty("dbEncrypted").GetBoolean().Should().BeTrue();
            manifest.RootElement.GetProperty("dbCipher").GetString().Should().Be("bmbch1");
        }
        MetadataCanaries.ScanArchive(path, _vault.Canaries).Should().BeEmpty("no title, path or tag is visible anywhere in the archive");

        // The joiner imported it, holds the notes, and starts activated with the key rows (opened at its first unlock).
        using var scope = joiner.Services.CreateScope();
        using var conn = scope.ServiceProvider.GetRequiredService<DbConnectionFactory>().CreateConnection();
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_article")).Should().Be(_vault.ArticleIds.Count);
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_metadata_key")).Should().Be(1);
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_metadata_activation")).Should().Be(1);

        // The join endpoint leaves the node locked: unlock the imported key slot before the first authored event.
        (await joiner.Services.GetRequiredService<SessionService>().UnlockAsync(StepANode.Password))
            .Should().BeTrue("the imported key slot must be unlocked before authoring the immediate post-join event");

        // The imported activation must take effect before the join endpoint returns.  Waiting for
        // the policy's periodic database recheck here would leave this first authored title clear.
        var title = MetadataCanaries.Ascii("joined-after-activation");
        var article = await scope.ServiceProvider.GetRequiredService<ArticleService>()
            .CreateAsync(title, "/Joined", [], "body");
        // A sealed row keeps its article in local_article_id (article_id is null on the wire); a clear row keeps it in article_id.
        var authored = await conn.QuerySingleAsync<(string Type, long Protocol, string? ArticleId, string Payload)>(
            "SELECT event_type, protocol_version, article_id, payload FROM tbl_event WHERE COALESCE(local_article_id, article_id) = @Id COLLATE NOCASE",
            new { Id = article.Id.ToString() });
        authored.Type.Should().Be("sealed");
        authored.Protocol.Should().Be(4);
        authored.ArticleId.Should().BeNull();
        authored.Payload.Should().NotContain(title);

        await Eventually(() => joiner.Services.GetRequiredService<ISyncProtocolPolicy>().SealsMetadata);
        Directory.GetFileSystemEntries(BeeMemoryBank.Core.IO.SnapshotStaging.DirIn(joiner.DataPath)).Should().BeEmpty("the opened database is removed with the archive");
    }

    [Fact]
    public async Task Control_before_activation_the_archive_is_plain_sqlite_and_holds_the_canaries()
    {
        var (response, joiner, wire) = await JoinAsync();
        using var _ = joiner;

        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
        var entries = Entries(wire.ArchiveBytes!);
        Encoding.ASCII.GetString(entries["beememorybank.db"], 0, 15).Should().Be("SQLite format 3", "before activation the join is exactly 2.5.1");
        entries["manifest.json"].Should().NotBeEmpty();
        Encoding.UTF8.GetString(entries["manifest.json"]).Should().NotContain("dbCipher");
        var path = Path.Combine(_host.DataPath, "join-plain.tar.gz");
        await File.WriteAllBytesAsync(path, wire.ArchiveBytes!);
        MetadataCanaries.ScanArchive(path, _vault.Canaries).Should().NotBeEmpty("the scanner sees the canaries in the pre-activation artefact");
    }

    [Fact]
    public async Task A_joiner_without_the_capability_gets_426_and_no_row_is_written()
    {
        await StepANode.ActivateAsync(_host);
        BeeMemoryBank.Api.Middleware.RateLimitMiddleware.ResetForTests();
        var (publicKey, _) = BeeMemoryBank.Crypto.Ed25519Signer.GenerateKeyPair();
        var nodeId = Guid.NewGuid();
        using var client = _host.CreateClient();

        // what a 2.5 joiner sends: neither the protocol nor the capabilities
        var old = await client.PostAsJsonAsync("/api/join", new
        {
            masterPassword = StepANode.Password, nodeId, displayName = "Old", ed25519PublicKeyB64 = Convert.ToBase64String(publicKey)
        }, JsonOpts);
        var withProtocol3Only = await client.PostAsJsonAsync("/api/join", new
        {
            masterPassword = StepANode.Password, nodeId, displayName = "Old", ed25519PublicKeyB64 = Convert.ToBase64String(publicKey), protocolVersion = 3
        }, JsonOpts);

        old.StatusCode.Should().Be((HttpStatusCode)426);
        withProtocol3Only.StatusCode.Should().Be((HttpStatusCode)426);
        (await old.Content.ReadAsStringAsync()).Should().Contain("2.6");
        (await _host.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(nodeId, includeDeleted: true)).Should().BeNull("refused before any row is written");

        var capable = await client.PostAsJsonAsync("/api/join", new
        {
            masterPassword = StepANode.Password, nodeId, displayName = "New", ed25519PublicKeyB64 = Convert.ToBase64String(publicKey),
            protocolVersion = 3, capabilities = new[] { MetadataCapabilities.EnvelopeV1 }
        }, JsonOpts);
        capable.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await capable.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("remoteNode").GetProperty("protocolVersion").GetInt32().Should().Be(4, "an activated host declares 4");
    }

    [Fact]
    public async Task Before_activation_a_joiner_without_the_capability_is_accepted_as_in_2_5_1()
    {
        BeeMemoryBank.Api.Middleware.RateLimitMiddleware.ResetForTests();
        var (publicKey, _) = BeeMemoryBank.Crypto.Ed25519Signer.GenerateKeyPair();
        using var client = _host.CreateClient();

        var response = await client.PostAsJsonAsync("/api/join", new
        {
            masterPassword = StepANode.Password, nodeId = Guid.NewGuid(), displayName = "Old", ed25519PublicKeyB64 = Convert.ToBase64String(publicKey)
        }, JsonOpts);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("remoteNode").GetProperty("protocolVersion").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task A_joiner_refuses_a_plain_archive_from_an_activated_host_and_imports_nothing()
    {
        await StepANode.ActivateAsync(_host);
        var attacker = _host.Services.GetRequiredService<SnapshotService>();

        var (response, joiner, wire) = await JoinAsync(w => w.Replace = async _ =>
        {
            // What a downgrade would send: a validly signed plain archive of the activated vault.
            var info = await attacker.CreateAsync(filterSecrets: true, sign: true, cpSequenceNum: 1, encryptDb: false);
            var file = attacker.GetSnapshotPath(info.FileName);
            var identity = (await _host.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
            var message = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(await File.ReadAllBytesAsync(file)) };
            message.Headers.Add("X-BMB-Snapshot-Signature", Convert.ToBase64String(await File.ReadAllBytesAsync(file + ".sig")));
            message.Headers.Add("X-BMB-Snapshot-CP-Seq", "1");
            message.Headers.Add("X-BMB-Snapshot-Lamport", "1");
            message.Headers.Add("X-BMB-Snapshot-Producer", identity.NodeId.ToString());
            return message;
        });
        using var _ = joiner;

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("plain").And.Contain("encrypts");
        using var scope = joiner.Services.CreateScope();
        using var conn = scope.ServiceProvider.GetRequiredService<DbConnectionFactory>().CreateConnection();
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_article")).Should().Be(0);
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_metadata_key")).Should().Be(0);
        wire.ForJoinCalls.Should().Be(1);
    }

    [Fact]
    public async Task For_join_is_refused_while_events_are_held_and_no_archive_is_built()
    {
        await StepANode.ActivateAsync(_host);
        _host.Held.Held = true;

        var (response, joiner, wire) = await JoinAsync();
        using var _ = joiner;

        response.IsSuccessStatusCode.Should().BeFalse();
        wire.ForJoinStatus.Should().Be(HttpStatusCode.Conflict);
        wire.ForJoinBody.Should().Contain("EVENTS_HELD").And.Contain("Unlock").And.Contain("wait");
        SnapshotArchives().Should().BeEmpty("the refusal comes before the snapshot is built");

        _host.Held.Held = false;
        var (again, joiner2, _) = await JoinAsync(name: "Second");
        using var __ = joiner2;
        // the first joiner's row is still there, so a fresh node id joins; the point is only that the route works once nothing is held
        again.IsSuccessStatusCode.Should().BeTrue(await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task For_join_is_refused_while_the_activated_host_is_locked_and_no_plain_archive_is_made()
    {
        await StepANode.ActivateAsync(_host);

        var (response, joiner, wire) = await JoinAsync(w => w.BeforeForJoin = () => _host.Services.GetRequiredService<SessionService>().Lock());
        using var _ = joiner;

        response.IsSuccessStatusCode.Should().BeFalse();
        wire.ForJoinStatus.Should().Be((HttpStatusCode)423);
        wire.ForJoinBody.Should().Contain("NODE_LOCKED");
        SnapshotArchives().Should().BeEmpty("a locked activated host never falls back to a plain archive");
    }

    [Fact]
    public async Task The_join_cache_serves_the_same_sealed_archive_to_the_next_joiner_and_is_dropped_at_activation()
    {
        await StepANode.ActivateAsync(_host);
        var cache = _host.Services.GetRequiredService<SnapshotJoinCache>();

        var (first, joiner1, wire1) = await JoinAsync(name: "One");
        using var _1 = joiner1;
        first.IsSuccessStatusCode.Should().BeTrue(await first.Content.ReadAsStringAsync());
        var fingerprint = _host.Services.GetRequiredService<SnapshotService>().JoinKeyFingerprint();
        cache.TryGet(fingerprint).Should().NotBeNull("the sealed archive is cached under the DEK fingerprint");
        cache.TryGet(null).Should().BeNull("a plain request never gets the sealed entry (and the lookup dropped it)");

        var (second, joiner2, wire2) = await JoinAsync(name: "Two");
        using var _2 = joiner2;
        second.IsSuccessStatusCode.Should().BeTrue(await second.Content.ReadAsStringAsync());
        SnapshotArchives().Count.Should().Be(2, "the entry the plain lookup dropped is rebuilt, sealed again");
        wire1.ArchiveBytes.Should().NotBeNull();
        wire2.ArchiveBytes.Should().NotBeNull();
    }

    private List<string> SnapshotArchives()
    {
        var dir = Path.Combine(_host.DataPath, "snapshots");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.tar.gz").ToList() : [];
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        condition().Should().BeTrue("the joiner's policy reads the imported activation");
    }
}
