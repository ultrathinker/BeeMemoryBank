using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using BeeMemoryBank.Hosting;
using BeeMemoryBank.Node;

namespace BeeMemoryBank.Node.Tests;

/// <summary>
/// <c>GET /node/alarms</c> of the front (BMB-77): the shell's "which blind nodes need attention". It reaches the Api's
/// <c>GET /api/blind-nodes/alarms</c> only for a caller on this machine that presents the node's own internal key; the Api call carries
/// the front's own key and the superadmin role; the Api's report is passed on; an older Api is a 501; every failure is a fixed text that
/// never holds the key. The Api is a recording stub here.
/// </summary>
public class NodeAlarmsControlTests : IAsyncLifetime
{
    private const string InternalKey = "node-alarms-test-key-0123456789abcdef";
    private const string Report = """{"state":"ok","locked":false,"alarms":[{"kind":"silent","nodeId":"b11d0000-0000-8000-8000-00000000000a"}]}""";

    private readonly List<WebApplication> _apps = new();
    private readonly ConcurrentQueue<string> _logLines = new();
    private readonly ConcurrentQueue<ApiCall> _apiCalls = new();

    private record ApiCall(string Method, string Path, string? Key, string? Role, string? UserId);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var app in _apps)
        {
            try { await app.StopAsync(); await app.DisposeAsync(); }
            catch { /* shutdown noise */ }
        }
    }

    /// <summary>A recording stand-in for the Api's alarms route; no route at all for <paramref name="olderApi"/>.</summary>
    private async Task<string> StartApiStubAsync(int status = StatusCodes.Status200OK, string body = Report, TimeSpan? delay = null, bool olderApi = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        if (!olderApi)
            app.MapGet("/api/blind-nodes/alarms", async (HttpContext ctx) =>
            {
                _apiCalls.Enqueue(new ApiCall(ctx.Request.Method, ctx.Request.Path,
                    ctx.Request.Headers["X-Internal-Key"].FirstOrDefault(),
                    ctx.Request.Headers["X-User-Role"].FirstOrDefault(),
                    ctx.Request.Headers["X-User-Id"].FirstOrDefault()));
                if (delay is { } wait) await Task.Delay(wait, ctx.RequestAborted);
                return Results.Content(body, "application/json", statusCode: status);
            });
        await app.StartAsync();
        _apps.Add(app);
        return app.Urls.First();
    }

    private async Task<HttpClient> StartFrontAsync(string apiUrl, TimeSpan? apiTimeout = null, bool withControl = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton<IStartupFilter>(new RemoteIpFromHeader());
        builder.Logging.AddProvider(new CapturingLoggerProvider(_logLines));
        var front = new NodeFront(apiUrl, "http://127.0.0.1:2", new Dictionary<string, ReadyFileInfo>())
        {
            Alarms = withControl ? new NodeAlarmsControl(apiUrl, InternalKey, apiTimeout: apiTimeout) : null
        };
        front.RegisterServices(builder.Services);
        var app = builder.Build();
        front.MapEndpoints(app);
        await app.StartAsync();
        _apps.Add(app);
        return new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    }

    private static HttpRequestMessage AlarmsRequest(string? key = InternalKey, string? remoteIp = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/node/alarms");
        if (key != null) req.Headers.Add("X-Internal-Key", key);
        if (remoteIp != null) req.Headers.Add("X-Test-Remote-IP", remoteIp);
        return req;
    }

    [Fact]
    public async Task OnThisMachine_WithTheKey_TheApisReportIsPassedOn_AndTheApiSeesTheNodesOwnIdentity()
    {
        var api = await StartApiStubAsync();
        using var client = await StartFrontAsync(api);

        using var req = AlarmsRequest();
        req.Headers.Add("X-User-Role", "user");
        req.Headers.Add("X-User-Id", "42");
        var response = await client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        (await response.Content.ReadAsStringAsync()).Should().Be(Report);
        var call = _apiCalls.Should().ContainSingle().Subject;
        call.Method.Should().Be("GET");
        call.Path.Should().Be("/api/blind-nodes/alarms");
        call.Key.Should().Be(InternalKey);
        call.Role.Should().Be("superadmin", "the Api route is superadmin-only; the internal-key caller is the node's owner");
        call.UserId.Should().BeNull("the caller's identity is not forwarded");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-key")]
    [InlineData("node-alarms-test-key-0123456789abcdeX")]
    public async Task WithoutTheKey_OrWithAWrongOne_Refused403_AndTheApiIsNeverCalled(string? key)
    {
        var api = await StartApiStubAsync();
        using var client = await StartFrontAsync(api);

        var response = await client.SendAsync(AlarmsRequest(key));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"Unauthorized\"}");
        _apiCalls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("192.0.2.50")]
    [InlineData("2001:db8::1")]
    public async Task FromOffTheMachine_404_EvenWithTheRightKey(string remoteIp)
    {
        var api = await StartApiStubAsync();
        using var client = await StartFrontAsync(api);

        var response = await client.SendAsync(AlarmsRequest(remoteIp: remoteIp));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _apiCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task FromAProxyOnThisMachine_404_EvenWithTheRightKey()
    {
        var api = await StartApiStubAsync();
        using var client = await StartFrontAsync(api);

        using var req = AlarmsRequest(remoteIp: "127.0.0.1");
        req.Headers.Add("X-Forwarded-For", "203.0.113.9");
        var response = await client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _apiCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task AnOlderApiWithoutTheRoute_IsA501()
    {
        var api = await StartApiStubAsync(olderApi: true);
        using var client = await StartFrontAsync(api);

        var response = await client.SendAsync(AlarmsRequest());

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
    }

    [Theory]
    [InlineData(StatusCodes.Status403Forbidden, Report)]
    [InlineData(StatusCodes.Status200OK, "<html>not a report</html>")]
    [InlineData(StatusCodes.Status200OK, "[1,2,3]")]
    public async Task ARefusalOrAnAnswerThatIsNotAReport_Is502_WithAFixedText(int status, string body)
    {
        var api = await StartApiStubAsync(status, body);
        using var client = await StartFrontAsync(api);

        var response = await client.SendAsync(AlarmsRequest());

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("html").And.NotContain("[1,2,3]").And.StartWith("{\"error\":");
    }

    [Fact]
    public async Task AnAnswerLargerThanTheLimit_Is502_NotPassedOn()
    {
        var api = await StartApiStubAsync(body: "{\"state\":\"" + new string('x', NodeAlarmsControl.MaxBodyBytes) + "\"}");
        using var client = await StartFrontAsync(api);

        (await client.SendAsync(AlarmsRequest())).StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task WhenTheApiHangs_TheAnswerComesAtTheBound_503()
    {
        var api = await StartApiStubAsync(delay: TimeSpan.FromSeconds(30));
        using var client = await StartFrontAsync(api, apiTimeout: TimeSpan.FromMilliseconds(400));

        var response = await client.SendAsync(AlarmsRequest());

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"The vault service did not answer the alarms request in time.\"}");
    }

    [Fact]
    public async Task WhenNothingListensWhereTheApiWas_503()
    {
        using var client = await StartFrontAsync($"http://127.0.0.1:{FreePort()}");

        var response = await client.SendAsync(AlarmsRequest());

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"The vault service could not be reached.\"}");
    }

    [Fact]
    public async Task TheKey_NeverAppearsInAnAnswer_OrALogLine()
    {
        var okApi = await StartApiStubAsync();
        using var okFront = await StartFrontAsync(okApi);
        var badApi = await StartApiStubAsync(StatusCodes.Status500InternalServerError);
        using var badFront = await StartFrontAsync(badApi);
        using var deadFront = await StartFrontAsync($"http://127.0.0.1:{FreePort()}");

        var seen = new List<string>();
        foreach (var (client, key, ip) in new[]
                 {
                     (okFront, InternalKey, (string?)null), (badFront, InternalKey, null), (deadFront, InternalKey, null),
                     (okFront, "wrong", null), (okFront, InternalKey, "192.0.2.1"),
                 })
        {
            using var response = await client.SendAsync(AlarmsRequest(key, ip));
            seen.Add(response.ToString());
            seen.Add(await response.Content.ReadAsStringAsync());
        }

        seen.Should().OnlyContain(text => !text.Contains(InternalKey));
        _logLines.Where(line => line.Contains("NodeAlarmsControl")).Should().HaveCountGreaterThanOrEqualTo(2);
        _logLines.Should().OnlyContain(line => !line.Contains(InternalKey));
    }

    [Fact]
    public async Task WithoutAnInternalKeyForTheFront_TheRouteIsA501Stub()
    {
        using var client = await StartFrontAsync("http://127.0.0.1:1", withControl: false);

        (await client.SendAsync(AlarmsRequest())).StatusCode.Should().Be(HttpStatusCode.NotImplemented);
    }

    [Fact]
    public void TheConstructor_RefusesAnEmptyKey_AndAnEmptyApiAddress()
    {
        FluentActions.Invoking(() => new NodeAlarmsControl("http://127.0.0.1:1", "")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new NodeAlarmsControl(" ", "key")).Should().Throw<ArgumentException>();
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private sealed class RemoteIpFromHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, nextMiddleware) =>
            {
                if (ctx.Request.Headers.TryGetValue("X-Test-Remote-IP", out var ip))
                    ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
                await nextMiddleware();
            });
            next(app);
        };
    }

    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Capture(categoryName, lines);
        public void Dispose() { }

        private sealed class Capture(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue($"{category} {logLevel}: {formatter(state, exception)} {exception}");
        }
    }
}
