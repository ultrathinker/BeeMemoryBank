using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The PC's side of blind nodes once it has activated Step A (docs/design/step-a-spec.md 8.7, 9.2, 16.4 item 8): a blind node is refreshed with a
/// <c>baseline</c> package after the blobs it lacks were shipped, its log is not reset and the push position stays; a first seed is a
/// <c>full</c> package with its blobs; with no baseline yet nothing is sent and one is asked for. The blind node is a real blind host (track 2's
/// seed route adopts what is sent); the upload is also copied on its way and read back through the real format-2 reader.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class BlindNodeBaselineRefreshTests : IAsyncLifetime
{
    private const string Password = "blindRefreshPw1!";
    private readonly BlindNodeFactory _blind = new(drainWaitSeconds: 10);
    private readonly RefreshPcFactory _pc = new();
    private readonly SeedCatcher _catcher;
    private HttpClient _pcClient = null!;
    private readonly string _work = Path.Combine(Path.GetTempPath(), "bmb_blind_refresh_" + Guid.NewGuid().ToString("N"));

    public BlindNodeBaselineRefreshTests()
    {
        _catcher = new SeedCatcher(_blind);
    }

    /// <summary>A PC whose host also has the format-2 writer a full hub serves with (track 2's hook H3 registers it after the merge).</summary>
    private sealed class RefreshPcFactory : StepAFactory
    {
        public RecordingScheduler Scheduler { get; } = new();

        protected override void ConfigureStepAServices(IServiceCollection services)
        {
            services.Replace(ServiceDescriptor.Singleton<IBaselineScheduler>(Scheduler));
            services.TryAddScoped<IBlindPackageV2Writer>(sp => new BlindPackageV2Writer(
                sp.GetRequiredService<IDbConnectionFactory>(), sp.GetRequiredService<INodeIdentityRepository>(),
                sp.GetRequiredService<IWhitelistRepository>(), sp.GetRequiredService<ISyncPositionRepository>(),
                sp.GetRequiredService<IEventLogRepository>(),
                new BlindPackageV2WriterOptions(
                    (identity, payload) => sp.GetRequiredService<INodeAuthSigner>().SignChallenge(identity, payload),
                    Path.Combine(Path.GetTempPath(), "bmb_blind_refresh_out"), Path.Combine(Path.GetTempPath(), "bmb_blind_refresh_stage"),
                    ServingNodeIsSuperadmin: _ => Task.FromResult(true))));
        }
    }

    private sealed class RecordingScheduler : IBaselineScheduler
    {
        public List<string> Requests { get; } = [];
        public void RequestEarly(string reasonCode) => Requests.Add(reasonCode);
    }

    /// <summary>Everything goes to the blind host; the parts of a seed upload are copied on the way.</summary>
    private sealed class SeedCatcher : DelegatingHandler
    {
        public SeedCatcher(BlindNodeFactory blind) : base(blind.Server.CreateHandler()) { }

        public bool Recording { get; set; }
        public List<string> BlobBatches { get; } = [];
        public Dictionary<Guid, MemoryStream> Seeds { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/sync/blobs" && request.Method == HttpMethod.Post) BlobBatches.Add(path);
            if (Recording && path == "/api/blind/seed" && request.Method == HttpMethod.Post)
            {
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                var seedId = Guid.Parse(query["seedId"]!);
                if (!Seeds.TryGetValue(seedId, out var parts)) Seeds[seedId] = parts = new MemoryStream();
                var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
                parts.Write(bytes);
                request.Content = new ByteArrayContent(bytes);
            }
            return await base.SendAsync(request, ct);
        }
    }

    public async Task InitializeAsync()
    {
        _ = _blind.Services;
        _pc.RouteOutboundHttpThrough(_catcher);
        await _pc.InitializeNodeAsync("PC", Password);
        _pcClient = _pc.CreateClient();
        (await _pcClient.PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password })).EnsureSuccessStatusCode();
        Directory.CreateDirectory(_work);
    }

    public Task DisposeAsync()
    {
        _pcClient.Dispose();
        _pc.Dispose();
        _blind.Dispose();
        return Task.CompletedTask;
    }

    private async Task<BlindPairCode> PairCodeAsync()
    {
        using var console = _blind.CreateClient();
        var body = await console.GetFromJsonAsync<JsonElement>("/api/blind/pair-code");
        return BlindPairCode.Parse(body.GetProperty("code").GetString()!);
    }

    /// <summary>A blind node paired and seeded the way 2.5 does it, before the activation.</summary>
    private async Task<(Guid BlindId, long PushedAt)> PairBeforeActivationAsync()
    {
        await StepANode.SeedWithCanariesAsync(_pc);
        var code = await PairCodeAsync();
        var add = await _pcClient.PostAsJsonAsync("/api/blind-nodes/", new { code = code.ToString() });
        add.StatusCode.Should().Be(HttpStatusCode.OK, await add.Content.ReadAsStringAsync());
        var pushed = await _pc.Services.GetRequiredService<ISyncPushPositionRepository>().GetAsync(code.NodeId);
        return (code.NodeId, pushed!.LastPushedSeq);
    }

    private async Task<VerifiedPackageV2> ReadBackAsync(Guid seedId)
    {
        var archive = Path.Combine(_work, seedId.ToString("N") + ".tar.gz");
        await File.WriteAllBytesAsync(archive, _catcher.Seeds[seedId].ToArray());
        var identity = (await _pc.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        return await new BlindPackageV2Reader().ExtractVerifiedAsync(
            archive, detachedSignature: [], identity.Ed25519PublicKey,
            id => id == identity.NodeId ? (identity.Ed25519PublicKey, true) : null, Path.Combine(_work, seedId.ToString("N")));
    }

    [Fact]
    public async Task An_activated_pc_refreshes_a_blind_node_with_a_baseline_package_after_shipping_its_blobs_and_keeps_the_push_position()
    {
        var (blindId, pushedBefore) = await PairBeforeActivationAsync();
        // A note written after the seed: its body is a blob the blind node does not have yet.
        using (var scope = _pc.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Core.Services.ArticleService>().CreateAsync("After the seed", "/Late", [], "written after the blind node was seeded");
        await StepANode.ActivateAsync(_pc);
        var baseline = await _pc.Services.GetRequiredService<IBaselineProducer>().ProduceAsync(BaselineReasons.OnDemand);
        var listed = Format2Package.ParseBlobList(await File.ReadAllBytesAsync(Path.Combine(baseline.Directory, "blobs.txt")));
        listed.Should().NotBeEmpty("the vault's bodies are blobs");
        using (var before = _blind.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            (await before.QueryAsync<string>("SELECT hash FROM tbl_blob WHERE hash IN @listed", new { listed })).Count().Should().BeLessThan(listed.Count,
                "the note written after the seed is not on the blind node yet");
        _catcher.Recording = true;
        long eventsBefore;
        using (var db = _blind.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            eventsBefore = await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_event");

        await _pc.Services.GetRequiredService<BlindNodeManager>().ReseedAsync(blindId, CancellationToken.None);

        _catcher.Seeds.Should().ContainSingle("one baseline package went up");
        using (var db = _blind.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
        {
            (await db.QueryAsync<string>("SELECT hash FROM tbl_blob WHERE hash IN @listed", new { listed })).Should().BeEquivalentTo(listed,
                "every blob of the baseline was shipped before the package went up");
            (await db.ExecuteScalarAsync<string>("SELECT baseline_id FROM tbl_baseline WHERE is_current = 1")).Should()
                .BeEquivalentTo(baseline.Manifest.BaselineId.ToString(), "the blind node adopted the baseline it was sent");
            (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_event")).Should().BeGreaterThanOrEqualTo(eventsBefore,
                "the blind node's log is kept, not reset");
        }
        var package = await ReadBackAsync(_catcher.Seeds.Keys.Single());
        package.Manifest.Kind.Should().Be(BlindPackageKinds.Baseline);
        package.Manifest.ContainsBlobs.Should().BeFalse();
        package.ControlDbPath.Should().BeNull("a baseline package carries the baseline only");
        package.DeltasPath.Should().BeNull();
        package.Manifest.BaselineId.Should().Be(baseline.Manifest.BaselineId);
        package.Manifest.IncludesUpTo.Should().NotBeNull("what the blind node held was taken in first");
        // The sync that takes in what the blind node holds also pushes what it lacks, so the position may have moved on; it was not set back to
        // the package's cut, which a reseed of a node whose log is reset does (the blind node's log is kept here).
        (await _pc.Services.GetRequiredService<ISyncPushPositionRepository>().GetAsync(blindId))!.LastPushedSeq
            .Should().BeGreaterThanOrEqualTo(pushedBefore, "the blind node's log was not reset, so the push position is kept");

        // The next sync cycle's my-standing call shows what the blind node holds now and what it advertises.
        var row = (await _pc.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(blindId))!;
        using var http = _pc.Services.GetRequiredService<IHttpClientFactory>().CreateClient(SyncScheduler.HttpClientName);
        var manager = _pc.Services.GetRequiredService<BlindNodeManager>();
        await manager.AfterSyncAsync(row, http, failure: null, CancellationToken.None);
        var status = (await manager.ListAsync()).Single(n => n.NodeId == blindId);
        status.Store.Should().NotBeNull();
        status.Store!.BaselineId.Should().Be(baseline.Manifest.BaselineId);
        status.Capabilities.Should().Contain(MetadataCapabilities.EnvelopeV1);
    }

    /// <summary>
    /// Cross-track (integration round, spec 7.5 and 8.7 "push"): a blind node that reports a baseline older than the PC's newest gets the newer
    /// one at the next cycle, without asking. Without this a key rotation's baseline (key generation 2) and the baseline a hard delete asks for
    /// never reach a blind node that adopted one: it keeps the old key's baseline, its delta log is never trimmed and the hard-deleted content
    /// stays in its old baseline for good. Found by the Docker run of the integration round (a rotation, then 15 minutes, still generation 1).
    /// </summary>
    [Fact]
    public async Task A_blind_node_that_holds_an_older_baseline_than_the_newest_is_refreshed_at_the_next_cycle_once()
    {
        var (blindId, _) = await PairBeforeActivationAsync();
        await StepANode.ActivateAsync(_pc);
        var first = await _pc.Services.GetRequiredService<IBaselineProducer>().ProduceAsync(BaselineReasons.OnDemand);
        var manager = _pc.Services.GetRequiredService<BlindNodeManager>();
        await manager.ReseedAsync(blindId, CancellationToken.None);
        var row = (await _pc.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(blindId))!;
        using var http = _pc.Services.GetRequiredService<IHttpClientFactory>().CreateClient(SyncScheduler.HttpClientName);

        await manager.AfterSyncAsync(row, http, failure: null, CancellationToken.None);
        _catcher.Recording = true;
        await manager.AfterSyncAsync(row, http, failure: null, CancellationToken.None);
        _catcher.Seeds.Should().BeEmpty("the blind node holds the newest baseline: a cycle sends nothing");

        using (var scope = _pc.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Core.Services.ArticleService>().CreateAsync("After the first baseline", "/Later", [], "written after it");
        var second = await _pc.Services.GetRequiredService<IBaselineProducer>().ProduceAsync(BaselineReasons.KeyRotation);
        second.Manifest.BaselineId.Should().NotBe(first.Manifest.BaselineId);

        await manager.AfterSyncAsync(row, http, failure: null, CancellationToken.None);

        _catcher.Seeds.Should().ContainSingle("the newer baseline went up at that cycle");
        using (var db = _blind.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            (await db.ExecuteScalarAsync<string>("SELECT baseline_id FROM tbl_baseline WHERE is_current = 1")).Should()
                .BeEquivalentTo(second.Manifest.BaselineId.ToString(), "the blind node adopted the newer baseline without asking for it");
        await manager.AfterSyncAsync(row, http, failure: null, CancellationToken.None);
        _catcher.Seeds.Should().ContainSingle("a baseline is offered once: the next cycle finds the blind node current");
    }

    [Fact]
    public async Task With_no_baseline_yet_nothing_is_sent_one_is_asked_for_and_the_failure_is_the_one_the_page_shows()
    {
        var (blindId, _) = await PairBeforeActivationAsync();
        await StepANode.ActivateAsync(_pc);
        _catcher.Recording = true;

        var act = () => _pc.Services.GetRequiredService<BlindNodeManager>().ReseedAsync(blindId, CancellationToken.None);

        await act.Should().ThrowAsync<BaselineNotReadyException>();
        _catcher.Seeds.Should().BeEmpty();
        _pc.Scheduler.Requests.Should().Contain(BaselineReasons.BlindReseed);
        (await _pcClient.PostAsync($"/api/blind-nodes/{blindId}/reseed", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_blind_node_that_asks_for_a_reseed_before_the_first_baseline_exists_is_not_a_failed_cycle()
    {
        var (blindId, _) = await PairBeforeActivationAsync();
        await StepANode.ActivateAsync(_pc);
        _catcher.Recording = true;
        var row = (await _pc.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(blindId))!;
        using var http = _pc.Services.GetRequiredService<IHttpClientFactory>().CreateClient(SyncScheduler.HttpClientName);

        // A push gap is one of the two reasons the manager reseeds on its own; with no baseline it waits instead of throwing into the loop.
        var act = () => _pc.Services.GetRequiredService<BlindNodeManager>().AfterSyncAsync(row, http, new PushGapException("gap", blindId, 1, 1, 1), CancellationToken.None);

        await act.Should().NotThrowAsync();
        _catcher.Seeds.Should().BeEmpty();
        _pc.Scheduler.Requests.Should().Contain(BaselineReasons.BlindReseed);
    }

    [Fact]
    public async Task Adding_a_blind_node_after_the_activation_seeds_it_with_a_full_package_that_carries_its_blobs()
    {
        await StepANode.SeedWithCanariesAsync(_pc);
        await StepANode.ActivateAsync(_pc);
        var baseline = await _pc.Services.GetRequiredService<IBaselineProducer>().ProduceAsync(BaselineReasons.OnDemand);
        var code = await PairCodeAsync();
        _catcher.Recording = true;

        var added = await _pc.Services.GetRequiredService<BlindNodeManager>().AddAsync(code.ToString(), CancellationToken.None);

        _catcher.Seeds.Should().ContainSingle();
        var package = await ReadBackAsync(_catcher.Seeds.Keys.Single());
        package.Manifest.Kind.Should().Be(BlindPackageKinds.Full);
        package.Manifest.ContainsBlobs.Should().BeTrue();
        package.ControlDbPath.Should().NotBeNull();
        package.DeltasPath.Should().NotBeNull();
        package.Manifest.BaselineId.Should().Be(baseline.Manifest.BaselineId);
        Directory.EnumerateFiles(package.BlobsDirectory!).Select(Path.GetFileName).Should()
            .BeEquivalentTo(Format2Package.ParseBlobList(await File.ReadAllBytesAsync(Path.Combine(baseline.Directory, "blobs.txt"))));
        (await _pc.Services.GetRequiredService<ISyncPushPositionRepository>().GetAsync(code.NodeId))!.LastPushedSeq
            .Should().Be(added.PackageCp, "a node seeded from nothing is pushed to from the package's cp on");
        using var db = _blind.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        (await db.ExecuteScalarAsync<string>("SELECT baseline_id FROM tbl_baseline WHERE is_current = 1")).Should()
            .BeEquivalentTo(baseline.Manifest.BaselineId.ToString(), "the blind node adopted the baseline of the full package");
    }

    [Fact]
    public async Task Adding_a_blind_node_after_the_activation_with_no_baseline_changes_nothing()
    {
        await StepANode.SeedWithCanariesAsync(_pc);
        await StepANode.ActivateAsync(_pc);
        var code = await PairCodeAsync();
        _catcher.Recording = true;

        var act = () => _pc.Services.GetRequiredService<BlindNodeManager>().AddAsync(code.ToString(), CancellationToken.None);

        await act.Should().ThrowAsync<BaselineNotReadyException>();
        (await _pc.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(code.NodeId))
            .Should().BeNull("no row and no event are made for a node that cannot be seeded yet");
        _catcher.Seeds.Should().BeEmpty();
        (await _pcClient.PostAsJsonAsync("/api/blind-nodes/", new { code = code.ToString() })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
