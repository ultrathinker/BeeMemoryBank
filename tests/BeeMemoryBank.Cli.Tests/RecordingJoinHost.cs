using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BeeMemoryBank.Cli.Tests;

/// <summary>One request a <see cref="RecordingJoinHost"/> received.</summary>
internal sealed record SeenRequest(string Method, string Path, string Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// A local HTTP host for the join tests that records every request in full (method, path, body, headers) and answers by
/// what the test says; no answer (null) is the 404 of a host that does not know the route. Used where a test has to see
/// what a joiner sent to <c>/api/join/abort</c>, and what it did when that route answered something else.
/// </summary>
internal sealed class RecordingJoinHost : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<SeenRequest, (int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body)?> _answer;
    private readonly Task _loop;
    private readonly List<SeenRequest> _seen = new();

    public RecordingJoinHost(Func<SeenRequest, (int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body)?> answer)
    {
        _answer = answer;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = $"http://localhost:{port}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public string Url { get; }

    public IReadOnlyList<SeenRequest> Requests
    {
        get { lock (_seen) return _seen.ToList(); }
    }

    public IReadOnlyList<string> Paths => Requests.Select(r => r.Path).ToList();

    public static (int, IReadOnlyDictionary<string, string>, byte[]) Reply(int status, string json = "{}") =>
        (status, new Dictionary<string, string>(), Encoding.UTF8.GetBytes(json));

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync();
            var headers = ctx.Request.Headers.AllKeys.Where(k => k != null).ToDictionary(k => k!, k => ctx.Request.Headers[k] ?? "");
            var seen = new SeenRequest(ctx.Request.HttpMethod, ctx.Request.Url!.AbsolutePath, body, headers);
            lock (_seen) _seen.Add(seen);
            var (status, replyHeaders, replyBody) = _answer(seen) ?? (404, new Dictionary<string, string>(), []);
            ctx.Response.StatusCode = status;
            foreach (var (name, value) in replyHeaders) ctx.Response.Headers[name] = value;
            ctx.Response.ContentLength64 = replyBody.Length;
            await ctx.Response.OutputStream.WriteAsync(replyBody);
            ctx.Response.Close();
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
        try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch { /* the listener is gone either way */ }
    }
}
