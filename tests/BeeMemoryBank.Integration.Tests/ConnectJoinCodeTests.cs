extern alias WebApp;

using System.Net;
using BeeMemoryBank.Core.Models;
using ConnectModel = WebApp::BeeMemoryBank.Web.Pages.ConnectModel;
using LanStatus = WebApp::BeeMemoryBank.Web.Services.NodeLanClient.LanStatus;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// What the Connect page puts on screen for the node's LAN listener (plan section 10): one join code
/// per LAN address carrying the address, the session token and the key pin — and nothing while the
/// listener is off, so a stale code is never shown.
/// </summary>
public class ConnectJoinCodeTests
{
    private const string Pin = "q7m1Xo2W3x0b7mB8pSxYl0Fh3y1rJk3n9gW2hQx1r2A";
    private const string Token = "2uTVKRgqdGVqPDxaGwLmhA";

    [Fact]
    public void ActiveListener_OneJoinCodePerAddress_WithTokenAndPin()
    {
        var status = new LanStatus("on-demand", true, Token, DateTimeOffset.UtcNow.AddMinutes(15), Pin, 5311, true);

        var endpoints = ConnectModel.BuildEndpoints(status, [IPAddress.Parse("192.0.2.20"), IPAddress.Parse("192.0.2.5")]);

        endpoints.Select(e => e.Url).Should().Equal("https://192.0.2.20:5311", "https://192.0.2.5:5311");
        JoinCode.TryParse(endpoints[0].Code, out var code).Should().BeTrue();
        code.Should().Be(new JoinCode("https://192.0.2.20:5311", Token, Pin));
        endpoints[0].QrDataUri.Should().StartWith("data:image/png;base64,");
    }

    [Fact]
    public void PermanentListener_CodeWithoutToken()
    {
        var status = new LanStatus("permanent", true, null, null, Pin, 5311, true);

        var endpoint = ConnectModel.BuildEndpoints(status, [IPAddress.Parse("192.0.2.20")]).Single();

        JoinCode.TryParse(endpoint.Code, out var code).Should().BeTrue();
        code!.Token.Should().BeNull();
    }

    [Fact]
    public void ListenerOff_OrNoPin_NoCodes()
    {
        var addresses = new[] { IPAddress.Parse("192.0.2.20") };

        // A closed session with its old token and pin still in hand must not show them.
        ConnectModel.BuildEndpoints(new LanStatus("on-demand", false, Token, null, Pin, 5311, true), addresses)
            .Should().BeEmpty("a code for a closed listener would send the phone nowhere");
        ConnectModel.BuildEndpoints(new LanStatus("on-demand", true, Token, null, null, 5311, true), addresses)
            .Should().BeEmpty("without a pin the phone could not check whom it sends the password to");
        ConnectModel.BuildEndpoints(LanStatus.Unavailable, addresses).Should().BeEmpty();
    }
}
