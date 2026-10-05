using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
/// <c>POST /node/lock</c> of the front (card BMB-116): the shell's "lock the vault now" when the computer sleeps. It reaches the Api's
/// <c>POST /api/session/lock</c> only for a caller that is on this machine AND presents the node's own internal key; the Api call
/// carries the front's own key and the superadmin role, is bounded, and every answer is a fixed text that never holds the key.
/// The Api is a recording stub here (the real Api is driven by the integration test and by the headless proof).
/// </summary>
public class NodeLockControlTests : IAsyncLifetime
{
    private const string InternalKey = "node-lock-test-key-0123456789abcdef";

    private readonly List<WebApplication> _apps = new();
    private readonly ConcurrentQueue<string> _logLines = new();

    private readonly ConcurrentQueue<ApiCall> _apiCalls = new();

    private record ApiCall(string Method, string Path, string? Key, string? Role, string? UserId, string? Passenger);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var app in _apps)
        {
            try { await app.StopAsync(); await app.DisposeAsync(); }
            catch { /* shutdown noise */ }
        }
    }

    /// <summary>A recording stand-in for the Api's lock route, answering <paramref name="status"/> after <paramref name="delay"/>.</summary>
    private async Task<string> StartApiStubAsync(int status = StatusCodes.Status200OK, TimeSpan? delay = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapPost("/api/session/lock", async (HttpContext ctx) =>
        {
            _apiCalls.Enqueue(new ApiCall(
                ctx.Request.Method, ctx.Request.Path,
                ctx.Request.Headers["X-Internal-Key"].FirstOrDefault(),
                ctx.Request.Headers["X-User-Role"].FirstOrDefault(),
                ctx.Request.Headers["X-User-Id"].FirstOrDefault(),
                ctx.Request.Headers["X-Passenger"].FirstOrDefault()));
            if (delay is { } wait) await Task.Delay(wait, ctx.RequestAborted);
            return Results.Json(new { unlocked = false }, statusCode: status);
        });
        await app.StartAsync();
        _apps.Add(app);
        return app.Urls.First();
    }

    /// <summary>The front with <c>POST /node/lock</c> wired to <paramref name="apiUrl"/>; the remote address can be faked with X-Test-Remote-IP.</summary>
    private async Task<HttpClient> StartFrontAsync(string apiUrl, TimeSpan? apiTimeout = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton<IStartupFilter>(new RemoteIpFromHeader());
        builder.Logging.AddProvider(new CapturingLoggerProvider(_logLines));
        var front = new NodeFront(apiUrl, "http://127.0.0.1:2", new Dictionary<string, ReadyFileInfo>())
        {
            Lock = new NodeLockControl(apiUrl, InternalKey, apiTimeout: apiTimeout)
        };
        front.RegisterServices(builder.Services);
        var app = builder.Build();
        front.MapEndpoints(app);
        await app.StartAsync();
        _apps.Add(app);
        return new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    }

    private static HttpRequestMessage LockRequest(string? key = InternalKey, string? remoteIp = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/node/lock");
        if (key != null) req.Headers.Add("X-Internal-Key", key);
        if (remoteIp != null) req.Headers.Add("X-Test-Remote-IP", remoteIp);
        return req;
    }

    [Fact]
    public async Task OnThisMachine_WithTheKey_TheApiGetsExactlyOneLockCall_WithTheKeyAndTheSuperadminRole_AndTheNodeAnswers204()
    {
        var api = await StartApiStubAsync();
        using var client = await StartFrontAsync(api);

        using var req = LockRequest();
        // A caller's own identity headers are not forwarded: the front speaks as the node.
        req.Headers.Add("X-User-Role", "user");
        req.Headers.Add("X-User-Id", "42");
        req.Headers.Add("X-Passenger", "keep-me");
        var response = await client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
        var call = _apiCalls.Should().ContainSingle().Subject;
        call.Method.Should().Be("POST");
        call.Path.Should().Be("/api/session/lock");
        call.Key.Should().Be(InternalKey, "the Api validates the node's own key");
        call.Role.Should().Be("superadmin", "the Api route is superadmin-only; the internal-key caller is the node's owner");
        call.UserId.Should().BeNull("the caller's identity is not forwarded");
        call.Passenger.Should().BeNull("nothing of the caller's request is forwarded");
    }

    [Fact]
    public async Task LockingTwice_IsFine_EachCallReachesTheApi_BothAnswer204()
    {
        var api = await StartApiStubAsync();
        using var client = await StartFrontAsync(api);

        (await client.SendAsync(LockRequest())).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.SendAsync(LockRequest())).StatusCode.Should().Be(HttpStatusCode.NoContent);

        _apiCalls.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-key")]
    [InlineData("node-lock-test-key-0123456789abcdeX")]
    [InlineData("node-lock-test-key-0123456789abcdef-and-more")]
    public async Task WithoutTheKey_OrWithAWrongOne_Refused403_AndTheApiIsNeverCalled(string? key)
    {
        var api = await StartApiStubAsync();
        using var client = await StartFrontAsync(api);

        var response = await client.SendAsync(LockRequest(key));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("{\"error\":\"Unauthorized\"}", "it does not say whether the key was missing or wrong");
        _apiCalls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("192.0.2.50")]
    [InlineData("8.8.8.8")]
    [InlineData("2001:db8::1")]
    public async Task FromOffTheMachine_404_EvenWithTheRightKey_AndTheApiIsNeverCalled(string remoteIp)
    {
        var api = await StartApiStubAsync();
        using var client = await StartFrontAsync(api);

        var withKey = await client.SendAsync(LockRequest(remoteIp: remoteIp));
        var withoutKey = await client.SendAsync(LockRequest(key: null, remoteIp: remoteIp));

        withKey.StatusCode.Should().Be(HttpStatusCode.NotFound);
        withoutKey.StatusCode.Should().Be(HttpStatusCode.NotFound, "an off-machine caller learns nothing, not even that a key is needed");
        (await withKey.Content.ReadAsStringAsync()).Should().BeEmpty();
        _apiCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenTheApiRefuses_502_WithAFixedText_NotTheApisAnswer()
    {
        var api = await StartApiStubAsync(status: StatusCodes.Status403Forbidden);
        using var client = await StartFrontAsync(api);

        var response = await client.SendAsync(LockRequest());

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("{\"error\":\"The vault service refused the lock request.\"}");
        _apiCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task WhenNothingListensWhereTheApiWas_503_Quickly_WithAFixedText()
    {
        using var client = await StartFrontAsync($"http://127.0.0.1:{FreePort()}");

        var clock = Stopwatch.StartNew();
        var response = await client.SendAsync(LockRequest());
        clock.Stop();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"The vault service could not be reached.\"}");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WhenTheApiHangs_TheAnswerComesAtTheBound_503_NotAHang()
    {
        var api = await StartApiStubAsync(delay: TimeSpan.FromSeconds(30));
        using var client = await StartFrontAsync(api, apiTimeout: TimeSpan.FromMilliseconds(400));

        var clock = Stopwatch.StartNew();
        var response = await client.SendAsync(LockRequest());
        clock.Stop();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"The vault service did not answer the lock request in time.\"}");
        clock.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(300), "it waited for the Api up to the bound")
            .And.BeLessThan(TimeSpan.FromSeconds(10), "and no longer: a sleep must not hang on a stuck Api");
    }

    [Fact]
    public void TheDefaultBound_IsAFewSeconds_ShorterThanTheShellsOwnWait()
    {
        // The macOS monitor waits 4 s for the whole request, the shell's HTTP client 5 s: the node must answer first.
        NodeLockControl.DefaultApiTimeout.Should().BeGreaterThan(TimeSpan.FromSeconds(1)).And.BeLessThan(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task TheKey_NeverAppearsInAnAnswer_AHeader_OrALogLine_InAnyOutcome()
    {
        // success, a refusing Api, a hanging Api, a dead Api, a wrong key, a foreign address
        var okApi = await StartApiStubAsync();
        using var okFront = await StartFrontAsync(okApi);
        var badApi = await StartApiStubAsync(status: StatusCodes.Status500InternalServerError);
        using var badFront = await StartFrontAsync(badApi);
        var slowApi = await StartApiStubAsync(delay: TimeSpan.FromSeconds(30));
        using var slowFront = await StartFrontAsync(slowApi, apiTimeout: TimeSpan.FromMilliseconds(200));
        using var deadFront = await StartFrontAsync($"http://127.0.0.1:{FreePort()}");

        var seen = new List<string>();
        foreach (var (client, key, ip) in new[]
                 {
                     (okFront, InternalKey, (string?)null), (badFront, InternalKey, null), (slowFront, InternalKey, null),
                     (deadFront, InternalKey, null), (okFront, "wrong", null), (okFront, InternalKey, "192.0.2.1"),
                 })
        {
            using var response = await client.SendAsync(LockRequest(key, ip));
            seen.Add(response.ToString());
            seen.Add(await response.Content.ReadAsStringAsync());
        }

        seen.Should().NotBeEmpty();
        seen.Should().OnlyContain(text => !text.Contains(InternalKey), "no answer or header carries the key");
        _logLines.Where(line => line.Contains("NodeLockControl")).Should().HaveCountGreaterThanOrEqualTo(3, "the refusing, hanging and dead Api are each logged");
        _logLines.Should().OnlyContain(line => !line.Contains(InternalKey), "no log line carries the key");
    }

    [Fact]
    public async Task WithoutAnInternalKeyForTheFront_TheRouteStaysTheOld501Stub()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var front = new NodeFront("http://127.0.0.1:1", "http://127.0.0.1:2", new Dictionary<string, ReadyFileInfo>());
        front.RegisterServices(builder.Services);
        var app = builder.Build();
        front.MapEndpoints(app);
        await app.StartAsync();
        _apps.Add(app);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        (await client.SendAsync(LockRequest())).StatusCode.Should().Be(HttpStatusCode.NotImplemented);
    }

    [Fact]
    public void TheConstructor_RefusesAnEmptyKey_AndAnEmptyApiAddress()
    {
        FluentActions.Invoking(() => new NodeLockControl("http://127.0.0.1:1", "")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new NodeLockControl(" ", "key")).Should().Throw<ArgumentException>();
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

    /// <summary>Collects every formatted log line (category, level, message and exception text) so a test can look for what must not be there.</summary>
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
