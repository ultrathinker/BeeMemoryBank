using System.Net;
using System.Net.Sockets;
using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos.Tests;

/// <summary>Before a join sends the master password to a node on the local network, the app waits until a plain TCP connection opens.</summary>
public class LocalNetworkTests
{
    [Theory]
    [InlineData("192.168.1.19", true)]
    [InlineData("10.0.0.7", true)]
    [InlineData("172.16.5.1", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("169.254.10.10", true)]
    [InlineData("my-pc.local", true)]
    [InlineData("[fd12:3456::1]", true)]
    [InlineData("[fe80::1]", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("bmb.example.com", false)]
    [InlineData("127.0.0.1", false)]
    public void TheLocalNetwork_IsWhatIosAsksAbout(string host, bool local) => LocalNetwork.IsLocal(host).Should().Be(local);

    [Fact]
    public async Task AnAddressOutsideTheLocalNetwork_IsNotTried()
    {
        var started = DateTime.UtcNow;
        (await LocalNetwork.WaitUntilReachableAsync(new Uri("https://bmb.example.invalid:1"), TimeSpan.FromSeconds(30))).Should().BeTrue();
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AListeningNode_IsReachableAtOnce_AndNothingIsSentToIt()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = listener.AcceptTcpClientAsync();

        (await LocalNetwork.ConnectAsync(new Uri($"https://127.0.0.1:{port}"), TimeSpan.FromSeconds(10))).Should().BeTrue();

        using var server = await accepted;
        var buffer = new byte[16];
        var read = await server.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        read.Should().Be(0, "the connection is closed without a byte: the password goes only through the join's pinned client");
    }

    [Fact]
    public async Task ANodeThatNeverAnswers_EndsTheWaitAfterThePatience()
    {
        // A port that was free a moment ago: refused at once, every try.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var started = DateTime.UtcNow;
        (await LocalNetwork.ConnectAsync(new Uri($"https://127.0.0.1:{port}"), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(200))).Should().BeFalse();
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(10));
    }
}
