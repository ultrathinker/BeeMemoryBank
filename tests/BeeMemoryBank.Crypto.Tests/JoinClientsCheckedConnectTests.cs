using System.Net;

namespace BeeMemoryBank.Crypto.Tests;

/// <summary>
/// BMB-307 fix round 3 (G5): the clients that carry the other computer's join - the one for a typed address, the one pinned by a join code, the pinned handler, and the
/// no-redirect client of a typed address (the Web setup join, the probe relay) - used ordinary handlers, so a NAME that resolves to a link-local or cloud-metadata address
/// was dialled all the same (only the scheduler and the hub probe had the checked connect). Every one of them now opens its sockets through <see cref="PeerSocketConnector"/>
/// (at the handler seam, with the test's own resolver) and uses no proxy; loopback and private LAN addresses stay allowed.
/// </summary>
public class JoinClientsCheckedConnectTests
{
    private static readonly string Pin = new('A', 43);

    public static IEnumerable<object[]> Clients() =>
    [
        ["the join client of a typed address"],
        ["the join client pinned by a code"],
        ["the pinned handler"],
        ["the no-redirect handler"],
    ];

    private static HttpClient ClientFor(string kind, PeerNetwork network) => kind switch
    {
        "the join client of a typed address" => JoinHttp.CreateClient(null, network),
        "the join client pinned by a code" => JoinHttp.CreateClient(Pin, network),
        "the pinned handler" => new HttpClient(SpkiPin.CreatePinnedHandler(Pin, network)),
        _ => new HttpClient(PeerHttp.NoRedirectHandler(network)),
    };

    private sealed class Resolver(params IPAddress[][] answers)
    {
        public List<string> Asked { get; } = [];

        public PeerNetwork Network => new(PeerNetworkRules.IsAllowed, (host, _) =>
        {
            lock (Asked) Asked.Add(host);
            return Task.FromResult(answers[Math.Min(Asked.Count, answers.Length) - 1]);
        });
    }

    private static IPAddress[] Of(params string[] addresses) => addresses.Select(IPAddress.Parse).ToArray();

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task A_name_that_resolves_to_a_cloud_metadata_address_is_never_dialled(string kind)
    {
        var resolver = new Resolver(Of("169.254.169.254"));
        using var client = ClientFor(kind, resolver.Network);

        var join = () => client.PostAsync("http://alias.example:5300/api/join", new StringContent("{\"masterPassword\":\"never-sent\"}"));

        var failure = (await join.Should().ThrowAsync<HttpRequestException>()).Which;
        PeerSocketConnector.IsRefusal(failure).Should().BeTrue("the connect step refused the address, before a socket was opened");
        resolver.Asked.Should().Equal("alias.example");
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task One_forbidden_answer_among_good_ones_refuses_the_name(string kind)
    {
        var resolver = new Resolver(Of("10.0.0.7", "169.254.169.254", "192.168.1.5"));
        using var client = ClientFor(kind, resolver.Network);

        var join = () => client.GetAsync("http://mixed.example:5300/");

        var failure = (await join.Should().ThrowAsync<HttpRequestException>()).Which;
        PeerSocketConnector.IsRefusal(failure).Should().BeTrue("a name that resolves to a metadata address anywhere in its answer is not dialled at all");
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task A_name_that_resolves_to_a_link_local_or_unspecified_address_is_refused(string kind)
    {
        foreach (var address in new[] { "fe80::1", "0.0.0.0", "::" })
        {
            var resolver = new Resolver(Of(address));
            using var client = ClientFor(kind, resolver.Network);

            var join = () => client.GetAsync("http://elsewhere.example:5300/");

            PeerSocketConnector.IsRefusal((await join.Should().ThrowAsync<HttpRequestException>()).Which).Should().BeTrue(address);
        }
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task A_name_that_resolves_differently_the_second_time_is_checked_again_and_loopback_is_allowed(string kind)
    {
        await using var server = JoinHttpTests.PlainServer.Start("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
        await using var second = JoinHttpTests.PlainServer.Start("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
        var resolver = new Resolver(Of("127.0.0.1"), Of("169.254.169.254"));
        using var client = ClientFor(kind, resolver.Network);

        using (var first = await client.GetAsync($"http://rebind.example:{server.Port}/"))
            first.StatusCode.Should().Be(HttpStatusCode.OK, "a loopback address is a place a node may call");

        var rebound = () => client.GetAsync($"http://rebind.example:{second.Port}/");

        PeerSocketConnector.IsRefusal((await rebound.Should().ThrowAsync<HttpRequestException>()).Which).Should().BeTrue("the second answer of the name is not the first");
        resolver.Asked.Should().Equal("rebind.example", "rebind.example");
        (await second.ReceivedOrNothingAsync()).Should().BeEmpty("nothing was sent to the second place");
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("10.0.0.7")]
    [InlineData("172.16.4.2")]
    [InlineData("192.168.1.5")]
    [InlineData("fd12:3456::7")]
    public void Loopback_and_private_LAN_addresses_are_allowed(string address) =>
        PeerNetworkRules.IsAllowed(IPAddress.Parse(address)).Should().BeTrue();

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.0.7")]
    [InlineData("100.100.100.200")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("fe80::1")]
    [InlineData("::")]
    [InlineData("fd00:ec2::254")]
    [InlineData("::ffff:169.254.169.254")]
    public void Metadata_link_local_unspecified_and_multicast_addresses_are_refused(string address) =>
        PeerNetworkRules.IsAllowed(IPAddress.Parse(address)).Should().BeFalse();

    [Fact]
    public void The_no_redirect_handler_follows_no_redirect_uses_no_proxy_and_has_the_checked_connect()
    {
        using var handler = PeerHttp.NoRedirectHandler();

        handler.AllowAutoRedirect.Should().BeFalse();
        handler.UseProxy.Should().BeFalse("a proxy would resolve and connect on this node's behalf, and the check would judge the proxy");
        handler.ConnectCallback.Should().NotBeNull();
    }
}
