using System.Net;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

public class SyncClient410Tests
{
    private class MockHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new();

        public void MapRoute(string path, Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _routes[path] = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("No URI");
            foreach (var (path, handler) in _routes)
            {
                if (uri.AbsolutePath.EndsWith(path, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(handler(request));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task PullEvents_Returns410_ThrowsSnapshotRequiredException()
    {
        var handler = new MockHandler();

        handler.MapRoute("/api/sync/sentinel", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });

        var testNodeId = Guid.NewGuid();
        handler.MapRoute("/api/sync/identity", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                nodeId = testNodeId,
                displayName = "TestRemote",
                ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32])
            }), Encoding.UTF8, "application/json")
        });

        handler.MapRoute("/api/sync/challenge", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                challenge = Convert.ToBase64String(new byte[32]),
                serverNodeId = testNodeId
            }), Encoding.UTF8, "application/json")
        });

        handler.MapRoute("/api/sync/authenticate", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                token = "test-token"
            }), Encoding.UTF8, "application/json")
        });

        handler.MapRoute("/api/sync/events", _ => new HttpResponseMessage(HttpStatusCode.Gone)
        {
            Content = new StringContent(
                """{"last_compaction_cp":500,"current_head_seq":1886,"message":"Too old"}""",
                Encoding.UTF8, "application/json")
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri("http://test.local") };

        var factory = new BmbWebApplicationFactory();
        try
        {
            await factory.InitializeNodeAsync("TestNode", "testPassword");

            using var scope = factory.Services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Core.Services.SessionService>();
            await session.UnlockAsync("testPassword");
            var syncClient = scope.ServiceProvider.GetRequiredService<SyncClient>();

            var ex = await Assert.ThrowsAsync<SnapshotRequiredException>(
                () => syncClient.SyncWithAsync(http, "http://test.local"));

            Assert.Equal(500, ex.LastCompactionCp);
            Assert.Equal(1886, ex.CurrentHeadSeq);
            Assert.Equal("http://test.local", ex.RemoteUrl);
            Assert.Equal("Too old", ex.Message);
        }
        finally
        {
            factory.Dispose();
        }
    }

    [Fact]
    public async Task PullEvents_Returns410_NonJsonBody_UsesDefaults()
    {
        var handler = new MockHandler();

        handler.MapRoute("/api/sync/sentinel", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });

        var testNodeId = Guid.NewGuid();
        handler.MapRoute("/api/sync/identity", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                nodeId = testNodeId,
                displayName = "TestRemote",
                ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32])
            }), Encoding.UTF8, "application/json")
        });

        handler.MapRoute("/api/sync/challenge", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                challenge = Convert.ToBase64String(new byte[32]),
                serverNodeId = testNodeId
            }), Encoding.UTF8, "application/json")
        });

        handler.MapRoute("/api/sync/authenticate", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                token = "test-token"
            }), Encoding.UTF8, "application/json")
        });

        handler.MapRoute("/api/sync/events", _ => new HttpResponseMessage(HttpStatusCode.Gone)
        {
            Content = new StringContent("not json", Encoding.UTF8, "text/plain")
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri("http://test.local") };

        var factory = new BmbWebApplicationFactory();
        try
        {
            await factory.InitializeNodeAsync("TestNode", "testPassword");

            using var scope = factory.Services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Core.Services.SessionService>();
            await session.UnlockAsync("testPassword");
            var syncClient = scope.ServiceProvider.GetRequiredService<SyncClient>();

            var ex = await Assert.ThrowsAsync<SnapshotRequiredException>(
                () => syncClient.SyncWithAsync(http, "http://test.local"));

            Assert.Equal(0, ex.LastCompactionCp);
            Assert.Equal(0, ex.CurrentHeadSeq);
            Assert.Equal("http://test.local", ex.RemoteUrl);
        }
        finally
        {
            factory.Dispose();
        }
    }

    [Fact]
    public void SnapshotRequiredState_SetAndClear()
    {
        var state = new SnapshotRequiredState();
        Assert.False(state.IsRequired);
        Assert.Null(state.LastException);

        var ex = new SnapshotRequiredException("http://remote", 100, 200, "test");
        state.Set(ex);
        Assert.True(state.IsRequired);
        Assert.Same(ex, state.LastException);
        Assert.Equal(100, state.LastException!.LastCompactionCp);

        state.Clear();
        Assert.False(state.IsRequired);
        Assert.Null(state.LastException);
    }
}
