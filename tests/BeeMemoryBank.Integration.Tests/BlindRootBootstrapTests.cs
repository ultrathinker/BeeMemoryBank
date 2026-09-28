extern alias WebApp;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Review r1-merge #3, the owner's main scenario: one PC and one blind node. No peer can confirm the PC's
/// standing and no event ever granted it, so its authority on the blind node comes from the operator:
/// the pair code issued at the blind node's console says the device that uses it will manage the node,
/// and the PC's first seed with it makes the PC the blind node's superadmin. Later changes of that
/// authority come only from signed standing events.
/// </summary>
public class BlindRootBootstrapTests : IAsyncLifetime
{
    private const string Password = "rootBootstrapPw1!";

    private readonly BlindNodeFactory _blind = new();
    private readonly BmbWebApplicationFactory _pc = new();
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

    [Fact]
    public async Task ThePairCode_SaysInPlainWords_WhatUsingItGrants()
    {
        using var console = _blind.CreateClient();

        var body = await console.GetFromJsonAsync<JsonElement>("/api/blind/pair-code");

        body.GetProperty("notice").GetString().Should().Be(BlindPairing.AuthorityNotice)
            .And.Contain("will manage this blind node");
    }

    [Fact]
    public async Task ThePcAloneInItsNetwork_IsWarned_NotRefused_AndTheCodeMakesItTheBlindsAuthority()
    {
        var added = await AddBlindNodeAsync();

        added.GetProperty("warnings").EnumerateArray().Select(w => w.GetString())
            .Should().Equal([BlindPreflight.NoConfirmationWarning], "nobody could confirm the PC's standing, and the add goes on");
        var pc = await IdentityAsync(_pc);
        var row = (await _blind.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(pc.NodeId))!;
        row.IsSuperadmin.Should().BeTrue("the PC used the pair code the operator issued at the blind node");
        row.Ed25519PublicKey.Should().Equal(pc.Ed25519PublicKey, "bound to the key the seeder proof named");
    }

    /// <summary>
    /// Review l-root2 #3: the same add through the Web page. The operator is told that the blind node
    /// trusts the PC only because it holds the pair code.
    /// </summary>
    [Fact]
    public async Task TheWebPage_ShowsTheWarning_WhenNobodyCouldConfirmThePc()
    {
        using var console = _blind.CreateClient();
        var code = (await console.GetFromJsonAsync<JsonElement>("/api/blind/pair-code")).GetProperty("code").GetString()!;
        var page = new WebApp::BeeMemoryBank.Web.Pages.BlindNodesModel(new WebApp::BeeMemoryBank.Web.Services.ApiClient(_pcClient));

        var result = await page.OnPostAddAsync(code);

        var redirect = result.Should().BeOfType<RedirectToPageResult>().Subject;
        redirect.RouteValues!["msg"].Should().Be("Blind node added and seeded.");
        redirect.RouteValues["warn"].Should().Be(BlindPreflight.NoConfirmationWarning);
    }

    /// <summary>
    /// A superadmin-only event of the PC is applied, not refused as an ordinary peer's: it is in the log
    /// and the anchor is stored (tbl_state_anchor, R1's applier).
    /// </summary>
    [Fact]
    public async Task ThePcsStateAnchor_IsApplied_AndRecorded()
    {
        await AddBlindNodeAsync();
        var pc = await IdentityAsync(_pc);
        var anchorId = Guid.NewGuid();
        var anchor = SignedByPc(pc, 1_000, EventTypes.StateAnchor, new StateAnchorPayload(anchorId.ToString("D"), new string('a', 64),
            new Dictionary<string, long>(), "sd7:" + new string('b', 64), new string('c', 64), DateTime.UtcNow.ToString("O")));

        using var scope = _blind.Services.CreateScope();
        var apply = () => scope.ServiceProvider.GetRequiredService<EventApplier>().ApplyAsync(anchor);

        (await apply.Should().NotThrowAsync("the PC is superadmin here")).Which.Should().Be(EventApplyResult.Applied);
        (await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().ExistsAsync(anchor.EventId))
            .Should().BeTrue("an applied event is in the blind node's log, to be served on");
        using var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        (await conn.QuerySingleOrDefaultAsync<(string Author, long Lamport)?>(
                "SELECT author_node_id, lamport_ts FROM tbl_state_anchor WHERE anchor_id = @id COLLATE NOCASE", new { id = anchorId.ToString("D") }))
            .Should().NotBeNull("the anchor is stored").And.Match<(string Author, long Lamport)?>(
                a => Guid.Parse(a!.Value.Author) == pc.NodeId && a.Value.Lamport == 1_000);
    }

    [Fact]
    public async Task ThePc_ReseedsTheBlindNode()
    {
        await AddBlindNodeAsync();

        (await ReseedAsync()).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// After the grant, authority changes only by signed standing: here the PC signs its own demotion
    /// (stepping down needs nobody's permission), and the blind node no longer takes its reseed.
    /// </summary>
    [Fact]
    public async Task ALaterSignedDemotion_TakesTheAuthorityAway()
    {
        await AddBlindNodeAsync();
        var pc = await IdentityAsync(_pc);

        using (var scope = _blind.Services.CreateScope())
        {
            var demote = () => scope.ServiceProvider.GetRequiredService<EventApplier>().ApplyAsync(SignedByPc(pc, 2_000,
                EventTypes.WhitelistUpdate, new WhitelistUpdatePayload(pc.NodeId, null, null, IsSuperadmin: false)));
            (await demote.Should().NotThrowAsync("the PC is superadmin here")).Which.Should().Be(EventApplyResult.Applied);
        }

        (await _blind.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(pc.NodeId))!
            .IsSuperadmin.Should().BeFalse();
        (await ReseedAsync()).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<JsonElement> AddBlindNodeAsync()
    {
        using var console = _blind.CreateClient();
        var code = (await console.GetFromJsonAsync<JsonElement>("/api/blind/pair-code")).GetProperty("code").GetString()!;
        var add = await _pcClient.PostAsJsonAsync("/api/blind-nodes/", new { code });
        add.StatusCode.Should().Be(HttpStatusCode.OK, await add.Content.ReadAsStringAsync());
        return await add.Content.ReadFromJsonAsync<JsonElement>();
    }

    private SyncEvent SignedByPc<T>(NodeIdentity pc, long lamport, string type, T payload)
    {
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = pc.NodeId, LamportTs = lamport, EventType = type,
            Payload = JsonSerializer.Serialize(payload), ProtocolVersion = SyncProtocolVersion.Current, CreatedAt = DateTime.UtcNow
        };
        using var scope = _pc.Services.CreateScope();
        evt.Signature = scope.ServiceProvider.GetRequiredService<INodeAuthSigner>().SignChallenge(pc, EventSignature.BuildPayload(evt));
        return evt;
    }

    private async Task<HttpResponseMessage> ReseedAsync()
    {
        var pc = await IdentityAsync(_pc);
        var blindId = (await IdentityAsync(_blind)).NodeId;
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
        var reseed = new HttpRequestMessage(HttpMethod.Post,
            $"/api/blind/seed?seedId={package.Manifest.SeedId}&offset=0&total={bytes.Length}&sha256={package.Sha256}")
        { Content = new ByteArrayContent(bytes) };
        reseed.Headers.Authorization = new("Bearer", token);
        return await http.SendAsync(reseed);
    }

    private static async Task<NodeIdentity> IdentityAsync(BmbWebApplicationFactory node) =>
        (await node.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
}
