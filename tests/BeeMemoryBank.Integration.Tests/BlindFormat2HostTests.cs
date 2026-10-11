using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
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
/// The Docker blind node's side of Step A (docs/design/step-a-spec.md 8.7, 9, 10.3; red tests 16.3 items 1-5, 7, 10) on the
/// Api started as a blind node: a format-2 first seed through the pair code and a baseline reseed through a superadmin
/// token, format 1 refused once activated, the reseed request at activation, relay storage without canaries and with the
/// stored events byte-identical to the author's, clear content dropped, the operational tables, serving the replica and the
/// restore package in format 2 (304 with <c>?have=</c>, 503 with nothing sealed), and the anchor without a digest.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public sealed class BlindFormat2HostTests : IDisposable
{
    private readonly List<IDisposable> _owned = [];
    private readonly string _work = Path.Combine(Path.GetTempPath(), "bmb_f2host_" + Guid.NewGuid().ToString("N"));

    public BlindFormat2HostTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_owned)) d.Dispose();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
    }

    private sealed class LoggingBlindFactory(StepALogCapture logs) : BlindNodeFactory(drainWaitSeconds: 10)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s => s.AddSingleton<ILoggerProvider>(logs));
        }
    }

    private (BlindNodeFactory Blind, StepALogCapture Logs) NewBlind()
    {
        var logs = new StepALogCapture();
        var blind = new LoggingBlindFactory(logs);
        _owned.Add(blind);
        _ = blind.Services;
        return (blind, logs);
    }

    private async Task<StepASeeder> NewSeederAsync()
    {
        var seeder = await StepASeeder.CreateAsync();
        _owned.Add(seeder);
        return seeder;
    }

    /// <summary>The material of one scripted history: canaries in every name, clear content before the activation, sealed after.</summary>
    private sealed record Script(List<string> Canaries, SyncEvent ClearBefore, List<SyncEvent> Sealed, SyncEvent KeySet, SyncEvent Activation,
        string BaselineBlob);

    private static async Task<Script> AuthorAsync(StepASeeder seeder)
    {
        var title = MetadataCanaries.Ascii("title");
        var folder = MetadataCanaries.NonAscii("folder");
        var tag = MetadataCanaries.Ascii("tag");
        var renamed = MetadataCanaries.Ascii("renamed");
        var articleId = Guid.NewGuid();
        var bodyBlob = await seeder.StoreBlobAsync();
        var clear = await seeder.ClearAsync(EventTypes.ArticleCreate, articleId, StepASeeder.ArticleCreate(title, "/" + folder, bodyBlob, tag));
        var (keySet, activation) = await seeder.ActivateAsync();
        var sealedEvents = new List<SyncEvent>
        {
            await seeder.SealedAsync(EventTypes.FolderCreate, null, StepASeeder.FolderCreate("/" + folder, folder)),
            await seeder.SealedAsync(EventTypes.ArticleCreate, articleId, StepASeeder.ArticleCreate(title, "/" + folder, bodyBlob, tag)),
            await seeder.SealedAsync(EventTypes.FolderRename, null, StepASeeder.FolderRename("/" + folder, "/" + renamed, renamed)),
            await seeder.SealedAsync(EventTypes.ConceptTagRename, Guid.Empty, StepASeeder.TagRename(tag, tag.ToUpperInvariant())),
        };
        return new Script([title, folder, tag, renamed], clear, sealedEvents, keySet, activation, await seeder.StoreBlobAsync());
    }

    /// <summary>A 2.5 blind node materialised the vault: its content tables and its log hold the canaries in clear.</summary>
    private static async Task<List<string>> MaterialiseLegacyContentAsync(BlindNodeFactory blind)
    {
        var title = MetadataCanaries.Ascii("legacytitle");
        var folder = MetadataCanaries.NonAscii("legacyfolder");
        using var conn = blind.Services.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        var now = DateTime.UtcNow.ToString("O");
        await conn.ExecuteAsync("INSERT INTO tbl_folder (id, path, name, created_at, updated_at) VALUES (@id, @path, @folder, @now, @now)",
            new { id = Guid.NewGuid().ToString(), path = "/" + folder, folder, now });
        await conn.ExecuteAsync("INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) VALUES (@id, @title, @path, 'A', @now, @now)",
            new { id = Guid.NewGuid().ToString(), title, path = "/" + folder, now });
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_event (event_id, node_id, lamport_ts, event_type, payload, signature, created_at, protocol_version)
              VALUES (@id, @node, 1, 'article_create', @payload, X'00', @now, 3)",
            new { id = Guid.NewGuid().ToString(), node = Guid.NewGuid().ToString(), payload = $"{{\"title\":\"{title}\"}}", now });
        return [title, folder];
    }

    private static async Task<HttpResponseMessage> UploadAsync(BlindNodeFactory blind, BlindPackageV2 package, StepASeeder seeder, bool pairCode)
    {
        var bytes = await File.ReadAllBytesAsync(package.FilePath);
        using var http = blind.Server.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/blind/seed?seedId={package.Manifest.SeedId}&offset=0&total={bytes.Length}&sha256={package.Sha256}")
        { Content = new ByteArrayContent(bytes) };
        await AuthorizeAsync(req, blind, seeder, package.Manifest.SeedId, pairCode);
        return await http.SendAsync(req);
    }

    private static async Task AuthorizeAsync(HttpRequestMessage req, BlindNodeFactory blind, StepASeeder seeder, Guid seedId, bool pairCode)
    {
        if (pairCode)
        {
            var code = await blind.Services.GetRequiredService<BlindPairing>().GetCodeAsync(renew: false);
            req.Headers.Add(BlindSeederProof.NodeIdHeader, seeder.NodeId.ToString());
            req.Headers.Add(BlindSeederProof.KeyHeader, Convert.ToBase64String(seeder.PublicKey));
            req.Headers.Add(BlindSeederProof.MacHeader, BlindSeederProof.Compute(code.Secret, seedId, seeder.NodeId, seeder.PublicKey));
        }
        else
        {
            req.Headers.Authorization = new("Bearer", blind.Services.GetRequiredService<SyncTokenStore>().IssueToken(seeder.NodeId, EventClasses.SealedProtocol));
        }
    }

    /// <summary>The first seed of a 2.5-materialised blind node with a format-2 full package through the pair code.</summary>
    private async Task<(BlindNodeFactory Blind, StepALogCapture Logs, StepASeeder Seeder, Script Script, BaselineFiles Baseline, List<string> Legacy)> FirstSeedAsync()
    {
        var (blind, logs) = NewBlind();
        var legacy = await MaterialiseLegacyContentAsync(blind);
        var seeder = await NewSeederAsync();
        var script = await AuthorAsync(seeder);
        var baseline = seeder.WriteBaseline(Path.Combine(_work, "b1-" + Guid.NewGuid().ToString("N")), [script.BaselineBlob], lamportAtCut: 50);
        var package = await seeder.WriteAsync(BlindPackageKinds.Full, new ServableBaseline(baseline, 0));
        var resp = await UploadAsync(blind, package, seeder, pairCode: true);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (blind, logs, seeder, script, baseline, legacy);
    }

    [Fact]
    public async Task AFormat2FirstSeed_AdoptsTheBaseline_RelaysTheDeltasByteForByte_AndPurgesTheLegacyContent()
    {
        var (blind, logs, seeder, script, baseline, legacy) = await FirstSeedAsync();
        var sp = blind.Services;

        sp.GetRequiredService<ISyncProtocolPolicy>().SealsMetadata.Should().BeTrue("control.db carried the activation");
        (await sp.GetRequiredService<IBaselineStore>().GetCurrentAsync())!.Files.Manifest.BaselineId.Should().Be(baseline.Manifest.BaselineId);
        (await sp.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(seeder.NodeId))!.IsSuperadmin.Should().BeTrue("the pair code");
        (await sp.GetRequiredService<BlindPairing>().IsCodeActiveAsync()).Should().BeFalse("one seed per code");
        (await new BlindState(sp.GetRequiredService<IDbConnectionFactory>()).GetReseedNeededAsync()).Should().BeNull(
            "the activation asked for a reseed and this adoption answered it");

        // Blind isomorphism: every event stored here is the author's, byte for byte; the clear content never arrived.
        foreach (var authored in script.Sealed.Append(script.KeySet).Append(script.Activation))
        {
            var stored = await StoredAsync(blind, authored.EventId);
            stored.Should().NotBeNull($"{authored.EventType} {authored.EventId} is a delta");
            stored!.Payload.Should().Be(authored.Payload);
            stored.Signature.Should().Equal(authored.Signature);
            stored.EventType.Should().Be(authored.EventType);
            stored.ProtocolVersion.Should().Be(authored.ProtocolVersion);
        }
        foreach (var sealedEvent in script.Sealed)
            (await StoredAsync(blind, sealedEvent.EventId))!.ApplyState.Should().Be(EventApplyStates.Relayed);
        (await StoredAsync(blind, script.ClearBefore.EventId)).Should().BeNull("a protocol-3 content row never travels in a package");

        // The operational tables (spec 9.1) and nothing else hold rows; no canary in the file, its WAL, the log or a log line.
        (await NonOperationalTablesWithRowsAsync(blind)).Should().BeEmpty();
        SqliteConnection.ClearAllPools();
        var canaries = script.Canaries.Concat(legacy).ToList();
        MetadataCanaries.ScanDirectory(blind.DataPath, canaries).Should().BeEmpty();
        MetadataCanaries.Scan(Encoding.UTF8.GetBytes(string.Join('\n', logs.Lines)), canaries, "log").Should().BeEmpty();

        // The status says so, without a note count.
        using var client = blind.CreateClient();
        var status = await (await client.GetAsync("/api/blind/status")).Content.ReadFromJsonAsync<JsonElement>();
        status.GetProperty("activated").GetBoolean().Should().BeTrue();
        status.GetProperty("protocol").GetInt32().Should().Be(4);
        status.GetProperty("baseline").GetProperty("id").GetString().Should().Be(baseline.Manifest.BaselineId.ToString());
        status.GetProperty("deltas").GetProperty("count").GetInt64().Should().BeGreaterThanOrEqualTo(script.Sealed.Count);
        status.GetProperty("metadata_in_backups").GetBoolean().Should().BeFalse("the first adoption purged the legacy content");
    }

    /// <summary>Spec 16.3 item 4 on the host: the control of the purge test - the canaries are there before the seed.</summary>
    [Fact]
    public async Task TheLegacyContent_IsWhereTheScannerLooks_BeforeTheFirstFormat2Seed()
    {
        var (blind, _) = NewBlind();
        var legacy = await MaterialiseLegacyContentAsync(blind);
        MetadataCanaries.ScanDirectory(blind.DataPath, legacy).Should().NotBeEmpty();
        using var client = blind.CreateClient();
        var status = await (await client.GetAsync("/api/blind/status")).Content.ReadFromJsonAsync<JsonElement>();
        status.GetProperty("metadata_in_backups").GetBoolean().Should().BeTrue("nothing purged it yet");
    }

    [Fact]
    public async Task OnceActivated_AFormat1Package_IsRefused()
    {
        var (blind, _) = NewBlind();
        await ActivateByRecipeAsync(blind);
        var seeder = await NewSeederAsync();

        // A format-1 package as far as the format decides it (its blind manifest's format string).
        var archive = Path.Combine(_work, "v1.tar.gz");
        using (var file = File.Create(archive))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, BlindManifest.FileName)
                { DataStream = new MemoryStream("""{"format":"bmb-blind-package-v1"}"""u8.ToArray()) });
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "beememorybank.db") { DataStream = new MemoryStream(new byte[64]) });
        }
        var bytes = await File.ReadAllBytesAsync(archive);
        var seedId = Guid.NewGuid();
        using var http = blind.Server.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/blind/seed?seedId={seedId}&offset=0&total={bytes.Length}&sha256={Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))}")
        { Content = new ByteArrayContent(bytes) };
        await AuthorizeAsync(req, blind, seeder, seedId, pairCode: true);
        var resp = await http.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("activated", "an activated node takes format 2 only (spec 2)");
    }

    [Fact]
    public async Task ABaselineReseed_ByASuperadminToken_KeepsTheLog_AndRefusesABaselineWhoseBlobsAreMissing()
    {
        var (blind, _, seeder, script, _, _) = await FirstSeedAsync();
        var newer = seeder.WriteBaseline(Path.Combine(_work, "b2"), [script.BaselineBlob], lamportAtCut: 80);
        var package = await seeder.WriteAsync(BlindPackageKinds.Baseline, new ServableBaseline(newer, 0));

        var resp = await UploadAsync(blind, package, seeder, pairCode: false);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await blind.Services.GetRequiredService<IBaselineStore>().GetCurrentAsync())!.Files.Manifest.BaselineId.Should().Be(newer.Manifest.BaselineId);
        foreach (var sealedEvent in script.Sealed)
            (await StoredAsync(blind, sealedEvent.EventId)).Should().NotBeNull("a reseed with format 2 does not reset the log");

        var missing = seeder.WriteBaseline(Path.Combine(_work, "b3"), [script.BaselineBlob, new string('d', 64)], lamportAtCut: 90);
        var refused = await UploadAsync(blind, await seeder.WriteAsync(BlindPackageKinds.Baseline, new ServableBaseline(missing, 0)), seeder, pairCode: false);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("not stored here");
        (await blind.Services.GetRequiredService<IBaselineStore>().GetCurrentAsync())!.Files.Manifest.BaselineId.Should().Be(newer.Manifest.BaselineId);
    }

    [Fact]
    public async Task AFirstSeed_NeedsAFullPackage_AndAPackageFromAnotherNode_IsRefused()
    {
        var (blind, _) = NewBlind();
        var seeder = await NewSeederAsync();
        await AuthorAsync(seeder);
        var baseline = seeder.WriteBaseline(Path.Combine(_work, "bk"), [], lamportAtCut: 5);
        var baselineOnly = await seeder.WriteAsync(BlindPackageKinds.Baseline, new ServableBaseline(baseline, 0));
        var resp = await UploadAsync(blind, baselineOnly, seeder, pairCode: true);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("full package");

        var other = await NewSeederAsync();
        var foreign = await other.WriteAsync(BlindPackageKinds.Full, new ServableBaseline(other.WriteBaseline(Path.Combine(_work, "bo"), [], 5), 0));
        var refused = await UploadAsync(blind, foreign, seeder, pairCode: true);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, "signed by another node than the one the pair code vouched for");
        blind.Services.GetRequiredService<ISyncProtocolPolicy>().SealsMetadata.Should().BeFalse("nothing was taken");
    }

    [Fact]
    public async Task TheActivation_AsksTheBlindNodeForAReseed()
    {
        var (blind, _) = NewBlind();
        var seeder = await NewSeederAsync();
        await TrustAsync(blind, seeder.NodeId, seeder.PublicKey, superadmin: true);
        var (keySet, activation) = await seeder.ActivateAsync();
        using (var scope = blind.Services.CreateScope())
        {
            var applier = scope.ServiceProvider.GetRequiredService<EventApplier>();
            (await applier.ApplyAsync(keySet)).Should().Be(EventApplyResult.Applied);
            (await applier.ApplyAsync(activation)).Should().Be(EventApplyResult.Applied);
        }
        (await new BlindState(blind.Services.GetRequiredService<IDbConnectionFactory>()).GetReseedNeededAsync())
            .Should().Be(BlindActivationObserver.ReseedReason);
    }

    /// <summary>Spec 16.3 items 1-2: pushed to an activated blind node, sealed events are stored as sent; clear content is not.</summary>
    [Fact]
    public async Task PushedToAnActivatedBlindNode_SealedEventsAreRelayedAsSent_ClearContentIsDropped()
    {
        var (blind, logs, seeder, _, _, legacy) = await FirstSeedAsync();
        var more = new List<string> { MetadataCanaries.Ascii("pushtitle"), MetadataCanaries.NonAscii("pushpath") };
        var blob = await seeder.StoreBlobAsync();
        var pushed = new List<SyncEvent>
        {
            await seeder.SealedAsync(EventTypes.FolderCreate, null, StepASeeder.FolderCreate("/" + more[1], more[1])),
            await seeder.SealedAsync(EventTypes.ArticleCreate, Guid.NewGuid(), StepASeeder.ArticleCreate(more[0], "/" + more[1], blob, "t")),
        };
        var clearTitle = MetadataCanaries.Ascii("straggler");
        var straggler = await seeder.ClearAsync(EventTypes.FolderCreate, null, StepASeeder.FolderCreate("/" + clearTitle, clearTitle));

        using var http = blind.Server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", blind.Services.GetRequiredService<SyncTokenStore>().IssueToken(seeder.NodeId, 4));
        var resp = await http.PostAsJsonAsync("/api/sync/events", pushed.Append(straggler).ToArray());
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());

        foreach (var evt in pushed)
        {
            var stored = await StoredAsync(blind, evt.EventId);
            stored!.Payload.Should().Be(evt.Payload);
            stored.Signature.Should().Equal(evt.Signature);
            stored.ApplyState.Should().Be(EventApplyStates.Relayed);
        }
        (await StoredAsync(blind, straggler.EventId)).Should().BeNull("an activated blind host drops clear content (spec 5.7)");
        (await NonOperationalTablesWithRowsAsync(blind)).Should().BeEmpty();
        SqliteConnection.ClearAllPools();
        var canaries = more.Append(clearTitle).Concat(legacy).ToList();
        MetadataCanaries.ScanDirectory(blind.DataPath, canaries).Should().BeEmpty();
        MetadataCanaries.Scan(Encoding.UTF8.GetBytes(string.Join('\n', logs.Lines)), canaries, "log").Should().BeEmpty();
    }

    /// <summary>
    /// Spec 9.5 and 16.3 item 5 on the host: the log trimmer of an activated blind node cuts only as far as its current
    /// baseline covers of its own log, though every peer has read everything and the anchor covers everything.
    /// </summary>
    [Fact]
    public async Task TheLogTrimmer_OfAnActivatedBlindNode_IsBoundedByTheCurrentBaseline()
    {
        var (blind, _, seeder, script, _, _) = await FirstSeedAsync();
        var blindId = (await blind.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
        long head;
        using (var scope = blind.Services.CreateScope())
        {
            head = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();
            var push = scope.ServiceProvider.GetRequiredService<ISyncPushPositionRepository>();
            await push.RecordReportedPositionAsync(seeder.NodeId, head);
            await push.UpdatePositionAsync(seeder.NodeId, head);
        }
        head.Should().BeGreaterThanOrEqualTo(script.Sealed.Count + 2);
        using (var conn = blind.Services.GetRequiredService<IDbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_state_anchor (anchor_id, author_node_id, dek_fingerprint, position_vector, digest, hmac, created_at, lamport_ts)
                  VALUES ('a1', @author, 'fp', @vector, 'd', 'h', @at, 10)",
                new { author = seeder.NodeId.ToString(), vector = $"{{\"{seeder.NodeId}\": 1000000}}", at = DateTime.UtcNow.ToString("O") });
        var trimmer = blind.Services.GetRequiredService<BlindLogTrimmer>();

        (await trimmer.TrimAsync()).Should().BeNull("the first baseline covers none of this node's log (cut 0): nothing may go");
        (await StoredAsync(blind, script.Sealed[0].EventId)).Should().NotBeNull();

        // A newer baseline that covers the first three rows of this node's log, sent by the superadmin.
        var newer = seeder.WriteBaseline(Path.Combine(_work, "trim-b2"), [script.BaselineBlob], lamportAtCut: 80,
            pullPositions: [new BlindManifestPosition(blindId, 3)]);
        var package = await seeder.WriteAsync(BlindPackageKinds.Baseline, new ServableBaseline(newer, 0));
        (await UploadAsync(blind, package, seeder, pairCode: false)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var check = blind.Services.CreateScope();
        var events = check.ServiceProvider.GetRequiredService<IEventLogRepository>();
        (await events.GetMinSequenceAsync()).Should().Be(4, "rows 1-3 are covered by the baseline; the rest are its deltas");
        (await events.GetLastCompactionCpAsync()).Should().Be(3);
        (await trimmer.TrimAsync()).Should().BeNull("nothing more is covered");
    }

    /// <summary>Spec 8.7, 9.7: the replica once activated is format 2; 304 for the baseline the caller holds.</summary>
    [Fact]
    public async Task TheReplica_IsFormat2_With304ForTheBaselineHeld_AndABaselinePackageOtherwise()
    {
        var (blind, _, seeder, _, baseline, _) = await FirstSeedAsync();
        var phone = BlindNodeId.NewId();
        await TrustAsync(blind, phone, new byte[32], superadmin: false);
        using var http = blind.Server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", blind.Services.GetRequiredService<SyncTokenStore>().IssueToken(phone, 4));
        var blindKey = (await blind.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!.Ed25519PublicKey;

        var full = await http.GetAsync("/api/blind/replica");
        full.StatusCode.Should().Be(HttpStatusCode.OK);
        var verified = await ReadAsync(full, blindKey, seeder);
        verified.Manifest.Kind.Should().Be(BlindPackageKinds.Full);
        verified.Manifest.BaselineId.Should().Be(baseline.Manifest.BaselineId);
        verified.Manifest.ContainsBlobs.Should().BeTrue();
        verified.Manifest.ProducerNodeId.Should().NotBe(seeder.NodeId, "the serving node is the blind node; the baseline's producer is the PC");

        (await http.GetAsync($"/api/blind/replica?have={baseline.Manifest.BaselineId}")).StatusCode.Should().Be(HttpStatusCode.NotModified);
        var update = await http.GetAsync($"/api/blind/replica?have={Guid.NewGuid()}");
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(update, blindKey, seeder)).Manifest.Kind.Should().Be(BlindPackageKinds.Baseline);
    }

    [Fact]
    public async Task AnActivatedBlindNodeWithNoSealedBaseline_Answers503_ForTheReplicaAndTheRestorePackage()
    {
        var (blind, _) = NewBlind();
        await ActivateByRecipeAsync(blind);
        var phone = BlindNodeId.NewId();
        await TrustAsync(blind, phone, new byte[32], superadmin: false);
        using var http = blind.Server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", blind.Services.GetRequiredService<SyncTokenStore>().IssueToken(phone, 4));

        var replica = await http.GetAsync("/api/blind/replica");
        replica.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "never a format-1 package once activated (spec 9.7)");
        replica.Headers.RetryAfter.Should().NotBeNull();

        var restore = await RestorePackageAsync(blind);
        restore.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        restore.Headers.RetryAfter.Should().NotBeNull();
    }

    [Fact]
    public async Task TheRestorePackage_IsAFullFormat2PackageWithBlobs_SignedByTheBlindNode()
    {
        var (blind, _, seeder, script, baseline, _) = await FirstSeedAsync();
        var resp = await RestorePackageAsync(blind);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var producerKey = Convert.FromBase64String(resp.Headers.GetValues("X-BMB-Snapshot-Producer-Key").Single());
        var verified = await ReadAsync(resp, producerKey, seeder);
        verified.Manifest.Kind.Should().Be(BlindPackageKinds.Full);
        verified.Manifest.ContainsBlobs.Should().BeTrue();
        verified.Baseline.Manifest.BaselineId.Should().Be(baseline.Manifest.BaselineId);
        File.Exists(Path.Combine(verified.BlobsDirectory!, script.BaselineBlob)).Should().BeTrue();
    }

    /// <summary>Spec 10.3: an activated blind host computes no digest; it names the anchor and who checks it.</summary>
    [Fact]
    public async Task OnAnActivatedBlindNode_TheAnchorIsReportedWithoutADigestVerdict()
    {
        var (blind, _) = NewBlind();
        await ActivateByRecipeAsync(blind);
        var author = Guid.NewGuid();
        using (var conn = blind.Services.GetRequiredService<IDbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_state_anchor (anchor_id, author_node_id, dek_fingerprint, position_vector, digest, hmac, created_at, lamport_ts)
                  VALUES ('anchor-1', @author, 'fp', '{}', 'digest', 'hmac', @at, 10)",
                new { author = author.ToString(), at = DateTime.UtcNow.ToString("O") });
        using var client = blind.CreateClient();
        var anchor = (await (await client.GetAsync("/api/blind/status")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("anchor");
        anchor.GetProperty("confirmed").ValueKind.Should().Be(JsonValueKind.Null);
        anchor.GetProperty("note").GetString().Should().Be(RecoveryStatusService.CheckedByARestoringNode);
        anchor.GetProperty("id").GetString().Should().Be("anchor-1");
        anchor.GetProperty("author").GetString().Should().Be(author.ToString());
    }

    /// <summary>
    /// Review round, finding 2: a signed full package whose baseline is refused (older than the current one) takes nothing
    /// with it - not a peer of its whitelist, not the serving node's position - because every refusal is decided before the
    /// node's live tables are touched.
    /// </summary>
    [Fact]
    public async Task AStaleFullPackage_IsRefused_BeforeAnythingOfItIsTaken()
    {
        var (blind, _, seeder, script, _, _) = await FirstSeedAsync();
        var positions = blind.Services.GetRequiredService<ISyncPositionRepository>();
        var before = (await positions.GetAllAsync()).Single(p => p.RemoteNodeId == seeder.NodeId).LastSequenceNum;

        // The serving node has since trusted a new peer and written two more rows; its stale baseline is older than the adopted one.
        var newcomer = Guid.NewGuid();
        await new WhitelistRepository(seeder.Factory).CreateAsync(new WhitelistEntry
        {
            NodeId = newcomer, DisplayName = "newcomer", Ed25519PublicKey = new byte[32], Status = "A", IsSuperadmin = false,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, LamportTs = 5, SourceNodeId = seeder.NodeId,
        });
        await seeder.SealedAsync(EventTypes.FolderCreate, null, StepASeeder.FolderCreate("/later1", "later1"));
        await seeder.SealedAsync(EventTypes.FolderCreate, null, StepASeeder.FolderCreate("/later2", "later2"));
        var stale = seeder.WriteBaseline(Path.Combine(_work, "stale"), [script.BaselineBlob], lamportAtCut: 10);
        var package = await seeder.WriteAsync(BlindPackageKinds.Full, new ServableBaseline(stale, 0));

        var resp = await UploadAsync(blind, package, seeder, pairCode: false);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("older");
        (await blind.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(newcomer, includeDeleted: true))
            .Should().BeNull("a refused package adds no peer");
        (await positions.GetAllAsync()).Single(p => p.RemoteNodeId == seeder.NodeId).LastSequenceNum
            .Should().Be(before, "the position moves only for rows that were stored");
    }

    /// <summary>
    /// Review round, finding 2: the serving node's position moves after its deltas are stored, never before. A delta that
    /// cannot be stored yet (its author is unknown here) fails the seed; the later ordinary pull must not start after it.
    /// </summary>
    [Fact]
    public async Task ADeltaThatCannotBeStoredYet_FailsTheSeed_AndTheServingNodesPositionDoesNotMove()
    {
        var (blind, _) = NewBlind();
        var seeder = await NewSeederAsync();
        var script = await AuthorAsync(seeder);
        var stranger = await StepASeeder.CreateAsync("stranger");
        _owned.Add(stranger);
        await stranger.ActivateAsync();
        await seeder.Events.AppendAsync(await stranger.SealedAsync(EventTypes.FolderCreate, null, StepASeeder.FolderCreate("/s", "s"), append: false));
        var baseline = seeder.WriteBaseline(Path.Combine(_work, "stranger-b"), [script.BaselineBlob], lamportAtCut: 50);
        var package = await seeder.WriteAsync(BlindPackageKinds.Full, new ServableBaseline(baseline, 0));

        var resp = await UploadAsync(blind, package, seeder, pairCode: true);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await blind.Services.GetRequiredService<ISyncPositionRepository>().GetAllAsync())
            .Should().NotContain(p => p.RemoteNodeId == seeder.NodeId && p.LastSequenceNum > 0, "the tail was not stored");
        (await blind.Services.GetRequiredService<BlindPairing>().IsCodeActiveAsync()).Should().BeTrue("the sender may send the seed again");
    }

    /// <summary>
    /// Review round 2, finding 2: a signed package whose content this node cannot use is refused as a whole, before the baseline
    /// is adopted, the legacy content purged, a peer taken or a position moved. Each of these passed the reader and failed
    /// (or half-applied) in the seed after the adoption.
    /// </summary>
    [Theory]
    [InlineData("positions omitted", "positions")]
    [InlineData("a peer key that is not base64", "public key")]
    [InlineData("a control database this node cannot import", "control database")]
    [InlineData("a control database made of allow-listed views", "control database")]
    // Review round 3: the rows of control.db are held against the appliers' rules before the adoption, too.
    [InlineData("a control row at the maximum Lamport time", "control database")]
    [InlineData("a control row with key material stored as text", "control database")]
    [InlineData("an activation naming a key the package lacks", "control database")]
    [InlineData("a recovery box with a preset its kind may not have", "control database")]
    // Review round 5: baseline and delta Lamport values are package input too, and must be bounded before adoption.
    [InlineData("a delta past the Lamport bound", "deltas.jsonl")]
    [InlineData("a baseline Lamport time past the format-2 bound", "baseline")]
    public async Task ASignedFullPackageWhoseContentCannotBeUsed_IsRefused_BeforeAnythingIsAdopted(string what, string message)
    {
        var (blind, _) = NewBlind();
        await MaterialiseLegacyContentAsync(blind);
        var seeder = await NewSeederAsync();
        var peer = Guid.NewGuid();
        await new WhitelistRepository(seeder.Factory).CreateAsync(new WhitelistEntry
        {
            NodeId = peer, DisplayName = "another peer", Ed25519PublicKey = new byte[32], Status = "A", IsSuperadmin = false,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, LamportTs = 5, SourceNodeId = seeder.NodeId,
        });
        var script = await AuthorAsync(seeder);
        var baseline = seeder.WriteBaseline(Path.Combine(_work, "unusable-" + Guid.NewGuid().ToString("N")), [script.BaselineBlob], lamportAtCut: 50);
        var originalPackage = await seeder.WriteAsync(BlindPackageKinds.Full, new ServableBaseline(baseline, 0));
        byte[]? invalidBaselineBytes = null;
        byte[]? invalidBaselineSignature = null;
        if (what == "a baseline Lamport time past the format-2 bound")
        {
            invalidBaselineBytes = (BaselineManifest.Parse(baseline.ManifestBytes) with
            {
                LamportAtCut = BlindManifestV2.MaximumLamport + 1,
            }).ToBytes();
            invalidBaselineSignature = Ed25519Signer.Sign(seeder.Identity.Ed25519PrivateKey,
                BaselineManifest.SignaturePayload(invalidBaselineBytes));
        }
        var package = await seeder.ResignAsync(originalPackage,
            entries => entries.Select(e =>
            {
                if (e.Name == Format2Package.BaselineManifestEntry && invalidBaselineBytes is not null)
                    return (e.Name, invalidBaselineBytes);
                if (e.Name == Format2Package.BaselineSignatureEntry && invalidBaselineSignature is not null)
                    return (e.Name, invalidBaselineSignature);
                if (e.Name == BlindManifestV2.DeltasFileName && what == "a delta past the Lamport bound")
                {
                    var deltas = Encoding.UTF8.GetString(e.Data).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(line => JsonSerializer.Deserialize<SyncEvent>(line, Format2Package.DeltaJson)!).ToList();
                    deltas[0].LamportTs = BlindManifestV2.MaximumLamport + 1;
                    return (e.Name, Encoding.UTF8.GetBytes(string.Concat(deltas.Select(Format2Package.ToDeltaLine).Select(line => line + "\n"))));
                }
                if (e.Name == BlindManifestV2.FileName)
                {
                    var json = System.Text.Json.Nodes.JsonNode.Parse(e.Data)!.AsObject();
                    if (what == "positions omitted") json.Remove("positions");
                    if (what == "a peer key that is not base64")
                        json["whitelist"]!.AsArray().Single(p => p!["node_id"]!.GetValue<string>() == peer.ToString())!["public_key"] = "%%% not base64 %%%";
                    return (e.Name, Encoding.UTF8.GetBytes(json.ToJsonString()));
                }
                if (e.Name == BlindManifestV2.ControlDbFileName && what == "a control database this node cannot import")
                    return (e.Name, ControlDatabaseWithNoUsableColumns());
                if (e.Name == BlindManifestV2.ControlDbFileName && what == "a control database made of allow-listed views")
                    return (e.Name, ControlDatabaseWithAllowListedViews(e.Data));
                if (e.Name == BlindManifestV2.ControlDbFileName && ControlRowEdits.TryGetValue(what, out var sql))
                    return (e.Name, EditedControlDatabase(e.Data, sql));
                return (e.Name, e.Data);
            }).ToList());

        var resp = await UploadAsync(blind, package, seeder, pairCode: true);

        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain(message);
        await AssertNothingWasTakenAsync(blind, seeder, peer);
    }

    /// <summary>The SQL run against the package's control.db for the cases of the theory above that are about one of its rows.</summary>
    private static readonly Dictionary<string, string> ControlRowEdits = new()
    {
        ["a control row at the maximum Lamport time"] = "UPDATE tbl_metadata_key SET lamport_ts = 9223372036854775807",
        ["a control row with key material stored as text"] = "UPDATE tbl_metadata_key SET wrapped = 'not a blob'",
        ["an activation naming a key the package lacks"] = "UPDATE tbl_metadata_activation SET key_id = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'",
        ["a recovery box with a preset its kind may not have"] =
            "INSERT INTO tbl_recovery_box VALUES ('11111111-1111-1111-1111-111111111111', 'strong', '22222222-2222-2222-2222-222222222222', "
            + "'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc', 1, 'd64t3', zeroblob(32), zeroblob(49), zeroblob(12), "
            + "'2026-10-10T00:00:00.0000000Z', 'A', NULL, 5, NULL)",
    };

    /// <summary>The control database bytes with <paramref name="sql"/> run against a copy of them.</summary>
    private byte[] EditedControlDatabase(byte[] data, string sql)
    {
        var path = Path.Combine(_work, "ctl-edit-" + Guid.NewGuid().ToString("N") + ".db");
        File.WriteAllBytes(path, data);
        using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            conn.Execute(sql);
        }
        return File.ReadAllBytes(path);
    }

    /// <summary>
    /// What "nothing was adopted" means on a blind host that held legacy content and had not been seeded: no baseline, the legacy
    /// rows still there (nothing purged), neither the seeder nor another peer in the whitelist, no position, not activated, no
    /// activation row, and the pair code still usable for a good package.
    /// </summary>
    private static async Task AssertNothingWasTakenAsync(BlindNodeFactory blind, StepASeeder seeder, params Guid[] peers)
    {
        var sp = blind.Services;
        (await sp.GetRequiredService<IBaselineStore>().GetCurrentAsync()).Should().BeNull("a refused package adopts nothing");
        (await NonOperationalTablesWithRowsAsync(blind)).Should().NotBeEmpty("a refused package purges nothing");
        foreach (var peer in peers)
            (await sp.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(peer, includeDeleted: true)).Should().BeNull();
        (await sp.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(seeder.NodeId, includeDeleted: true)).Should().BeNull();
        (await sp.GetRequiredService<ISyncPositionRepository>().GetAllAsync()).Should().BeEmpty();
        sp.GetRequiredService<ISyncProtocolPolicy>().SealsMetadata.Should().BeFalse("the activation was not imported");
        using (var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection())
        {
            (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_metadata_activation")).Should().Be(0);
            (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_metadata_key")).Should().Be(0);
        }
        (await sp.GetRequiredService<BlindPairing>().IsCodeActiveAsync()).Should().BeTrue("the sender may send a good package");
    }

    /// <summary>
    /// Review round 3: a first seed computes its grant time (one above everything the node will hold) before the baseline is adopted.
    /// This node holds a peer row stamped at the limit of a Lamport time - it survives the purge - so no time is left to grant: the
    /// seed is refused whole and the legacy content is still there. Before, the baseline was adopted and the content purged, and only
    /// then did the grant fail.
    /// </summary>
    [Fact]
    public async Task AFirstSeedThatLeavesNoTimeToGrant_IsRefused_BeforeTheBaselineIsAdopted()
    {
        var (blind, _) = NewBlind();
        await MaterialiseLegacyContentAsync(blind);
        await blind.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = Guid.NewGuid(), DisplayName = "stamped", Ed25519PublicKey = new byte[32], Status = "A", IsSuperadmin = false,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, LamportTs = RecoveryRestoreService.MaxRestoredLamport - 1, SourceNodeId = Guid.NewGuid(),
        });
        var seeder = await NewSeederAsync();
        var script = await AuthorAsync(seeder);
        var baseline = seeder.WriteBaseline(Path.Combine(_work, "nogrant-" + Guid.NewGuid().ToString("N")), [script.BaselineBlob], lamportAtCut: 50);
        var package = await seeder.WriteAsync(BlindPackageKinds.Full, new ServableBaseline(baseline, 0));

        var resp = await UploadAsync(blind, package, seeder, pairCode: true);

        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("Lamport time");
        await AssertNothingWasTakenAsync(blind, seeder);
    }

    /// <summary>
    /// The highest package value leaves exactly one value for the pair-code grant, and that grant remains below the
    /// exclusive restore ceiling.
    /// </summary>
    [Fact]
    public async Task AFirstSeed_AtTheHighestPackageLamport_ProducesAGrantThatCanBeRestored()
    {
        var (blind, _) = NewBlind();
        await MaterialiseLegacyContentAsync(blind);
        var seeder = await NewSeederAsync();
        var script = await AuthorAsync(seeder);
        var baseline = seeder.WriteBaseline(Path.Combine(_work, "atbound-" + Guid.NewGuid().ToString("N")), [script.BaselineBlob], lamportAtCut: 50);
        var package = await seeder.ResignAsync(
            await seeder.WriteAsync(BlindPackageKinds.Full, new ServableBaseline(baseline, 0)),
            entries => entries.Select(e => e.Name == BlindManifestV2.ControlDbFileName
                ? (e.Name, EditedControlDatabase(e.Data, $"UPDATE tbl_metadata_activation SET lamport_ts = {BlindManifestV2.MaximumLamport}"))
                : (e.Name, e.Data)).ToList());

        var resp = await UploadAsync(blind, package, seeder, pairCode: true);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var row = await blind.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(seeder.NodeId);
        BlindManifestV2.RestoreLamportExclusive.Should().Be(RecoveryRestoreService.MaxRestoredLamport,
            "format-2 and recovery must share the same exclusive ceiling");
        row!.LamportTs.Should().Be(BlindManifestV2.MaximumLamport + 1, "the grant is one above the highest time the package brought");
        row.LamportTs.Should().BeLessThan(RecoveryRestoreService.MaxRestoredLamport,
            "RecoveryRestoreService rejects the exclusive ceiling itself");
    }

    /// <summary>A control.db with every control table by name, each holding one column no live table has: the names pass the reader, the import cannot.</summary>
    private byte[] ControlDatabaseWithNoUsableColumns()
    {
        var path = Path.Combine(_work, "ctl-" + Guid.NewGuid().ToString("N") + ".db");
        using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            foreach (var table in SnapshotTables.Control)
                conn.Execute($"CREATE TABLE \"{table}\" (zzz_not_a_column TEXT)");
        }
        return File.ReadAllBytes(path);
    }

    /// <summary>All six allow-listed names are views with the expected columns but no rows; the importer only accepts real tables.</summary>
    private byte[] ControlDatabaseWithAllowListedViews(byte[] data)
    {
        var path = Path.Combine(_work, "ctl-views-" + Guid.NewGuid().ToString("N") + ".db");
        File.WriteAllBytes(path, data);
        using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            foreach (var table in SnapshotTables.Control)
            {
                var columns = conn.Query<string>("SELECT name FROM pragma_table_info(@table)", new { table }).ToList();
                conn.Execute($"DROP TABLE \"{table}\"");
                var projection = string.Join(", ", columns.Select(column => $"NULL AS \"{column}\""));
                conn.Execute($"CREATE VIEW \"{table}\" AS SELECT {projection} WHERE 0");
            }
        }
        return File.ReadAllBytes(path);
    }

    /// <summary>
    /// Review round 2, finding 1: the trimmer finishes an unfinished purge although the current baseline's files can no longer
    /// be served (a damaged container, a switch that did not complete). The purge needs that a baseline was adopted, not that
    /// it is servable now; the log trim, which does need a servable baseline, trims nothing meanwhile.
    /// </summary>
    [Fact]
    public async Task TheLogTrimmer_FinishesAPurgeThatDidNotFinish_EvenWhenTheCurrentBaselineCannotBeServed()
    {
        var (blind, _, _, _, _, _) = await FirstSeedAsync();
        var leftover = MetadataCanaries.Ascii("leftover");
        using (var conn = blind.Services.GetRequiredService<IDbConnectionFactory>().CreateConnection())
        {
            await conn.ExecuteAsync("INSERT INTO tbl_folder (id, path, name, created_at, updated_at) VALUES ('f', @leftover, @leftover, 'now', 'now')", new { leftover });
            await conn.ExecuteAsync("DELETE FROM tbl_blind_state WHERE key = 'opaque_store'");
        }
        var container = Path.Combine(blind.DataPath, "baselines", "current", BaselineManifest.ContainerFileName);
        File.Exists(container).Should().BeTrue();
        var damaged = await File.ReadAllBytesAsync(container);
        damaged[^1] ^= 1;
        await File.WriteAllBytesAsync(container, damaged);
        (await blind.Services.GetRequiredService<IBaselineStore>().GetCurrentAsync()).Should().BeNull("the control: the baseline cannot be served now");

        var cutoff = await blind.Services.GetRequiredService<BlindLogTrimmer>().TrimAsync();

        cutoff.Should().BeNull("nothing is trimmed without a servable baseline");
        (await NonOperationalTablesWithRowsAsync(blind)).Should().BeEmpty();
        using var check = blind.Services.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        (await check.ExecuteScalarAsync<string>("SELECT value FROM tbl_blind_state WHERE key = 'opaque_store'")).Should().Be("1");
        SqliteConnection.ClearAllPools();
        MetadataCanaries.ScanDirectory(blind.DataPath, [leftover]).Should().BeEmpty();
    }

    /// <summary>The other side of the same rule: before any baseline was adopted the legacy rows are the node's only copy, and the trimmer leaves them.</summary>
    [Fact]
    public async Task TheLogTrimmer_DoesNotPurge_BeforeAnyBaselineWasAdopted()
    {
        var (blind, _) = NewBlind();
        await MaterialiseLegacyContentAsync(blind);
        await ActivateByRecipeAsync(blind);

        await blind.Services.GetRequiredService<BlindLogTrimmer>().TrimAsync();

        (await NonOperationalTablesWithRowsAsync(blind)).Should().Contain("tbl_folder").And.Contain("tbl_article");
        (await BaselineStore.IsOpaqueAsync(blind.Services.GetRequiredService<IDbConnectionFactory>())).Should().BeFalse();
    }

    /// <summary>Review round, finding 1: the trimmer's start-up and periodic run finish a purge that did not finish.</summary>
    [Fact]
    public async Task TheLogTrimmer_FinishesAPurgeThatDidNotFinish()
    {
        var (blind, _, _, _, _, _) = await FirstSeedAsync();
        var leftover = MetadataCanaries.Ascii("leftover");
        using (var conn = blind.Services.GetRequiredService<IDbConnectionFactory>().CreateConnection())
        {
            await conn.ExecuteAsync("INSERT INTO tbl_folder (id, path, name, created_at, updated_at) VALUES ('f', @leftover, @leftover, 'now', 'now')", new { leftover });
            await conn.ExecuteAsync("DELETE FROM tbl_blind_state WHERE key = 'opaque_store'");
        }

        await blind.Services.GetRequiredService<BlindLogTrimmer>().TrimAsync();

        (await NonOperationalTablesWithRowsAsync(blind)).Should().BeEmpty();
        using var check = blind.Services.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        (await check.ExecuteScalarAsync<string>("SELECT value FROM tbl_blind_state WHERE key = 'opaque_store'")).Should().Be("1");
        SqliteConnection.ClearAllPools();
        MetadataCanaries.ScanDirectory(blind.DataPath, [leftover]).Should().BeEmpty();
    }

    /// <summary>
    /// Track 1's contract (spec 5.7, review round): an activated host never serves a protocol-1..3 content row to a blind peer, and
    /// a page is empty only when no row the peer may have is left, so a pull client paging by its cursor reaches the sealed rows
    /// behind the clear ones. On a blind host the clear rows are the legacy ones of a 2.5 node between the activation and its
    /// first adoption; the route is the one the full nodes serve (it is linked into the BlindNode host).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task OnAnActivatedBlindHost_APagingBlindPeer_NeverGetsLegacyClearRows_AndReachesTheSealedRowsBehindThem(int limit)
    {
        var (blind, _) = NewBlind();
        var seeder = await NewSeederAsync();
        await TrustAsync(blind, seeder.NodeId, seeder.PublicKey, superadmin: true);
        await ActivateByRecipeAsync(blind);
        var legacy = new List<Guid>();
        using (var conn = blind.Services.GetRequiredService<IDbConnectionFactory>().CreateConnection())
            for (var i = 0; i < 3; i++)
            {
                var id = Guid.NewGuid();
                legacy.Add(id);
                await conn.ExecuteAsync(
                    @"INSERT INTO tbl_event (event_id, node_id, lamport_ts, event_type, payload, signature, created_at, protocol_version)
                      VALUES (@id, @node, @lamport, 'folder_create', @payload, X'00', @now, 3)",
                    new { id = id.ToString(), node = seeder.NodeId.ToString(), lamport = 1 + i, payload = "{}", now = DateTime.UtcNow.ToString("O") });
            }
        await seeder.ActivateAsync();
        var sealedEvents = new List<SyncEvent>
        {
            await seeder.SealedAsync(EventTypes.FolderCreate, null, StepASeeder.FolderCreate("/a", "a")),
            await seeder.SealedAsync(EventTypes.FolderCreate, null, StepASeeder.FolderCreate("/b", "b")),
        };
        using (var pusher = blind.Server.CreateClient())
        {
            pusher.DefaultRequestHeaders.Authorization = new("Bearer", blind.Services.GetRequiredService<SyncTokenStore>().IssueToken(seeder.NodeId, 4));
            (await pusher.PostAsJsonAsync("/api/sync/events", sealedEvents.ToArray())).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var phone = BlindNodeId.NewId();
        await TrustAsync(blind, phone, new byte[32], superadmin: false);
        using var http = blind.Server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", blind.Services.GetRequiredService<SyncTokenStore>().IssueToken(phone, 4));
        var paged = new List<string>();
        long cursor = 0;
        for (var round = 0; round < 50; round++)
        {
            var resp = await http.GetAsync($"/api/sync/events?afterSequence={cursor}&limit={limit}");
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            var page = await resp.Content.ReadFromJsonAsync<List<JsonElement>>() ?? [];
            if (page.Count == 0) break;
            page.Count.Should().BeLessThanOrEqualTo(limit);
            paged.AddRange(page.Select(e => e.GetProperty("eventId").GetString()!.ToLowerInvariant()));
            cursor = page[^1].GetProperty("sequenceNum").GetInt64();
        }

        paged.Should().NotContain(legacy.Select(i => i.ToString().ToLowerInvariant()), "a blind peer never gets protocol-1..3 content once activated");
        paged.Should().Contain(sealedEvents.Select(e => e.EventId.ToString().ToLowerInvariant()),
            "paging by the cursor reaches the sealed rows behind the clear ones, whatever the page size");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<VerifiedPackageV2> ReadAsync(HttpResponseMessage resp, byte[] servingKey, StepASeeder seeder)
    {
        var path = Path.Combine(_work, Guid.NewGuid().ToString("N") + ".tar.gz");
        await File.WriteAllBytesAsync(path, await resp.Content.ReadAsByteArrayAsync());
        var signature = Convert.FromBase64String(resp.Headers.GetValues("X-BMB-Snapshot-Signature").Single());
        return await new BlindPackageV2Reader().ExtractVerifiedAsync(path, signature, servingKey,
            id => id == seeder.NodeId ? (seeder.PublicKey, true) : null, Path.Combine(_work, "x-" + Guid.NewGuid().ToString("N")));
    }

    private static async Task<HttpResponseMessage> RestorePackageAsync(BlindNodeFactory blind)
    {
        using var console = blind.CreateClient();
        var issued = await console.PostAsync("/api/blind/restore-code", null);
        issued.StatusCode.Should().Be(HttpStatusCode.OK);
        var code = BlindRestoreCode.Parse((await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        using var http = blind.Server.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/blind/restore/package");
        req.Headers.Add("X-Restore-Code", code.Secret);
        return await http.SendAsync(req);
    }

    /// <summary>Spec 14: one key row and one activation row, then the policy told.</summary>
    private static async Task ActivateByRecipeAsync(BlindNodeFactory blind)
    {
        using (var conn = blind.Services.GetRequiredService<IDbConnectionFactory>().CreateConnection())
        {
            var now = DateTime.UtcNow.ToString("O");
            var keyId = new string('b', 32);
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_metadata_key (key_id, generation, dek_fingerprint, epoch_hint, wrapped, iv, author_node_id, created_at, lamport_ts, event_id)
                  VALUES (@keyId, 1, @fp, 0, @wrapped, @iv, @author, @now, 1, @eventId)",
                new { keyId, fp = new string('f', 64), wrapped = new byte[48], iv = new byte[12], author = Guid.NewGuid().ToString(), now, eventId = Guid.NewGuid().ToString() });
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_metadata_activation (activation_id, key_id, generation, activated_at, author_node_id, event_id, lamport_ts, applied_at)
                  VALUES (@id, @keyId, 1, @now, @author, @eventId, 2, @now)",
                new { id = Guid.NewGuid().ToString(), keyId, now, author = Guid.NewGuid().ToString(), eventId = Guid.NewGuid().ToString() });
        }
        blind.Services.GetRequiredService<ISyncProtocolPolicy>().MarkActivated();
    }

    private static Task TrustAsync(BlindNodeFactory blind, Guid node, byte[] key, bool superadmin) =>
        blind.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = node, DisplayName = "peer", Ed25519PublicKey = key, Status = "A", IsSuperadmin = superadmin,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, LamportTs = 1, SourceNodeId = node,
        });

    private sealed record StoredRow(string EventType, string Payload, byte[] Signature, int ProtocolVersion, int ApplyState);

    private static async Task<StoredRow?> StoredAsync(BlindNodeFactory blind, Guid eventId)
    {
        using var conn = blind.Services.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<(string EventType, string Payload, byte[] Signature, long ProtocolVersion, long ApplyState)?>(
            "SELECT event_type, payload, signature, protocol_version, apply_state FROM tbl_event WHERE event_id = @id COLLATE NOCASE",
            new { id = eventId.ToString() });
        return row is { } r ? new StoredRow(r.EventType, r.Payload, r.Signature, (int)r.ProtocolVersion, (int)r.ApplyState) : null;
    }

    private static async Task<List<string>> NonOperationalTablesWithRowsAsync(BlindNodeFactory blind)
    {
        using var conn = blind.Services.GetRequiredService<IDbConnectionFactory>().CreateConnection();
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
