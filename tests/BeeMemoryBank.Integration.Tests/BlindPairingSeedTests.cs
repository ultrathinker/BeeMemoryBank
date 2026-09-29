using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Api.Endpoints;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.BlindBackup;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Plan 4.2-4.3 and 5.2 end to end: a PC adds a blind node by its pair code and seeds it; the
/// blind node takes the seed only with the code's secret, one at a time, checked by hash and
/// signature, keeping its previous database; a reseed first takes in what the blind node has and
/// the blind node replays what reached it after the package's cut-off. The PC's outbound calls
/// are routed into the blind node's test host (no TLS there — the pin itself is covered by
/// SpkiPinRegistryTests).
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class BlindPairingSeedTests : IAsyncLifetime
{
    private const string Password = "blindPairingPw1!";
    // A short drain wait: the timeout path has to be provable, and proving it against the
    // production minute would mean a minute of wall clock per run. The test that holds a
    // connection across the drain holds it for longer than this; the one that releases it does so
    // after 2 s, well inside it.
    private readonly BlindNodeFactory _blind = new(drainWaitSeconds: 10);
    private readonly BmbWebApplicationFactory _pc = new();
    private HttpClient _pcClient = null!;

    public async Task InitializeAsync()
    {
        _ = _blind.Services; // start it: identity, certificate
        _pc.RouteOutboundHttpThrough(_blind.Server.CreateHandler());
        // The PC's first seed uses the pair code, which makes it the blind node's superadmin and reseed
        // authority (review r1-merge #3; BlindRootBootstrapTests).
        await _pc.InitializeNodeAsync("PC", Password);
        _pcClient = _pc.CreateClient();
        (await _pcClient.PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password }))
            .EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _pcClient.Dispose();
        _pc.Dispose();
        _blind.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AddByCode_SeedsTheBlindNode_PinsItsKey_AndSpendsTheCode()
    {
        using (var scope = _pc.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync("From the PC", "/Notes", [], "body");
        var blindId = (await IdentityAsync(_blind)).NodeId;
        var code = await PairCodeAsync();

        var add = await _pcClient.PostAsJsonAsync("/api/blind-nodes/", new { code = code.ToString() });
        add.StatusCode.Should().Be(HttpStatusCode.OK, await add.Content.ReadAsStringAsync());

        // The blind node now holds the PC's content and trusts the PC — under its own identity.
        (await IdentityAsync(_blind)).NodeId.Should().Be(blindId);
        (await ScalarAsync(_blind, "SELECT COUNT(*) FROM tbl_article WHERE title = 'From the PC'")).Should().Be(1);
        var pcId = (await IdentityAsync(_pc)).NodeId;
        var pcRow = await _blind.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(pcId);
        pcRow.Should().NotBeNull();
        pcRow!.IsSuperadmin.Should().BeTrue("the PC used the pair code");
        File.Exists(Path.Combine(_blind.DataPath, "beememorybank.db.pre-seed")).Should().BeFalse("the previous database is wiped once the seed is committed");

        // The PC trusts the blind node's key and pushes from the package's checkpoint on.
        var blindRow = await _pc.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(blindId);
        blindRow!.TlsSpki.Should().Be(code.TlsSpki);
        blindRow.ApiAddress.Should().Be(BlindNodeFactory.PublicAddress);
        var pushed = await _pc.Services.GetRequiredService<ISyncPushPositionRepository>().GetAsync(blindId);
        pushed.Should().NotBeNull();
        pushed!.LastPushedSeq.Should().BeGreaterThan(0);

        // One seed per code.
        var again = await _pcClient.PostAsJsonAsync("/api/blind-nodes/", new { code = code.ToString() });
        again.IsSuccessStatusCode.Should().BeFalse("the secret was spent by the first seed");
    }

    [Fact]
    public async Task Seed_WithoutOrWithAWrongSecret_IsRefused()
    {
        await PairCodeAsync();
        using var http = _blind.Server.CreateClient();
        var url = $"/api/blind/seed?seedId={Guid.NewGuid()}&offset=0&total=10&sha256={new string('0', 64)}";

        (await http.PostAsync(url, new ByteArrayContent(new byte[10]))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var wrong = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(new byte[10]) };
        await AddSeederAsync(wrong, new string('a', 64), Guid.Parse(url.Split('=')[1].Split('&')[0]));
        (await http.SendAsync(wrong)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>Plan 4.2 step 1: a code whose key is not the one the node at its address has is refused.</summary>
    [Fact]
    public async Task ACodeThatDoesNotMatchTheNodeAtItsAddress_IsRefused_AndNothingIsAdded()
    {
        var code = await PairCodeAsync() with { PublicKeyB64 = Convert.ToBase64String(Ed25519Signer.GenerateKeyPair().publicKey) };

        var add = await _pcClient.PostAsJsonAsync("/api/blind-nodes/", new { code = code.ToString() });

        add.IsSuccessStatusCode.Should().BeFalse();
        (await _pc.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(code.NodeId))
            .Should().BeNull("nothing is published about a node that failed the check");
    }

    /// <summary>Plan 4.3: a valid package under a hash other than the one announced is not applied.</summary>
    [Fact]
    public async Task AValidPackage_WithAnotherAnnouncedHash_IsRefused()
    {
        var code = await PairCodeAsync();
        BlindPackage package;
        using (var scope = _pc.Services.CreateScope())
            package = await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
                .BuildAsync(Guid.NewGuid(), includesUpTo: null, producerIsSuperadmin: true);
        var bytes = await File.ReadAllBytesAsync(package.FilePath);
        using var http = _blind.Server.CreateClient();

        var resp = await PartAsync(http, code, package.Manifest.SeedId, 0, bytes, 0, bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(new byte[1])));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        File.Exists(Path.Combine(_blind.DataPath, "beememorybank.db.pre-seed")).Should().BeFalse("nothing was swapped in");
    }

    /// <summary>Plan 5.2: a reseed must be signed by the superadmin that sends it — not relayed from another node.</summary>
    [Fact]
    public async Task Reseed_WithAPackageBuiltByAnotherNode_IsRefused()
    {
        await AddBlindNodeAsync();
        var blind = await IdentityAsync(_blind);
        var (hubId, hubKey) = await TrustSuperadminOnBothAsync();
        BlindPackage package;
        using (var scope = _pc.Services.CreateScope())
            package = await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
                .BuildAsync(Guid.NewGuid(), includesUpTo: null, producerIsSuperadmin: true);
        var bytes = await File.ReadAllBytesAsync(package.FilePath);
        using var http = _blind.Server.CreateClient();
        var hubToken = _blind.Services.GetRequiredService<SyncTokenStore>().IssueToken(hubId, BeeMemoryBank.Sync.SyncProtocolVersion.Current);

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/blind/seed?seedId={package.Manifest.SeedId}&offset=0&total={bytes.Length}&sha256={package.Sha256}")
        { Content = new ByteArrayContent(bytes) };
        req.Headers.Authorization = new("Bearer", hubToken);
        var resp = await http.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the hub may reseed, but only with a package it built");
        blind.NodeId.Should().Be((await IdentityAsync(_blind)).NodeId);
    }

    /// <summary>
    /// Review L-stage1 #3: the first seed is verified against the key the pairing secret vouched for,
    /// never against a key the package names itself. A package another node built and signed — its
    /// own key in its own manifest — is refused although the PC's proof is valid.
    /// </summary>
    [Fact]
    public async Task FirstSeed_WithASelfSignedPackageOfAnotherNode_IsRefused()
    {
        var code = await PairCodeAsync();
        using var other = new BmbWebApplicationFactory();
        await other.InitializeNodeAsync("Other", Password);
        using (var otherClient = other.CreateClient())
            (await otherClient.PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password }))
                .EnsureSuccessStatusCode();
        BlindPackage package;
        using (var scope = other.Services.CreateScope())
            package = await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
                .BuildAsync(Guid.NewGuid(), includesUpTo: null, producerIsSuperadmin: true);

        var resp = await UploadWholeAsync(package, code.Secret);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        File.Exists(Path.Combine(_blind.DataPath, "beememorybank.db.pre-seed")).Should().BeFalse("nothing was swapped in");
    }

    /// <summary>
    /// Review L-stage1 #3: the MAC binds the key. Right secret, but the key sent is not the one the MAC
    /// was made over → refused before a byte is read; and a proof for some other node id, however
    /// valid, does not let the PC's package in either.
    /// </summary>
    [Fact]
    public async Task FirstSeed_UnderAKeyTheMacDoesNotCover_OrForAnotherSeeder_IsRefused()
    {
        var code = await PairCodeAsync();
        BlindPackage package;
        using (var scope = _pc.Services.CreateScope())
            package = await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
                .BuildAsync(Guid.NewGuid(), includesUpTo: null, producerIsSuperadmin: true);
        var pc = await IdentityAsync(_pc);
        var stranger = Ed25519Signer.GenerateKeyPair().publicKey;

        var swappedKey = await UploadWholeAsync(package, code.Secret, claimedKey: stranger, macOverKey: pc.Ed25519PublicKey);
        var otherSeeder = await UploadWholeAsync(package, code.Secret, claimedNodeId: Guid.NewGuid(), claimedKey: stranger);

        swappedKey.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        otherSeeder.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Review l-root2 #1: two uploads under the same proof at once. Both pass the endpoint's proof check
    /// before either has the upload gate; the first one through seeds and spends the code, and the second
    /// is refused under the gate — it neither switches the database again nor grants authority again.
    /// </summary>
    [Fact]
    public async Task TwoUploadsUnderTheSameProof_AtOnce_OnlyOneSeeds()
    {
        var code = await PairCodeAsync();
        BlindPackage package;
        using (var scope = _pc.Services.CreateScope())
            package = await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
                .BuildAsync(Guid.NewGuid(), includesUpTo: null, producerIsSuperadmin: true);

        var answers = await Task.WhenAll(UploadWholeAsync(package, code.Secret), UploadWholeAsync(package, code.Secret));

        answers.Select(a => a.StatusCode).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Unauthorized]);
    }

    /// <summary>
    /// Review l-root2 #1, the same race made deterministic: a proof checked while the code was current
    /// is presented to the seed service only after another seed spent the code.
    /// </summary>
    [Fact]
    public async Task AProofCheckedBeforeTheCodeWasSpent_IsRefusedAfterIt()
    {
        var code = await PairCodeAsync();
        var pc = await IdentityAsync(_pc);
        BlindPackage package;
        using (var scope = _pc.Services.CreateScope())
            package = await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
                .BuildAsync(Guid.NewGuid(), includesUpTo: null, producerIsSuperadmin: true);
        var keyB64 = Convert.ToBase64String(pc.Ed25519PublicKey);
        var codeId = await _blind.Services.GetRequiredService<BlindPairing>().VerifySeederAsync(package.Manifest.SeedId, pc.NodeId,
            keyB64, BlindSeederProof.Compute(code.Secret, package.Manifest.SeedId, pc.NodeId, pc.Ed25519PublicKey));
        codeId.Should().NotBeNull("precondition: the proof held when it was checked");
        (await UploadWholeAsync(package, code.Secret)).StatusCode.Should().Be(HttpStatusCode.OK);

        var bytes = await File.ReadAllBytesAsync(package.FilePath);
        var late = () => _blind.Services.GetRequiredService<BlindSeedService>().ReceiveAsync(package.Manifest.SeedId, 0, bytes.Length,
            package.Sha256, new BlindSeedAuthority.PairingSecret(pc.NodeId, keyB64, codeId!), new MemoryStream(bytes), CancellationToken.None);

        await late.Should().ThrowAsync<BlindSeedAuthorityGoneException>();
    }

    /// <summary>
    /// Review l-root4 #1: a connection to the live database opened before a reseed and still open when
    /// the package arrives. The switch waits for it instead of moving the file from under it (on Linux
    /// the connection would go on reading and writing the moved file); once it closes, the switch goes
    /// ahead, and a connection opened then reads the new database.
    /// </summary>
    [Fact]
    public async Task AConnectionOpenAcrossTheSwitch_IsWaitedFor_AndTheNextOneReadsTheNewDatabase()
    {
        await AddBlindNodeAsync();
        var live = _blind.Services.GetRequiredService<DbConnectionFactory>();
        var held = live.CreateConnection();
        await held.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_whitelist");
        // Only the new database has this peer: the PC adds it right before it cuts the package.
        var phone = Guid.NewGuid();
        await _pc.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = phone, DisplayName = "Phone", Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey,
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        var (http, request) = await ReseedRequestAsync(includesUpTo: 0);

        var reseed = http.SendAsync(request);
        await Task.Delay(TimeSpan.FromSeconds(2));

        reseed.IsCompleted.Should().BeFalse("the switch waits for the connection that still has the live database open");
        held.Dispose();
        var response = await reseed.WaitAsync(TimeSpan.FromSeconds(60));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await _blind.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(phone))
            .Should().NotBeNull("a connection opened after the switch reads the new database");
        http.Dispose();
    }

    /// <summary>
    /// Floor (Release A and B): after a reseed completes, no file of the blind node's data directory holds the database
    /// it replaced. That database is the whole vault under its old key (after a content re-key) and holds recovery
    /// material a purge removed; it used to stay as beememorybank.db.pre-seed and go into every copy of the volume.
    /// A random probe written only into the old database must be found in no file afterwards.
    /// </summary>
    [Fact]
    public async Task AfterAReseed_NoFileOfTheBlindDataDir_HoldsTheOldDatabase()
    {
        await AddBlindNodeAsync();
        var probe = RandomNumberGenerator.GetBytes(48);
        using (var conn = _blind.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
        {
            await conn.ExecuteAsync("INSERT INTO tbl_migration_marker (key, value, set_at) VALUES ('preseed-probe', @probe, 'now')", new { probe });
            await conn.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE)");
        }
        FilesHolding(_blind.DataPath, probe).Should().NotBeEmpty("the probe is in the old database before the reseed");
        var (http, request) = await ReseedRequestAsync(includesUpTo: 0);

        var response = await http.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        http.Dispose();
        FilesHolding(_blind.DataPath, probe).Should().BeEmpty("no file keeps a page of the old database");
        File.Exists(Path.Combine(_blind.DataPath, "beememorybank.db.pre-seed")).Should().BeFalse();
        Directory.Exists(BlindSeedCutover.DirOf(_blind.DataPath)).Should().BeFalse();
    }

    private static List<string> FilesHolding(string dir, byte[] needle) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f =>
            {
                try
                {
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var ms = new MemoryStream();
                    fs.CopyTo(ms);
                    return ms.GetBuffer().AsSpan(0, (int)ms.Length).IndexOf(needle) >= 0;
                }
                catch (IOException) { return false; }
            })
            .ToList();

    /// <summary>
    /// Review l-root5 #2: the other half of the switch's contract. A connection that NEVER closes
    /// must not be waited for indefinitely, and must not be worked around either: the seed fails
    /// closed with 400, the file is not switched, and the gate opens again so the node keeps
    /// serving. Shortening the wait is what makes this provable without a minute of wall clock.
    /// </summary>
    [Fact]
    public async Task AConnectionHeldPastTheDrainTimeout_FailsClosed_AndTheGateReopens()
    {
        await AddBlindNodeAsync();
        var live = _blind.Services.GetRequiredService<DbConnectionFactory>();
        using var held = live.CreateConnection();
        await held.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_whitelist");

        // Only the new database would have this peer: the PC adds it right before it cuts the package.
        var phone = Guid.NewGuid();
        await _pc.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = phone, DisplayName = "Phone", Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey,
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        var probe = await WriteProbeAsync();
        var (http, request) = await ReseedRequestAsync(includesUpTo: 0);

        var response = await http.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(90));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().Contain("stayed in use");
        (await _blind.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(phone))
            .Should().BeNull("the file was never switched, so the peer the package carried is not in the live database");
        (await HasProbeAsync(probe)).Should().BeTrue("the previous database was not touched");

        // The gate is released with the failed seed, not left set: the node goes on serving.
        // Scoped, because this connection is itself a holder: the retry below would wait for it and
        // time out too, which is the drain working rather than a bug in it.
        held.Dispose();
        using (var after = live.CreateConnection())
        {
            (await after.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_whitelist")).Should().BeGreaterThan(0);
        }

        // And a later seed of the same kind is not permanently refused.
        var (retryHttp, retry) = await ReseedRequestAsync(includesUpTo: 0);
        var retried = await retryHttp.SendAsync(retry).WaitAsync(TimeSpan.FromSeconds(90));
        retried.StatusCode.Should().Be(HttpStatusCode.OK, await retried.Content.ReadAsStringAsync());
        (await _blind.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(phone))
            .Should().NotBeNull("the retry switched the file once nothing held it");
        retryHttp.Dispose();
        http.Dispose();
    }

    /// <summary>
    /// Review l-root4 #2: the seeder of a first seed becomes the blind node's superadmin, so it must be
    /// a full node. Another blind node with a valid pairing proof for its own key and its own package is
    /// refused before anything is staged: nothing switched, no row for it.
    /// </summary>
    [Fact]
    public async Task ABlindNodeAsTheSeeder_IsRefusedBeforeStaging_AndGetsNoRow()
    {
        var code = await PairCodeAsync();
        using var other = new BlindNodeFactory();
        var otherIdentity = await IdentityAsync(other);
        BlindPackage package;
        using (var scope = other.Services.CreateScope())
            package = await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
                .BuildAsync(Guid.NewGuid(), includesUpTo: null, producerIsSuperadmin: false);
        var bytes = await File.ReadAllBytesAsync(package.FilePath);
        using var http = _blind.Server.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/blind/seed?seedId={package.Manifest.SeedId}&offset=0&total={bytes.Length}&sha256={package.Sha256}")
        { Content = new ByteArrayContent(bytes) };
        req.Headers.Add(BlindSeederProof.NodeIdHeader, otherIdentity.NodeId.ToString());
        req.Headers.Add(BlindSeederProof.KeyHeader, Convert.ToBase64String(otherIdentity.Ed25519PublicKey));
        req.Headers.Add(BlindSeederProof.MacHeader, BlindSeederProof.Compute(code.Secret, package.Manifest.SeedId,
            otherIdentity.NodeId, otherIdentity.Ed25519PublicKey));

        var resp = await http.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("cannot seed a blind node", "it is refused before staging");
        (await _blind.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(otherIdentity.NodeId, includeDeleted: true))
            .Should().BeNull();
        File.Exists(Path.Combine(_blind.DataPath, "beememorybank.db.pre-seed")).Should().BeFalse("nothing was switched");
    }

    /// <summary>Review L-stage1 #3: a first seed spends the code — its proof does not open a second one.</summary>
    [Fact]
    public async Task TheCode_IsSpentByTheFirstSeed()
    {
        var code = await PairCodeAsync();
        (await _pcClient.PostAsJsonAsync("/api/blind-nodes/", new { code = code.ToString() })).EnsureSuccessStatusCode();
        BlindPackage package;
        using (var scope = _pc.Services.CreateScope())
            package = await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
                .BuildAsync(Guid.NewGuid(), includesUpTo: null, producerIsSuperadmin: true);

        var again = await UploadWholeAsync(package, code.Secret);

        again.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task APackageAboveTheSizeLimit_IsRefusedBeforeAnyByte()
    {
        var code = await PairCodeAsync();
        using var http = _blind.Server.CreateClient();
        var seedId = Guid.NewGuid();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/blind/seed?seedId={seedId}&offset=0&total={BlindSeedService.DefaultMaxPackageBytes + 1}&sha256={new string('0', 64)}")
        { Content = new ByteArrayContent(new byte[1]) };
        await AddSeederAsync(req, code.Secret, seedId);

        (await http.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Directory.Exists(Path.Combine(_blind.DataPath, "blind-tmp")).Should().BeFalse("nothing was staged");
    }

    [Fact]
    public async Task RenewingTheCode_InvalidatesTheOldSecret()
    {
        var old = await PairCodeAsync();
        using var console = _blind.CreateClient();
        var renewed = BlindPairCode.Parse((await (await console.PostAsync("/api/blind/pair-code/renew", null))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!);

        var pairing = _blind.Services.GetRequiredService<BlindPairing>();
        var seedId = Guid.NewGuid();
        var (nodeId, key) = (Guid.NewGuid(), Ed25519Signer.GenerateKeyPair().publicKey);
        var keyB64 = Convert.ToBase64String(key);

        renewed.Secret.Should().NotBe(old.Secret);
        (await pairing.VerifySeederAsync(seedId, nodeId, keyB64, BlindSeederProof.Compute(old.Secret, seedId, nodeId, key)) is not null)
            .Should().BeFalse();
        (await pairing.VerifySeederAsync(seedId, nodeId, keyB64, BlindSeederProof.Compute(renewed.Secret, seedId, nodeId, key)) is not null)
            .Should().BeTrue();
    }

    /// <summary>Plan 5.2: only a peer this blind node marks superadmin may reseed it.</summary>
    [Fact]
    public async Task Reseed_ByAnOrdinaryPeer_IsRefused()
    {
        await AddBlindNodeAsync();
        var phone = Guid.NewGuid();
        await _blind.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = phone, DisplayName = "Phone", Ed25519PublicKey = new byte[32], Status = "A", IsSuperadmin = false,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        using var http = _blind.Server.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/blind/seed?seedId={Guid.NewGuid()}&offset=0&total=10&sha256={new string('0', 64)}")
        { Content = new ByteArrayContent(new byte[10]) };
        req.Headers.Authorization = new("Bearer", _blind.Services.GetRequiredService<SyncTokenStore>().IssueToken(phone, BeeMemoryBank.Sync.SyncProtocolVersion.Current));

        (await http.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Upload_IsOneSeedAtATime_Resumable_AndCheckedByHash()
    {
        var code = await PairCodeAsync();
        using var http = _blind.Server.CreateClient();
        var bytes = RandomNumberGenerator.GetBytes(1000);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var first = Guid.NewGuid();

        (await PartAsync(http, code, first, 0, bytes, 0, 400, sha)).StatusCode.Should().Be(HttpStatusCode.Accepted);

        // Another seed while this one is open: 409 naming the one in progress.
        var other = await PartAsync(http, code, Guid.NewGuid(), 0, bytes, 0, 400, sha);
        other.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await other.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("seed_id").GetGuid().Should().Be(first);

        // A part at the wrong offset: 409 with where to resume.
        var skipped = await PartAsync(http, code, first, 600, bytes, 600, 400, sha);
        skipped.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await skipped.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("received").GetInt64().Should().Be(400);

        // The rest, with a hash that does not match what arrives: refused, nothing applied.
        var badSha = Convert.ToHexStringLower(SHA256.HashData(new byte[1]));
        var tampered = await PartAsync(http, code, Guid.NewGuid(), 0, bytes, 0, 1000, badSha);
        tampered.StatusCode.Should().Be(HttpStatusCode.Conflict, "the first seed still holds the slot");
        var last = await PartAsync(http, code, first, 400, bytes, 400, 600, sha);
        last.StatusCode.Should().Be(HttpStatusCode.BadRequest, "random bytes are no blind package");
        File.Exists(Path.Combine(_blind.DataPath, "beememorybank.db.pre-seed")).Should().BeFalse();
    }

    /// <summary>
    /// Review L-merge #1: a part that dies half-way (a dropped connection) leaves uncounted bytes on
    /// disk. Its retry at the offset the node still reports is written there — over them, not after.
    /// </summary>
    [Fact]
    public async Task APartThatDiesHalfWay_IsRetriedAtItsOffset_NotAfterItsLeftovers()
    {
        var seeds = _blind.Services.GetRequiredService<BlindSeedService>();
        var bytes = RandomNumberGenerator.GetBytes(1000);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var who = new BlindSeedAuthority.PairingSecret(Guid.NewGuid(), Convert.ToBase64String(new byte[32]),
            BlindPairing.CodeIdOf((await PairCodeAsync()).Secret));
        var seedId = Guid.NewGuid();
        await seeds.ReceiveAsync(seedId, 0, bytes.Length, sha, who, new MemoryStream(bytes, 0, 400), CancellationToken.None);

        var dropped = () => seeds.ReceiveAsync(seedId, 400, bytes.Length, sha, who, new DropsAfter(bytes, 400, 150), CancellationToken.None);
        await dropped.Should().ThrowAsync<IOException>();
        seeds.GetProgress(seedId)!.Received.Should().Be(400, "precondition: the dead part was not counted");
        var retried = await seeds.ReceiveAsync(seedId, 400, bytes.Length, sha, who, new MemoryStream(bytes, 400, 300), CancellationToken.None);

        retried.Received.Should().Be(700);
        var onDisk = Directory.GetFiles(_blind.DataPath, "package.tar.gz", SearchOption.AllDirectories).Single();
        (await File.ReadAllBytesAsync(onDisk)).Should().Equal(bytes[..700], "the retried part starts where the node said it would");
    }

    /// <summary>A request body that delivers <c>take</c> bytes from <c>from</c>, then loses the connection.</summary>
    private sealed class DropsAfter(byte[] data, int from, int take) : Stream
    {
        private int _sent;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_sent >= take) throw new IOException("The connection was lost.");
            var n = Math.Min(count, take - _sent);
            Array.Copy(data, from + _sent, buffer, offset, n);
            _sent += n;
            return n;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_sent >= take) throw new IOException("The connection was lost.");
            var n = Math.Min(buffer.Length, take - _sent);
            data.AsMemory(from + _sent, n).CopyTo(buffer);
            _sent += n;
            return ValueTask.FromResult(n);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _sent; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Plan 5.2: a phone pushed something to the blind node that the PC never saw. Reseeding must
    /// not lose it: the PC pulls everything first and ships it back in the package.
    /// </summary>
    [Fact]
    public async Task Reseed_TakesInWhatOnlyTheBlindNodeHad()
    {
        await AddBlindNodeAsync();
        var blindId = (await IdentityAsync(_blind)).NodeId;
        var (hubId, hubKey) = await TrustSuperadminOnBothAsync();
        var onlyOnBlind = await ApplyOnBlindAsync(WhitelistAdd(hubId, hubKey, lamport: 1000, Guid.NewGuid(), "Phone"));

        var reseed = await _pcClient.PostAsync($"/api/blind-nodes/{blindId}/reseed", null);
        reseed.StatusCode.Should().Be(HttpStatusCode.OK, await reseed.Content.ReadAsStringAsync());

        (await _pc.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(onlyOnBlind))
            .Should().NotBeNull("the PC takes in everything the blind node has before it cuts the package");
        (await _blind.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(onlyOnBlind))
            .Should().NotBeNull("what reached only the blind node must survive its reseed");
    }

    /// <summary>
    /// F4 (live stand): the replay of the blind node's own tail has to be allowed to write what the
    /// event says — including creating a folder. Reached through the seeding request, it ran with the
    /// <i>seeding peer's</i> caller scope, which is deny-everything for folder paths: the apply was
    /// refused ("Write access denied for path"), classified permanent, and quarantined — so an event
    /// the blind held beyond the package (a phone push, or anything in a new folder) was lost on it
    /// for good. It now runs under the system scope, exactly like the sync apply it mirrors.
    /// </summary>
    [Fact]
    public async Task Reseed_ReplaysAnEventThatCreatesANewFolder()
    {
        await AddBlindNodeAsync();

        // An article both nodes hold, in a folder both of them have.
        var created = await _pcClient.PostAsJsonAsync("/api/articles",
            new { title = "Moving", treePath = "/Notes", content = "body" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var articleId = Guid.Parse((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
        await CopyBlobsAsync();
        var createdEvents = (await _pc.Services.GetRequiredService<IEventLogRepository>().GetAfterSequenceAsync(0))
            .Where(e => e.ArticleId == articleId).ToList();
        createdEvents.Should().NotBeEmpty("the article's own events are what the blind node takes in");
        foreach (var evt in createdEvents)
            await ApplyOnBlindRawAsync(evt);

        // The package is built here, before the move: its tables hold the article in /Notes and no
        // /Offline folder at all, so the move reaches the blind node only through the replay.
        var cutOff = await ScalarAsync(_blind, "SELECT MAX(sequence_num) FROM tbl_event");
        BlindPackage package;
        using (var scope = _pc.Services.CreateScope())
            package = await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
                .BuildAsync(Guid.NewGuid(), includesUpTo: cutOff, producerIsSuperadmin: true);

        // The move itself: an ordinary article event in a folder the package has never heard of.
        await _pc.Services.GetRequiredService<ArticleService>().MoveAsync(articleId, "/Offline");
        await CopyBlobsAsync();
        var move = (await _pc.Services.GetRequiredService<IEventLogRepository>().GetAfterSequenceAsync(0))
            .Where(e => e.ArticleId == articleId)
            .OrderByDescending(e => e.LamportTs)
            .First(e => e.EventType == EventTypes.ArticleUpdate);
        await ApplyOnBlindRawAsync(move);
        (await ScalarAsync(_blind, "SELECT COUNT(*) FROM tbl_event WHERE sequence_num > @cutOff", new { cutOff }))
            .Should().Be(1, "the move is the blind node's own tail, in no package");

        var (http, request) = await ReseedRequestAsync(cutOff, prebuilt: package);
        using (http)
        {
            var resp = await http.SendAsync(request);
            resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        }

        (await TextAsync(_blind, "SELECT tree_path FROM tbl_article WHERE id = @id", new { id = articleId }))
            .Should().Be("/Offline", "the replayed event creates its folder and lands — nothing the blind held beyond the package is lost");
        (await ScalarAsync(_blind, "SELECT COUNT(*) FROM tbl_sync_quarantine WHERE event_id = @id", new { id = move.EventId }))
            .Should().Be(0, "and it is not quarantined as something that can never apply");
    }

    /// <summary>
    /// Plan 5.2, the blind node's half: what arrived after the package's cut-off is in no package,
    /// so the blind node replays its own events after "includes up to X" onto the new database.
    /// </summary>
    [Fact]
    public async Task Reseed_ReplaysTheBlindNodesEventsAfterTheCutOff()
    {
        await AddBlindNodeAsync();
        var blind = await IdentityAsync(_blind);
        var (hubId, hubKey) = await TrustSuperadminOnBothAsync();
        await ApplyOnBlindAsync(WhitelistAdd(hubId, hubKey, lamport: 1000, Guid.NewGuid(), "Before"));
        var cutOff = await _blind.Services.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();
        var afterCutOff = await ApplyOnBlindAsync(WhitelistAdd(hubId, hubKey, lamport: 1001, Guid.NewGuid(), "After"));

        // A package that claims to include the blind node's log up to cutOff — built by the PC
        // without the later event, as a reseed racing a push would be.
        BlindPackage package;
        using (var scope = _pc.Services.CreateScope())
            package = await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
                .BuildAsync(Guid.NewGuid(), includesUpTo: cutOff, producerIsSuperadmin: true);
        using var http = _blind.Server.CreateClient();
        string token;
        using (var scope = _pc.Services.CreateScope())
            token = await PeerAuthenticator.AuthenticateAsync(scope.ServiceProvider.GetRequiredService<INodeAuthSigner>(),
                http, http.BaseAddress!.ToString(), await IdentityAsync(_pc), blind.NodeId);

        var bytes = await File.ReadAllBytesAsync(package.FilePath);
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/blind/seed?seedId={package.Manifest.SeedId}&offset=0&total={bytes.Length}&sha256={package.Sha256}")
        { Content = new ByteArrayContent(bytes) };
        req.Headers.Authorization = new("Bearer", token);
        var resp = await http.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());

        (await _blind.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(afterCutOff))
            .Should().NotBeNull("the blind node replays what it received after the cut-off");
        (await _blind.Services.GetRequiredService<IEventLogRepository>().GetLastCompactionCpAsync())
            .Should().Be(cutOff, "a peer that pulled below the cut-off must get a 410, not a silent hole");
    }

    /// <summary>
    /// Review L-stage1 #4: an event whose apply is already running when the cutover starts is waited
    /// for and replayed — the tail is read only after writes are held off, so it cannot fall between
    /// the tail and the switch.
    /// </summary>
    [Fact]
    public async Task Reseed_KeepsAnEventWrittenWhileTheCutoverWaits()
    {
        await AddBlindNodeAsync();
        var (hubId, hubKey) = await TrustSuperadminOnBothAsync();
        var cutOff = await _blind.Services.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();
        var (http, req) = await ReseedRequestAsync(cutOff);
        var inGap = WhitelistAdd(hubId, hubKey, lamport: 2000, Guid.NewGuid(), "In the gap");

        var inFlight = await EventWriteGate.Instance.EnterAsync();
        var upload = http.SendAsync(req);
        var waited = DateTime.UtcNow;
        while (!EventWriteGate.Instance.IsQuiescing && DateTime.UtcNow - waited < TimeSpan.FromSeconds(30))
            await Task.Delay(10);
        // What the apply in flight writes, while the cutover waits for it.
        await _blind.Services.GetRequiredService<IEventLogRepository>().AppendAsync(inGap);
        inFlight.Dispose();
        var resp = await upload;

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await _blind.Services.GetRequiredService<IWhitelistRepository>()
                .GetByNodeIdAsync(JsonSerializer.Deserialize<WhitelistAddPayload>(inGap.Payload)!.NodeId))
            .Should().NotBeNull("the event written while the cutover waited is replayed into the new database");
    }

    /// <summary>
    /// Review L-stage1 #4: a tail event that cannot apply on top of the package yet (its author is not
    /// in the package's whitelist) is not dropped: the node goes back to its old database, keeps the
    /// event, and refuses the seed so it can be sent again.
    /// </summary>
    [Fact]
    public async Task Reseed_WhoseReplayCannotFinish_RollsBack_AndKeepsTheEvent()
    {
        await AddBlindNodeAsync();
        var blind = await IdentityAsync(_blind);
        var (hubPub, hubKey) = Ed25519Signer.GenerateKeyPair();
        var hubId = Guid.NewGuid();
        await _blind.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = hubId, DisplayName = "Only the blind node knows me", Ed25519PublicKey = hubPub, Status = "A",
            IsSuperadmin = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        var cutOff = await _blind.Services.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();
        var added = await ApplyOnBlindAsync(WhitelistAdd(hubId, hubKey, lamport: 3000, Guid.NewGuid(), "After"));
        var (http, req) = await ReseedRequestAsync(cutOff);

        var resp = await http.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a replay that cannot finish is not swallowed");
        (await _blind.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(added))
            .Should().NotBeNull("the node is back on its old database, with the event");
        (await IdentityAsync(_blind)).NodeId.Should().Be(blind.NodeId);
        Directory.Exists(BlindSeedCutover.DirOf(_blind.DataPath)).Should().BeFalse();
    }

    /// <summary>
    /// Review L-stage1 round 3 #2: on a node that had no media directory, a seed that fails after the
    /// switch (here its replay) leaves no media directory behind: the seed's goes with the seed.
    /// </summary>
    [Fact]
    public async Task Reseed_OnANodeWithoutMedia_WhoseReplayFails_LeavesNoMediaOfTheSeed()
    {
        await AddBlindNodeAsync();
        var (hubPub, hubKey) = Ed25519Signer.GenerateKeyPair();
        var hubId = Guid.NewGuid();
        await _blind.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = hubId, DisplayName = "Only the blind node knows me", Ed25519PublicKey = hubPub, Status = "A",
            IsSuperadmin = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        var cutOff = await _blind.Services.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();
        await ApplyOnBlindAsync(WhitelistAdd(hubId, hubKey, lamport: 3000, Guid.NewGuid(), "After"));
        var media = Path.Combine(_blind.DataPath, "media");
        if (Directory.Exists(media)) Directory.Delete(media, recursive: true);
        var (http, req) = await ReseedRequestAsync(cutOff);

        var resp = await http.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "precondition: the replay fails after the switch");
        Directory.Exists(media).Should().BeFalse("there was no media directory before the seed");
    }

    /// <summary>
    /// Review L-stage1 round 2 #2: the DEK-exposure alarm is cleared by an operator, not by a reseed
    /// replacing the database it was recorded in.
    /// </summary>
    [Fact]
    public async Task Reseed_KeepsTheDekExposureAlarm()
    {
        await AddBlindNodeAsync();
        var blindDb = _blind.Services.GetRequiredService<IDbConnectionFactory>();
        using (var conn = blindDb.CreateConnection())
            await conn.ExecuteAsync("INSERT INTO tbl_blind_state (key, value, updated_at) VALUES (@k, @v, @v)",
                new { k = BlindState.DekExposureKey, v = "held an envelope" });
        var cutOff = await _blind.Services.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();
        var (http, req) = await ReseedRequestAsync(cutOff);

        (await http.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await new BlindState(blindDb).GetDekExposureAsync()).Should().Be("held an envelope");
    }

    /// <summary>
    /// Review L-stage1 round 2 #3: a failure in the switch after the database was already moved —
    /// here the media directory cannot be put in place — puts the old database back before the node
    /// is released, instead of leaving the new one live without its media.
    /// </summary>
    [Fact]
    public async Task Reseed_WhoseSwitchFailsAfterTheDatabaseMoved_IsRolledBack()
    {
        await AddBlindNodeAsync();
        var (hubId, hubKey) = await TrustSuperadminOnBothAsync();
        var cutOff = await _blind.Services.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();
        var kept = await ApplyOnBlindAsync(WhitelistAdd(hubId, hubKey, lamport: 4000, Guid.NewGuid(), "Only in the old database"));
        // The live media path is a file: the switch moves the database, then cannot move media in.
        var media = Path.Combine(_blind.DataPath, "media");
        if (Directory.Exists(media)) Directory.Delete(media, recursive: true);
        await File.WriteAllTextAsync(media, "in the way");
        var package = await BuildPackageWithoutAsync(kept, cutOff);
        var (http, req) = await ReseedRequestAsync(cutOff, package);

        var resp = await http.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await _blind.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(kept))
            .Should().NotBeNull("the old database is live again");
        Directory.Exists(BlindSeedCutover.DirOf(_blind.DataPath)).Should().BeFalse("nothing is left half-switched");
    }

    /// <summary>
    /// Review L-stage1 round 2 #3: a new seed never discards an unresolved switch — its old database and
    /// media are the only way back until a start rolls it back.
    /// </summary>
    [Fact]
    public async Task Reseed_WhileAnInterruptedSwitchIsUnresolved_IsRefused_AndKeepsTheWayBack()
    {
        await AddBlindNodeAsync();
        var cutover = new BlindSeedCutover(_blind.DataPath);
        cutover.Prepare();
        var oldDb = Path.Combine(BlindSeedCutover.DirOf(_blind.DataPath), "old.db");
        await File.WriteAllTextAsync(oldDb, "the only way back");
        cutover.WriteMarker(Guid.NewGuid(), BlindSeedCutover.Switching);
        var cutOff = await _blind.Services.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();
        var (http, req) = await ReseedRequestAsync(cutOff);

        var resp = await http.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        File.Exists(oldDb).Should().BeTrue("the unresolved switch's old database is kept");
    }

    /// <summary>
    /// Codex round 2, security #7 / agy #5. A restic backup reads <c>dataPath/media</c> while the
    /// cutover renames that whole directory aside and moves the package's in — and later deletes the
    /// old one. On Windows the rename fails under an open handle and the seed is refused; on Linux
    /// the reader captures a half-moved tree. The cutover must therefore take the same reservation
    /// the backup jobs do (BlindJobManager) and wait for the one running before it touches media.
    /// </summary>
    [Fact]
    public async Task Reseed_WhileABackupJobIsRunning_WaitsForItBeforeMovingMedia()
    {
        await AddBlindNodeAsync();
        await TrustSuperadminOnBothAsync();
        var cutOff = await _blind.Services.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();
        var (http, req) = await ReseedRequestAsync(cutOff);

        var jobs = _blind.Services.GetRequiredService<BlindJobManager>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        jobs.TryStart("backup", async (_, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
        }).Should().NotBeNull("the manager is free before the seed");

        var send = http.SendAsync(req);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var finishedWhileTheJobRan = await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(3))) == send;

        release.TrySetResult();
        var resp = await send;
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        finishedWhileTheJobRan.Should().BeFalse(
            "the seed switched the media directories while a restic reader was holding them open");
    }

    private async Task<BlindPackage> BuildPackageWithoutAsync(Guid _, long includesUpTo)
    {
        using var scope = _pc.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
            .BuildAsync(Guid.NewGuid(), includesUpTo: includesUpTo, producerIsSuperadmin: true);
    }

    /// <summary>A reseed from the PC, including the blind node's log up to <paramref name="includesUpTo"/>.</summary>
    private async Task<(HttpClient Http, HttpRequestMessage Request)> ReseedRequestAsync(long includesUpTo, BlindPackage? prebuilt = null)
    {
        var blind = await IdentityAsync(_blind);
        var package = prebuilt;
        if (package is null)
            using (var scope = _pc.Services.CreateScope())
                package = await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
                    .BuildAsync(Guid.NewGuid(), includesUpTo: includesUpTo, producerIsSuperadmin: true);
        var http = _blind.Server.CreateClient();
        string token;
        using (var scope = _pc.Services.CreateScope())
            token = await PeerAuthenticator.AuthenticateAsync(scope.ServiceProvider.GetRequiredService<INodeAuthSigner>(),
                http, http.BaseAddress!.ToString(), await IdentityAsync(_pc), blind.NodeId);
        var bytes = await File.ReadAllBytesAsync(package.FilePath);
        var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/blind/seed?seedId={package.Manifest.SeedId}&offset=0&total={bytes.Length}&sha256={package.Sha256}")
        { Content = new ByteArrayContent(bytes) };
        req.Headers.Authorization = new("Bearer", token);
        return (http, req);
    }

    /// <summary>
    /// Plan 5.1-5.2: the PC compacted past what it had pushed to the blind node (the operator
    /// accepted cutting peers off). Pushing on would silently skip the removed events; the gap
    /// detector says so instead, and the scheduler's reseeder turns that into a reseed.
    /// </summary>
    [Fact]
    public async Task PushGap_AfterCompactionCutsTheBlindNodeOff_IsDetected_AndReseeds()
    {
        await AddBlindNodeAsync();
        var blindId = (await IdentityAsync(_blind)).NodeId;
        using (var scope = _pc.Services.CreateScope())
        {
            var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
            for (var i = 0; i < 3; i++) await articles.CreateAsync($"Written later {i}", "/Notes", [], "body");
            var head = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();
            await scope.ServiceProvider.GetRequiredService<CompactionService>()
                .ExecuteAsync(explicitCp: head, reason: "test", acceptCuttingOffPeers: true);
        }

        using var http = _pc.Services.GetRequiredService<IHttpClientFactory>().CreateClient("SyncScheduler");
        var row = (await _pc.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(blindId))!;
        Exception? failure;
        using (var scope = _pc.Services.CreateScope())
        {
            var sync = () => scope.ServiceProvider.GetRequiredService<SyncClient>()
                .SyncWithAsync(http, row.ApiAddress!, blindId);
            failure = (await sync.Should().ThrowAsync<PushGapException>()).Which;
        }

        await _pc.Services.GetRequiredService<IBlindPeerReseeder>().AfterSyncAsync(row, http, failure, CancellationToken.None);

        (await ScalarAsync(_blind, "SELECT COUNT(*) FROM tbl_article WHERE title LIKE 'Written later %'"))
            .Should().Be(3, "the reseed brought what the compaction removed from the PC's log");
    }

    /// <summary>Plan 5.3: a blind node that saw a restore_network asks to be reseeded; a superadmin PC does it.</summary>
    [Fact]
    public async Task ReseedFlag_OnTheBlindNode_IsHonouredByASuperadminPc()
    {
        await AddBlindNodeAsync();
        var blindId = (await IdentityAsync(_blind)).NodeId;
        await _blind.Services.GetRequiredService<BlindState>().SetReseedNeededAsync("restore_network test");
        using var http = _pc.Services.GetRequiredService<IHttpClientFactory>().CreateClient("SyncScheduler");
        var row = (await _pc.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(blindId))!;

        await _pc.Services.GetRequiredService<IBlindPeerReseeder>().AfterSyncAsync(row, http, null, CancellationToken.None);

        (await _blind.Services.GetRequiredService<BlindState>().GetReseedNeededAsync())
            .Should().BeNull("the reseed replaced the database, flag included");
        File.Exists(Path.Combine(_blind.DataPath, "beememorybank.db.pre-seed")).Should().BeFalse("the replaced database is wiped");
    }

    /// <summary>Without a reason, a sync with a blind peer never starts a reseed (plan 5.2: only on the detector or the flag).</summary>
    [Fact]
    public async Task NoGapNoFlag_NoReseed()
    {
        await AddBlindNodeAsync();
        var blindId = (await IdentityAsync(_blind)).NodeId;
        var probe = await WriteProbeAsync();
        using var http = _pc.Services.GetRequiredService<IHttpClientFactory>().CreateClient("SyncScheduler");
        var row = (await _pc.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(blindId))!;

        await _pc.Services.GetRequiredService<IBlindPeerReseeder>().AfterSyncAsync(row, http, null, CancellationToken.None);

        (await HasProbeAsync(probe)).Should().BeTrue("no reseed replaced the database");
    }

    /// <summary>A row only the blind node's current database has: gone once a reseed replaces the database.</summary>
    private async Task<string> WriteProbeAsync()
    {
        var probe = "probe-" + Guid.NewGuid().ToString("N");
        using var conn = _blind.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        await conn.ExecuteAsync("INSERT INTO tbl_migration_marker (key, value, set_at) VALUES (@probe, 'x', 'now')", new { probe });
        return probe;
    }

    private async Task<bool> HasProbeAsync(string probe)
    {
        using var conn = _blind.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_migration_marker WHERE key = @probe", new { probe }) == 1;
    }

    private async Task AddBlindNodeAsync()
    {
        var code = await PairCodeAsync();
        var add = await _pcClient.PostAsJsonAsync("/api/blind-nodes/", new { code = code.ToString() });
        add.StatusCode.Should().Be(HttpStatusCode.OK, await add.Content.ReadAsStringAsync());
    }

    private async Task<BlindPairCode> PairCodeAsync()
    {
        using var console = _blind.CreateClient();
        var body = await console.GetFromJsonAsync<JsonElement>("/api/blind/pair-code");
        return BlindPairCode.Parse(body.GetProperty("code").GetString()!);
    }

    private async Task<HttpResponseMessage> PartAsync(
        HttpClient http, BlindPairCode code, Guid seedId, long offset, byte[] bytes, int from, int count, string sha)
    {
        var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/blind/seed?seedId={seedId}&offset={offset}&total={bytes.Length}&sha256={sha}")
        { Content = new ByteArrayContent(bytes, from, count) };
        await AddSeederAsync(req, code.Secret, seedId);
        return await http.SendAsync(req);
    }

    /// <summary>The PC names itself under the pair code's secret, as BlindNodeManager does.</summary>
    private async Task AddSeederAsync(HttpRequestMessage req, string secret, Guid seedId,
        Guid? claimedNodeId = null, byte[]? claimedKey = null, byte[]? macOverKey = null)
    {
        var pc = await IdentityAsync(_pc);
        var nodeId = claimedNodeId ?? pc.NodeId;
        var key = claimedKey ?? pc.Ed25519PublicKey;
        req.Headers.Add(BlindSeederProof.NodeIdHeader, nodeId.ToString());
        req.Headers.Add(BlindSeederProof.KeyHeader, Convert.ToBase64String(key));
        req.Headers.Add(BlindSeederProof.MacHeader, BlindSeederProof.Compute(secret, seedId, nodeId, macOverKey ?? key));
    }

    private async Task<HttpResponseMessage> UploadWholeAsync(BlindPackage package, string secret,
        Guid? claimedNodeId = null, byte[]? claimedKey = null, byte[]? macOverKey = null)
    {
        var bytes = await File.ReadAllBytesAsync(package.FilePath);
        using var http = _blind.Server.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/blind/seed?seedId={package.Manifest.SeedId}&offset=0&total={bytes.Length}&sha256={package.Sha256}")
        { Content = new ByteArrayContent(bytes) };
        await AddSeederAsync(req, secret, package.Manifest.SeedId, claimedNodeId, claimedKey, macOverKey);
        return await http.SendAsync(req);
    }

    /// <summary>A hub both nodes know as superadmin — the author of the events used as markers.</summary>
    private async Task<(Guid Id, byte[] Key)> TrustSuperadminOnBothAsync()
    {
        var (pub, key) = Ed25519Signer.GenerateKeyPair();
        var id = Guid.NewGuid();
        foreach (var node in new BmbWebApplicationFactory[] { _pc, _blind })
            await node.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
            {
                NodeId = id, DisplayName = "Hub", Ed25519PublicKey = pub, Status = "A", IsSuperadmin = true,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
        return (id, key);
    }

    private static SyncEvent WhitelistAdd(Guid author, byte[] key, long lamport, Guid added, string name)
    {
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = author, LamportTs = lamport, EventType = EventTypes.WhitelistAdd,
            Payload = JsonSerializer.Serialize(new WhitelistAddPayload(added, name,
                Convert.ToBase64String(Ed25519Signer.GenerateKeyPair().publicKey), null, false)),
            ProtocolVersion = SyncProtocolVersion.Current, CreatedAt = DateTime.UtcNow
        };
        evt.Signature = Ed25519Signer.Sign(key, EventSignature.BuildPayload(evt));
        return evt;
    }

    private async Task<Guid> ApplyOnBlindAsync(SyncEvent evt)
    {
        await ApplyOnBlindRawAsync(evt);
        return JsonSerializer.Deserialize<WhitelistAddPayload>(evt.Payload)!.NodeId;
    }

    /// <summary>Applies any event on the blind node the way its own sync applies one, and insists it landed.</summary>
    private async Task ApplyOnBlindRawAsync(SyncEvent evt)
    {
        using var scope = _blind.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<EventApplier>().ApplyAsync(evt)).Should().Be(EventApplyResult.Applied);
    }

    /// <summary>Every blob this node has, copied to the blind node (its apply is the receiver's side of BlobTransport).</summary>
    private async Task CopyBlobsAsync()
    {
        using var conn = _pc.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        using var scope = _blind.Services.CreateScope();
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobRepository>();
        foreach (var data in await conn.QueryAsync<byte[]>("SELECT data FROM tbl_blob"))
            await blobs.StoreAsync(data);
    }

    private static async Task<NodeIdentity> IdentityAsync(BmbWebApplicationFactory node) =>
        (await node.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;

    private static async Task<long> ScalarAsync(BmbWebApplicationFactory node, string sql) =>
        await ScalarAsync(node, sql, null);

    private static async Task<long> ScalarAsync(BmbWebApplicationFactory node, string sql, object? param)
    {
        using var conn = node.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        return await conn.ExecuteScalarAsync<long>(sql, param);
    }

    private static async Task<string?> TextAsync(BmbWebApplicationFactory node, string sql, object? param)
    {
        using var conn = node.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        return await conn.ExecuteScalarAsync<string?>(sql, param);
    }
}
