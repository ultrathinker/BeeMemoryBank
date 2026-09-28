using System.Net;
using System.Reflection;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Review L-merge #5: a peer revoked between its token check and my-standing's read of the active
/// whitelist gets an authorization answer, not a 500. The race is made deterministic by a whitelist
/// that already leaves the caller out of the active list while its own lookup still finds it.
/// </summary>
public class MyStandingRevokedTests : IDisposable
{
    private static readonly Guid Peer = Guid.NewGuid();
    private readonly RevokedMidRequestFactory _node = new();

    public void Dispose() => _node.Dispose();

    [Fact]
    public async Task APeerRevokedMidRequest_Gets401()
    {
        await _node.InitializeNodeAsync(password: "myStandingPw1!");
        await _node.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = Peer, DisplayName = "peer", Ed25519PublicKey = new byte[32], Status = "A",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        using var http = _node.Server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer",
            _node.Services.GetRequiredService<SyncTokenStore>().IssueToken(Peer, SyncProtocolVersion.Current));

        var resp = await http.GetAsync("/api/sync/my-standing");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private sealed class RevokedMidRequestFactory : BmbWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s => s.AddSingleton<IWhitelistRepository>(sp =>
                RevokedFromActiveList.Wrap(new WhitelistRepository(sp.GetRequiredService<DbConnectionFactory>()))));
        }
    }

    /// <summary>Forwards everything; the active list alone no longer has <see cref="Peer"/>.</summary>
    public class RevokedFromActiveList : DispatchProxy
    {
        private IWhitelistRepository _inner = null!;

        public static IWhitelistRepository Wrap(IWhitelistRepository inner)
        {
            var proxy = Create<IWhitelistRepository, RevokedFromActiveList>();
            ((RevokedFromActiveList)(object)proxy)._inner = inner;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var result = method!.Invoke(_inner, args);
            return method.Name == nameof(IWhitelistRepository.GetAllActiveAsync)
                ? WithoutPeer((Task<List<WhitelistEntry>>)result!)
                : result;
        }

        private static async Task<List<WhitelistEntry>> WithoutPeer(Task<List<WhitelistEntry>> rows) =>
            (await rows).Where(r => r.NodeId != Peer).ToList();
    }
}
