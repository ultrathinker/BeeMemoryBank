using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-42, the divergence the owner's decision fixes: a node joined with the master password did a
/// superadmin-only action on its own copy (hard delete, password change) and applied it locally,
/// while the node it joined through refused the event — "requires superadmin", quarantined — and the
/// two copies stayed different for good. Both nodes here are real in-process nodes; the host's row
/// for the joiner is the one the real <c>/api/join</c> wrote.
/// </summary>
public class JoinSuperadminDivergenceTests : IAsyncLifetime
{
    private const string MasterPassword = "joinDivergencePassword";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private BmbWebApplicationFactory _host = null!;
    private BmbWebApplicationFactory _joiner = null!;
    private HttpClient _hostClient = null!;
    private HttpClient _joinerClient = null!;

    public async Task InitializeAsync()
    {
        _host = new BmbWebApplicationFactory();
        await _host.InitializeNodeAsync("Host", MasterPassword);
        _hostClient = _host.CreateClient();
        await UnlockAsync(_hostClient, MasterPassword);

        _joiner = new BmbWebApplicationFactory();
        await _joiner.JoinNodeAsync(_hostClient, "Joiner", MasterPassword);
        _joinerClient = _joiner.CreateClient();
        await UnlockAsync(_joinerClient, MasterPassword);
    }

    public Task DisposeAsync()
    {
        _hostClient.Dispose();
        _joinerClient.Dispose();
        _joiner.Dispose();
        _host.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task HardDeleteOnJoinedNode_IsAppliedByTheHostToo()
    {
        var create = await _hostClient.PostAsJsonAsync("/api/articles",
            new { title = "Doomed", treePath = "/Divergence", content = "body" });
        create.EnsureSuccessStatusCode();
        var articleId = (await create.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("id").GetString()!;
        await SyncJoinerWithHostAsync();
        (await _joinerClient.GetAsync($"/api/articles/{articleId}")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _joinerClient.PostAsync($"/api/hard-delete/article/{articleId}", null)).EnsureSuccessStatusCode();
        (await _joinerClient.GetAsync($"/api/articles/{articleId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        await SyncJoinerWithHostAsync();

        (await _hostClient.GetAsync($"/api/articles/{articleId}")).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the joiner knows the master password, so its hard delete must apply on the host as well");
        (await QuarantinedOnHostAsync()).Should().BeEmpty("the hard_delete must not have been refused");
    }

    [Fact]
    public async Task PasswordChangeOnJoinedNode_RaisesTheNoticeOnTheHost()
    {
        var change = await _joinerClient.PostAsJsonAsync("/api/keys/change-password",
            new { oldPassword = MasterPassword, newPassword = MasterPassword + "-new" });
        change.EnsureSuccessStatusCode();

        await SyncJoinerWithHostAsync();

        using var scope = _host.Services.CreateScope();
        var notice = await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>()
            .GetMasterPasswordNoticeAsync();
        notice.Should().NotBeNull(
            "the host must hear that the password changed elsewhere, or it keeps accepting the old one silently");
        notice!.Value.ByNode.Should().Be("Joiner");
        (await QuarantinedOnHostAsync()).Should().BeEmpty();
    }

    private async Task SyncJoinerWithHostAsync()
    {
        Guid hostId;
        using (var hostScope = _host.Services.CreateScope())
            hostId = (await hostScope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;

        using var scope = _joiner.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SyncClient>().SyncWithAsync(_hostClient, "", hostId);
    }

    private async Task<List<string>> QuarantinedOnHostAsync()
    {
        using var scope = _host.Services.CreateScope();
        var entries = await SyncEventQuarantine.ListAllAsync(
            scope.ServiceProvider.GetRequiredService<ISyncQuarantineRepository>());
        return entries.Select(e => $"{e.EventType}: {e.LastError}").ToList();
    }

    private static async Task UnlockAsync(HttpClient client, string password) =>
        (await client.PostAsJsonAsync("/api/session/unlock", new { Password = password })).EnsureSuccessStatusCode();
}
