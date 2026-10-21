using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Hosting.AspNetCore;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.TestSupport;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>A clock a test can move: the hub's invitations expire by it, and nothing else about the node cares.</summary>
public sealed class OffsetTimeProvider : TimeProvider
{
    public TimeSpan Offset { get; set; }

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
}

/// <summary>
/// One hub for the invitation tests (release 2.7): a full node on real Kestrel, https with a certificate a test chooses and http for its own front, whose
/// checks of its own address trust a test CA through the same abstraction production uses, and whose clock a test can move. A "device" is a test object
/// holding a blind NodeId and a key, calling the keyless routes the way the app does.
/// </summary>
public sealed class BlindInviteWorld : IAsyncLifetime
{
    public const string Password = "blindInvitePassword1";

    public TestPki Pki { get; } = new();
    public OffsetTimeProvider Clock { get; } = new();
    public X509Certificate2 CaLeaf { get; private set; } = null!;
    public X509Certificate2 SelfSigned { get; private set; } = null!;
    internal BlindHubHost Hub { get; private set; } = null!;
    public NodeIdentity HubIdentity { get; private set; } = null!;

    /// <summary>A caller with no internal key at all: the invited device, or anyone on the internet.</summary>
    public HttpClient Public { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        CaLeaf = Pki.IssueServer("localhost");
        SelfSigned = TestPki.SelfSigned("localhost");
        var anchors = new TlsTrustAnchors(Pki.Anchors);
        Hub = await BlindHubHost.StartAsync(CaLeaf, "Hub", Password, services =>
        {
            services.AddSingleton(anchors);
            services.AddSingleton(new HubTrustProbe(anchors, HubProbeAddressGuardTests.AnyAddress));
            services.AddSingleton<TimeProvider>(Clock);
        });
        Hub.Admin.DefaultRequestHeaders.Add("X-User-Id", "owner");
        Public = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Hub.HttpPort}") };
        using var scope = Hub.Services.CreateScope();
        HubIdentity = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
    }

    public async Task DisposeAsync()
    {
        Public.Dispose();
        await Hub.DisposeAsync();
        Pki.Dispose();
    }

    public T Service<T>() where T : notnull => Hub.Services.GetRequiredService<T>();

    public async Task UnlockAsync()
    {
        if (!Service<SessionService>().IsUnlocked)
            (await Hub.Admin.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();
    }

    /// <summary>Every test starts from: the CA certificate, the real clock, full budgets, no invitations, an unlocked hub.</summary>
    public async Task ResetAsync()
    {
        Hub.Certificate = CaLeaf;
        Clock.Offset = TimeSpan.Zero;
        Service<BlindInviteService>().Checkpoint = null;
        Service<BlindInviteRateLimits>().ResetAll();
        using var conn = Service<DbConnectionFactory>().CreateConnection();
        await conn.ExecuteAsync("DELETE FROM tbl_blind_invite");
        await conn.ExecuteAsync("DELETE FROM tbl_audit_log WHERE entity_type = 'blind_invite'");
        await UnlockAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class BlindInviteCollection : ICollectionFixture<BlindInviteWorld>
{
    public const string Name = "Blind invitations";
}

/// <summary>
/// The invitation of a blind copy, end to end on a real hub (release 2.7; docs/sync.md "Invitations"): issuing with a self-probe, the redemption that burns
/// the secret, the owner's confirmation with the master password that enrols the device like a paired phone, refusal, expiry by the clock, the budgets,
/// and what the audit trail and the table keep.
/// </summary>
[Collection(BlindInviteCollection.Name)]
public sealed partial class BlindInviteTests(BlindInviteWorld world)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed record Device(Guid NodeId, byte[] Seed, byte[] PublicKey, byte[] BackupKey, string Name)
    {
        public static Device New(string name = "Pixel of Anna")
        {
            var (publicKey, seed) = Ed25519Signer.GenerateKeyPair();
            return new Device(BlindNodeId.NewId(), seed, publicKey, RandomNumberGenerator.GetBytes(32), name);
        }
    }

    private sealed record Issued(string InviteId, string Text, BlindInvitation Invitation, JsonElement Body);

    // ── helpers ───────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> IssueRawAsync(object? request = null) =>
        await world.Hub.Admin.PostAsJsonAsync("/api/blind-nodes/invites", request ?? new
        {
            label = "Anna", sourceName = "Alex", lifetimeMinutes = 30, address = world.Hub.Origin, trust = "auto"
        });

    private async Task<Issued> IssueAsync(object? request = null)
    {
        var response = await IssueRawAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Web);
        var text = body.GetProperty("text").GetString()!;
        BlindInvitation.TryParse(text, out var invitation).Should().BeTrue("the owner copies exactly this string");
        return new Issued(body.GetProperty("inviteId").GetString()!, text, invitation!, body);
    }

    private static BlindInviteRedeemRequest RedeemRequest(BlindInvitation invitation, Device device, Guid? hubId = null, byte[]? secret = null)
    {
        var request = new BlindInviteRedeemRequest(invitation.InviteId, secret ?? invitation.Secret, device.NodeId, device.PublicKey, device.Name, "android", "2.7.0", 4,
            device.BackupKey, []);
        return request with
        {
            Proof = Ed25519Signer.Sign(device.Seed, BlindInviteProofs.RedeemMessage(invitation.InviteId, hubId ?? invitation.HubNodeId, device.NodeId, device.PublicKey, device.BackupKey))
        };
    }

    private async Task<HttpResponseMessage> RedeemAsync(BlindInvitation invitation, Device device, Guid? hubId = null, byte[]? secret = null) =>
        await PostPublicAsync("/api/sync/invite/redeem", RedeemRequest(invitation, device, hubId, secret).ToJson());

    private async Task<HttpResponseMessage> PostPublicAsync(string path, byte[] body)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new("application/json");
        return await world.Public.PostAsync(path, content);
    }

    private async Task<HttpResponseMessage> StatusAsync(BlindInvitation invitation, Device device, long? at = null, byte[]? seed = null)
    {
        var when = at ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = Ed25519Signer.Sign(seed ?? device.Seed, BlindInviteProofs.StatusMessage(invitation.InviteId, device.NodeId, when));
        return await PostPublicAsync("/api/sync/invite/status", new BlindInviteStatusRequest(invitation.InviteId, device.NodeId, when, signature).ToJson());
    }

    private static async Task<string> StateOfAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("state").GetString()!;

    private static async Task<string> ErrorOfAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("error").GetString()!;

    private async Task<(Issued Invite, Device Device, BlindInviteRedeemAnswer Answer)> RedeemedAsync(string name = "Pixel of Anna")
    {
        var issued = await IssueAsync();
        var device = Device.New(name);
        var response = await RedeemAsync(issued.Invitation, device);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        BlindInviteRedeemAnswer.TryParse(await response.Content.ReadAsByteArrayAsync(), out var answer).Should().BeTrue();
        return (issued, device, answer!);
    }

    private async Task<HttpResponseMessage> ConfirmAsync(string inviteId, string? password = BlindInviteWorld.Password) =>
        await world.Hub.Admin.PostAsJsonAsync($"/api/blind-nodes/invites/{inviteId}/confirm", new { password });

    private async Task<JsonElement> ListAsync() =>
        await world.Hub.Admin.GetFromJsonAsync<JsonElement>("/api/blind-nodes/invites/", Web);

    private async Task<string> RowStateAsync(string inviteId)
    {
        using var conn = world.Service<DbConnectionFactory>().CreateConnection();
        return await conn.ExecuteScalarAsync<string>("SELECT state FROM tbl_blind_invite WHERE invite_id = @inviteId", new { inviteId }) ?? "(none)";
    }

    private async Task<byte[]?> StoredBackupKeyAsync(string inviteId)
    {
        using var conn = world.Service<DbConnectionFactory>().CreateConnection();
        return await conn.ExecuteScalarAsync<byte[]?>("SELECT backup_key_enc FROM tbl_blind_invite WHERE invite_id = @inviteId", new { inviteId });
    }

    private async Task AssertNoStoredKeyInAnyFinalStateAsync()
    {
        using var conn = world.Service<DbConnectionFactory>().CreateConnection();
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_blind_invite WHERE state IN ('confirmed', 'refused', 'expired') AND backup_key_enc IS NOT NULL"))
            .Should().Be(0, "the backup key is cleared in every final state");
    }

    private async Task<List<(string Action, string Details)>> AuditAsync()
    {
        using var conn = world.Service<DbConnectionFactory>().CreateConnection();
        return (await conn.QueryAsync<(string, string)>("SELECT action, details FROM tbl_audit_log WHERE entity_type = 'blind_invite' ORDER BY id")).ToList();
    }

    private async Task<long> HeadAsync()
    {
        using var scope = world.Hub.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();
    }

    private async Task<List<SyncEvent>> EventsAfterAsync(long head)
    {
        using var scope = world.Hub.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetAfterSequenceAsync(head)).ToList();
    }

    private async Task<WhitelistEntry?> WhitelistRowAsync(Guid nodeId)
    {
        using var scope = world.Hub.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(nodeId);
    }

    // ── issuing ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AnInvitation_ForAHubOnAPublicCertificate_IsSignedByTheHub_AndStoredWithoutItsSecret()
    {
        await world.ResetAsync();

        var issued = await IssueAsync();

        var invitation = issued.Invitation;
        invitation.Trust.Should().Be(BlindTrust.PublicCa, "the test CA's certificate validates through the chain");
        invitation.SpkiPin.Should().BeEmpty();
        invitation.Address.Should().Be(world.Hub.Origin);
        invitation.HubNodeId.Should().Be(world.HubIdentity.NodeId);
        invitation.HubPublicKey.Should().Equal(world.HubIdentity.Ed25519PublicKey);
        invitation.SourceName.Should().Be("Alex");
        invitation.SignatureIsValid().Should().BeTrue("the hub signs the canonical fields with its identity key");
        invitation.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(30), TimeSpan.FromMinutes(1));
        issued.InviteId.Should().Be(invitation.InviteIdText);

        using var conn = world.Service<DbConnectionFactory>().CreateConnection();
        var row = await conn.QuerySingleAsync<(string State, string SecretHash, string Label)>("SELECT state, secret_hash, label FROM tbl_blind_invite WHERE invite_id = @id", new { id = issued.InviteId });
        row.State.Should().Be("issued");
        row.Label.Should().Be("Anna");
        row.SecretHash.Should().Be(Base64Url.EncodeToString(SHA256.HashData(invitation.Secret)), "only the hash is kept");
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_blind_invite WHERE instr(secret_hash, @secret) > 0 OR instr(address, @secret) > 0",
            new { secret = Base64Url.EncodeToString(invitation.Secret) })).Should().Be(0);

        (await AuditAsync()).Should().ContainSingle(a => a.Action == "invite_issued").Which.Details
            .Should().NotContain(Base64Url.EncodeToString(invitation.Secret)).And.NotContain(issued.Text);
    }

    [Fact]
    public async Task AnInvitation_ForAHubWithItsOwnCertificate_PinsTheKeyItPresents()
    {
        await world.ResetAsync();
        world.Hub.Certificate = world.SelfSigned;

        var issued = await IssueAsync();

        issued.Invitation.Trust.Should().Be(BlindTrust.Pin);
        issued.Invitation.SpkiPin.Should().Be(SpkiPin.Of(world.SelfSigned));
        issued.Invitation.SignatureIsValid().Should().BeTrue();
        issued.Body.GetProperty("notice").GetString().Should().Contain("pinned");
    }

    [Fact]
    public async Task ForcingThePinnedMode_OnAPubliclyValidCertificate_WarnsToUseTheNormalOne()
    {
        await world.ResetAsync();

        var issued = await IssueAsync(new { label = "Anna", sourceName = "Alex", lifetimeMinutes = 30, address = world.Hub.Origin, trust = "pin" });

        issued.Invitation.Trust.Should().Be(BlindTrust.Pin);
        issued.Body.GetProperty("notice").GetString().Should().Contain("normal certificate");
    }

    [Theory]
    [InlineData("http://localhost:1")]                 // not https
    [InlineData("https://localhost:1")]                // nothing listens: the probe says so
    [InlineData("https://localhost/some/path")]        // a path
    [InlineData("not an address")]
    [InlineData("")]
    public async Task AnInvitation_IsNotMade_WhenTheAddressDoesNotReachThisNode(string address)
    {
        await world.ResetAsync();

        var response = await IssueRawAsync(new { label = "Anna", sourceName = "Alex", lifetimeMinutes = 30, address });

        // An empty address falls back to BMB_PUBLIC_ADDRESS, which a test hub does not set.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
        (await ListAsync()).GetArrayLength().Should().Be(0, "a failed probe makes no invitation");
    }

    /// <summary>An address that is a literal of this machine, link-local or a cloud metadata service is refused at once, before this node dials anything.</summary>
    [Theory]
    [InlineData("https://100.100.100.200")]
    [InlineData("https://192.0.0.192")]
    [InlineData("https://168.63.129.16")]
    [InlineData("https://[fd00:ec2::254]")]
    [InlineData("https://[fd20:ce::254]")]
    [InlineData("https://169.254.169.254")]
    [InlineData("https://127.0.0.1:8443")]
    [InlineData("https://[::ffff:100.100.100.200]")]
    public async Task AnInvitation_IsNotMade_ForAnAddressThatCanNeverBeAHub(string address)
    {
        await world.ResetAsync();

        var response = await IssueRawAsync(new { label = "Anna", sourceName = "Alex", lifetimeMinutes = 30, address });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await ErrorOfAsync(response)).Should().NotBeNullOrWhiteSpace().And.NotContain("100.100").And.NotContain("168.63").And.NotContain("192.0.0").And.NotContain("fd00").And.NotContain("fd20")
            .And.NotContain("169.254.169", "the sentence does not quote the address");
        (await ListAsync()).GetArrayLength().Should().Be(0);
    }

    /// <summary>A loopback literal is a place a peer may be (a LAN node), but never a hub: the sentence says so, and nothing is dialled.</summary>
    [Fact]
    public async Task AnInvitation_IsNotMade_ForTheLoopbackOfThisMachine()
    {
        await world.ResetAsync();

        var response = await IssueRawAsync(new { label = "Anna", sourceName = "Alex", lifetimeMinutes = 30, address = "https://127.0.0.1:8443" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOfAsync(response)).Should().Contain("never be a hub");
    }

    [Theory]
    [InlineData(5)]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(1440)]
    public async Task OnlyFifteenThirtyOrSixtyMinutes_AreOffered(int minutes)
    {
        await world.ResetAsync();

        var response = await IssueRawAsync(new { label = "Anna", sourceName = "Alex", lifetimeMinutes = minutes, address = world.Hub.Origin });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(60)]
    public async Task TheOwnerChoosesTheLifetime(int minutes)
    {
        await world.ResetAsync();

        var issued = await IssueAsync(new { label = "Anna", sourceName = "Alex", lifetimeMinutes = minutes, address = world.Hub.Origin });

        issued.Invitation.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(minutes), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task ALabelWithAControlCharacter_OrANameOver64Characters_IsRefused()
    {
        await world.ResetAsync();

        (await IssueRawAsync(new { label = "An\nna", sourceName = "Alex", lifetimeMinutes = 30, address = world.Hub.Origin })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await IssueRawAsync(new { label = "Anna", sourceName = new string('x', 65), lifetimeMinutes = 30, address = world.Hub.Origin })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task WithoutAName_TheInvitationShowsTheNodesOwn()
    {
        await world.ResetAsync();

        var issued = await IssueAsync(new { label = "", lifetimeMinutes = 30, address = world.Hub.Origin });

        issued.Invitation.SourceName.Should().Be("Hub");
    }

    [Fact]
    public async Task ANodeHoldsAtMostTwentyOpenInvitations()
    {
        await world.ResetAsync();
        for (var i = 0; i < BlindInviteService.MaxOpen; i++) await IssueAsync();

        var response = await IssueRawAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Issuing_NeedsTheInternalKeyAndASuperadmin()
    {
        await world.ResetAsync();
        var body = JsonContent.Create(new { label = "Anna", address = world.Hub.Origin });

        (await world.Public.PostAsync("/api/blind-nodes/invites", body)).StatusCode.Should().Be(HttpStatusCode.NotFound, "an unpublished route answers 404 to a keyless caller");
        (await world.Public.GetAsync("/api/blind-nodes/invites/")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var user = new HttpClient { BaseAddress = world.Hub.Admin.BaseAddress };
        user.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);
        user.DefaultRequestHeaders.Add("X-User-Role", "user");
        (await user.PostAsJsonAsync("/api/blind-nodes/invites", new { label = "Anna", address = world.Hub.Origin })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.GetAsync("/api/blind-nodes/invites/")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AnAgent_EvenOneOwnedByASuperadmin_CannotMakeConfirmOrRefuse()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        int userId;
        using (var users = world.Hub.Services.CreateScope())
            userId = (await users.ServiceProvider.GetRequiredService<IUserRepository>().GetByUsernameAsync("admin"))!.Id;
        var dek = world.Service<SessionService>().GetMasterDek();
        var apiKey = AgentKeyHelper.GenerateApiKey();
        var (ciphertext, iv) = AgentKeyHelper.EncryptDek(apiKey, dek);
        Array.Clear(dek);
        using (var scope = world.Hub.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IAgentRepository>().CreateAsync(new Agent
            {
                Name = "Invite Test Agent", KeyPrefix = AgentKeyHelper.GetKeyPrefix(apiKey), KeyHash = AgentKeyHelper.ComputeKeyHash(apiKey),
                EncryptedDek = ciphertext, DekIV = iv, Status = "A", CreatedAt = DateTime.UtcNow, OwnerUserId = userId
            });
        using var agent = new HttpClient { BaseAddress = world.Hub.Admin.BaseAddress };
        agent.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);
        agent.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        var made = await agent.PostAsJsonAsync("/api/blind-nodes/invites", new { label = "x", address = world.Hub.Origin });
        var confirmed = await agent.PostAsJsonAsync($"/api/blind-nodes/invites/{issued.InviteId}/confirm", new { password = BlindInviteWorld.Password });
        var refused = await agent.PostAsync($"/api/blind-nodes/invites/{issued.InviteId}/refuse", null);

        foreach (var response in new[] { made, confirmed, refused })
        {
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await ErrorOfAsync(response)).Should().Contain("Agents cannot");
        }
        (await RowStateAsync(issued.InviteId)).Should().Be("redeemed");
        (await WhitelistRowAsync(device.NodeId)).Should().BeNull();
    }

    [Fact]
    public async Task AnInvitation_IsNotMade_WhileTheHubIsLocked()
    {
        await world.ResetAsync();
        try
        {
            (await world.Hub.Admin.PostAsync("/api/session/lock", null)).EnsureSuccessStatusCode();

            var response = await IssueRawAsync();

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            await world.UnlockAsync();
        }
    }

    // ── redeeming ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ARedemption_IsAnswered202WithTheMatchCode_AndTheOwnerSeesTheRequest()
    {
        await world.ResetAsync();
        var (issued, device, answer) = await RedeemedAsync();

        answer.State.Should().Be("awaiting");
        answer.SourceName.Should().Be("Alex");
        answer.MatchCode.Should().Be(BlindInviteMatchCode.Compute(issued.Invitation.InviteId, device.NodeId, device.PublicKey));
        answer.MatchCode.Should().Be(issued.Invitation.MatchCodeFor(device.NodeId, device.PublicKey), "both sides compute the same code");
        DateTimeOffset.FromUnixTimeSeconds(answer.ConfirmByUnix).Should().BeCloseTo(DateTimeOffset.UtcNow.AddHours(72), TimeSpan.FromMinutes(1));

        var list = await ListAsync();
        var row = list.EnumerateArray().Should().ContainSingle().Which;
        row.GetProperty("state").GetString().Should().Be("redeemed");
        row.GetProperty("matchCode").GetString().Should().Be(answer.MatchCode);
        row.GetProperty("deviceName").GetString().Should().Be(device.Name);
        row.GetProperty("platform").GetString().Should().Be("android");
        row.GetProperty("appVersion").GetString().Should().Be("2.7.0");
        row.GetProperty("seenFrom").GetString().Should().NotBeNullOrEmpty();
        row.GetProperty("label").GetString().Should().Be("Anna");

        var text = row.GetRawText();
        text.Should().NotContain(Base64Url.EncodeToString(issued.Invitation.Secret), "the list never shows the secret");
        text.Should().NotContain(Base64Url.EncodeToString(device.BackupKey), "nor the device's backup key");
        text.Should().NotContain(Base64Url.EncodeToString(SHA256.HashData(issued.Invitation.Secret)), "nor the hash that would let it be checked");
    }

    [Fact]
    public async Task TheStoredBackupKey_IsEncryptedUnderTheDek_BoundToTheInvitation()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();

        var stored = (await StoredBackupKeyAsync(issued.InviteId))!;

        stored.AsSpan().IndexOf(device.BackupKey).Should().Be(-1, "the key is not stored as it came");
        var dek = world.Service<SessionService>().GetMasterDek();
        try
        {
            BlindInviteService.TryDecryptBackupKey(stored, dek, issued.InviteId).Should().Equal(device.BackupKey);
            BlindInviteService.TryDecryptBackupKey(stored, dek, "someOtherInvitation").Should().BeNull("a blob moved to another row does not open");
            BlindInviteService.TryDecryptBackupKey(stored, new byte[32], issued.InviteId).Should().BeNull("only the master DEK opens it");
        }
        finally
        {
            Array.Clear(dek);
        }
    }

    [Fact]
    public async Task TheSameDeviceAskingAgain_GetsTheSameAnswer_AndNothingIsWrittenTwice()
    {
        await world.ResetAsync();
        var (issued, device, first) = await RedeemedAsync();
        var auditBefore = (await AuditAsync()).Count;

        var again = await RedeemAsync(issued.Invitation, device);

        again.StatusCode.Should().Be(HttpStatusCode.Accepted);
        BlindInviteRedeemAnswer.TryParse(await again.Content.ReadAsByteArrayAsync(), out var second).Should().BeTrue();
        second.Should().Be(first, "a lost answer on a mobile network must not cost the invitation");
        (await AuditAsync()).Count.Should().Be(auditBefore);
        (await ListAsync()).GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task AnotherDeviceRedeemingTheSameInvitation_GetsTheOneAnswerForEverythingNotValid_AndTheFirstIsUntouched()
    {
        await world.ResetAsync();
        var (issued, device, answer) = await RedeemedAsync();
        var other = Device.New("Somebody else");

        var response = await RedeemAsync(issued.Invitation, other);

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
        (await ErrorOfAsync(response)).Should().Be("invite_not_valid");
        (await WhitelistRowAsync(other.NodeId)).Should().BeNull();
        var row = (await ListAsync()).EnumerateArray().Single();
        row.GetProperty("deviceId").GetString().Should().Be(device.NodeId.ToString());
        row.GetProperty("matchCode").GetString().Should().Be(answer.MatchCode);
    }

    [Fact]
    public async Task SameNodeIdWithAnotherKey_IsNotTheSameDevice()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        var (otherKey, otherSeed) = Ed25519Signer.GenerateKeyPair();
        var impostor = device with { Seed = otherSeed, PublicKey = otherKey };

        var response = await RedeemAsync(issued.Invitation, impostor);

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task ExactlyOneOfManyDevicesRedeemingTogether_Wins()
    {
        await world.ResetAsync();
        var issued = await IssueAsync();
        // Twenty devices from one test address: neither the per-caller budget (10) nor the process-wide ones are what is measured here, so they are wide open.
        var limits = world.Service<BlindInviteRateLimits>();
        limits.Redeem = new SlidingWindowRateLimiter(1000, TimeSpan.FromMinutes(10), 100);
        limits.RedeemBudget = new PublicRouteBudget(burst: 100, refillPerSecond: 100, maxConcurrent: 64);
        // ...and so is the invitation's own reserved lane (burst 10, 4 at once), which twenty racers for ONE id would otherwise run into.
        limits.OpenInvites.Lane(BlindInviteRoute.Redeem, issued.InviteId, () => new PublicRouteBudget(burst: 100, refillPerSecond: 100, maxConcurrent: 64));
        var devices = Enumerable.Range(0, 20).Select(i => Device.New($"Device {i}")).ToList();

        var responses = await Task.WhenAll(devices.Select(d => Task.Run(() => RedeemAsync(issued.Invitation, d))));

        responses.Count(r => r.StatusCode == HttpStatusCode.Accepted).Should().Be(1, "one conditional UPDATE decides");
        responses.Count(r => r.StatusCode == HttpStatusCode.Gone).Should().Be(19);
        var row = (await ListAsync()).EnumerateArray().Single();
        var winner = devices[Array.FindIndex(responses, r => r.StatusCode == HttpStatusCode.Accepted)];
        row.GetProperty("deviceId").GetString().Should().Be(winner.NodeId.ToString());
    }

    [Fact]
    public async Task UnknownExpiredWrongSecretAndWithdrawn_AreOneAndTheSameAnswer()
    {
        await world.ResetAsync();
        var device = Device.New();

        // unknown id
        var unknown = BlindInvitation.CreateUnsignedForMalformedFixtures(world.Hub.Origin, world.HubIdentity.NodeId, BlindTrust.PublicCa, null, world.HubIdentity.Ed25519PublicKey,
            RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(32), DateTimeOffset.UtcNow.AddMinutes(30), "Alex");
        // wrong secret
        var issued = await IssueAsync();
        // withdrawn
        var withdrawn = await IssueAsync();
        (await world.Hub.Admin.DeleteAsync($"/api/blind-nodes/invites/{withdrawn.InviteId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        // expired by the clock
        var expired = await IssueAsync();
        world.Clock.Offset = TimeSpan.FromMinutes(31);

        var answers = new List<(string What, HttpStatusCode Status, string Body)>();
        foreach (var (what, response) in new[]
        {
            ("unknown", await RedeemAsync(unknown, device)),
            ("wrong secret", await RedeemAsync(issued.Invitation, device, secret: RandomNumberGenerator.GetBytes(32))),
            ("withdrawn", await RedeemAsync(withdrawn.Invitation, device)),
            ("expired", await RedeemAsync(expired.Invitation, device)),
            ("expired, right secret", await RedeemAsync(issued.Invitation, device)),
        })
            answers.Add((what, response.StatusCode, await response.Content.ReadAsStringAsync()));

        answers.Should().OnlyContain(a => a.Status == HttpStatusCode.Gone && a.Body == """{"error":"invite_not_valid"}""", string.Join("; ", answers));
        (await WhitelistRowAsync(device.NodeId)).Should().BeNull();
    }

    [Fact]
    public async Task ARedemptionSignedForAnotherHub_OrWithoutTheKeyItNames_IsABadRequest_AndReadsNoRow()
    {
        await world.ResetAsync();
        var issued = await IssueAsync();
        var device = Device.New();
        var thief = Device.New() with { NodeId = device.NodeId };

        (await RedeemAsync(issued.Invitation, device, hubId: Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.BadRequest, "replayed at another node");
        var forged = RedeemRequest(issued.Invitation, device) with { PublicKey = thief.PublicKey };
        (await PostPublicAsync("/api/sync/invite/redeem", forged.ToJson())).StatusCode.Should().Be(HttpStatusCode.BadRequest, "nobody enrols a key whose private half they do not hold");

        (await RowStateAsync(issued.InviteId)).Should().Be("issued", "the invitation is still there for its owner's friend");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("{\"inviteId\":\"x\"}")]
    public async Task ABodyThatIsNotExactlyARedeemRequest_IsABadRequest(string body)
    {
        await world.ResetAsync();

        var response = await PostPublicAsync("/api/sync/invite/redeem", Encoding.UTF8.GetBytes(body));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOfAsync(response)).Should().Be("bad_request");
    }

    [Fact]
    public async Task ABodyOverEightKilobytes_IsRefusedUnread()
    {
        await world.ResetAsync();

        (await PostPublicAsync("/api/sync/invite/redeem", new byte[BlindInviteRedeemRequest.MaxBodyBytes + 1])).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await PostPublicAsync("/api/sync/invite/status", new byte[BlindInviteStatusRequest.MaxBodyBytes + 1])).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task OnlyPostIsAnswered_AndNoSiblingRouteIsPublished()
    {
        await world.ResetAsync();

        (await world.Public.GetAsync("/api/sync/invite/redeem")).StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
        (await world.Public.GetAsync("/api/sync/invite/status")).StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
        (await PostPublicAsync("/api/sync/invite/other", "{}"u8.ToArray())).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await PostPublicAsync("/api/sync/invite", "{}"u8.ToArray())).StatusCode.Should().Be(HttpStatusCode.NotFound);
        InvitePublicSurface().Should().BeEquivalentTo(["POST /api/sync/invite/redeem", "POST /api/sync/invite/status"]);
    }

    private static IEnumerable<string> InvitePublicSurface() =>
        BeeMemoryBank.Hosting.AspNetCore.PublicSurface.Entries.Where(e => e.Pattern.StartsWith("/api/sync/invite", StringComparison.Ordinal)).Select(e => $"{e.Method} {e.Pattern}");

    [Fact]
    public async Task ARedemption_WhileTheHubIsLocked_Is503Locked_AndTheInvitationSurvives()
    {
        await world.ResetAsync();
        var issued = await IssueAsync();
        var device = Device.New();
        try
        {
            (await world.Hub.Admin.PostAsync("/api/session/lock", null)).EnsureSuccessStatusCode();

            var response = await RedeemAsync(issued.Invitation, device);

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await ErrorOfAsync(response)).Should().Be("locked");
            (await StatusAsync(issued.Invitation, device)).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }
        finally
        {
            await world.UnlockAsync();
        }
        (await RowStateAsync(issued.InviteId)).Should().Be("issued");
        (await RedeemAsync(issued.Invitation, device)).StatusCode.Should().Be(HttpStatusCode.Accepted, "the app retries until the invitation expires");
    }

    [Fact]
    public async Task ANodeIdAlreadyOnTheWhitelistUnderAnotherKey_Is409_AndTheInvitationIsNotBurned()
    {
        await world.ResetAsync();
        var issued = await IssueAsync();
        var device = Device.New();
        using (var scope = world.Hub.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
            {
                NodeId = device.NodeId, DisplayName = "Already here", Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey, Status = "A",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });

        var response = await RedeemAsync(issued.Invitation, device);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorOfAsync(response)).Should().Be("device_known_under_other_key");
        (await RowStateAsync(issued.InviteId)).Should().Be("issued");
    }

    [Fact]
    public async Task RedemptionsAreRateLimitedPerCaller_ThenAnswered429()
    {
        await world.ResetAsync();
        var device = Device.New();
        var unknown = BlindInvitation.CreateUnsignedForMalformedFixtures(world.Hub.Origin, world.HubIdentity.NodeId, BlindTrust.PublicCa, null, world.HubIdentity.Ed25519PublicKey,
            RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(32), DateTimeOffset.UtcNow.AddMinutes(30), "Alex");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++) statuses.Add((await RedeemAsync(unknown, device)).StatusCode);

        statuses.Take(10).Should().OnlyContain(s => s == HttpStatusCode.Gone);
        statuses.Skip(10).Should().OnlyContain(s => s == HttpStatusCode.TooManyRequests, "10 redemptions per 10 minutes per caller");
    }

    [Fact]
    public async Task StatusRequests_AreRateLimitedToSixtyPerTenMinutes()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();

        HttpStatusCode last = default;
        for (var i = 0; i < 61; i++) last = (await StatusAsync(issued.Invitation, device)).StatusCode;

        last.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task AStrangerGuessing_CannotGrowTheAuditLogWithoutBound()
    {
        await world.ResetAsync();
        var service = world.Service<BlindInviteService>();
        var unknown = BlindInvitation.CreateUnsignedForMalformedFixtures(world.Hub.Origin, world.HubIdentity.NodeId, BlindTrust.PublicCa, null, world.HubIdentity.Ed25519PublicKey,
            RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(32), DateTimeOffset.UtcNow.AddMinutes(30), "Alex");

        for (var i = 0; i < 100; i++)
            (await service.RedeemAsync(RedeemRequest(unknown, Device.New()).ToJson(), "203.0.113.9", CancellationToken.None)).Outcome.Should().Be(BlindInviteOutcome.NotValid);

        (await AuditAsync()).Count(a => a.Action == "invite_redeem_refused").Should().BeLessThanOrEqualTo(30);
    }

    // ── asking for the answer ─────────────────────────────────────────────

    [Fact]
    public async Task TheStatus_FollowsTheOwnersDecision()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();

        (await StateOfAsync(await StatusAsync(issued.Invitation, device))).Should().Be("awaiting");

        (await ConfirmAsync(issued.InviteId)).EnsureSuccessStatusCode();
        (await StateOfAsync(await StatusAsync(issued.Invitation, device))).Should().Be("confirmed");

        var (refusedInvite, refusedDevice, _) = await RedeemedAsync("Refused one");
        (await world.Hub.Admin.PostAsync($"/api/blind-nodes/invites/{refusedInvite.InviteId}/refuse", null)).EnsureSuccessStatusCode();
        (await StateOfAsync(await StatusAsync(refusedInvite.Invitation, refusedDevice))).Should().Be("refused");
    }

    [Fact]
    public async Task TheStatus_IsOnlyForTheDeviceThatRedeemed_WithinTenMinutesOfTheHubsClock()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        (await StatusAsync(issued.Invitation, device)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusAsync(issued.Invitation, device, seed: Ed25519Signer.GenerateKeyPair().privateKey)).StatusCode.Should().Be(HttpStatusCode.Gone, "another key");
        (await StatusAsync(issued.Invitation, device with { NodeId = BlindNodeId.NewId() })).StatusCode.Should().Be(HttpStatusCode.Gone, "another device");
        (await StatusAsync(issued.Invitation, device, at: now - 11 * 60)).StatusCode.Should().Be(HttpStatusCode.Gone, "a stale proof is a replay");
        (await StatusAsync(issued.Invitation, device, at: now + 11 * 60)).StatusCode.Should().Be(HttpStatusCode.Gone);
        var unredeemed = await IssueAsync();
        (await StatusAsync(unredeemed.Invitation, device)).StatusCode.Should().Be(HttpStatusCode.Gone, "nothing to say about an invitation this device did not redeem");
        (await ErrorOfAsync(await StatusAsync(unredeemed.Invitation, device))).Should().Be("invite_not_valid");
    }

    [Fact]
    public async Task TheStatus_SaysExpired_WhenTheOwnersTimeRunsOut()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();

        world.Clock.Offset = TimeSpan.FromHours(73);

        (await StateOfAsync(await StatusAsync(issued.Invitation, device, at: DateTimeOffset.UtcNow.AddHours(73).ToUnixTimeSeconds()))).Should().Be("expired");
    }

    // ── the owner's decision ──────────────────────────────────────────────

    [Fact]
    public async Task Confirming_NeedsTheMasterPassword_AndAWrongOneChangesNothing()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        var head = await HeadAsync();

        (await ConfirmAsync(issued.InviteId, "wrong password")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ConfirmAsync(issued.InviteId, null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ConfirmAsync(issued.InviteId, "")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await RowStateAsync(issued.InviteId)).Should().Be("redeemed");
        (await WhitelistRowAsync(device.NodeId)).Should().BeNull();
        (await HeadAsync()).Should().Be(head, "nothing is published");
    }

    [Fact]
    public async Task Confirming_EnrolsTheDeviceLikeAPairedPhone_APlainPeerWithItsBackupKeySealedAndSigned()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        var head = await HeadAsync();

        var response = await ConfirmAsync(issued.InviteId);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Web);
        body.GetProperty("nodeId").GetGuid().Should().Be(device.NodeId);
        body.GetProperty("displayName").GetString().Should().Be("Anna", "the owner's label names the device");

        var row = (await WhitelistRowAsync(device.NodeId))!;
        row.IsSuperadmin.Should().BeFalse("a blind copy is never a superadmin");
        row.Status.Should().Be("A");
        row.Ed25519PublicKey.Should().Equal(device.PublicKey);
        row.DisplayName.Should().Be("Anna");

        var events = await EventsAfterAsync(head);
        events.Should().ContainSingle(e => e.EventType == EventTypes.WhitelistAdd && JsonDocument.Parse(e.Payload).RootElement.GetProperty("node_id").GetGuid() == device.NodeId,
            "the mesh must hear of the device");
        var sealedSet = events.Where(e => e.EventType == EventTypes.SealedSecretSet).Select(e => JsonDocument.Parse(e.Payload).RootElement)
            .Single(p => p.GetProperty("name").GetString() == $"android-backup:{device.NodeId}");
        var dek = world.Service<SessionService>().GetMasterDek();
        var opened = SealedSecretCrypto.TryOpen(sealedSet.GetProperty("name").GetString()!,
            Convert.FromBase64String(sealedSet.GetProperty("wrapped").GetString()!), Convert.FromBase64String(sealedSet.GetProperty("iv").GetString()!), dek);
        Array.Clear(dek);
        BlindPhoneBackupSeal.TryDecode(opened, out var seal).Should().BeTrue();
        seal!.BackupKey.Should().Equal(device.BackupKey, "a restore from the device's backup opens with this seal");
        seal.ProducerNodeId.Should().Be(world.HubIdentity.NodeId);
        seal.ProducerPublicKey.Should().Equal(world.HubIdentity.Ed25519PublicKey);
        (seal.ProducerAddress, seal.ProducerTlsSpki, seal.ProducerTrust).Should().Be((world.Hub.Origin, "", BlindTrust.PublicCa));
        seal.PairedBy.Should().Be(world.HubIdentity.NodeId);
        Ed25519Signer.Verify(world.HubIdentity.Ed25519PublicKey, seal.PairingStatement(device.NodeId), seal.PairingSignature).Should().BeTrue("the hub signs the binding a restore checks");

        (await RowStateAsync(issued.InviteId)).Should().Be("confirmed");
        (await StoredBackupKeyAsync(issued.InviteId)).Should().BeNull("the key waited only for the decision");
        await AssertNoStoredKeyInAnyFinalStateAsync();
        (await AuditAsync()).Should().Contain(a => a.Action == "invite_confirmed" && a.Details.Contains("owner"));
    }

    [Fact]
    public async Task WithoutALabel_TheDeviceIsNamedAfterItself()
    {
        await world.ResetAsync();
        var issued = await IssueAsync(new { label = "", sourceName = "Alex", lifetimeMinutes = 30, address = world.Hub.Origin });
        var device = Device.New("Pixel 9");
        (await RedeemAsync(issued.Invitation, device)).EnsureSuccessStatusCode();

        (await ConfirmAsync(issued.InviteId)).EnsureSuccessStatusCode();

        (await WhitelistRowAsync(device.NodeId))!.DisplayName.Should().Be("Pixel 9 (invited)");
    }

    [Fact]
    public async Task ADeviceThatIsConfirmed_AuthenticatesAndDownloadsThePackage_WithTheCallCodeTheInvitationImplies()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        (await ConfirmAsync(issued.InviteId)).EnsureSuccessStatusCode();

        // What the app builds from the invitation once the status says confirmed: a call code for the hub, authentic by a fresh local secret.
        var secret = BlindPairingSecret.New();
        var invitation = issued.Invitation;
        var call = BlindCallCode.CreatePublicCa(invitation.Address, invitation.HubNodeId, invitation.HubPublicKey, secret);
        var anchors = new TlsTrustAnchors(world.Pki.Anchors);
        using var provider = new BlindHttpClientProvider(new BlindPhoneState(new MemoryStore()), null, null, anchors);
        var replica = await CreateReplicaClientAsync(device);

        using (var http = provider.GetClient(call))
            await replica.Client.FetchAndInstallAsync(http, call, replica.WorkDirectory, progress: null, CancellationToken.None);

        (await new NodeIdentityRepository(replica.Factory).GetAsync())!.NodeId.Should().Be(device.NodeId);
        (await new SyncPositionRepository(replica.Factory).GetAsync(world.HubIdentity.NodeId)).Should().NotBeNull("the next pull starts at the package's checkpoint");
    }

    [Fact]
    public async Task ADeviceThatIsNotConfirmed_CannotAuthenticate()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        var invitation = issued.Invitation;
        var call = BlindCallCode.CreatePublicCa(invitation.Address, invitation.HubNodeId, invitation.HubPublicKey, BlindPairingSecret.New());
        using var provider = new BlindHttpClientProvider(new BlindPhoneState(new MemoryStore()), null, null, new TlsTrustAnchors(world.Pki.Anchors));
        var replica = await CreateReplicaClientAsync(device);

        using var http = provider.GetClient(call);
        var act = () => replica.Client.FetchAndInstallAsync(http, call, replica.WorkDirectory, progress: null, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>("a redemption is not membership: before the owner confirms, the hub gives the device nothing");
        (await WhitelistRowAsync(device.NodeId)).Should().BeNull();
    }

    [Fact]
    public async Task Refusing_LeavesNoDeviceAndNoKey_AndTheInvitationCannotBeUsedAgain()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();

        (await world.Hub.Admin.PostAsync($"/api/blind-nodes/invites/{issued.InviteId}/refuse", null)).EnsureSuccessStatusCode();

        (await RowStateAsync(issued.InviteId)).Should().Be("refused");
        (await StoredBackupKeyAsync(issued.InviteId)).Should().BeNull();
        (await WhitelistRowAsync(device.NodeId)).Should().BeNull();
        (await RedeemAsync(issued.Invitation, device)).StatusCode.Should().Be(HttpStatusCode.Gone);
        (await ConfirmAsync(issued.InviteId)).StatusCode.Should().Be(HttpStatusCode.Conflict, "a refused redemption cannot be confirmed afterwards");
        (await world.Hub.Admin.PostAsync($"/api/blind-nodes/invites/{issued.InviteId}/refuse", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await AssertNoStoredKeyInAnyFinalStateAsync();
        (await AuditAsync()).Should().Contain(a => a.Action == "invite_refused");
    }

    [Fact]
    public async Task AConfirmedRedemption_ConfirmedAgainIsTheSameSuccess_ButCannotBeRefused()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        var first = await ConfirmAsync(issued.InviteId);
        first.EnsureSuccessStatusCode();
        var head = await HeadAsync();

        var again = await ConfirmAsync(issued.InviteId);

        again.StatusCode.Should().Be(HttpStatusCode.OK, "a lost answer or a double click is the same confirmation, not a conflict");
        (await again.Content.ReadAsStringAsync()).Should().Be(await first.Content.ReadAsStringAsync(), "and it says the same thing");
        (await ConfirmAsync(issued.InviteId, password: "wrong password")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "the master password is asked again, however many times");
        (await world.Hub.Admin.PostAsync($"/api/blind-nodes/invites/{issued.InviteId}/refuse", null)).StatusCode.Should().Be(HttpStatusCode.Conflict, "a different decision stays a conflict");

        (await HeadAsync()).Should().Be(head, "nothing more is published");
        (await WhitelistRowAsync(device.NodeId)).Should().NotBeNull();
    }

    [Fact]
    public async Task Withdrawing_AnUnusedInvitation_MakesItDead_ButNotARedeemedOne()
    {
        await world.ResetAsync();
        var unused = await IssueAsync();

        (await world.Hub.Admin.DeleteAsync($"/api/blind-nodes/invites/{unused.InviteId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await RowStateAsync(unused.InviteId)).Should().Be("expired");
        (await RedeemAsync(unused.Invitation, Device.New())).StatusCode.Should().Be(HttpStatusCode.Gone);

        var (redeemed, _, _) = await RedeemedAsync();
        (await world.Hub.Admin.DeleteAsync($"/api/blind-nodes/invites/{redeemed.InviteId}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await world.Hub.Admin.DeleteAsync("/api/blind-nodes/invites/doesNotExist")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await world.Hub.Admin.DeleteAsync("/api/blind-nodes/invites/bad%20id!")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AuditAsync()).Should().Contain(a => a.Action == "invite_withdrawn");
    }

    [Fact]
    public async Task Confirming_AfterTheOwnersTimeRanOut_IsRefused_AndTheStoredKeyIsGone()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();

        world.Clock.Offset = TimeSpan.FromHours(73);

        (await ConfirmAsync(issued.InviteId)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await WhitelistRowAsync(device.NodeId)).Should().BeNull();
        await world.Service<BlindInviteService>().SweepAsync();
        (await RowStateAsync(issued.InviteId)).Should().Be("expired");
        await AssertNoStoredKeyInAnyFinalStateAsync();
    }

    [Fact]
    public async Task ConfirmingAKeyThatNoLongerOpens_ExplainsItAndEnrolsNothing()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        using (var conn = world.Service<DbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync("UPDATE tbl_blind_invite SET backup_key_enc = x'00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff00112233' WHERE invite_id = @id", new { id = issued.InviteId });

        var response = await ConfirmAsync(issued.InviteId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Refuse it");
        (await WhitelistRowAsync(device.NodeId)).Should().BeNull();
    }

    // ── time ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheSweep_ExpiresWhatRanOut_ClearsTheirKeys_AndDeletesOldFinalRows()
    {
        await world.ResetAsync();
        var service = world.Service<BlindInviteService>();
        var unused = await IssueAsync();
        var (redeemed, _, _) = await RedeemedAsync();
        var (confirmed, _, _) = await RedeemedAsync("Confirmed one");
        (await ConfirmAsync(confirmed.InviteId)).EnsureSuccessStatusCode();

        world.Clock.Offset = TimeSpan.FromMinutes(31);
        await service.SweepAsync();
        (await RowStateAsync(unused.InviteId)).Should().Be("expired", "an issued invitation runs out with its lifetime");
        (await RowStateAsync(redeemed.InviteId)).Should().Be("redeemed", "a redemption waits for the owner for 72 hours, not for the invitation's 30 minutes");

        world.Clock.Offset = TimeSpan.FromHours(73);
        await service.SweepAsync();
        (await RowStateAsync(redeemed.InviteId)).Should().Be("expired");
        (await RowStateAsync(confirmed.InviteId)).Should().Be("confirmed");
        await AssertNoStoredKeyInAnyFinalStateAsync();

        world.Clock.Offset = TimeSpan.FromDays(31);
        await service.SweepAsync();
        (await RowStateAsync(confirmed.InviteId)).Should().Be("(none)", "a row decided at the start is 30 days old");
        (await RowStateAsync(redeemed.InviteId)).Should().Be("expired", "this one was decided at 73 hours, so it is not 30 days old yet");

        world.Clock.Offset = TimeSpan.FromDays(34);
        await service.SweepAsync();
        (await ListAsync()).GetArrayLength().Should().Be(0, "final rows are deleted 30 days after they were decided");
    }

    [Fact]
    public async Task AnExpiredInvitation_AnswersExpiredInTheList_WithoutWaitingForTheSweeper()
    {
        await world.ResetAsync();
        var issued = await IssueAsync();

        world.Clock.Offset = TimeSpan.FromMinutes(45);

        var row = (await ListAsync()).EnumerateArray().Single();
        row.GetProperty("state").GetString().Should().Be("expired");
        (await RedeemAsync(issued.Invitation, Device.New())).StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    // ── what is kept ──────────────────────────────────────────────────────

    [Fact]
    public async Task NoAuditLine_CarriesASecret_ABackupKey_APin_OrTheInvitationText()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        (await RedeemAsync(issued.Invitation, Device.New())).StatusCode.Should().Be(HttpStatusCode.Gone);
        (await ConfirmAsync(issued.InviteId)).EnsureSuccessStatusCode();

        var lines = await AuditAsync();

        lines.Select(l => l.Action).Should().Contain(["invite_issued", "invite_redeemed", "invite_redeem_refused", "invite_confirmed"]);
        var forbidden = new[]
        {
            Base64Url.EncodeToString(issued.Invitation.Secret), Base64Url.EncodeToString(SHA256.HashData(issued.Invitation.Secret)), Base64Url.EncodeToString(device.BackupKey),
            Base64Url.EncodeToString(device.PublicKey), Base64Url.EncodeToString(device.Seed), issued.Text, "bmb-invite:",
        };
        foreach (var (action, details) in lines)
        {
            details.Length.Should().BeLessThan(300, $"{action}: audit lines are short");
            foreach (var secret in forbidden) details.Should().NotContain(secret, $"{action}");
        }
    }

    [Fact]
    public async Task NoInviteRow_IsInAJoinSnapshot_ABlindPackage_OrABaseline()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        (await RowStateAsync(issued.InviteId)).Should().Be("redeemed");
        var scratch = Path.Combine(Path.GetTempPath(), "bmb_invite_snap_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        using var scope = world.Hub.Services.CreateScope();
        var snapshots = scope.ServiceProvider.GetRequiredService<SnapshotService>();
        var info = await snapshots.CreateAsync(filterSecrets: true, sign: true, encryptDb: false);
        var fromSnapshot = await ExtractedDatabaseAsync(snapshots.GetSnapshotPath(info.FileName), Path.Combine(scratch, "snapshot"));
        var package = await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>().BuildAsync(Guid.NewGuid(), includesUpTo: null, producerIsSuperadmin: true);
        var fromPackage = await ExtractedDatabaseAsync(package.FilePath, Path.Combine(scratch, "package"));

        // The plaintext a baseline seals: a VACUUM INTO copy of the live database, filtered the way the baseline producer filters it.
        var staged = Path.Combine(scratch, "baseline.db");
        using (var live = world.Service<DbConnectionFactory>().CreateConnection())
            Dapper.SqlMapper.Execute(live, $"VACUUM INTO '{staged.Replace("'", "''")}'");
        snapshots.FilterForBaseline(staged);

        foreach (var (what, db) in new[] { ("join snapshot", fromSnapshot), ("blind package", fromPackage), ("baseline", staged) })
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False");
            conn.Open();
            Dapper.SqlMapper.ExecuteScalar<long>(conn, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'tbl_blind_invite'").Should().Be(1, $"{what}: the table exists, as it does on every node");
            Dapper.SqlMapper.ExecuteScalar<long>(conn, "SELECT COUNT(*) FROM tbl_blind_invite").Should().Be(0, $"{what}: and holds no invitation");
        }
        SnapshotTables.Replicated.Should().NotContain("tbl_blind_invite");
        BlindPackageBuilder.ExtraTables.Should().NotContain("tbl_blind_invite");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private static async Task<string> ExtractedDatabaseAsync(string archive, string directory)
    {
        Directory.CreateDirectory(directory);
        await using (var gz = new System.IO.Compression.GZipStream(File.OpenRead(archive), System.IO.Compression.CompressionMode.Decompress))
            await System.Formats.Tar.TarFile.ExtractToDirectoryAsync(gz, directory, overwriteFiles: true);
        return Directory.EnumerateFiles(directory, "*.db", SearchOption.AllDirectories).Single();
    }

    // ── helpers for the device side ───────────────────────────────────────

    private sealed record Replica(DbConnectionFactory Factory, BlindPhoneReplicaClient Client, string WorkDirectory);

    private static async Task<Replica> CreateReplicaClientAsync(Device device)
    {
        var data = Path.Combine(Path.GetTempPath(), "bmb_invite_phone_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        var db = Path.Combine(data, "beememorybank.db");
        var factory = new DbConnectionFactory(db);
        await new MigrationRunner(factory).RunMigrationsAsync();
        await new NodeIdentityRepository(factory).CreateAsync(new NodeIdentity
        {
            NodeId = device.NodeId, DisplayName = device.Name, Ed25519PublicKey = device.PublicKey, Ed25519PrivateKey = [],
            Ed25519PrivateKeyV = NodeIdentityCrypto.ExternalKeyVersion, CanGenerateEmbeddings = false, CreatedAt = DateTime.UtcNow
        });
        var client = new BlindPhoneReplicaClient(factory, new NodeIdentityRepository(factory), new SeedSigner(device.Seed), data, db,
            NullLogger<BlindPhoneReplicaClient>.Instance);
        return new Replica(factory, client, Path.Combine(data, "replica"));
    }

    private sealed class SeedSigner(byte[] seed) : INodeAuthSigner
    {
        public byte[] SignChallenge(NodeIdentity identity, byte[] payload) => Ed25519Signer.Sign(seed, payload);
    }

    private sealed class MemoryStore : IBlindPhoneStore
    {
        private readonly Dictionary<string, string> _values = [];
        public string? Get(string key) => _values.GetValueOrDefault(key);

        public void Set(string key, string? value)
        {
            if (value is null) _values.Remove(key);
            else _values[key] = value;
        }
    }
}
