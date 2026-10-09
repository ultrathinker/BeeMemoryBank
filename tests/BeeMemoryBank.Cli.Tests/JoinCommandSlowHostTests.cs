using System.Net;
using System.Net.Sockets;
using BeeMemoryBank.Cli.Commands;
using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Tests;

/// <summary>
/// The host builds the whole join snapshot before it sends the first header (review revsrv F8). The 30 seconds <c>bmb join</c> allows a request
/// of the key exchange must not be the budget of that wait: a big vault takes minutes to build, and a join that failed on it had spent its
/// one-time code. Verified on the old code with a host that took 32 seconds: "canceled due to the configured HttpClient.Timeout of 30 seconds".
/// The limit is handed in here (1 second) so the test needs no real half minute.
/// </summary>
public class JoinCommandSlowHostTests : IDisposable
{
    private const string Password = "cliSlowHostPassword1";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "bmb_cli_join_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task AHostThatTakesLongerThanTheRequestLimitToBuildTheSnapshot_StillJoins()
    {
        var host = new FakeJoinHost(Guid.NewGuid());
        var answer = host.JoinResponseJson(Password);
        using var listener = Start(out var url);
        var serve = ServeAsync(listener, async path =>
        {
            // The real host's order: nothing at all (no header) until the archive and its signature exist.
            if (path == "/api/sync/snapshot/for-join") await Task.Delay(TimeSpan.FromSeconds(3));
            return path == "/api/join" ? (200, new Dictionary<string, string>(), System.Text.Encoding.UTF8.GetBytes(answer)) : host.Answer(path);
        }, until: "/api/sync/snapshot/for-join");

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_tempDir, url, Password, "CliJoiner", output: output, requestTimeout: TimeSpan.FromSeconds(1));
        await serve;

        rc.Should().Be(0, output.ToString());
        await using var services = await CliServiceProvider.CreateAsync(_tempDir);
        using var scope = services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.InitialSyncCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task AHostThatDoesNotAnswerTheJoinInTime_IsReportedAsNotAnswering_AndNothingIsLeft()
    {
        using var listener = Start(out var url);
        var serve = ServeAsync(listener, async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            return null;
        }, until: "/api/join");

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_tempDir, url, Password, "CliJoiner", output: output, requestTimeout: TimeSpan.FromSeconds(1));
        await serve;

        rc.Should().Be(1);
        output.ToString().Should().Contain("did not answer within 1 seconds").And.NotContain(Password);
        await using var services = await CliServiceProvider.CreateAsync(_tempDir);
        using var scope = services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync()).Should().BeNull();
    }

    private static HttpListener Start(out string url)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        url = $"http://localhost:{port}";
        var listener = new HttpListener();
        listener.Prefixes.Add(url + "/");
        listener.Start();
        return listener;
    }

    /// <summary>Answers each request in turn (the answer may wait first); done after the request for <paramref name="until"/>.</summary>
    private static async Task ServeAsync(
        HttpListener listener,
        Func<string, Task<(int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body)?>> answer,
        string until)
    {
        var path = "";
        while (path != until)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); } catch { return; }
            path = ctx.Request.Url!.AbsolutePath;
            using (var reader = new StreamReader(ctx.Request.InputStream)) await reader.ReadToEndAsync();
            var (status, headers, bytes) = await answer(path) ?? (404, new Dictionary<string, string>(), []);
            try
            {
                ctx.Response.StatusCode = status;
                foreach (var (name, value) in headers) ctx.Response.Headers[name] = value;
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // The client gave up on this request (the point of the second test).
            }
        }
    }
}
