extern alias WebApp;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Card = WebApp::BeeMemoryBank.Web.Services.DevicesOnMyNetworkCard;
using CardKind = WebApp::BeeMemoryBank.Web.Services.DevicesCardKind;
using FirewallStep = WebApp::BeeMemoryBank.Web.Services.DevicesFirewallStep;
using LanAddresses = WebApp::BeeMemoryBank.Web.Services.LanAddresses;
using LanStatus = WebApp::BeeMemoryBank.Web.Services.NodeLanClient.LanStatus;
using NodeLanClient = WebApp::BeeMemoryBank.Web.Services.NodeLanClient;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The "Devices on my network" card of Admin &gt; Nodes (task P7): what it offers on each system and in each state, and the client that
/// carries the switch to the desktop node. The card must say what applies instead of showing a dead switch (Docker), name the macOS
/// prompt instead of a firewall button (Mac), and never offer to edit a firewall the app does not manage (Windows service).
/// </summary>
public class DevicesOnMyNetworkCardTests
{
    private static readonly string[] Urls = ["https://192.168.1.20:5311"];

    private static LanStatus Status(string mode = "on-demand", bool active = false, string setting = "off", string platform = "windows",
        bool managed = true, bool rule = false, string? error = null) =>
        new(mode, active, null, null, "pin", 5311, rule, error, setting, platform, managed);

    [Fact]
    public void NotADesktopNode_HasNoSwitch_AndNothingElseToOffer()
    {
        var card = Card.From(LanStatus.Unavailable, Urls);

        card.Kind.Should().Be(CardKind.NotADesktopNode);
        card.On.Should().BeFalse();
        card.Firewall.Should().Be(FirewallStep.None);
        card.Urls.Should().BeEmpty("a dead address is worse than none");
    }

    [Fact]
    public void Off_ByDefault_ShowsNoAddress_AndOffersNothingAboutTheFirewall()
    {
        var card = Card.From(Status(), Urls);

        card.Kind.Should().Be(CardKind.Switchable);
        card.On.Should().BeFalse();
        card.ListenerFailed.Should().BeFalse();
        card.Urls.Should().BeEmpty();
        card.Firewall.Should().Be(FirewallStep.None);
    }

    [Fact]
    public void On_ShowsTheAddressToOpen_AndOnWindowsAsksForTheRule_UntilItExists()
    {
        var withoutRule = Card.From(Status("permanent", true, "on", rule: false), Urls);
        var withRule = Card.From(Status("permanent", true, "on", rule: true), Urls);

        withoutRule.On.Should().BeTrue();
        withoutRule.Urls.Should().Equal(Urls);
        withoutRule.Firewall.Should().Be(FirewallStep.AddRule);
        withRule.Firewall.Should().Be(FirewallStep.RuleInPlace);
    }

    [Fact]
    public void OffAgain_OnWindows_OffersToRemoveTheRuleThatIsStillThere()
    {
        Card.From(Status("on-demand", false, "off", rule: true), Urls).Firewall.Should().Be(FirewallStep.RemoveRule);
        Card.From(Status("on-demand", false, "off", rule: false), Urls).Firewall.Should().Be(FirewallStep.None);
    }

    [Fact]
    public void OnAMac_NamesTheSystemPrompt_AndOffersNoFirewallButton_AndOnlyWhileOn()
    {
        var on = Card.From(Status("permanent", true, "on", platform: "macos", managed: false), Urls);
        var off = Card.From(Status("on-demand", false, "off", platform: "macos", managed: false), Urls);

        on.Firewall.Should().Be(FirewallStep.MacAsksOnFirstConnection);
        off.Firewall.Should().Be(FirewallStep.None);
    }

    [Fact]
    public void UnderTheWindowsService_SaysTheInstallerOwnsTheFirewall_NotAButton()
    {
        var on = Card.From(Status("permanent", true, "on", platform: "windows", managed: false), Urls);

        on.Firewall.Should().Be(FirewallStep.ServiceInstaller);
    }

    [Fact]
    public void TheEnvironmentOverride_IsOn_ButNotSwitchable()
    {
        var card = Card.From(Status("permanent", true, "environment"), Urls);

        card.Kind.Should().Be(CardKind.ForcedByEnvironment);
        card.On.Should().BeTrue();
        card.Urls.Should().Equal(Urls);
    }

    [Fact]
    public void SwitchedOn_ButTheListenerCouldNotOpen_IsAFailureNotAnOn()
    {
        // The node reports "permanent" only while the listener is really up; the saved setting alone is not "on".
        var card = Card.From(Status("on-demand", false, "on"), Urls);

        card.ListenerFailed.Should().BeTrue();
        card.On.Should().BeFalse();
        card.Urls.Should().BeEmpty();
        card.Firewall.Should().Be(FirewallStep.AddRule, "the rule is still the next thing to check when the user wants it open");
    }

    [Fact]
    public void SwitchedOn_WhileOnlyTheTemporaryDoorIsOpen_IsStillAFailure_NotAnOn()
    {
        var card = Card.From(Status("on-demand", true, "on"), Urls);

        card.ListenerFailed.Should().BeTrue("an open door is not the permanent listener the setting asked for");
        card.On.Should().BeFalse();
    }

    [Fact]
    public void BrowserUrls_AreHttpsOnThePort_OnePerAddress()
    {
        LanAddresses.BrowserUrls(5311, [IPAddress.Parse("192.168.1.20"), IPAddress.Parse("10.0.0.7")])
            .Should().Equal("https://192.168.1.20:5311", "https://10.0.0.7:5311");
    }

    // ─── The client that carries the switch to the node ─────────────────────

    [Fact]
    public async Task TheClient_PostsTheSwitchWithTheInternalKey_AndReadsTheNodesAnswer()
    {
        var seen = new List<(string Method, string Path, string? Key, string Body)>();
        await using var front = await StartFrontAsync(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            seen.Add((ctx.Request.Method, ctx.Request.Path, ctx.Request.Headers["X-Internal-Key"].ToString(), await reader.ReadToEndAsync()));
            await ctx.Response.WriteAsJsonAsync(Status("permanent", true, "on", error: null), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        });
        var client = new NodeLanClient(new HttpClient(), front.DataPath, "the-key");

        var answer = await client.SetDevicesOnMyNetworkAsync(true);

        answer.Setting.Should().Be("on");
        answer.Mode.Should().Be("permanent");
        seen.Should().ContainSingle().Which.Should().Be(("POST", "/node/lan/network", "the-key", "{\"enabled\":true}"));
    }

    [Fact]
    public async Task TheClient_TurnsTheNodesRefusalIntoItsOwnSentence_NotIntoUnavailable()
    {
        await using var front = await StartFrontAsync(async ctx =>
        {
            ctx.Response.StatusCode = 409;
            await ctx.Response.WriteAsJsonAsync(new { error = "BMB_HTTPS_ENABLED=1 is set for this node." });
        });
        var client = new NodeLanClient(new HttpClient(), front.DataPath, "the-key");

        var answer = await client.SetDevicesOnMyNetworkAsync(false);

        answer.Error.Should().Contain("BMB_HTTPS_ENABLED");
    }

    [Fact]
    public async Task TheClient_WithoutARunningNode_AnswersUnavailable_SoDockerGetsTheDockerCard()
    {
        var empty = Path.Combine(Path.GetTempPath(), "bmb_nofront_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            var answer = await new NodeLanClient(new HttpClient(), empty, "the-key").GetAsync();

            answer.IsAvailable.Should().BeFalse();
            Card.From(answer, Urls).Kind.Should().Be(CardKind.NotADesktopNode);
        }
        finally { Directory.Delete(empty, recursive: true); }
    }

    // ─── Harness ────────────────────────────────────────────────────────────

    private sealed class Front(WebApplication app, string dataPath) : IAsyncDisposable
    {
        public string DataPath => dataPath;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
            try { Directory.Delete(dataPath, recursive: true); } catch { /* temp folder */ }
        }
    }

    /// <summary>A stand-in for the node's front: answers every request with <paramref name="handler"/>, and has a <c>.runtime.json</c> that points at it.</summary>
    private static async Task<Front> StartFrontAsync(Func<HttpContext, Task> handler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapFallback(handler);
        await app.StartAsync();

        var dir = Path.Combine(Path.GetTempPath(), "bmb_frontstub_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".runtime.json"), JsonSerializer.Serialize(new { frontUrl = app.Urls.First() }));
        return new Front(app, dir);
    }
}
