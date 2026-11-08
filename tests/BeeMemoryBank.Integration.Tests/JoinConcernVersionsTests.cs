using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Versions per concern (migration 045) through the host's own writers and a real join. The rename and the demotion endpoints stamp only the
/// field they change; the join answer carries the version of each field of each active peer in the optional <c>versions</c>; a joiner of this
/// version takes them, so it judges a later event about that peer as the host does. A 2.7.5 joiner reads the answer as before, and a joiner of
/// this version that joins a 2.7.5 host (no versions) writes the peers unversioned, where a late add changes no field.
/// </summary>
public class JoinConcernVersionsTests : IAsyncLifetime
{
    private const string Password = "joinConcernVersionsPassword1";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly Guid _laptop = Guid.NewGuid();

    private BmbWebApplicationFactory _host = null!;
    private HttpClient _hostClient = null!;
    private SyncEvent _add = null!;
    private RowVersion _rename;
    private RowVersion _demote;

    public async Task InitializeAsync()
    {
        BeeMemoryBank.Api.Middleware.RateLimitMiddleware.ResetForTests();
        _host = new BmbWebApplicationFactory();
        _hostClient = _host.CreateClient();
        await _host.InitializeNodeAsync("ConcernHost", Password);
        (await _hostClient.PostAsJsonAsync("/api/session/unlock", new { Password })).EnsureSuccessStatusCode();
        await StepANode.ActivateAsync(_host);

        using (var scope = _host.Services.CreateScope())
        {
            var whitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
            var events = scope.ServiceProvider.GetRequiredService<IEventLogger>();
            var now = DateTime.UtcNow;
            var laptop = new WhitelistEntry
            {
                NodeId = _laptop, DisplayName = "Laptop", Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey, ApiAddress = "https://laptop.example:5301",
                Status = WhitelistStatuses.Active, IsSuperadmin = true, CreatedAt = now, UpdatedAt = now
            };
            // As PeerAddressService.AdmitAsync writes a new peer: the add first, every concern at its version.
            var added = await events.LogWhitelistAddAsync(laptop);
            laptop.StampLocal(added, WhitelistEventConcerns.OfAdd);
            await whitelist.CreateAsync(laptop);
            _add = (await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetAfterSequenceAsync(0, 100_000))
                .Single(e => e.EventType == EventTypes.WhitelistAdd && e.Payload.Contains(_laptop.ToString()));
        }

        (await _hostClient.PutAsJsonAsync($"/api/whitelist/{_laptop}", new { displayName = "Laptop (office)" })).EnsureSuccessStatusCode();
        (await _hostClient.PutAsJsonAsync($"/api/whitelist/{_laptop}/superadmin", new { isSuperadmin = false })).EnsureSuccessStatusCode();
        using (var scope = _host.Services.CreateScope())
        {
            var log = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetAfterSequenceAsync(0, 100_000);
            var updates = log.Where(e => e.EventType == EventTypes.WhitelistUpdate && e.Payload.Contains(_laptop.ToString())).ToList();
            _rename = updates.Where(e => e.Payload.Contains("Laptop (office)")).Select(e => new RowVersion(e.LamportTs, e.NodeId)).Single();
            _demote = updates.Where(e => e.Payload.Contains("\"is_superadmin\":false")).Select(e => new RowVersion(e.LamportTs, e.NodeId)).Single();
        }
    }

    public Task DisposeAsync()
    {
        BeeMemoryBank.Api.Middleware.RateLimitMiddleware.ResetForTests();
        _hostClient.Dispose();
        _host.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TheRenameAndTheDemotionEndpoints_StampOnlyTheFieldTheyChange()
    {
        using var scope = _host.Services.CreateScope();
        var row = (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(_laptop))!;
        var add = new RowVersion(_add.LamportTs, _add.NodeId);

        row.VersionOf(WhitelistConcerns.Membership).Should().Be(add);
        row.VersionOf(WhitelistConcerns.Name).Should().Be(_rename);
        row.VersionOf(WhitelistConcerns.Superadmin).Should().Be(_demote);
        row.VersionOf(WhitelistConcerns.Address).Should().Be(add);
        row.VersionOf(WhitelistConcerns.Tls).Should().Be(add);
        row.Version.Should().Be(_demote, "the row's own version is the newest of its fields");
    }

    [Fact]
    public async Task TheJoinAnswer_CarriesTheVersionOfEachField_AndA275JoinerReadsItAsBefore()
    {
        var answer = await JoinAnswerAsync();

        var entry = answer.GetProperty("whitelist").EnumerateArray().Single(e => e.GetProperty("nodeId").GetGuid() == _laptop);
        var versions = entry.GetProperty("versions");
        Version(versions.GetProperty("membership")).Should().Be(new RowVersion(_add.LamportTs, _add.NodeId));
        Version(versions.GetProperty("name")).Should().Be(_rename);
        Version(versions.GetProperty("superadmin")).Should().Be(_demote);

        var old = JsonSerializer.Deserialize<Release275JoinResponse>(answer.GetRawText(), Release275JsonOptions);
        old!.Whitelist!.Single(e => e.NodeId == _laptop).Should().Be(
            new Release275WhitelistEntry(_laptop, "Laptop (office)", entry.GetProperty("ed25519PublicKeyB64").GetString()!, "https://laptop.example:5301"),
            "a 2.7.5 joiner ignores the member it does not know");
    }

    [Fact]
    public async Task TheSetupPagesJoin_GivesThePeerTheHostsVersionOfEachField()
    {
        using var joiner = await JoinThroughSetupAsync(_host.Server.CreateHandler());

        using var scope = joiner.Services.CreateScope();
        var row = (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(_laptop))!;
        row.VersionOf(WhitelistConcerns.Name).Should().Be(_rename);
        row.VersionOf(WhitelistConcerns.Superadmin).Should().Be(_demote);
        row.VersionOf(WhitelistConcerns.Membership).Should().Be(new RowVersion(_add.LamportTs, _add.NodeId));
        (row.IsSuperadmin, row.DisplayName).Should().Be((false, "Laptop (office)"));

        // The host's add arrives again from some other node's log: older than every field here, it changes nothing.
        (await scope.ServiceProvider.GetRequiredService<EventApplier>().ApplyAsync(_add)).Should().Be(EventApplyResult.Applied);
        var after = (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(_laptop))!;
        (after.IsSuperadmin, after.DisplayName).Should().Be((false, "Laptop (office)"));
    }

    /// <summary>A 2.7.5 host sends no versions: the peer is written unversioned, and the host's own add, arriving late, does not make it a superadmin again.</summary>
    [Fact]
    public async Task AJoinerOfA275Host_WritesThePeerUnversioned_AndALateAddChangesNoField()
    {
        using var joiner = await JoinThroughSetupAsync(new As275Host(_host.Server.CreateHandler()));

        using var scope = joiner.Services.CreateScope();
        var rows = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        var row = (await rows.GetByNodeIdAsync(_laptop))!;
        foreach (var concern in WhitelistConcernSet.Each) row.VersionOf(concern).LamportTs.Should().Be(0, concern.ToString());

        (await scope.ServiceProvider.GetRequiredService<EventApplier>().ApplyAsync(_add)).Should().Be(EventApplyResult.Applied);

        var after = (await rows.GetByNodeIdAsync(_laptop))!;
        (after.IsSuperadmin, after.DisplayName).Should().Be((false, "Laptop (office)"), "an add that cannot be dated against the host's word changes no field");
        after.VersionOf(WhitelistConcerns.Membership).Should().Be(new RowVersion(_add.LamportTs, _add.NodeId));
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static RowVersion Version(JsonElement v) => RowVersion.Of(v.GetProperty("lamportTs").GetInt64(),
        v.GetProperty("sourceNodeId").ValueKind == JsonValueKind.Null ? null : v.GetProperty("sourceNodeId").GetGuid());

    private async Task<JsonElement> JoinAnswerAsync()
    {
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();
        var resp = await _hostClient.PostAsJsonAsync("/api/join", new
        {
            masterPassword = Password,
            nodeId = Guid.NewGuid(),
            displayName = "Joiner",
            ed25519PublicKeyB64 = Convert.ToBase64String(publicKey),
            apiAddress = (string?)null,
            protocolVersion = BeeMemoryBank.Sync.Metadata.EventClasses.SealedProtocol
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
    }

    private static async Task<BmbWebApplicationFactory> JoinThroughSetupAsync(HttpMessageHandler toHost)
    {
        var joiner = new BmbWebApplicationFactory();
        joiner.RouteOutboundHttpThrough(toHost);
        var resp = await joiner.CreateClient().PostAsJsonAsync("/api/init/join", new
        {
            adminUsername = "admin",
            passwordHint = "A test hint",
            displayName = "Joiner",
            remoteUrl = "http://host",
            password = Password
        }, JsonOpts);
        resp.IsSuccessStatusCode.Should().BeTrue(await resp.Content.ReadAsStringAsync());
        (await joiner.Services.GetRequiredService<SessionService>().UnlockAsync(Password)).Should().BeTrue();
        return joiner;
    }

    /// <summary>The answer of <c>POST /api/join</c> as a 2.7.5 host sends it: no versions, no <c>revokedPeers</c>.</summary>
    private sealed class As275Host(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath != "/api/join" || !response.IsSuccessStatusCode) return response;
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
            body.Remove("revokedPeers");
            foreach (var entry in body["whitelist"]!.AsArray())
            {
                entry!.AsObject().Remove("lamportTs");
                entry.AsObject().Remove("sourceNodeId");
                entry.AsObject().Remove("versions").Should().BeTrue("this host's answer carries the versions the test takes out");
            }
            response.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            return response;
        }
    }

    // The answer type and serializer options of the joiners of release 2.7.5.
    private static readonly JsonSerializerOptions Release275JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record Release275JoinResponse(Release275RemoteNode RemoteNode, List<Release275WhitelistEntry>? Whitelist);

    private sealed record Release275RemoteNode(Guid NodeId, string DisplayName, string Ed25519PublicKeyB64, int ProtocolVersion);

    private sealed record Release275WhitelistEntry(
        Guid NodeId, string DisplayName, string Ed25519PublicKeyB64, string? ApiAddress, bool IsSuperadmin = false, string? TlsTrust = null, string? TlsSpki = null);
}
