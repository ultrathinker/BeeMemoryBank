extern alias CliApp;

using System.Net;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Startup;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync;
using CliApp::BeeMemoryBank.Cli;
using CliApp::BeeMemoryBank.Cli.Commands;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-81, end to end: <c>bmb join</c> (the real <see cref="JoinCommand"/>) against a real Api on Kestrel. The CLI used
/// to stop after the key exchange: no snapshot, no pull position, exit code 0 - and the first sync then asked a host whose
/// log had ever been compacted (or re-keyed) for sequence 0, got 410 forever, and the joined node stayed empty. It now
/// takes the snapshot like the Setup page and the phone, and a failure leaves no half-made node.
/// </summary>
[Collection(ProcessWideRateLimiterCollection.Name)]
public class CliJoinTests : IAsyncLifetime
{
    private const string Password = "cliJoinHostPassword1";
    private const int NoteCount = 12;

    private readonly string _dataA = Path.Combine(Path.GetTempPath(), "bmb_clijoin_a_" + Guid.NewGuid().ToString("N"));
    private readonly string _dataB = Path.Combine(Path.GetTempPath(), "bmb_clijoin_b_" + Guid.NewGuid().ToString("N"));
    private WebApplication _nodeA = null!;
    private string _urlA = null!;
    private Guid _nodeAId;
    private volatile bool _forgeSnapshotSignature;

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("BMB_INTERNAL_KEY", BmbWebApplicationFactory.InternalKeyForTests);
        Directory.CreateDirectory(_dataA);
        BeeMemoryBank.Api.Middleware.RateLimitMiddleware.ResetForTests();

        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = "Testing";
        builder.WebHost.UseSetting("BeeMemoryBank:DataPath", _dataA);
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.AddBeeApiServices(_dataA);
        _nodeA = builder.Build();
        await _nodeA.RunBeeApiStartupTasksAsync(_dataA);
        // A host whose snapshot does not verify (damaged in transit, or forged): the joiner must refuse it.
        _nodeA.Use(async (ctx, next) =>
        {
            if (_forgeSnapshotSignature && ctx.Request.Path == "/api/sync/snapshot/for-join")
                ctx.Response.OnStarting(() =>
                {
                    ctx.Response.Headers["X-BMB-Snapshot-Signature"] = Convert.ToBase64String(new byte[64]);
                    return Task.CompletedTask;
                });
            await next();
        });
        _nodeA.UseBeeApiPipeline();
        _nodeA.MapBeeApiEndpoints();
        await _nodeA.StartAsync();
        _urlA = _nodeA.Urls.First(u => u.StartsWith("http://127.0.0.1"));

        using var scope = _nodeA.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<InitializationService>().InitializeAsync("admin", "NodeA", Password);
        _nodeAId = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
        (await _nodeA.Services.GetRequiredService<SessionService>().UnlockAsync(Password)).Should().BeTrue();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        for (var i = 1; i <= NoteCount; i++)
            await articles.CreateAsync($"Note {i}", "/Notes", [], $"Body of note {i}.");
    }

    public async Task DisposeAsync()
    {
        await _nodeA.StopAsync();
        await _nodeA.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var dir in new[] { _dataA, _dataB })
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort, like the factory */ }
    }

    [Fact]
    public async Task CliJoin_AfterTheSourceCompacted_ImportsTheNotes_AndWritesTheSyncPosition()
    {
        using (var scope = _nodeA.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CompactionService>()
                .ExecuteAsync(explicitCp: 8, reason: "BMB-81", acceptCuttingOffPeers: true);

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_dataB, _urlA, Password, "CliJoiner", output: output);

        rc.Should().Be(0, output.ToString());
        await using var services = await CliServiceProvider.CreateAsync(_dataB);
        using var joined = services.CreateScope();
        (await CountNotesAsync(joined.ServiceProvider)).Should().Be(NoteCount, "the snapshot carries every note, compacted log or not");
        var position = await joined.ServiceProvider.GetRequiredService<ISyncPositionRepository>().GetAsync(_nodeAId);
        position.Should().NotBeNull("without a pull position the first sync asks for sequence 0");
        position!.LastSequenceNum.Should().BeGreaterThanOrEqualTo(8, "the position is the snapshot's checkpoint, past the compaction");
        (await joined.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.InitialSyncCompleted.Should().BeTrue();

        // The first sync after the join continues from the checkpoint instead of hitting 410 SEQUENCE_TOO_OLD.
        (await joined.ServiceProvider.GetRequiredService<SessionService>().UnlockAsync(Password)).Should().BeTrue();
        using var http = new HttpClient();
        var sync = () => joined.ServiceProvider.GetRequiredService<SyncClient>().SyncWithPeerAsync(http, _urlA, _nodeAId);
        await sync.Should().NotThrowAsync("the first sync after the join must not fail for any reason, not only for a snapshot the peer still asks for");
    }

    [Fact]
    public async Task CliJoin_WithoutCompaction_StillImportsTheNotes()
    {
        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_dataB, _urlA, Password, "CliJoiner", output: output);

        rc.Should().Be(0, output.ToString());
        output.ToString().Should().Contain($"Imported {NoteCount} note(s)");
        await using var services = await CliServiceProvider.CreateAsync(_dataB);
        using var joined = services.CreateScope();
        (await CountNotesAsync(joined.ServiceProvider)).Should().Be(NoteCount);
        (await joined.ServiceProvider.GetRequiredService<ISyncPositionRepository>().GetAsync(_nodeAId)).Should().NotBeNull();
    }

    [Fact]
    public async Task CliJoin_WhenTheSnapshotCannotBeImported_FailsNonZero_AndLeavesNoNode()
    {
        _forgeSnapshotSignature = true;
        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_dataB, _urlA, Password, "CliJoiner", output: output);

        rc.Should().NotBe(0, output.ToString());
        output.ToString().Should().Contain("signature verification failed");
        await using (var services = await CliServiceProvider.CreateAsync(_dataB))
        {
            using var scope = services.CreateScope();
            (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync()).Should().BeNull("no half-made node");
            (await CountNotesAsync(scope.ServiceProvider)).Should().Be(0);
        }

        _forgeSnapshotSignature = false;
        BeeMemoryBank.Api.Middleware.RateLimitMiddleware.ResetForTests();
        var again = new StringWriter();
        (await JoinCommand.HandleAsync(_dataB, _urlA, Password, "CliJoiner", output: again)).Should().Be(0, again.ToString());
        await using var after = await CliServiceProvider.CreateAsync(_dataB);
        using var joined = after.CreateScope();
        (await CountNotesAsync(joined.ServiceProvider)).Should().Be(NoteCount);
    }

    private static async Task<long> CountNotesAsync(IServiceProvider services)
    {
        using var conn = services.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tbl_article WHERE status = 'A'";
        return await Task.Run(() => Convert.ToInt64(cmd.ExecuteScalar()));
    }
}
