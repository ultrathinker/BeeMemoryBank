using System.Net;
using System.Net.Sockets;
using System.Text;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Sync.Blind;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// BMB-307 fix round 2 (T2): the ordinary sync client resolves a name itself and refuses every address a node may not call, where the socket is opened. A literal
/// address is judged when it is typed; a NAME passes that check and then resolves to whatever its DNS says - an alias of the cloud metadata address, an answer
/// that mixes a good address with a bad one, an answer that changes between the check and the connection. Loopback and private LAN stay allowed.
/// </summary>
public class PeerSocketConnectorTests : IAsyncLifetime
{
    private sealed class Fixture : SyncTestFixture { }

    private readonly Fixture _node = new();
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private int _accepted;

    public async Task InitializeAsync()
    {
        await _node.InitializeAsync();
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public async Task DisposeAsync()
    {
        _listener.Stop();
        await _node.DisposeAsync();
    }

    private int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>A minimal HTTP/1.1 server: every connection is counted, every request is answered 200 with a one-word body.</summary>
    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch (Exception) { return; }
            Interlocked.Increment(ref _accepted);
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    try
                    {
                        while (await stream.ReadAsync(buffer) > 0)
                            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
                    }
                    catch (Exception) { }
                }
            });
        }
    }

    private sealed class Resolver(params IPAddress[][] answers)
    {
        private int _calls;
        public int Calls => _calls;

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _calls);
            return Task.FromResult(answers[Math.Min(n, answers.Length) - 1]);
        }
    }

    private HttpClient Client(Resolver resolver)
    {
        var scopes = new ServiceCollection().AddSingleton<IWhitelistRepository>(_node.WhitelistRepo).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var registry = new SpkiPinRegistry(scopes, network: new PeerNetwork(PeerAddressPolicy.IsAllowedAddress, resolver.ResolveAsync));
        return new HttpClient(registry.CreateHandler()) { Timeout = TimeSpan.FromSeconds(10) };
    }

    [Fact]
    public async Task A_name_that_is_an_alias_of_the_metadata_address_is_never_connected_to()
    {
        var resolver = new Resolver([IPAddress.Parse("169.254.169.254")]);
        using var http = Client(resolver);

        var act = () => http.GetAsync("http://metadata-alias.example:80/latest/meta-data");

        var thrown = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        PeerSocketConnector.IsRefusal(thrown).Should().BeTrue("the name was resolved by the node and refused at the connect step");
        PeerContactLog.Describe(thrown).Should().Contain("does not call").And.NotContain("169.254");
        resolver.Calls.Should().Be(1);
    }

    [Fact]
    public async Task One_bad_address_among_good_ones_refuses_the_whole_answer_and_nothing_is_connected_to()
    {
        var resolver = new Resolver([IPAddress.Loopback, IPAddress.Parse("169.254.169.254")]);
        using var http = Client(resolver);

        var act = () => http.GetAsync($"http://mixed.example:{Port}/x");

        PeerSocketConnector.IsRefusal((await act.Should().ThrowAsync<HttpRequestException>()).Which).Should().BeTrue();
        _accepted.Should().Be(0, "the good address of the answer was not tried either");
    }

    [Fact]
    public async Task A_name_that_resolves_differently_the_second_time_is_connected_to_only_by_the_answer_that_was_checked()
    {
        // The first resolution is the checked one (loopback, here); a rebind makes the next answer the metadata address. The connection of the first
        // request goes to the checked address and nowhere else, and the second connection's own resolution is checked again - and refused.
        var resolver = new Resolver([IPAddress.Loopback], [IPAddress.Parse("169.254.169.254")]);

        using (var first = Client(resolver))
        {
            using var response = await first.GetAsync($"http://rebind.example:{Port}/x");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        _accepted.Should().Be(1);
        resolver.Calls.Should().Be(1, "one resolution per connection: the check and the connect use the same answer");

        using var second = Client(resolver);
        var act = () => second.GetAsync($"http://rebind.example:{Port}/x");
        PeerSocketConnector.IsRefusal((await act.Should().ThrowAsync<HttpRequestException>()).Which).Should().BeTrue();
        _accepted.Should().Be(1, "the second answer was refused before any connection");
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.20.30.40")]
    [InlineData("192.168.1.20")]
    [InlineData("172.16.5.5")]
    public async Task Loopback_and_private_lan_addresses_stay_allowed(string literal)
    {
        // Allowed by the predicate; the connection itself is tried only for the loopback one (the listener is there).
        PeerAddressPolicy.IsAllowedAddress(IPAddress.Parse(literal)).Should().BeTrue();
        if (literal != "127.0.0.1") return;
        var resolver = new Resolver([IPAddress.Parse(literal)]);
        using var http = Client(resolver);

        using var response = await http.GetAsync($"http://lan-peer.example:{Port}/x");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.0.1")]
    [InlineData("fe80::1")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("224.0.0.251")]
    [InlineData("255.255.255.255")]
    [InlineData("ff02::1")]
    [InlineData("100.100.100.200")]
    [InlineData("fd00:ec2::254")]
    [InlineData("192.0.0.192")]
    [InlineData("168.63.129.16")]
    [InlineData("fd00:ec2::253")]
    [InlineData("fd20:ce::254")]
    [InlineData("64:ff9b::6464:64c8")]
    [InlineData("2002:a9fe:a9fe::1")]
    [InlineData("::ffff:169.254.169.254")]
    public void Addresses_a_node_never_calls_are_refused_for_a_literal_and_for_a_resolved_name_alike(string literal)
    {
        PeerAddressPolicy.IsAllowedAddress(IPAddress.Parse(literal)).Should().BeFalse();
        PeerAddressPolicy.Canonicalize(literal.Contains(':') ? $"http://[{literal}]" : $"http://{literal}").Ok.Should().BeFalse("the typed form is judged by the same predicate");
    }

    [Fact]
    public async Task A_literal_address_is_judged_at_the_connect_step_too()
    {
        using var http = Client(new Resolver([IPAddress.Loopback]));

        var act = () => http.GetAsync("http://169.254.169.254/latest/meta-data");

        PeerSocketConnector.IsRefusal((await act.Should().ThrowAsync<HttpRequestException>()).Which).Should().BeTrue();
    }
}
