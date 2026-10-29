using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.TestSupport;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The integration round's canary run (docs/design/step-a-spec.md 16.5, cut to what one process can hold) on the merged tree of the
/// three tracks, with nothing faked between them: a full node and a Docker blind host (the real hosts), a blind node paired the 2.5
/// way before the switch (so it holds the readable copy), then the real readiness check and the real <c>POST /api/sync/activation</c>
/// (track 1), the real baseline scheduler and producer reacting to it (track 3), the blind node's own activation observer asking for a
/// reseed and adopting the baseline (track 2) and the sealed deltas pushed after it (track 1 on the wire, track 2 in the store).
/// Titles, folder names (also after a rename), tags (two letter cases, merged), the actor's display name and a title edit carry
/// distinct canaries before and after the switch; every byte the blind host stores, every log line and every body that crossed
/// between the two hosts is scanned for them.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public sealed class StepAEndToEndCanaryTests : IAsyncLifetime
{
    private const string Password = "stepAEndToEndCanary1!";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly StepALogCapture _pcLogs = new();
    private readonly StepALogCapture _blindLogs = new();
    private readonly string _work = Path.Combine(Path.GetTempPath(), "bmb_e2ecanary_" + Guid.NewGuid().ToString("N"));
    private LoggingBlind _blind = null!;
    private LoggingPc _pc = null!;
    private Wire _wire = null!;
    private HttpClient _pcClient = null!;

    private sealed class LoggingBlind(StepALogCapture logs) : BlindNodeFactory(drainWaitSeconds: 10)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s => s.AddSingleton<ILoggerProvider>(logs));
        }
    }

    private sealed class LoggingPc(StepALogCapture logs) : BmbWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s => s.AddSingleton<ILoggerProvider>(logs));
        }
    }

    /// <summary>Everything the PC sends to the blind host and everything it gets back, labelled with the phase the test was in.</summary>
    private sealed class Wire : DelegatingHandler
    {
        public Wire(BlindNodeFactory blind) : base(blind.Server.CreateHandler()) { }

        public string Phase { get; set; } = "pair";
        public List<(string Phase, string Where, byte[] Bytes, bool Gzip)> Exchanges { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var where = $"{request.Method} {request.RequestUri!.PathAndQuery}";
            var seedUpload = request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/api/blind/seed";
            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsByteArrayAsync(ct);
                var copy = new ByteArrayContent(body);
                foreach (var h in request.Content.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
                request.Content = copy;
                Add(Phase, "request " + where, [.. Encoding.UTF8.GetBytes(where + "\n"), .. body], seedUpload);
            }
            else
            {
                Add(Phase, "request " + where, Encoding.UTF8.GetBytes(where), false);
            }
            var response = await base.SendAsync(request, ct);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            var replacement = new ByteArrayContent(bytes);
            foreach (var h in response.Content.Headers) replacement.Headers.TryAddWithoutValidation(h.Key, h.Value);
            response.Content = replacement;
            Add(Phase, "response " + where, bytes, false);
            return response;
        }

        private void Add(string phase, string where, byte[] bytes, bool gzip)
        {
            lock (Exchanges) Exchanges.Add((phase, where, bytes, gzip));
        }

        public List<string> Seen(string phase)
        {
            lock (Exchanges) return Exchanges.Where(e => e.Phase == phase).Select(e => e.Where).Distinct().ToList();
        }

        public List<CanaryHit> Scan(string phase, string directionPrefix, IEnumerable<string> canaries)
        {
            var list = canaries.ToList();
            List<(string Phase, string Where, byte[] Bytes, bool Gzip)> snapshot;
            lock (Exchanges) snapshot = Exchanges.ToList();
            var hits = new List<CanaryHit>();
            foreach (var x in snapshot.Where(e => e.Phase == phase && e.Where.StartsWith(directionPrefix, StringComparison.Ordinal)))
            {
                hits.AddRange(MetadataCanaries.Scan(x.Bytes, list, x.Where));
                if (!x.Gzip) continue;
                // A seed upload is a .tar.gz: its entries are read decompressed as well. The recorded bytes are "<where>\n" + the part.
                var path = Path.Combine(Path.GetTempPath(), "bmb_e2ecanary_seed_" + Guid.NewGuid().ToString("N") + ".tar.gz");
                try
                {
                    var newline = Array.IndexOf(x.Bytes, (byte)'\n');
                    File.WriteAllBytes(path, x.Bytes[(newline + 1)..]);
                    try { hits.AddRange(MetadataCanaries.ScanArchive(path, list)); }
                    catch (InvalidDataException) { /* one part of a chunked upload is not a whole archive: its raw bytes were scanned above */ }
                }
                finally { try { File.Delete(path); } catch (IOException) { } }
            }
            return hits;
        }
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_work);
        _blind = new LoggingBlind(_blindLogs);
        _ = _blind.Services;
        _wire = new Wire(_blind);
        _pc = new LoggingPc(_pcLogs);
        _pc.RouteOutboundHttpThrough(_wire);
        await _pc.InitializeNodeAsync("PC", Password);
        _pcClient = _pc.CreateClient();
        (await _pcClient.PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _pcClient.Dispose();
        _pc.Dispose();
        _blind.Dispose();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private sealed class Vault(List<string> all)
    {
        public List<string> Names { get; } = all;
        public string Add(string canary) { Names.Add(canary); return canary; }
        public string Ascii(string label) => Add(MetadataCanaries.Ascii(label));
        public string NonAscii(string label) => Add(MetadataCanaries.NonAscii(label));
    }

    /// <summary>
    /// The scripted run: content in clear, a blind node paired 2.5-style, (the switch,) content again, two sync cycles. With
    /// <paramref name="activate"/> false nothing is switched on: the control that shows the scans below find the same content on the same path.
    /// </summary>
    private async Task<(Vault Before, Vault After, List<string> Titles, ServableBaseline? Baseline)> RunAsync(bool activate, bool cacheAReplicaBeforeTheSwitch = false)
    {
        var before = new Vault([]);
        var after = new Vault([]);
        var adminId = (await _pc.Services.GetRequiredService<IUserRepository>().GetByUsernameAsync("admin"))!.Id;

        // ── Before the switch, the 2.5 way: content in clear, a blind node paired and seeded with the readable copy.
        var actorBefore = before.NonAscii("actor");
        await SetDisplayNameAsync(adminId, actorBefore);
        var top = before.Ascii("fa");
        var folder = $"/{top}/{before.Ascii("fb")}/{before.NonAscii("fc")}";
        var tag = before.Ascii("tag");
        var titleBefore = before.Ascii("title");
        var titleBefore2 = before.NonAscii("title");
        await CreateArticleAsync(titleBefore, folder, [tag, tag.ToUpperInvariant()]);
        var mergedInto = before.Ascii("mergedtag");
        await CreateArticleAsync(titleBefore2, $"/{before.Ascii("other")}", [before.NonAscii("tag"), mergedInto]);
        var renamedTop = before.Ascii("renamed");
        (await _pcClient.PatchAsJsonAsync("/api/folders/?path=" + Uri.EscapeDataString("/" + top), new { newPath = "/" + renamedTop }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await _pcClient.PostAsJsonAsync("/api/concept-tags/merge", new { source = tag, target = mergedInto })).EnsureSuccessStatusCode();

        var code = await PairCodeAsync();
        (await _pcClient.PostAsJsonAsync("/api/blind-nodes/", new { code = code.ToString() })).StatusCode.Should().Be(HttpStatusCode.OK);
        var blindId = code.NodeId;

        // Controls: the scanner finds the pre-switch content on the blind node and on the wire (red before the switch).
        SqliteConnection.ClearAllPools();
        ScanBlindDisk(before.Names).Should().NotBeEmpty("a 2.5-style blind copy holds titles and folder names in clear");
        _wire.Scan("pair", "request", before.Names).Should().NotBeEmpty("the 2.5-style seed package carries them in clear");

        if (cacheAReplicaBeforeTheSwitch)
        {
            // A phone asked this blind node for its replica before the switch: the format-1 package (the readable copy) sits in the cache.
            await using var lease = await _blind.Services.GetRequiredService<BlindReplicaPackageCache>().AcquireAsync(producerIsSuperadmin: true, CancellationToken.None);
            SqliteConnection.ClearAllPools();
            ScanBlindDisk(before.Names).Should().Contain(h => h.Where.Contains("blind-replica", StringComparison.Ordinal), "control: the cached package holds the readable copy");
        }

        // ── The switch: the PC learns what the blind node can do, the readiness check passes, the owner activates with the master password.
        var manager = _pc.Services.GetRequiredService<BlindNodeManager>();
        await ObserveAsync(manager, blindId);
        ServableBaseline? baseline = null;
        if (activate)
        {
            var readiness = await _pcClient.GetFromJsonAsync<JsonElement>("/api/sync/readiness?peers=false", JsonOpts);
            readiness.GetProperty("ready").GetBoolean().Should().BeTrue(readiness.ToString());
            _wire.Phase = "activation";
            var activation = await _pcClient.PostAsJsonAsync("/api/sync/activation", new { password = Password });
            activation.StatusCode.Should().Be(HttpStatusCode.OK, await activation.Content.ReadAsStringAsync());

            // The activation asked the scheduler for a baseline (track 3 reacting to track 1); wait for the real one.
            var source = _pc.Services.GetRequiredService<IBaselineSource>();
            for (var i = 0; i < 120 && baseline is null; i++)
            {
                baseline = await source.GetServableAsync(CancellationToken.None);
                if (baseline is null) await Task.Delay(500);
            }
            baseline.Should().NotBeNull("the activation requests a baseline and the scheduler produces it");
        }

        // ── After the switch: new content with new canaries (sealed), then the sync cycles the scheduler would run.
        _wire.Phase = "post";
        var actorAfter = after.NonAscii("actor");
        await SetDisplayNameAsync(adminId, actorAfter);
        var topAfter = after.Ascii("fa");
        var folderAfter = $"/{topAfter}/{after.NonAscii("fb")}";
        var tagAfter = after.Ascii("tag");
        var created = await CreateArticleAsync(after.Ascii("title"), folderAfter, [tagAfter, tagAfter.ToUpperInvariant()]);
        var titleAfter2 = after.NonAscii("title");
        var mergedAfter = after.Ascii("mergedtag");
        await CreateArticleAsync(titleAfter2, $"/{after.Ascii("other")}", [after.NonAscii("tag"), mergedAfter]);
        var retitled = after.Ascii("retitled");
        (await _pcClient.PutAsJsonAsync($"/api/articles/{created.GetProperty("id").GetString()}", new { title = retitled, content = "edited body", conceptTags = new[] { tagAfter } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var renamedAfter = after.NonAscii("renamed");
        (await _pcClient.PatchAsJsonAsync("/api/folders/?path=" + Uri.EscapeDataString("/" + topAfter), new { newPath = "/" + renamedAfter }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await _pcClient.PostAsJsonAsync("/api/concept-tags/merge", new { source = tagAfter, target = mergedAfter })).EnsureSuccessStatusCode();
        after.Add(retitled);

        // Cycle 1: the PC pushes the key, the activation and the sealed events; the blind node applies the activation and asks for a reseed,
        // which the PC answers with the baseline (after the blobs). Cycle 2: whatever is left, as the next scheduler pass would.
        for (var cycle = 0; cycle < 2; cycle++) await CycleAsync(manager, blindId);
        return (before, after, [titleBefore, titleBefore2, retitled, titleAfter2], baseline);
    }

    [Fact]
    public async Task ARealActivation_OnAPairedBlindNode_LeavesNoCanaryInTheBlindStoreTheLogsOrWhatCrossesTheWire()
    {
        var (before, after, titles, baseline) = await RunAsync(activate: true);

        // ── The blind node: adopted the baseline of this PC, holds sealed deltas, nothing readable anywhere.
        var policy = _blind.Services.GetRequiredService<ISyncProtocolPolicy>();
        policy.SealsMetadata.Should().BeTrue("the activation event reached the blind node");
        var store = _blind.Services.GetRequiredService<IBaselineStore>();
        (await store.GetCurrentAsync())!.Files.Manifest.BaselineId.Should().Be(baseline!.Files.Manifest.BaselineId,
            "the blind node adopted the baseline the activation made");
        (await NonOperationalTablesWithRowsAsync()).Should().BeEmpty("the first adoption purged the readable copy");
        using (var conn = _blind.Services.GetRequiredService<IDbConnectionFactory>().CreateConnection())
        {
            (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_event WHERE event_type = 'sealed'")).Should().BeGreaterThan(0,
                "the sealed events authored after the switch were relayed");
            (await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM tbl_event WHERE event_type NOT IN ('sealed') AND protocol_version < 4 AND event_type IN ('article_create','article_update','folder_create','folder_rename','concept_tag_merge','concept_tag_add')"))
                .Should().Be(0, "no protocol-1..3 content row is left on the blind node");
        }

        SqliteConnection.ClearAllPools();
        var everything = before.Names.Concat(after.Names).ToList();
        ScanBlindDisk(everything).Should().BeEmpty("file, WAL, journal, packages: nothing readable on the blind node");
        MetadataCanaries.Scan(Encoding.UTF8.GetBytes(string.Join('\n', _blindLogs.Lines)), everything, "blind log").Should().BeEmpty();
        MetadataCanaries.Scan(Encoding.UTF8.GetBytes(string.Join('\n', _pcLogs.Lines)), everything, "pc log").Should().BeEmpty();

        // ── The wire. Everything the PC sent to the blind node after the switch carries no canary of either set; what it got back carries none
        // of the content written after the switch (rows written before it are served in clear to a full node, spec 5.7, and only until the purge).
        _wire.Scan("activation", "request", everything).Should().BeEmpty("sent to the blind node during the activation");
        _wire.Scan("post", "request", everything).Should().BeEmpty("sent to the blind node after the switch: sealed deltas, blobs, the baseline package");
        _wire.Scan("activation", "response", after.Names).Should().BeEmpty();
        _wire.Scan("post", "response", after.Names).Should().BeEmpty("sealed events authored after the switch come back sealed too");
        var legacyBack = _wire.Scan("post", "response", before.Names);
        legacyBack.All(h => h.Where.Contains("/api/sync/events", StringComparison.Ordinal)).Should().BeTrue(
            "only the pull that takes in what the blind node still held before its first adoption may show pre-switch rows");

        // The scans are not scanning nothing: the traffic after the switch holds the baseline package, the pushes and the blobs.
        var seen = _wire.Seen("activation").Concat(_wire.Seen("post")).ToList();
        seen.Should().Contain(w => w.StartsWith("request POST /api/blind/seed", StringComparison.Ordinal), "the baseline package went up");
        seen.Should().Contain(w => w.StartsWith("request POST /api/sync/events", StringComparison.Ordinal), "sealed events were pushed");
        seen.Should().Contain(w => w.StartsWith("response GET /api/sync/events", StringComparison.Ordinal), "the PC pulled from the blind node");

        // The PC itself stays whole: it still reads its own notes.
        (await TitlesAsync()).Should().Contain(titles);
    }

    /// <summary>
    /// A package a phone asked the blind node for before the switch is a readable copy of the vault's metadata on the blind disk. After the
    /// first adoption nothing readable may stay there (spec 9.3 "purge legacy content"; stolen-disk story of the programme).
    /// </summary>
    [Fact]
    public async Task AReplicaPackageCachedBeforeTheSwitch_LeavesNoReadableCopyOnTheBlindDiskAfterTheFirstAdoption()
    {
        var (before, after, _, _) = await RunAsync(activate: true, cacheAReplicaBeforeTheSwitch: true);

        SqliteConnection.ClearAllPools();
        ScanBlindDisk(before.Names.Concat(after.Names).ToList()).Should().BeEmpty(
            "the cache of replica packages is part of the blind disk");
    }

    /// <summary>The control: the same run with no switch. The scans find the content on the blind node, in its log and on the wire.</summary>
    [Fact]
    public async Task TheSameRunWithoutTheSwitch_ShowsTheCanaries_SoTheScansAreNotBlind()
    {
        var (before, after, _, _) = await RunAsync(activate: false);

        SqliteConnection.ClearAllPools();
        _blind.Services.GetRequiredService<ISyncProtocolPolicy>().SealsMetadata.Should().BeFalse();
        ScanBlindDisk(after.Names).Should().NotBeEmpty("without the switch the blind node holds what was written after pairing, in clear");
        _wire.Scan("post", "request", after.Names).Should().NotBeEmpty("without the switch the push carries it in clear");
    }

    // ─── steps ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnArticleAuthoringScope_WaitsForABaselineSnapshotCutover()
    {
        using var cutover = await EventWriteGate.Instance.QuiesceAsync();
        using var scope = _pc.Services.CreateScope();

        var authoring = scope.ServiceProvider.GetRequiredService<IEventLogger>().BeginAuthoringAsync();

        authoring.IsCompletedSuccessfully.Should().BeFalse(
            "a baseline snapshot has excluded new event writes before it starts VACUUM");
        cutover.Dispose();
        using var entered = await authoring;
    }

    /// <summary>
    /// The event-write admission is reentrant inside one authoring operation: once the outer scope holds it, a nested
    /// BeginAuthoringAsync on the same flow (a copy, an import, a composite endpoint scope over many writes) goes straight
    /// in even with a cutover holding new writes off. A nested call that queued its own admission instead would wait for
    /// the cutover while the cutover waits for the outer scope — both until the node shuts down.
    /// </summary>
    [Fact]
    public async Task ANestedAuthoringScope_PassesACutoverItsOuterScopeAlreadyHolds()
    {
        using var scope = _pc.Services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<IEventLogger>();

        // A write in flight, so the cutover below really waits while the outer scope is already inside.
        var inFlight = await EventWriteGate.Instance.EnterAsync();
        var outer = await logger.BeginAuthoringAsync();
        var cutover = EventWriteGate.Instance.QuiesceAsync();
        ValueTask<IDisposable>? pending = null;
        try
        {
            var waited = DateTime.UtcNow;
            while (!EventWriteGate.Instance.IsQuiescing && DateTime.UtcNow - waited < TimeSpan.FromSeconds(30))
                await Task.Delay(10);
            EventWriteGate.Instance.IsQuiescing.Should().BeTrue("the cutover holds new writes off");

            pending = logger.BeginAuthoringAsync();
            pending.Value.IsCompletedSuccessfully.Should().BeTrue(
                "the nested scope is inside the operation the outer admission already covers");
        }
        finally
        {
            // The nested scope of the unfixed code finishes only once the cutover lets go: release the writers first,
            // then the cutover, then it — nothing stays behind for the next test either way.
            outer.Dispose();
            inFlight.Dispose();
            (await cutover).Dispose();
            if (pending is { } entered) (await entered).Dispose();
        }
    }

    /// <summary>One scheduler pass for a blind node: the sync, then what <see cref="BlindNodeManager"/> does after it.</summary>
    private async Task CycleAsync(BlindNodeManager manager, Guid blindId)
    {
        var row = (await _pc.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(blindId))!;
        using var http = _pc.Services.GetRequiredService<IHttpClientFactory>().CreateClient(SyncScheduler.HttpClientName);
        Exception? failure = null;
        try
        {
            using var scope = _pc.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<SyncClient>().SyncWithPeerAsync(http, row.ApiAddress!, blindId, CancellationToken.None);
        }
        catch (PushGapException ex) { failure = ex; }
        await manager.AfterSyncAsync(row, http, failure, CancellationToken.None);
    }

    private async Task ObserveAsync(BlindNodeManager manager, Guid blindId)
    {
        var row = (await _pc.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(blindId))!;
        using var http = _pc.Services.GetRequiredService<IHttpClientFactory>().CreateClient(SyncScheduler.HttpClientName);
        await manager.AfterSyncAsync(row, http, failure: null, CancellationToken.None);
    }

    /// <summary>Every file of the blind node's data folder as raw bytes, and every .tar.gz in it also decompressed (a cached package is one).</summary>
    private List<CanaryHit> ScanBlindDisk(IEnumerable<string> canaries)
    {
        var list = canaries.ToList();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var hits = MetadataCanaries.ScanDirectory(_blind.DataPath, list);
                foreach (var archive in Directory.EnumerateFiles(_blind.DataPath, "*.tar.gz", SearchOption.AllDirectories))
                {
                    try { hits.AddRange(MetadataCanaries.ScanArchive(archive, list)); }
                    catch (InvalidDataException) { }
                }
                return hits;
            }
            catch (IOException) when (attempt < 20)
            {
                // SQLite sidecars and staged packages can be replaced while the blind host is still running.
                // Retry the complete listing/read for a bounded five-second observation window.
                Thread.Sleep(250);
            }
        }
    }

    private async Task<BlindPairCode> PairCodeAsync()
    {
        using var console = _blind.CreateClient();
        var body = await console.GetFromJsonAsync<JsonElement>("/api/blind/pair-code");
        return BlindPairCode.Parse(body.GetProperty("code").GetString()!);
    }

    private async Task SetDisplayNameAsync(int userId, string displayName) =>
        (await _pcClient.PutAsJsonAsync($"/api/users/{userId}", new { displayName, role = "superadmin" })).StatusCode.Should().Be(HttpStatusCode.OK);

    private async Task<JsonElement> CreateArticleAsync(string title, string treePath, string[] tags)
    {
        var resp = await _pcClient.PostAsJsonAsync("/api/articles", new { title, treePath, content = "a plain body", conceptTags = tags });
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
    }

    private async Task<List<string?>> TitlesAsync()
    {
        var list = await _pcClient.GetFromJsonAsync<JsonElement>("/api/articles", JsonOpts);
        var items = list.ValueKind == JsonValueKind.Array ? list : list.GetProperty("items");
        return items.EnumerateArray().Select(a => a.GetProperty("title").GetString()).ToList();
    }

    private async Task<List<string>> NonOperationalTablesWithRowsAsync()
    {
        using var conn = _blind.Services.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        var tables = (await conn.QueryAsync<(string Name, string? Sql)>("SELECT name, sql FROM sqlite_master WHERE type = 'table'")).ToList();
        var virtualTables = tables.Where(t => t.Sql?.StartsWith("CREATE VIRTUAL", StringComparison.OrdinalIgnoreCase) == true).Select(t => t.Name).ToList();
        var withRows = new List<string>();
        foreach (var (name, _) in tables)
        {
            if (BaselineStore.OperationalTables.Contains(name) || name.StartsWith("sqlite_", StringComparison.Ordinal)) continue;
            if (virtualTables.Any(v => name.StartsWith(v + "_", StringComparison.Ordinal))) continue;
            if (await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM \"{name}\"") > 0) withRows.Add(name);
        }
        return withRows;
    }
}
