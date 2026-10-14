using System.Net;
using System.Net.Sockets;

namespace BeeMemoryBank.TestSupport;

/// <summary>
/// For a test that must name the port of its own server before the server binds it (an <see cref="HttpListener"/> prefix cannot say "any
/// port", and a door that is built for one address is told it up front). A port that was free when it was picked can be taken by another
/// socket of the machine before it is bound: the system also hands the same numbers to the client side of every connection a parallel
/// test opens. The pick and the bind are therefore repeated with a new port, a few times, and only when the bind failed because the address
/// is taken; any other failure, and a clash that does not go away, reaches the test. Where a server can bind port 0 itself, do that instead.
/// </summary>
public static class PortBinding
{
    private const int Attempts = 5;

    /// <summary>A loopback port that was free when it was asked for. Not reserved: bind it through <see cref="Bind{T}"/> or retry on <see cref="IsAddressInUse"/>.</summary>
    public static int PickFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>Picks a port and binds it with <paramref name="bind"/>; picks again when the address was taken in between.</summary>
    public static T Bind<T>(Func<int, T> bind)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return bind(PickFreePort()); }
            catch (Exception ex) when (attempt < Attempts && IsAddressInUse(ex)) { }
        }
    }

    /// <summary>An <see cref="HttpListener"/> already started on <c>http://{host}:{port}/</c>, with the port it got.</summary>
    public static (HttpListener Listener, int Port) StartHttpListener(string host = "localhost") => Bind(port =>
    {
        var listener = new HttpListener();
        try
        {
            listener.Prefixes.Add($"http://{host}:{port}/");
            listener.Start();
            return (listener, port);
        }
        catch
        {
            listener.Close();
            throw;
        }
    });

    /// <summary>True when something failed because its address was taken, however the server wrapped the socket error.</summary>
    public static bool IsAddressInUse(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }) return true;
            // The managed listener carries the operating system's own code (98 on Linux, 48 on macOS); http.sys (Windows) says the registration exists (183) or the port is in use (32).
            if (e is HttpListenerException h && h.ErrorCode is (int)SocketError.AddressAlreadyInUse or 98 or 48 or 183 or 32) return true;
        }
        return false;
    }
}
