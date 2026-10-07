using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Models;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Task P7, the Setup page's "Connect to another device": the join code the other computer's Connect a device card shows is pasted, the
/// address comes from it (not from what is typed), a damaged code is refused on the page, and what the Api answers reaches the person
/// in words. The Api behind the page is the real one; the other computer is a closed port, so a valid code ends at "did not answer".
/// The join itself, against a real door, is <see cref="JoinByCodeThroughTheDoorTests"/>.
/// </summary>
public sealed class SetupJoinCodeTests : IAsyncLifetime
{
    private const string Pin = "q7m1Xo2W3x0b7mB8pSxYl0Fh3y1rJk3n9gW2hQx1r2A";
    private const string Token = "2uTVKRgqdGVqPDxaGwLmhA";

    private static readonly Regex TokenField = new(
        "__RequestVerificationToken[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    private static readonly string SetupScript = File.ReadAllText(Path.Combine(
        FindRepoRoot(), "server", "BeeMemoryBank.Web", "wwwroot", "js", "pages", "setup.js"));

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();

    public Task InitializeAsync()
    {
        using var _ = _api.CreateClient(); // builds the API host; the node stays uninitialized
        _web.RouteOutboundHttpThrough(_api.Server.CreateHandler());
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        ((IDisposable)_api).Dispose();
        ((IDisposable)_web).Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TheJoinForm_AsksForTheCodeFirst_AndNoLongerRequiresAnAddress()
    {
        using var browser = _web.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await (await browser.GetAsync("/Setup")).Content.ReadAsStringAsync();

        html.Should().MatchRegex("<sl-input name=\"joinCode\" id=\"join-code\"[^>]*label=\"Join code from the other computer\"");
        html.Should().MatchRegex("<sl-input name=\"remoteUrl\" id=\"join-remote-url\"(?![^>]*\\brequired\\b)",
            "with a code the address is not typed, so the browser must not insist on one");
        html.Should().Contain("Connect a device").And.Contain("Devices on my network",
            "the scan button says which computers it can find, and where the code comes from");
    }

    [Fact]
    public async Task ADamagedCode_IsRefusedOnThePage_WithoutAskingTheApi()
    {
        var html = await PostJoinAsync(joinCode: "bmb-join:?a=http%3A%2F%2F192.0.2.1&t=x&s=y");

        html.Should().Contain("That join code is not valid");
    }

    [Fact]
    public async Task NeitherACodeNorAnAddress_SaysWhichOfTheTwoIsMissing()
    {
        var html = await PostJoinAsync(joinCode: "", remoteUrl: "");

        html.Should().Contain("Paste the join code from the other computer, or type its address.");
    }

    [Fact]
    public async Task AValidCode_IsHandedToTheApi_AndTheClosedDoorIsSaidInWords()
    {
        var code = new JoinCode($"https://127.0.0.1:{FreePort()}", Token, Pin).ToString();

        var html = await PostJoinAsync(joinCode: code);

        html.Should().Contain("The other computer did not answer").And.Contain("15 minutes",
            "a closed door is the usual reason: Connect a device stays open for 15 minutes");
        html.Should().NotContain("Cannot reach remote node", "the code's own sentence replaces the raw connection error");
    }

    [Fact]
    public async Task TheCodesAddress_WinsOverWhatWasTyped()
    {
        var code = new JoinCode($"https://127.0.0.1:{FreePort()}", Token, Pin).ToString();

        var html = await PostJoinAsync(joinCode: code, remoteUrl: "https://elsewhere.invalid");

        // Had the typed address been used, the answer would be about elsewhere.invalid (a name that does not resolve).
        html.Should().Contain("The other computer did not answer");
        html.Should().NotContain("elsewhere.invalid");
    }

    [Fact]
    public void TheScript_ReadsTheCodesAddressForDisplay_AndExplainsAnEmptyScan()
    {
        SetupScript.Should().Contain("bmb-join:?").And.Contain("join-code").And.Contain("join-remote-url");
        SetupScript.Should().Contain("Devices on my network", "an empty scan says which computers can be found");
        SetupScript.Should().Contain("Connect a device", "and where the code to use instead comes from");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private async Task<string> PostJoinAsync(string joinCode, string remoteUrl = "")
    {
        using var browser = _web.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var form = await browser.GetAsync("/Setup");
        var token = TokenField.Match(await form.Content.ReadAsStringAsync()).Groups[1].Value;

        using var post = new HttpRequestMessage(HttpMethod.Post, "/Setup?handler=Join")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["joinCode"] = joinCode,
                ["remoteUrl"] = remoteUrl,
                ["joinAdminUsername"] = "admin",
                ["joinDisplayName"] = "NodeB",
                ["joinPassword"] = "doorJoinPassword123",
                ["__RequestVerificationToken"] = token,
            }),
        };
        post.Headers.TryAddWithoutValidation("Cookie", string.Join("; ",
            form.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])));

        using var answer = await browser.SendAsync(post);
        answer.StatusCode.Should().Be(HttpStatusCode.OK, "a refused join shows the form again with the reason");
        return await answer.Content.ReadAsStringAsync();
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }
}
