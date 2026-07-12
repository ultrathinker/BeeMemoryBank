using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using FluentAssertions;
using BeeMemoryBank.Hosting;
using BeeMemoryBank.Node;

namespace BeeMemoryBank.Node.Tests;

public class NodeFrontTests : IAsyncDisposable
{
    private readonly List<WebApplication> _startedApps = new();

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _startedApps)
        {
            try
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
            catch
            {
                // Suppress shutdown errors in tests
            }
        }
    }

    private async Task<WebApplication> StartStubServerAsync(Action<WebApplication> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            // Real Api/Web already raise this to 500 MB themselves (see their Program.cs) -
            // mirror that here so the large-body test proves the front's own override,
            // not an incidental default-limit rejection at the stub destination.
            options.Limits.MaxRequestBodySize = 500L * 1024 * 1024;
        });
        var app = builder.Build();
        configure(app);
        await app.StartAsync();
        _startedApps.Add(app);
        return app;
    }

    private async Task<(WebApplication ProxyApp, string ProxyUrl)> StartProxyServerAsync(
        string apiUrl, 
        string webUrl, 
        IReadOnlyDictionary<string, ReadyFileInfo> children)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));

        // Inject test middleware to mock remote IP address based on header
        builder.Services.AddSingleton<IStartupFilter>(new TestStartupFilter());

        var front = new NodeFront(apiUrl, webUrl, children);
        front.RegisterServices(builder.Services);

        var app = builder.Build();
        front.MapEndpoints(app);

        await app.StartAsync();
        _startedApps.Add(app);

        return (app, app.Urls.First());
    }

    private class TestStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return builder =>
            {
                builder.Use(async (context, nextMiddleware) =>
                {
                    if (context.Request.Headers.TryGetValue("X-Test-Remote-IP", out var ipStr))
                    {
                        context.Connection.RemoteIpAddress = IPAddress.Parse(ipStr.ToString());
                    }
                    await nextMiddleware();
                });
                next(builder);
            };
        }
    }

    [Fact]
    public async Task RoutesRequestsToCorrectDestinations_BasedOnPathsAndMethods()
    {
        // Arrange stubs
        var apiStub = await StartStubServerAsync(app =>
        {
            app.MapGet("/mcp", (HttpContext ctx) => 
                Results.Ok(new { server = "api", route = "mcp", forwardedFor = ctx.Request.Headers["X-Forwarded-For"].ToString() }));
            
            app.MapGet("/mcp/nested/path", (HttpContext ctx) => 
                Results.Ok(new { server = "api", route = "mcp-nested" }));
            
            app.MapGet("/api/sync/all", (HttpContext ctx) => 
                Results.Ok(new { server = "api", route = "sync" }));
            
            app.MapPost("/api/join", (HttpContext ctx) => 
                Results.Ok(new { server = "api", route = "join" }));
            
            app.MapGet("/health", (HttpContext ctx) => 
                Results.Ok(new { server = "api", route = "health" }));
        });

        var webStub = await StartStubServerAsync(app =>
        {
            // Web serves everything else
            app.MapFallback((HttpContext ctx) => 
                Results.Ok(new { server = "web", path = ctx.Request.Path.Value }));
        });

        var dummyChildren = new Dictionary<string, ReadyFileInfo>
        {
            { "Api", new ReadyFileInfo(111, apiStub.Urls.ToList(), "BeeMemoryBank.Api", "1.0.0", DateTime.UtcNow) },
            { "Web", new ReadyFileInfo(222, webStub.Urls.ToList(), "BeeMemoryBank.Web", "1.0.0", DateTime.UtcNow) }
        };

        var (_, proxyUrl) = await StartProxyServerAsync(apiStub.Urls.First(), webStub.Urls.First(), dummyChildren);
        using var client = new HttpClient();

        // Act & Assert
        // 1. /mcp -> Api
        var mcpRes = await client.GetFromJsonAsync<DummyResponse>($"{proxyUrl}/mcp");
        mcpRes.Should().NotBeNull();
        mcpRes!.Server.Should().Be("api");
        mcpRes.Route.Should().Be("mcp");
        mcpRes.ForwardedFor.Should().NotBeNullOrEmpty(); // Verify forwarded headers are sent

        // 2. /mcp/nested/path -> Api
        var mcpNestedRes = await client.GetFromJsonAsync<DummyResponse>($"{proxyUrl}/mcp/nested/path");
        mcpNestedRes!.Server.Should().Be("api");

        // 3. /api/sync/all -> Api
        var syncRes = await client.GetFromJsonAsync<DummyResponse>($"{proxyUrl}/api/sync/all");
        syncRes!.Server.Should().Be("api");

        // 4. POST /api/join -> Api
        var joinPostRes = await client.PostAsync($"{proxyUrl}/api/join", new StringContent(""));
        joinPostRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var joinPostObj = await joinPostRes.Content.ReadFromJsonAsync<DummyResponse>();
        joinPostObj!.Server.Should().Be("api");

        // 5. /health -> Api
        var healthRes = await client.GetFromJsonAsync<DummyResponse>($"{proxyUrl}/health");
        healthRes!.Server.Should().Be("api");

        // 6. GET /api/join -> falls through to Web (404/fallback)
        var joinGetRes = await client.GetFromJsonAsync<DummyResponse>($"{proxyUrl}/api/join");
        joinGetRes!.Server.Should().Be("web");
        joinGetRes.Path.Should().Be("/api/join");

        // 7. GET /api/users -> falls through to Web (unexposed API endpoint)
        var usersRes = await client.GetFromJsonAsync<DummyResponse>($"{proxyUrl}/api/users");
        usersRes!.Server.Should().Be("web");
        usersRes.Path.Should().Be("/api/users");

        // 8. Other web path -> Web
        var homeRes = await client.GetFromJsonAsync<DummyResponse>($"{proxyUrl}/some-web-page");
        homeRes!.Server.Should().Be("web");
    }

    [Fact]
    public async Task NodeEndpoints_AreGuardedByLoopbackClientCheck()
    {
        // Arrange
        var dummyChildren = new Dictionary<string, ReadyFileInfo>
        {
            { "Api", new ReadyFileInfo(111, new[] { "http://localhost:5001" }, "BeeMemoryBank.Api", "1.0.0", DateTime.UtcNow) },
            { "Web", new ReadyFileInfo(222, new[] { "http://localhost:5002" }, "BeeMemoryBank.Web", "1.0.0", DateTime.UtcNow) }
        };

        var (_, proxyUrl) = await StartProxyServerAsync("http://localhost:5001", "http://localhost:5002", dummyChildren);
        using var client = new HttpClient();

        // 1. Loopback request (simulate 127.0.0.1 via header)
        var reqLoopback = new HttpRequestMessage(HttpMethod.Get, $"{proxyUrl}/node/status");
        reqLoopback.Headers.Add("X-Test-Remote-IP", "127.0.0.1");
        var resLoopback = await client.SendAsync(reqLoopback);
        resLoopback.StatusCode.Should().Be(HttpStatusCode.OK);

        var statusJson = await resLoopback.Content.ReadAsStringAsync();
        statusJson.Should().Contain("version");
        statusJson.Should().Contain("children");

        // 2. Non-loopback request (simulate 8.8.8.8 via header)
        var reqNonLoopback = new HttpRequestMessage(HttpMethod.Get, $"{proxyUrl}/node/status");
        reqNonLoopback.Headers.Add("X-Test-Remote-IP", "8.8.8.8");
        var resNonLoopback = await client.SendAsync(reqNonLoopback);
        resNonLoopback.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 3. POST /node/lock -> 501
        var reqLock = new HttpRequestMessage(HttpMethod.Post, $"{proxyUrl}/node/lock");
        reqLock.Headers.Add("X-Test-Remote-IP", "127.0.0.1");
        var resLock = await client.SendAsync(reqLock);
        resLock.StatusCode.Should().Be(HttpStatusCode.NotImplemented);

        // 4. POST /node/sync-now -> 501
        var reqSync = new HttpRequestMessage(HttpMethod.Post, $"{proxyUrl}/node/sync-now");
        reqSync.Headers.Add("X-Test-Remote-IP", "127.0.0.1");
        var resSync = await client.SendAsync(reqSync);
        resSync.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
    }

    [Fact]
    public async Task LargeRequestBodySize_PassesThroughSuccessfully()
    {
        // Arrange
        var apiStub = await StartStubServerAsync(app =>
        {
            app.MapPost("/api/sync/upload", async (HttpContext ctx) =>
            {
                using var ms = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(ms);
                return Results.Ok(new { server = "api", size = ms.Length });
            });
        });

        var dummyChildren = new Dictionary<string, ReadyFileInfo>
        {
            { "Api", new ReadyFileInfo(111, apiStub.Urls.ToList(), "BeeMemoryBank.Api", "1.0.0", DateTime.UtcNow) },
            { "Web", new ReadyFileInfo(222, new[] { "http://localhost:5002" }, "BeeMemoryBank.Web", "1.0.0", DateTime.UtcNow) }
        };

        var (_, proxyUrl) = await StartProxyServerAsync(apiStub.Urls.First(), "http://localhost:5002", dummyChildren);
        using var client = new HttpClient();

        // 40 MB payload: Kestrel's real default MaxRequestBodySize is ~30 MB, so this would be
        // REJECTED without the 500 MB override in NodeFront - this size is chosen specifically
        // to cross that default boundary and prove the override is actually in effect.
        byte[] largeData = new byte[40_000_000];
        new Random().NextBytes(largeData);

        // Act
        var content = new ByteArrayContent(largeData);
        var response = await client.PostAsync($"{proxyUrl}/api/sync/upload", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<UploadResponse>();
        result.Should().NotBeNull();
        result!.Server.Should().Be("api");
        result.Size.Should().Be(largeData.Length);
    }

    private class DummyResponse
    {
        public string? Server { get; set; }
        public string? Route { get; set; }
        public string? ForwardedFor { get; set; }
        public string? Path { get; set; }
    }

    private class UploadResponse
    {
        public string? Server { get; set; }
        public long Size { get; set; }
    }
}
