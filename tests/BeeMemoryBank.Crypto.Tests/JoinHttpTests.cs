using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BeeMemoryBank.Crypto.Tests;

/// <summary>
/// The client every master-password join uses (the phone's setup without a join code, <c>bmb join</c>):
/// a 307/308 answer must never make it resend the password — to plain http or anywhere else.
/// </summary>
public class JoinHttpTests
{
    private const string Marker = "master-password-7c1e";

    [Theory]
    [InlineData(307)]
    [InlineData(308)]
    [InlineData(302)]
    public async Task ARedirect_IsAnError_AndThePasswordIsNotResent(int status)
    {
        await using var recorder = PlainServer.Start("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await using var redirector = PlainServer.Start(
            $"HTTP/1.1 {status} Redirect\r\nLocation: http://127.0.0.1:{recorder.Port}/api/join\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var client = JoinHttp.CreateClient();

        var join = () => client.PostAsync($"http://127.0.0.1:{redirector.Port}/api/join",
            new StringContent($"{{\"masterPassword\":\"{Marker}\"}}"));

        await join.Should().ThrowAsync<HttpRequestException>("a join never follows a redirect");
        (await recorder.ReceivedOrNothingAsync()).Should().NotContain(Marker);
    }

    /// <summary>One connection: records what it reads (until the body marker or a pause) and answers.</summary>
    internal sealed class PlainServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task<string> _received;

        private PlainServer(TcpListener listener, string response)
        {
            _listener = listener;
            _received = ServeAsync(response);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static PlainServer Start(string response)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new PlainServer(listener, response);
        }

        public async Task<string> ReceivedOrNothingAsync()
        {
            var done = await Task.WhenAny(_received, Task.Delay(TimeSpan.FromSeconds(2)));
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
                while (!received.ToString().Contains(Marker) && !received.ToString().Contains("\r\n\r\n{"))
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

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try { await _received; } catch { }
        }
    }
}
