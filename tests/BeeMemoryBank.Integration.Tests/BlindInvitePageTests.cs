extern alias WebApp;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Blind;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using ApiClient = WebApp::BeeMemoryBank.Web.Services.ApiClient;
using BlindNodesModel = WebApp::BeeMemoryBank.Web.Pages.BlindNodesModel;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// "Invite a blind copy" on the Blind nodes page (release 2.7), driven the way the page's handlers are: through its model and the Web layer's
/// <c>ApiClient</c> against a real hub. The page shows the invitation once, in the response to the request that made it; a redemption appears in the
/// waiting list with its match code; confirming needs the master password.
/// </summary>
[Collection(BlindInviteCollection.Name)]
public sealed class BlindInvitePageTests(BlindInviteWorld world)
{
    private BlindNodesModel NewPage() => new(new ApiClient(world.Hub.Admin))
    {
        PageContext = new PageContext { HttpContext = new DefaultHttpContext { Request = { Scheme = "https", Host = new HostString("mem.example.org") } } }
    };

    private static (Guid NodeId, byte[] Seed, byte[] Key, byte[] Backup) NewDevice()
    {
        var (key, seed) = Ed25519Signer.GenerateKeyPair();
        return (BlindNodeId.NewId(), seed, key, RandomNumberGenerator.GetBytes(32));
    }

    private async Task<HttpStatusCode> RedeemAsync(BlindInvitation invitation, (Guid NodeId, byte[] Seed, byte[] Key, byte[] Backup) device, string name)
    {
        var request = new BlindInviteRedeemRequest(invitation.InviteId, invitation.Secret, device.NodeId, device.Key, name, "android", "2.7.0", 4, device.Backup, []);
        request = request with
        {
            Proof = Ed25519Signer.Sign(device.Seed, BlindInviteProofs.RedeemMessage(invitation.InviteId, invitation.HubNodeId, device.NodeId, device.Key, device.Backup))
        };
        using var content = new ByteArrayContent(request.ToJson());
        return (await world.Public.PostAsync("/api/sync/invite/redeem", content)).StatusCode;
    }

    [Fact]
    public async Task ThePage_MakesTheInvitation_ShowsItOnce_AndDrawsItsQr()
    {
        await world.ResetAsync();
        var page = NewPage();

        await page.OnGetAsync(null, null, null, null);
        page.InviteAddress.Should().Be("https://mem.example.org", "with nothing declared, the page's own address is what the form starts from");
        page.Issued.Should().BeNull();

        var result = await page.OnPostInviteAsync("Anna's phone", "Alex", 30, world.Hub.Origin);

        result.Should().BeOfType<PageResult>("the invitation is shown in this response, not carried by a redirect");
        page.InviteError.Should().BeNull();
        page.Issued.Should().NotBeNull();
        BlindInvitation.TryParse(page.Issued!.Text, out var invitation).Should().BeTrue();
        invitation!.SourceName.Should().Be("Alex");
        page.IssuedQr.Should().StartWith("data:image/png;base64,");
        page.Response.Headers.CacheControl.ToString().Should().Be("no-store", "the page holds a bearer secret");
        page.OpenInvites.Should().ContainSingle(i => i.InviteId == page.Issued.InviteId && i.Label == "Anna's phone");

        var again = NewPage();
        await again.OnGetAsync(null, null, null, null);
        again.Issued.Should().BeNull("a second visit does not show the string again");
        again.OpenInvites.Should().ContainSingle();
    }

    [Fact]
    public async Task ARefusedInvitation_ShowsTheApisSentence_AndKeepsWhatTheOperatorTyped()
    {
        await world.ResetAsync();
        var page = NewPage();

        await page.OnPostInviteAsync("Anna", "Alex", 30, "https://localhost:1");

        page.Issued.Should().BeNull();
        page.InviteError.Should().NotBeNullOrWhiteSpace();
        page.InviteLabel.Should().Be("Anna");
        page.InviteAddress.Should().Be("https://localhost:1");
        page.Invites.Should().BeEmpty("a failed probe makes no invitation");
    }

    [Fact]
    public async Task ARedemption_AppearsWithItsMatchCode_AndConfirmingNeedsThePassword()
    {
        await world.ResetAsync();
        var page = NewPage();
        await page.OnPostInviteAsync("Anna", "Alex", 30, world.Hub.Origin);
        BlindInvitation.TryParse(page.Issued!.Text, out var invitation).Should().BeTrue();
        var device = NewDevice();
        (await RedeemAsync(invitation!, device, "Pixel of Anna")).Should().Be(HttpStatusCode.Accepted);

        var waiting = NewPage();
        await waiting.OnGetAsync(null, null, null, null);
        var request = waiting.WaitingInvites.Should().ContainSingle().Which;
        request.MatchCode.Should().Be(invitation!.MatchCodeFor(device.NodeId, device.Key));
        request.DeviceName.Should().Be("Pixel of Anna");
        waiting.OpenInvites.Should().BeEmpty("a redeemed invitation is no longer open");

        var wrong = await waiting.OnPostConfirmInviteAsync(request.InviteId, "not the password");
        wrong.Should().BeOfType<RedirectToPageResult>().Which.RouteValues!["err"].Should().Be("Invalid master password");
        using (var scope = world.Hub.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(device.NodeId)).Should().BeNull();

        var right = await waiting.OnPostConfirmInviteAsync(request.InviteId, BlindInviteWorld.Password);
        right.Should().BeOfType<RedirectToPageResult>().Which.RouteValues!.Should().ContainKey("msg").And.NotContainKey("err");
        using (var scope = world.Hub.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(device.NodeId))!.DisplayName.Should().Be("Anna");

        var after = NewPage();
        await after.OnGetAsync(null, null, null, null);
        after.WaitingInvites.Should().BeEmpty();
        after.DecidedInvites.Should().ContainSingle(i => i.State == "confirmed");
    }

    [Fact]
    public async Task AServerNodesRedemption_AppearsAsInvitedServer_WithTheSameMatchCodeAsADevices()
    {
        await world.ResetAsync();
        var page = NewPage();
        await page.OnPostInviteAsync("Anna's server", "Alex", 30, world.Hub.Origin);
        BlindInvitation.TryParse(page.Issued!.Text, out var invitation).Should().BeTrue();
        var (key, seed) = Ed25519Signer.GenerateKeyPair();
        var nodeId = BlindNodeId.NewId();
        var pairCode = new BlindPairCode(nodeId, Convert.ToBase64String(key), "https://server.example.org:5610", "pin", new string('a', 64), DateTime.UtcNow.AddHours(73)).ToString();
        var proof = Ed25519Signer.Sign(seed, BlindInviteProofs.ServerRedeemMessage(invitation!.InviteId, invitation.HubNodeId, nodeId, key, pairCode));
        var request = new BlindInviteRedeemRequest(invitation.InviteId, invitation.Secret, nodeId, key, "Server of Anna", "linux", "2.7.0", 4, null, proof, pairCode);
        using var content = new ByteArrayContent(request.ToJson());

        (await world.Public.PostAsync("/api/sync/invite/redeem", content)).StatusCode.Should().Be(HttpStatusCode.Accepted);

        var waiting = NewPage();
        await waiting.OnGetAsync(null, null, null, null);
        var row = waiting.WaitingInvites.Should().ContainSingle().Which;
        row.Kind.Should().Be("server");
        row.MatchCode.Should().Be(invitation.MatchCodeFor(nodeId, key));
        row.DeviceName.Should().Be("Server of Anna");
    }

    [Fact]
    public async Task RefusingAndWithdrawing_GoThroughThePage()
    {
        await world.ResetAsync();
        var page = NewPage();
        await page.OnPostInviteAsync("Anna", "Alex", 30, world.Hub.Origin);
        BlindInvitation.TryParse(page.Issued!.Text, out var redeemedInvitation).Should().BeTrue();
        await page.OnPostInviteAsync("Bob", "Alex", 15, world.Hub.Origin);
        BlindInvitation.TryParse(page.Issued!.Text, out _).Should().BeTrue();
        (await RedeemAsync(redeemedInvitation!, NewDevice(), "Stranger")).Should().Be(HttpStatusCode.Accepted);

        var model = NewPage();
        await model.OnGetAsync(null, null, null, null);
        var waiting = model.WaitingInvites.Should().ContainSingle().Which;
        var open = model.OpenInvites.Should().ContainSingle().Which;

        (await model.OnPostRefuseInviteAsync(waiting.InviteId)).Should().BeOfType<RedirectToPageResult>().Which.RouteValues!.Should().ContainKey("msg");
        (await model.OnPostWithdrawInviteAsync(open.InviteId)).Should().BeOfType<RedirectToPageResult>().Which.RouteValues!.Should().ContainKey("msg");
        (await model.OnPostRefuseInviteAsync(waiting.InviteId)).Should().BeOfType<RedirectToPageResult>().Which.RouteValues!.Should().ContainKey("err", "a second refusal is told it is too late");

        var after = NewPage();
        await after.OnGetAsync(null, null, null, null);
        after.WaitingInvites.Should().BeEmpty();
        after.OpenInvites.Should().BeEmpty();
        after.DecidedInvites.Select(i => i.State).Should().BeEquivalentTo(["refused", "expired"]);
    }

    [Fact]
    public async Task TheDefaults_NameTheNodeAndTheLifetimes()
    {
        await world.ResetAsync();

        var defaults = await world.Hub.Admin.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/blind-nodes/invites/defaults");

        defaults.GetProperty("sourceName").GetString().Should().Be("Hub");
        defaults.GetProperty("lifetimes").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(15, 30, 60);
        defaults.GetProperty("defaultLifetime").GetInt32().Should().Be(30);
    }

    /// <summary>
    /// The Web layer's copy of the issued invitation is a record too: a generated ToString() would print the bearer string into any log line or exception dump
    /// that formats it.
    /// </summary>
    [Fact]
    public void TheWebLayersIssuedInvitation_PrintsNoCapability()
    {
        var dto = new WebApp::BeeMemoryBank.Web.Models.BlindInviteIssuedDto("abcdef", "bmb-invite:?v=1&k=SECRETMATERIAL", DateTime.UtcNow, "https://hub.example", "public-ca", null, null);

        foreach (var printed in new[] { dto.ToString(), $"{dto}", string.Format("{0}", dto) })
            printed.Should().NotContain("SECRETMATERIAL").And.NotContain("bmb-invite:");
    }

    /// <summary>
    /// The grep the review asked for, kept as a test: every record of the invitation code (the Api service and endpoints, the Core codec and the Vault row, the
    /// Web layer's DTOs) that has a text member which could hold bearer material - the invitation, a secret, a password, a token, a key - declares its own
    /// ToString(). A byte[] member prints only its type name, so only strings are looked at.
    /// </summary>
    [Fact]
    public void EveryRecordOfTheInvitationCode_WithABearerSounding_StringMember_HasItsOwnToString()
    {
        var assemblies = new[]
        {
            typeof(BeeMemoryBank.Api.Services.BlindInviteService).Assembly,
            typeof(BlindInvitation).Assembly,
            typeof(BlindInviteRecord).Assembly,
            typeof(WebApp::BeeMemoryBank.Web.Models.BlindInviteDto).Assembly,
        };
        var bearer = new System.Text.RegularExpressions.Regex("(?i)(secret|password|text|token|capability|backupkey|invitation|passphrase)");
        var offenders = new List<string>();

        foreach (var type in assemblies.SelectMany(a => a.GetTypes()).Where(t => t.FullName is { } n && n.Contains("BlindInvit", StringComparison.OrdinalIgnoreCase)))
        {
            var isRecord = type.GetMethod("<Clone>$") is not null;
            if (!isRecord) continue;
            var risky = type.GetProperties().Where(p => p.PropertyType == typeof(string) && bearer.IsMatch(p.Name)).Select(p => p.Name).ToList();
            var declaresToString = type.GetMethod("ToString", Type.EmptyTypes)!.DeclaringType == type;
            if (risky.Count > 0 && !declaresToString) offenders.Add($"{type.FullName} ({string.Join(", ", risky)})");
        }

        offenders.Should().BeEmpty("a generated ToString() prints every member into a log or an exception dump");
    }
}
