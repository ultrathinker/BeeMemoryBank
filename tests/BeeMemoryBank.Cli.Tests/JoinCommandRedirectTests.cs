using System.Net;
using System.Net.Sockets;
using System.Text;
using BeeMemoryBank.Cli.Commands;

namespace BeeMemoryBank.Cli.Tests;

/// <summary>
/// <c>bmb join</c> sends the master password: a 307 from the remote must fail the join, never resend
/// the password to the redirect target.
/// </summary>
public class JoinCommandRedirectTests : IDisposable
{
    private const string Password = "cliRedirectPassword1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_cli_redirect_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task ARedirectedJoin_Fails_AndThePasswordIsNotResent()
    {
        var recorder = Server.Start("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}", Password);
        var redirector = Server.Start(
            $"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{recorder.Port}/api/join\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", Password);

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_dir, $"http://127.0.0.1:{redirector.Port}", Password, "CliJoiner", output: output);

        rc.Should().NotBe(0, output.ToString());
        (await recorder.ReceivedOrNothingAsync()).Should().NotContain(Password,
            "the master password must never be resent to a redirect target");
    }
}

/// <summary>One connection: records what it reads (until <c>marker</c> or a pause) and answers.</summary>
internal sealed class Server
{
    private readonly TcpListener _listener;
    private readonly Task<string> _received;

    private Server(TcpListener listener, string response, string marker)
    {
        _listener = listener;
        _received = ServeAsync(response, marker);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static Server Start(string response, string marker)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new Server(listener, response, marker);
    }

    public async Task<string> ReceivedOrNothingAsync()
    {
        var done = await Task.WhenAny(_received, Task.Delay(TimeSpan.FromSeconds(2)));
        _listener.Stop();
        return done == _received ? await _received : "";
    }

    private async Task<string> ServeAsync(string response, string marker)
    {
        using var tcp = await _listener.AcceptTcpClientAsync();
        var stream = tcp.GetStream();
        var received = new StringBuilder();
        var buffer = new byte[8192];
        using var pause = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            while (!received.ToString().Contains(marker))
            {
                var n = await stream.ReadAsync(buffer, pause.Token);
                if (n == 0) break;
                received.Append(Encoding.UTF8.GetString(buffer, 0, n));
            }
        }
        catch (OperationCanceledException) { }
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
        return received.ToString();
    }
}
