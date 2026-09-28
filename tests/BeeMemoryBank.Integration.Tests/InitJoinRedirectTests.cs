using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The Web setup's "join an existing network" (<c>POST /api/init/join</c>) sends the master password to
/// the remote over real sockets: a 307 from the remote must fail the join, never resend the password to
/// the redirect target.
/// </summary>
public class InitJoinRedirectTests
{
    private const string Password = "initRedirectPassword1";

    [Fact]
    public async Task ARedirectedSetupJoin_Fails_AndThePasswordIsNotResent()
    {
        var recorder = RecordingServer.Start("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");
        var redirector = RecordingServer.Start(
            $"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{recorder.Port}/api/join\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var node = new BmbWebApplicationFactory(); // outbound HTTP NOT routed: real sockets
        using var client = node.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/init/join", new
        {
            adminUsername = "admin",
            displayName = "NodeB",
            remoteUrl = $"http://127.0.0.1:{redirector.Port}",
            password = Password
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        resp.IsSuccessStatusCode.Should().BeFalse(await resp.Content.ReadAsStringAsync());
        (await recorder.ReceivedOrNothingAsync()).Should().NotContain(Password,
            "the master password must never be resent to a redirect target");
    }

    private sealed class RecordingServer
    {
        private readonly TcpListener _listener;
        private readonly Task<string> _received;

        private RecordingServer(TcpListener listener, string response)
        {
            _listener = listener;
            _received = ServeAsync(response);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static RecordingServer Start(string response)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new RecordingServer(listener, response);
        }

        public async Task<string> ReceivedOrNothingAsync()
        {
            var done = await Task.WhenAny(_received, Task.Delay(TimeSpan.FromSeconds(2)));
            _listener.Stop();
            return done == _received ? await _received : "";
        }

        private async Task<string> ServeAsync(string response)
        {
            using var tcp = await _listener.AcceptTcpClientAsync();
            var stream = tcp.GetStream();
            var received = new StringBuilder();
            var buffer = new byte[8192];
            using var pause = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                while (!received.ToString().Contains(Password))
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
}
