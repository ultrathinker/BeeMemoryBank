using System.Net.Http.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Metadata;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>A held-events answer a test sets (the real status is track 1's, merged later).</summary>
public sealed class ControllableHeldEventStatus : IHeldEventStatus
{
    public bool Held { get; set; }

    public Task<bool> AnyHeldAsync(CancellationToken ct = default) => Task.FromResult(Held);

    public Task<HeldEventSummary> GetSummaryAsync(CancellationToken ct = default) =>
        Task.FromResult(new HeldEventSummary(Held ? 1 : 0, Held ? 1 : 0, 0, 0, null, 0, 0, 0));
}

/// <summary>A full node for the Step A tests: initialized, unlocked, with a held-events answer the test controls.</summary>
public class StepAFactory : BmbWebApplicationFactory
{
    public ControllableHeldEventStatus Held { get; } = new();

    /// <summary>What a test adds to the host's services (after the held-events answer is in place).</summary>
    protected virtual void ConfigureStepAServices(IServiceCollection services) { }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IHeldEventStatus>(Held));
            ConfigureStepAServices(services);
        });
    }
}

/// <summary>Set-up helpers shared by the Step A tests of track 3 (baselines, join, restore).</summary>
public static class StepANode
{
    public const string Password = "stepAPassword1!";

    /// <summary>Initializes the node and logs in, so the vault is unlocked.</summary>
    public static async Task<StepAFactory> CreateAsync(string displayName = "StepANode")
    {
        var node = new StepAFactory();
        await node.InitializeNodeAsync(displayName, Password);
        using var client = node.CreateClient();
        (await client.PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password })).EnsureSuccessStatusCode();
        return node;
    }

    /// <summary>
    /// Activates the node the way spec 14 says a test may (the activation service is track 1's): one key row wrapped under the master DEK and
    /// one activation row, then the policy flipped. Returns the key id and the open metadata key.
    /// </summary>
    public static Task<(string KeyId, byte[] Mek)> ActivateAsync(BmbWebApplicationFactory node, int generation = 1) => ActivateAsync(node.Services, generation);

    /// <summary>The same for any host's services (a test that builds its own host): the node must be unlocked.</summary>
    public static async Task<(string KeyId, byte[] Mek)> ActivateAsync(IServiceProvider services, int generation = 1)
    {
        var dek = services.GetRequiredService<SessionService>().GetMasterDek();
        var (mek, row) = MetadataKeyCrypto.Create(generation, dek);
        var self = (await services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var now = DateTime.UtcNow.ToString("o");
        using var conn = services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_metadata_key (key_id, generation, dek_fingerprint, epoch_hint, wrapped, iv, author_node_id, created_at, lamport_ts, event_id)
              VALUES (@KeyId, @Generation, @Fp, 0, @Wrapped, @Iv, @Author, @Now, 1, @EventId)",
            new { row.KeyId, row.Generation, Fp = row.DekFingerprint, row.Wrapped, row.Iv, Author = self.NodeId.ToString(), Now = now, EventId = Guid.NewGuid().ToString() });
        await conn.ExecuteAsync(
            @"INSERT OR IGNORE INTO tbl_metadata_activation (activation_id, key_id, generation, activated_at, author_node_id, event_id, lamport_ts, applied_at)
              VALUES (@Id, @KeyId, @Generation, @Now, @Author, @EventId, 1, @Now)",
            new { Id = Guid.NewGuid().ToString(), row.KeyId, row.Generation, Now = now, Author = self.NodeId.ToString(), EventId = Guid.NewGuid().ToString() });
        services.GetRequiredService<ISyncProtocolPolicy>().MarkActivated();
        return (row.KeyId, mek);
    }

    /// <summary>Canary strings for every kind of metadata a vault holds (spec 16.1), written through the real services.</summary>
    public sealed record SeededVault(List<string> Canaries, List<Guid> ArticleIds);

    public static async Task<SeededVault> SeedWithCanariesAsync(BmbWebApplicationFactory node)
    {
        var canaries = new List<string>();
        string Ascii(string label) { var c = MetadataCanaries.Ascii(label); canaries.Add(c); return c; }
        string NonAscii(string label) { var c = MetadataCanaries.NonAscii(label); canaries.Add(c); return c; }

        using var scope = node.Services.CreateScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var ids = new List<Guid>();

        var folder = $"/{Ascii("fa")}/{Ascii("fb")}/{NonAscii("fc")}";
        var tag = Ascii("tag");
        ids.Add((await articles.CreateAsync(Ascii("title"), folder, [tag, tag.ToUpperInvariant()], "the first body")).Id);
        ids.Add((await articles.CreateAsync(NonAscii("title"), $"/{Ascii("other")}", [NonAscii("tag")], "the second body")).Id);
        return new SeededVault(canaries, ids);
    }

    /// <summary>The baseline's plaintext database, opened with the metadata key into <paramref name="targetPath"/>.</summary>
    public static async Task OpenBaselineAsync(string directory, string keyId, byte[] mek, string targetPath)
    {
        await ChunkedContainer.DecryptFileAsync(
            Path.Combine(directory, "baseline.bmbch"), targetPath, ChunkedContainer.PurposeBaseline,
            h => h.Key == ChunkedContainer.MekKeyLabel(keyId) ? mek : null);
    }
}
