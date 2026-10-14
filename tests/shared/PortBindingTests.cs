using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.TestSupport;

/// <summary>The port helper (one source in tests/shared, compiled into every test project that picks a port before it binds it).</summary>
public sealed class PortBindingTests
{
    [Fact]
    public void AHeldPort_IsRecognisedAsTaken_ByASocketAndByAnHttpListener()
    {
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        var taken = ((IPEndPoint)holder.LocalEndpoint).Port;

        var socket = () => new TcpListener(IPAddress.Loopback, taken).Start();
        PortBinding.IsAddressInUse(socket.Should().Throw<Exception>().Which).Should().BeTrue();

        if (OperatingSystem.IsWindows()) return;   // http.sys may share a port that a plain socket holds; the managed listener of Linux and macOS may not
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{taken}/");
        var http = () => listener.Start();
        PortBinding.IsAddressInUse(http.Should().Throw<Exception>().Which).Should().BeTrue();
    }

    [Fact]
    public void ATakenAddress_IsTriedAgainWithANewPort_AndOnlyThen()
    {
        var ports = new List<int>();

        var bound = PortBinding.Bind(port =>
        {
            ports.Add(port);
            if (ports.Count < 3) throw new SocketException((int)SocketError.AddressAlreadyInUse);
            return port;
        });

        ports.Should().HaveCount(3);
        bound.Should().Be(ports[2]);
    }

    [Fact]
    public void AnyOtherFailure_ReachesTheTestAtOnce()
    {
        var calls = 0;

        var act = () => PortBinding.Bind<int>(_ =>
        {
            calls++;
            throw new SocketException((int)SocketError.ConnectionRefused);
        });

        act.Should().Throw<SocketException>();
        calls.Should().Be(1, "a failure that is not a taken address says something real");
    }

    [Fact]
    public void AClashThatDoesNotGoAway_IsNotHidden()
    {
        var calls = 0;

        var act = () => PortBinding.Bind<int>(_ =>
        {
            calls++;
            throw new SocketException((int)SocketError.AddressAlreadyInUse);
        });

        act.Should().Throw<SocketException>();
        calls.Should().BeGreaterThan(1).And.BeLessThan(10);
    }

    [Fact]
    public void AStartedHttpListener_ServesOnThePortItReports()
    {
        var (listener, port) = PortBinding.StartHttpListener("127.0.0.1");
        using (listener)
        {
            listener.IsListening.Should().BeTrue();
            listener.Prefixes.Should().ContainSingle().Which.Should().Be($"http://127.0.0.1:{port}/");
        }
    }
}
