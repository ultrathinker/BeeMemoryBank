using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Endpoints;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.BlindLimits;
using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Integration.Tests;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindNode.Tests;

/// <summary>
/// The contract of the node's door (srvfix8, review RV6, tests the review named): every route of <see cref="BlindUsageMiddleware.DataRouteKeys"/> reaches
/// <see cref="BlindAdmissionExtensions.ReserveForPeer"/> in its REAL handler once it has a valid credential. A handler that forgets the hook still works - its bytes are counted - but
/// reserves nothing, so N callers could pass the same remaining budget together; nothing else fails when that happens, so this does. A new data route has no recipe here and fails too.
/// </summary>
public sealed class BlindMeteredRouteHookTests : IDisposable
{
    private readonly SpyNodeFactory _node = new();

    public void Dispose() => _node.Dispose();

    /// <summary>What the host saw: for each "METHOD path", whether the handler had told the door who was asking by the time the request ended.</summary>
    private sealed class SpyNodeFactory : BlindNodeFactory
    {
        public ConcurrentDictionary<string, bool> Reserved { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new Spy(Reserved)));
        }
    }

    private sealed class Spy(ConcurrentDictionary<string, bool> seen) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, nextMiddleware) =>
            {
                var items = new RecordingItems(ctx.Items);
                ctx.Items = items;
                // The note is made when the response STARTS - the handler has called the hook, or answered without it, by then - so a test never waits for the pipeline to unwind.
                ctx.Response.OnStarting(() =>
                {
                    if (items.Admission is { } admission) seen[ctx.Request.Method + " " + ctx.Request.Path.Value] = admission.IsAuthenticated;
                    return Task.CompletedTask;
                });
                await nextMiddleware(ctx);
            });
            next(app);
        };
    }

    /// <summary>The request's items, remembering the admission the door puts in them (the door takes it out again when the request ends).</summary>
    private sealed class RecordingItems(IDictionary<object, object?> inner) : IDictionary<object, object?>
    {
        public BlindAdmission? Admission { get; private set; }

        public object? this[object key]
        {
            get => inner[key];
            set
            {
                if (value is BlindAdmission admission) Admission = admission;
                inner[key] = value;
            }
        }

        public ICollection<object> Keys => inner.Keys;
        public ICollection<object?> Values => inner.Values;
        public int Count => inner.Count;
        public bool IsReadOnly => inner.IsReadOnly;
        public void Add(object key, object? value) => this[key] = value;
        public void Add(KeyValuePair<object, object?> item) => this[item.Key] = item.Value;
        public void Clear() => inner.Clear();
        public bool Contains(KeyValuePair<object, object?> item) => inner.Contains(item);
        public bool ContainsKey(object key) => inner.ContainsKey(key);
        public void CopyTo(KeyValuePair<object, object?>[] array, int arrayIndex) => inner.CopyTo(array, arrayIndex);
        public IEnumerator<KeyValuePair<object, object?>> GetEnumerator() => inner.GetEnumerator();
        public bool Remove(object key) => inner.Remove(key);
        public bool Remove(KeyValuePair<object, object?> item) => inner.Remove(item);
        public bool TryGetValue(object key, out object? value) => inner.TryGetValue(key, out value);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private async Task<string> PeerTokenAsync()
    {
        var peer = Guid.NewGuid();
        using var scope = _node.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = peer, DisplayName = "Phone", Ed25519PublicKey = new byte[32], Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        return _node.Services.GetRequiredService<SyncTokenStore>().IssueToken(peer, SyncProtocolVersion.Current);
    }

    private async Task<string> RestoreCodeAsync()
    {
        using var scope = _node.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<BlindRestoreCodeService>().IssueAsync("test")).Code;
    }

    private static StringContent Json(string text) => new(text, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Every_data_route_reaches_ReserveForPeer_in_its_handler_with_a_valid_credential()
    {
        _ = _node.Services;
        var token = await PeerTokenAsync();
        using var local = _node.CreateClient();
        var pairCode = BlindPairCode.Parse((await local.GetFromJsonAsync<JsonElement>("/api/blind/pair-code")).GetProperty("code").GetString()!);
        var seeder = Guid.NewGuid();
        var http = _node.Server.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);

        HttpRequestMessage Peer(HttpMethod method, string path, HttpContent? body = null)
        {
            var request = new HttpRequestMessage(method, path) { Content = body };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return request;
        }

        async Task<HttpRequestMessage> Restore(HttpMethod method, string path, HttpContent? body = null)
        {
            var request = new HttpRequestMessage(method, path) { Content = body };
            request.Headers.Add(BlindRestoreEndpoints.RestoreCodeHeader, await RestoreCodeAsync());
            return request;
        }

        var seedId = Guid.NewGuid();
        var hash = "{\"hashes\":[\"" + new string('a', 64) + "\"]}";
        var claim = JsonSerializer.Serialize(new { nodeId = Guid.NewGuid(), displayName = "Restored", publicKeyB64 = Convert.ToBase64String(new byte[32]), apiAddress = (string?)null });
        var recipes = new Dictionary<string, Func<Task<HttpRequestMessage>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["GET /api/sync/events"] = () => Task.FromResult(Peer(HttpMethod.Get, "/api/sync/events?afterSequence=0&limit=10")),
            ["POST /api/sync/events"] = () => Task.FromResult(Peer(HttpMethod.Post, "/api/sync/events", Json("[]"))),
            ["POST /api/sync/blobs"] = () => Task.FromResult(Peer(HttpMethod.Post, "/api/sync/blobs", Json("{\"blobs\":[]}"))),
            ["POST /api/sync/blobs/check"] = () => Task.FromResult(Peer(HttpMethod.Post, "/api/sync/blobs/check", Json(hash))),
            ["POST /api/sync/blobs/get"] = () => Task.FromResult(Peer(HttpMethod.Post, "/api/sync/blobs/get", Json(hash))),
            ["GET /api/blind/replica"] = () => Task.FromResult(Peer(HttpMethod.Get, "/api/blind/replica")),
            ["GET /api/blind/restore/package"] = () => Restore(HttpMethod.Get, "/api/blind/restore/package"),
            ["GET /api/blind/restore/events"] = () => Restore(HttpMethod.Get, "/api/blind/restore/events"),
            ["POST /api/blind/claim"] = () => Restore(HttpMethod.Post, "/api/blind/claim", Json(claim)),
            ["POST /api/blind/seed"] = () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"/api/blind/seed?seedId={seedId}&offset=0&total=10&sha256={new string('0', 64)}") { Content = new ByteArrayContent(new byte[10]) };
                request.Headers.Add(BlindSeederProof.NodeIdHeader, seeder.ToString());
                request.Headers.Add(BlindSeederProof.KeyHeader, Convert.ToBase64String(new byte[32]));
                request.Headers.Add(BlindSeederProof.MacHeader, BlindSeederProof.Compute(pairCode.Secret, seedId, seeder, new byte[32]));
                return Task.FromResult(request);
            },
        };

        var problems = new List<string>();
        foreach (var key in BlindUsageMiddleware.DataRouteKeys)
        {
            if (!recipes.TryGetValue(key, out var recipe))
            {
                problems.Add($"{key}: no recipe for this data route - add one here, and make its handler call ReserveForPeer() once the caller is known");
                continue;
            }
            using var request = await recipe();
            using var response = await http.SendAsync(request);
            await response.Content.ReadAsByteArrayAsync();
            var path = request.RequestUri!.AbsolutePath;   // the client has made it absolute by now
            if (response.StatusCode == HttpStatusCode.Unauthorized) problems.Add($"{key}: the recipe's credential was refused (401); the test does not reach the handler");
            else if (!_node.Reserved.TryGetValue(request.Method + " " + path, out var reserved)) problems.Add($"{key}: the door never admitted the request (answered {(int)response.StatusCode})");
            else if (!reserved) problems.Add($"{key}: the handler answered {(int)response.StatusCode} without calling ReserveForPeer() - the request reserved nothing");
        }

        string.Join(Environment.NewLine, problems).Should().BeEmpty("a data route whose handler forgets ReserveForPeer() counts its bytes but reserves none");
    }
}
