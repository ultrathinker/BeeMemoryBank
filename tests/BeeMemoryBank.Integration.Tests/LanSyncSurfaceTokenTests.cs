using System.Net;
using System.Text;
using BeeMemoryBank.Node;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The sync surface of the LAN listener (docs/design/lan-peer-sync.md section 5) is the node-key-authenticated peer routes: every route of
/// <see cref="LanSurfaces.Sync"/> but the four of the handshake refuses a caller that has no sync token, on the real Api, as a keyless caller from the
/// LAN reaches it.
/// </summary>
public sealed class LanSyncSurfaceTokenTests : IDisposable
{
    private readonly BmbWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task EveryRouteButTheHandshake_RefusesACallerWithoutAToken()
    {
        using var keyless = _factory.Server.CreateClient();   // no internal key: what the LAN listener forwards
        foreach (var (method, path) in LanSurfaces.Sync.Where(r => !LanSurfaces.IsHandshake(r.Method, new Microsoft.AspNetCore.Http.PathString(r.Path))))
        {
            var url = path == "/api/sync/report-position" ? path + "?sequence=0" : path;
            using var req = new HttpRequestMessage(new HttpMethod(method), url);
            if (method == "POST") req.Content = new StringContent(path.EndsWith("/blobs/check") || path.EndsWith("/blobs/get") ? "{\"hashes\":[]}" : path.EndsWith("/blobs") ? "{\"blobs\":[]}" : path.EndsWith("/events") ? "[]" : "{}", Encoding.UTF8, "application/json");
            var resp = await keyless.SendAsync(req);
            resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{method} {path} needs a sync token");

            using var forged = new HttpRequestMessage(new HttpMethod(method), url);
            forged.Headers.Authorization = new("Bearer", "not-a-token");
            if (method == "POST") forged.Content = new StringContent(req.Content is null ? "{}" : await req.Content.ReadAsStringAsync(), Encoding.UTF8, "application/json");
            (await keyless.SendAsync(forged)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{method} {path} refuses a token it did not issue");
        }
    }
}
