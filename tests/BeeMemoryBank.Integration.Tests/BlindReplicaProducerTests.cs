using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Review L-merge #2 and #3: how the producer describes itself in a blind package — a replica says
/// superadmin only where the network verifiably does, and the producer can be dialled and pinned where
/// it said it can be reached. And what makes a first seed's producer the blind node's authority: the
/// pair code the operator issued, not what its manifest claims (review r1-merge #3); after that, only
/// signed standing changes it.
/// </summary>
public class BlindReplicaProducerTests : IAsyncLifetime
{
    private const string Password = "blindProducerPw1!";
    private const string PcAddress = "https://pc.test:5301";
    private static readonly string PcSpki = Base64Url.EncodeToString(SHA256.HashData("the PC's key"u8));

    private readonly BlindNodeFactory _blind = new();
    private readonly ReachablePcFactory _pc = new();
    private HttpClient _pcClient = null!;

    public async Task InitializeAsync()
    {
        _ = _blind.Services;
        _pc.RouteOutboundHttpThrough(_blind.Server.CreateHandler());
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

    /// <summary>#3: seeded from its only hub, the blind node has that hub as a dialable, pinned peer.</summary>
    [Fact]
    public async Task ASeedFromTheOnlyHub_LeavesItDialable_AndPinned()
    {
        await AddBlindNodeAsync();

        var pcId = (await _pc.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
        var row = await _blind.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(pcId);
        row!.ApiAddress.Should().Be(PcAddress, "the scheduler dials only rows with an address");
        var pins = _blind.Services.GetRequiredService<SpkiPinRegistry>();
        pins.Invalidate();
        pins.PinFor(new Uri(PcAddress + "/api/sync/events")).Should().Be(PcSpki);
    }

    /// <summary>
    /// #2: a full node nobody in the network confirms as superadmin (here: no full peer to ask) serves
    /// a replica whose producer row is not superadmin — so it cannot become the receiver's reseed
    /// authority (the authority rule itself: Reseed_ByAnOrdinaryPeer_IsRefused).
    /// </summary>
    [Fact]
    public async Task AReplicaFromAnUnconfirmedFullNode_DoesNotMakeItSuperadmin()
    {
        await AddBlindNodeAsync();
        var blindId = (await _blind.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
        using var http = _pc.Server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer",
            _pc.Services.GetRequiredService<SyncTokenStore>().IssueToken(blindId, SyncProtocolVersion.Current));

        var resp = await http.GetAsync("/api/blind/replica");

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var file = Path.Combine(_pc.DataPath, "replica-received.tar.gz");
        await File.WriteAllBytesAsync(file, await resp.Content.ReadAsByteArrayAsync());
        var package = await _pc.Services.GetRequiredService<SnapshotService>()
            .ExtractVerifiedAsync(file, Path.Combine(_pc.DataPath, "replica-extracted"));
        var manifest = BlindManifest.Parse(await File.ReadAllBytesAsync(Path.Combine(package.Directory, BlindManifest.FileName)));
        var producer = manifest.Whitelist.Single(p => p.NodeId == manifest.ProducerNodeId);
        producer.IsSuperadmin.Should().BeFalse("no full peer confirmed this node as superadmin");
        producer.ApiAddress.Should().Be(PcAddress);
        producer.TlsSpki.Should().Be(PcSpki);
    }

    /// <summary>
    /// Review r1-merge #3: the authority comes from the code, not from the manifest. Through the real
    /// seed endpoint and the real seeder proof, the PC sends a package whose manifest does not claim
    /// superadmin for it, and no signed standing either — the blind node still takes it as superadmin,
    /// bound to the paired key, and takes its reseed.
    /// </summary>
    [Fact]
    public async Task TheCodeMakesTheProducerTheAuthority_WhateverItsManifestClaims()
    {
        var pc = (await _pc.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        BlindPackage package;
        using (var scope = _pc.Services.CreateScope())
            package = await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
                .BuildAsync(Guid.NewGuid(), includesUpTo: null, producerIsSuperadmin: false);
        package.Manifest.Whitelist.Single(p => p.NodeId == pc.NodeId).IsSuperadmin.Should().BeFalse("the manifest claims nothing");
        package.Manifest.Standing.Should().BeNullOrEmpty();

        var seed = await SeedUnderThePairCodeAsync(pc, package);

        seed.StatusCode.Should().Be(HttpStatusCode.OK, await seed.Content.ReadAsStringAsync());
        var row = (await _blind.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(pc.NodeId))!;
        row.IsSuperadmin.Should().BeTrue("the PC used the pair code");
        row.Ed25519PublicKey.Should().Equal(pc.Ed25519PublicKey);
        (await ReseedAsync(pc)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Review L-merge round 3 #1: the producer's only promotion is its signed admission — a root-signed
    /// whitelist_add with is_superadmin — and that is enough: the blind node holds it as superadmin, and
    /// its reseed is accepted.
    /// </summary>
    [Fact]
    public async Task ASignedAdmissionAsSuperadmin_LetsTheProducerReseed()
    {
        var pc = (await _pc.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var (rootId, rootKey) = await TrustRootOnPcAsync();
        await ApplyOnPcAsync(Signed(rootId, rootKey, 10, EventTypes.WhitelistAdd,
            new WhitelistAddPayload(pc.NodeId, "PC", Convert.ToBase64String(pc.Ed25519PublicKey), null, false, IsSuperadmin: true)));

        await AddBlindNodeAsync();

        (await _blind.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(pc.NodeId))!
            .IsSuperadmin.Should().BeTrue("the root's signed admission made it superadmin");
        (await ReseedAsync(pc)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Review r1-merge #3: the producer was admitted as superadmin and later revoked, both signed by the
    /// root and both in its log and its first seed. The operator gave it the pair code anyway, and that
    /// grant is newer than anything the stream says: it is the blind node's authority. The grant keeps
    /// the version the stream left, so an older revoke arriving later changes nothing, and a newer one,
    /// signed by the root, takes the authority away.
    /// </summary>
    [Fact]
    public async Task ARevokedProducerGivenTheCode_IsTheAuthority_UntilANewerSignedRevoke()
    {
        var pc = (await _pc.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var (rootId, rootKey) = await TrustRootOnPcAsync();
        await ApplyOnPcAsync(Signed(rootId, rootKey, 10, EventTypes.WhitelistAdd,
            new WhitelistAddPayload(pc.NodeId, "PC", Convert.ToBase64String(pc.Ed25519PublicKey), null, false, IsSuperadmin: true)));
        await ApplyOnPcAsync(Signed(rootId, rootKey, 20, EventTypes.WhitelistRevoke, new WhitelistRevokePayload(pc.NodeId)));

        await AddBlindNodeAsync();

        var whitelist = _blind.Services.GetRequiredService<IWhitelistRepository>();
        (await whitelist.GetByNodeIdAsync(pc.NodeId, includeDeleted: true)).Should()
            .Match<WhitelistEntry>(r => r.Status == "A" && r.IsSuperadmin, "the operator's grant");
        await ApplyOnBlindAsync(Signed(rootId, rootKey, 15, EventTypes.WhitelistRevoke, new WhitelistRevokePayload(pc.NodeId)));
        (await whitelist.GetByNodeIdAsync(pc.NodeId, includeDeleted: true)).Should()
            .Match<WhitelistEntry>(r => r.Status == "A" && r.IsSuperadmin, "a revoke older than the row's version loses");

        await ApplyOnBlindAsync(Signed(rootId, rootKey, 30, EventTypes.WhitelistRevoke, new WhitelistRevokePayload(pc.NodeId)));

        (await whitelist.GetByNodeIdAsync(pc.NodeId)).Should().BeNull("a newer signed revoke ends the authority");
        var reseed = () => ReseedAsync(pc);
        (await reseed.Should().ThrowAsync<HttpRequestException>("a revoked peer is refused already at the handshake"))
            .Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Review l-root2 #2: the grant is a versioned decision. The package carries no standing for the PC,
    /// and a demotion signed by the root that the package did not carry (no reachable peer had passed it
    /// on) and that is not newer than what the package reflected arrives afterwards: it loses against the
    /// grant. A newer one wins, as everywhere.
    /// </summary>
    [Fact]
    public async Task ADemotionThePackageDidNotCarry_AndNoNewerThanIt_DoesNotUndoTheGrant()
    {
        var pc = (await _pc.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var blindId = (await _blind.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
        var (rootId, rootKey) = await TrustRootOnPcAsync();
        // Some history the package reflects: the root admitted a phone, at Lamport 50.
        await ApplyOnPcAsync(Signed(rootId, rootKey, 50, EventTypes.WhitelistAdd, new WhitelistAddPayload(Guid.NewGuid(), "Phone",
            Convert.ToBase64String(Ed25519Signer.GenerateKeyPair().publicKey), null, false, IsSuperadmin: false)));
        await AddBlindNodeAsync();
        var whitelist = _blind.Services.GetRequiredService<IWhitelistRepository>();
        var granted = (await whitelist.GetByNodeIdAsync(pc.NodeId))!;

        // Signed at 50: no newer than the history the package reflected, and not in it.
        await ApplyOnBlindAsync(Signed(rootId, rootKey, 50, EventTypes.WhitelistUpdate,
            new WhitelistUpdatePayload(pc.NodeId, null, null, IsSuperadmin: false)));
        (await whitelist.GetByNodeIdAsync(pc.NodeId))!.IsSuperadmin.Should().BeTrue("a demotion no newer than the grant loses");

        await ApplyOnBlindAsync(Signed(rootId, rootKey, granted.LamportTs + 1, EventTypes.WhitelistUpdate,
            new WhitelistUpdatePayload(pc.NodeId, null, null, IsSuperadmin: false)));
        (await whitelist.GetByNodeIdAsync(pc.NodeId))!.IsSuperadmin.Should().BeFalse("a newer signed demotion wins");
        granted.SourceNodeId.Should().Be(blindId, "the grant is this node's own decision");
    }

    /// <summary>
    /// Review l-root3: the grant's time comes from the raw maximum, not through the Lamport clock, whose
    /// update caps a jump at 10,000,000 ahead of it. A peer row in the package stands at 100,000,000; a
    /// root-signed demotion at 20,000,000 that the package did not carry is not newer than the grant.
    /// </summary>
    [Fact]
    public async Task AGrantAfterAHighLamportTime_IsNotCappedByTheClock()
    {
        var pc = (await _pc.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var (rootId, rootKey) = await TrustRootOnPcAsync();
        await PeerOnPcAtAsync(100_000_000);
        await AddBlindNodeAsync();

        await ApplyOnBlindAsync(Signed(rootId, rootKey, 20_000_000, EventTypes.WhitelistUpdate,
            new WhitelistUpdatePayload(pc.NodeId, null, null, IsSuperadmin: false)));

        (await _blind.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(pc.NodeId))!
            .IsSuperadmin.Should().BeTrue("the grant comes after 100,000,000, and 20,000,000 is not newer");
    }

    /// <summary>
    /// Review l-root3: a package holding a Lamport time at long.MaxValue leaves no time after it for the
    /// grant. It is refused before the switch: the blind node keeps its database and grants nothing.
    /// </summary>
    [Fact]
    public async Task APackageAtTheLamportLimit_IsRefused_AndNothingIsSwitched()
    {
        var pc = (await _pc.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        await PeerOnPcAtAsync(long.MaxValue);
        BlindPackage package;
        using (var scope = _pc.Services.CreateScope())
            package = await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
                .BuildAsync(Guid.NewGuid(), includesUpTo: null, producerIsSuperadmin: true);

        var seed = await SeedUnderThePairCodeAsync(pc, package);

        seed.StatusCode.Should().Be(HttpStatusCode.BadRequest, await seed.Content.ReadAsStringAsync());
        (await _blind.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(pc.NodeId, includeDeleted: true))
            .Should().BeNull("nothing was switched, so nothing was granted");
        File.Exists(Path.Combine(_blind.DataPath, "beememorybank.db.pre-seed")).Should().BeFalse("the live database was never replaced");
    }

    /// <summary>A peer row on the PC at Lamport <paramref name="lamport"/>, which the package's whitelist carries.</summary>
    private async Task PeerOnPcAtAsync(long lamport) =>
        await _pc.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = Guid.NewGuid(), DisplayName = "Phone", Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey,
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, LamportTs = lamport, SourceNodeId = Guid.NewGuid()
        });

    private async Task ApplyOnBlindAsync(SyncEvent evt)
    {
        using var scope = _blind.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<EventApplier>().ApplyAsync(evt)).Should().Be(EventApplyResult.Applied);
    }

    /// <summary>The PC's first seed, as BlindNodeManager sends it: the pair code's proof names the PC and its key.</summary>
    private async Task<HttpResponseMessage> SeedUnderThePairCodeAsync(NodeIdentity pc, BlindPackage package)
    {
        using var console = _blind.CreateClient();
        var code = BlindPairCode.Parse((await console.GetFromJsonAsync<JsonElement>("/api/blind/pair-code")).GetProperty("code").GetString()!);
        var bytes = await File.ReadAllBytesAsync(package.FilePath);
        using var http = _blind.Server.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/blind/seed?seedId={package.Manifest.SeedId}&offset=0&total={bytes.Length}&sha256={package.Sha256}")
        { Content = new ByteArrayContent(bytes) };
        req.Headers.Add(BlindSeederProof.NodeIdHeader, pc.NodeId.ToString());
        req.Headers.Add(BlindSeederProof.KeyHeader, Convert.ToBase64String(pc.Ed25519PublicKey));
        req.Headers.Add(BlindSeederProof.MacHeader, BlindSeederProof.Compute(code.Secret, package.Manifest.SeedId, pc.NodeId, pc.Ed25519PublicKey));
        return await http.SendAsync(req);
    }

    private async Task<(Guid Id, byte[] Key)> TrustRootOnPcAsync()
    {
        var (pub, key) = Ed25519Signer.GenerateKeyPair();
        var id = Guid.NewGuid();
        await _pc.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = id, DisplayName = "Root", Ed25519PublicKey = pub, Status = "A", IsSuperadmin = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        return (id, key);
    }

    private static SyncEvent Signed<T>(Guid author, byte[] key, long lamport, string type, T payload)
    {
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = author, LamportTs = lamport, EventType = type,
            Payload = JsonSerializer.Serialize(payload), ProtocolVersion = SyncProtocolVersion.Current, CreatedAt = DateTime.UtcNow
        };
        evt.Signature = Ed25519Signer.Sign(key, EventSignature.BuildPayload(evt));
        return evt;
    }

    private async Task ApplyOnPcAsync(SyncEvent evt)
    {
        using var scope = _pc.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EventApplier>().ApplyAsync(evt);
    }

    private async Task<HttpResponseMessage> ReseedAsync(NodeIdentity pc)
    {
        var blindId = (await _blind.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
        BlindPackage package;
        using (var scope = _pc.Services.CreateScope())
            package = await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
                .BuildAsync(Guid.NewGuid(), includesUpTo: 0, producerIsSuperadmin: true);
        using var http = _blind.Server.CreateClient();
        string token;
        using (var scope = _pc.Services.CreateScope())
            token = await PeerAuthenticator.AuthenticateAsync(scope.ServiceProvider.GetRequiredService<INodeAuthSigner>(),
                http, http.BaseAddress!.ToString(), pc, blindId);
        var bytes = await File.ReadAllBytesAsync(package.FilePath);
        var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/blind/seed?seedId={package.Manifest.SeedId}&offset=0&total={bytes.Length}&sha256={package.Sha256}")
        { Content = new ByteArrayContent(bytes) };
        req.Headers.Authorization = new("Bearer", token);
        return await http.SendAsync(req);
    }

    private async Task AddBlindNodeAsync()
    {
        using var console = _blind.CreateClient();
        var body = await console.GetFromJsonAsync<JsonElement>("/api/blind/pair-code");
        var code = body.GetProperty("code").GetString()!;
        var add = await _pcClient.PostAsJsonAsync("/api/blind-nodes/", new { code });
        add.StatusCode.Should().Be(HttpStatusCode.OK, await add.Content.ReadAsStringAsync());
    }

    /// <summary>A PC that says in its configuration where it can be reached, and its pin there.</summary>
    private sealed class ReachablePcFactory : BmbWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("BMB_PUBLIC_ADDRESS", PcAddress);
            builder.UseSetting("BMB_PUBLIC_TLS_SPKI", PcSpki);
        }
    }
}
