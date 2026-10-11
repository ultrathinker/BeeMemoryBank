using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Media;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Sync.Recovery;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Metadata;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SealedEnvelope = BeeMemoryBank.Sync.Metadata.SealedEnvelope;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The sealed path changes how an event travels, never what it does (docs/design/step-a-spec.md 16.2 item 5). One
/// scripted sequence is authored on A after the switch, so A writes it sealed; B3 receives it as protocol 3 (the same
/// events in clear, signed by their authors: what A wrote before the switch) and B4 receives it sealed, byte for byte as A
/// serves it. Both end with the same replicated tables (up to what every receiver fills in for itself; a second clear
/// receiver is the control of which columns those are) and the same state digest. The applier logs of all nodes carry
/// no canary (item 9), and what B4 serves from its event log holds none either: there the script exists only sealed.
/// </summary>
public class SealedPathIsomorphismTests : IAsyncLifetime
{
    private readonly StepALogCapture _log = new();
    private readonly Node _a;
    private readonly Node _b3;
    private readonly Node _b4;
    private readonly Node _b3Control;
    // A second writer of the same network: its edit is newer than A's folder delete.
    private readonly StepAAuthor _other = new();
    private readonly List<string> _canaries = [];
    private byte[] _dek = [];

    public SealedPathIsomorphismTests()
    {
        _a = new Node(_log);
        _b3 = new Node(_log);
        _b4 = new Node(_log);
        _b3Control = new Node(_log);
    }

    public async Task InitializeAsync()
    {
        await _a.InitializeAsync();
        await _a.InitService.InitializeAsync("admin", "NodeA", "isomorphism-pass");
        await _a.Session.UnlockAsync("isomorphism-pass");
        _dek = _a.Session.GetMasterDek();
        var a = (await _a.NodeRepo.GetAsync())!;
        await _a.WhitelistRepo.CreateAsync(_other.Entry("Other", superadmin: false));

        foreach (var b in new[] { _b3, _b4, _b3Control })
        {
            await b.InitializeAsync();
            var (pub, seed) = Ed25519Signer.GenerateKeyPair();
            await b.NodeRepo.CreateAsync(new NodeIdentity
            {
                NodeId = Guid.NewGuid(), DisplayName = "Receiver", Ed25519PublicKey = pub, Ed25519PrivateKey = seed,
                Ed25519PrivateKeyV = 0, CreatedAt = DateTime.UtcNow
            });
            // One network, one master DEK (a joined node holds A's): the MEK wrapped under it opens on B4.
            b.Session.UnlockWithDek((byte[])_dek.Clone());
            await b.WhitelistRepo.CreateAsync(new WhitelistEntry
            {
                NodeId = a.NodeId, DisplayName = a.DisplayName, Ed25519PublicKey = a.Ed25519PublicKey, Status = "A",
                IsSuperadmin = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await b.WhitelistRepo.CreateAsync(_other.Entry("Other", superadmin: false));
        }
    }

    public async Task DisposeAsync()
    {
        CryptographicOperations.ZeroMemory(_dek);
        await _a.DisposeAsync();
        await _b3.DisposeAsync();
        await _b4.DisposeAsync();
        await _b3Control.DisposeAsync();
    }

    [Fact]
    public async Task OneScript_AppliedInClearAndSealed_GivesTheSameTables_AndTheSameDigest()
    {
        // The switch on A, as MetadataActivationService writes it: generation 1 under A's DEK, then the activation.
        var publisher = new MetadataEventPublisher(_a.NodeRepo, _a.EventLogRepo, _a.Clock, _a.Session, _a.EventApplier,
            new SyncTrigger(), _a.ProtocolPolicy);
        var key = await publisher.PublishNewKeyAsync(1);
        await publisher.PublishActivationAsync(Guid.NewGuid(), new MetadataKeyInfo(key.KeyId, key.Generation));
        _a.ProtocolPolicy.SealsMetadata.Should().BeTrue();
        _other.UseKey(MetadataKeyCrypto.TryUnwrap(key, [_dek])!, key);

        var ids = await RunScriptOnAAsync();

        var log = await _a.EventLogRepo.GetAfterSequenceAsync(0, 10_000);
        log.Where(e => EventClasses.IsSealable(e.EventType)).Should().BeEmpty("after the switch no content event is written in clear");
        log.Count(e => e.EventType == EventTypes.Sealed).Should().BeGreaterThan(20);

        var outcomes = new List<(string Type, EventApplyResult Clear, EventApplyResult Sealed)>();
        foreach (var evt in log)
        {
            if (evt.EventType is EventTypes.MetadataKeySet or EventTypes.MetadataActivation)
            {
                (await _b4.ApplyFromAsync(_a, evt)).Should().Be(EventApplyResult.Applied);
                continue;
            }
            var clear = evt.EventType == EventTypes.Sealed ? await InClearAsync(evt) : evt;
            var sealedResult = await _b4.ApplyFromAsync(_a, evt);
            outcomes.Add((clear.EventType, await _b3.ApplyFromAsync(_a, clear), sealedResult));
            await _b3Control.ApplyFromAsync(_a, clear);
        }
        _b4.ProtocolPolicy.SealsMetadata.Should().BeTrue();
        _b3.ProtocolPolicy.SealsMetadata.Should().BeFalse();

        // Every event had the same outcome both ways, and nothing waited.
        outcomes.Should().OnlyContain(o => o.Clear == o.Sealed, string.Join(", ", outcomes.Where(o => o.Clear != o.Sealed)));
        outcomes.Should().NotContain(o => o.Sealed == EventApplyResult.Held);
        outcomes.Select(o => o.Type).Should().Contain([EventTypes.ArticleCreate, EventTypes.ArticleUpdate, EventTypes.FolderRename,
            EventTypes.FolderDelete, EventTypes.HardDelete, EventTypes.ConceptTagRename, EventTypes.ConceptTagMerge,
            EventTypes.ConceptTagDelete, EventTypes.MediaCreate, EventTypes.MediaLink, EventTypes.CommentCreate]);

        // The script did what it says on both receivers.
        foreach (var b in new[] { _b3, _b4 })
        {
            (await b.ArticleRepo.GetByIdAsync(ids.Plan))!.Title.Should().Be(ids.PlanTitle);
            (await b.ArticleRepo.GetByIdAsync(ids.Revived))!.TreePath.Should().Be(ids.RevivedPath, "the newer edit keeps its folder");
            (await b.ArticleRepo.GetByIdAsync(ids.Doomed)).Should().BeNull("the subtree was hard-deleted");
        }

        // Equal tables, run against run (the clear control tables exist only where the switch arrived). The only columns
        // that differ are those that differ between two clear receivers as well: what a node makes up for itself when it
        // stores a row (the time, the id of a folder an article implies, a conflict copy's id and expiry).
        var local = new HashSet<string>();
        var differing = new HashSet<string>();
        var tables = new List<(string Table, List<Dictionary<string, string>> Clear, List<Dictionary<string, string>> Sealed)>();
        foreach (var table in SnapshotTables.Replicated.Except(["tbl_metadata_key", "tbl_metadata_activation"]))
        {
            var (rows3, rows4, control) = (await RowsAsync(_b3, table), await RowsAsync(_b4, table), await RowsAsync(_b3Control, table));
            rows4.Count.Should().Be(rows3.Count, table);
            control.Count.Should().Be(rows3.Count, table);
            foreach (var column in rows3.Concat(rows4).SelectMany(r => r.Keys).Distinct())
            {
                if (!Column(rows3, column).SequenceEqual(Column(rows4, column))) differing.Add($"{table}.{column}");
                if (!Column(rows3, column).SequenceEqual(Column(control, column))) local.Add($"{table}.{column}");
            }
            tables.Add((table, rows3, rows4));
        }
        local.Should().BeSubsetOf(NodeLocalColumns);
        differing.Should().BeSubsetOf(local, "only what differs between two clear receivers differs");
        foreach (var (table, rows3, rows4) in tables)
            Lines(table, rows4, local).Should().Equal(Lines(table, rows3, local), $"{table} is the same after the sealed run");

        // The state digest, run against run (content and history; the trust sections hold each node's own whitelist). A
        // conflict copy's id and times and the id of a folder the applier revived for a newer edit are the receiver's own,
        // as the control shows: set alike on both before hashing.
        // Against the author the digest differs in protocol 3 already (the author keeps version history, a receiver keeps
        // conflict copies instead), so the run against run comparison is the one that isolates the sealed path.
        foreach (var b in new[] { _b3, _b4 }) await NormaliseNodeLocalRowsAsync(b);
        var (digest3, digest4) = (await DigestAsync(_b3), await DigestAsync(_b4));
        StateDigest.Matches(digest3.Digest, digest4).Should().BeTrue();
        StateDigest.Matches(digest4.Digest, digest3).Should().BeTrue();
        StateDigest.CompareHistory(digest3.Digest, digest4)!.Matches.Should().BeTrue();

        // No canary in any applier log line (item 9); B4 serves the script only sealed, B3 in clear (the scanner's control).
        _log.Lines.Should().NotBeEmpty();
        _log.Scan(_canaries).Should().BeEmpty();
        (await EventLogBytesAsync(_b3)).Should().NotBeEmpty();
        MetadataCanaries.Scan(await EventLogBytesAsync(_b3), _canaries, "B3 tbl_event").Should().NotBeEmpty("the scanner's control");
        MetadataCanaries.Scan(await EventLogBytesAsync(_b4), _canaries, "B4 tbl_event").Should().BeEmpty();
    }

    private sealed record ScriptIds(Guid Plan, string PlanTitle, Guid Revived, string RevivedPath, Guid Doomed);

    /// <summary>
    /// Create, edit, move, subtree rename, folder delete with a newer edit inside, hard delete of a subtree, tag rename,
    /// merge and delete in two cases each, media created on an article and linked to one, a comment.
    /// </summary>
    private async Task<ScriptIds> RunScriptOnAAsync()
    {
        var folderRepo = new FolderRepository(_a.Factory, new CallerScopeHolder());
        var folders = FolderServiceOf(_a);
        var tags = new ConceptTagService(new ConceptTagRepository(_a.Factory, new CallerScopeHolder()), new FakeEmbeddingGenerator(), _a.EventLogger);
        var comments = new CommentService(_a.CommentRepo, _a.BodyRepo, _a.Session, _a.EventLogger, _a.ArticleRepo, _a.EventLogRepo);
        var media = new MediaService(new MediaRepository(_a.Factory, new CallerScopeHolder()), _a.ArticleRepo, _a.Session, _a.NodeRepo,
            _a.Clock, _a.EventLogger, new MediaStorageOptions(Path.GetTempPath()), _a.Factory, new SkiaImageTranscoder(),
            logger: null, blobRepo: new BlobRepository(_a.Factory));

        string Ascii(string label) { var c = MetadataCanaries.Ascii(label); _canaries.Add(c); return c; }
        string Wide(string label) { var c = MetadataCanaries.NonAscii(label); _canaries.Add(c); return c; }

        var (work, sub, shelf, gone, doomed, deeper) =
            (Ascii("work"), Ascii("sub"), Ascii("shelf"), Ascii("gone"), Ascii("doomed"), Ascii("deeper"));
        var (alpha, beta, gamma, delta, eps, zeta, lone, theta) =
            (Ascii("alpha"), Ascii("beta"), Ascii("gamma"), Ascii("delta"), Ascii("eps"), Ascii("zeta"), Ascii("lone"), Ascii("theta"));

        // Folders first, so every receiver holds the author's folder ids (an implied folder gets an id of its own).
        foreach (var path in new[] { $"/{work}", $"/{work}/{sub}", $"/{shelf}", $"/{gone}", $"/{doomed}", $"/{doomed}/{deeper}" })
            await folders.CreateAsync(path);

        // Create, edit, move.
        var plan = await _a.ArticleService.CreateAsync(Wide("Plan"), $"/{work}/{sub}", [alpha, beta], "plan body");
        var note = await _a.ArticleService.CreateAsync(Ascii("Note"), $"/{work}", [delta, lone], "note body");
        var kept = await _a.ArticleService.CreateAsync(Wide("Kept"), $"/{shelf}", [eps, zeta, beta], "kept body");
        var planTitle = Wide("PlanEdited");
        await _a.ArticleService.UpdateAsync(plan.Id, title: planTitle, plaintext: "plan body, edited");
        await _a.ArticleService.UpdateAsync(note.Id, treePath: $"/{shelf}");

        // Subtree rename: /work with /work/sub and the article in it.
        var job = Ascii("job");
        await folders.RenameAsync((await folderRepo.GetByPathAsync($"/{work}"))!.Id, job);

        // Tags: a rename of a tag on one article and of one on two; a merge into a tag on the same article and into one
        // elsewhere; a delete of a tag nobody carries any more and of one in use.
        await tags.RenameAsync(alpha, gamma);
        await tags.RenameAsync(beta, theta);
        await tags.MergeAsync(eps, zeta);
        await tags.MergeAsync(lone, gamma);
        await _a.ArticleService.UpdateAsync(note.Id, tags: [gamma]);
        await tags.DeleteAsync(delta);
        await tags.DeleteAsync(theta);

        // A folder deleted while another writer's newer edit of an article inside it is on its way.
        var revived = await _a.ArticleService.CreateAsync(Ascii("Inside"), $"/{gone}", [], "inside body");
        var createdInside = (await _a.EventLogRepo.GetAfterSequenceAsync(0, 10_000)).Last();
        await folders.DeleteAsync((await folderRepo.GetByPathAsync($"/{gone}"))!.Id);
        var edit = JsonNode.Parse((await InClearAsync(createdInside)).Payload)!;
        edit["title"] = Wide("EditedElsewhere");
        var newer = _other.Sealed(EventTypes.ArticleUpdate, revived.Id, edit.ToJsonString());
        (await _a.EventApplier.ApplyAsync(newer)).Should().Be(EventApplyResult.Applied);
        (await _a.ArticleRepo.GetByIdAsync(revived.Id))!.TreePath.Should().Be($"/{gone}");

        // Hard delete of a subtree.
        var doomedArticle = await _a.ArticleService.CreateAsync(Ascii("Doomed"), $"/{doomed}", [], "doomed body");
        await _a.ArticleService.CreateAsync(Ascii("Deeper"), $"/{doomed}/{deeper}", [], "deeper body");
        await _a.HardDeleteService.DeleteFolderAsync($"/{doomed}", 1, null, CancellationToken.None);

        // Media: created on an article, and uploaded on its own then linked.
        await media.CreateAsync(Ascii("attached") + ".txt", "text/plain", Encoding.UTF8.GetBytes("attached"), kept.Id, isAttachment: true);
        var orphan = await media.CreateAsync(Ascii("linked") + ".txt", "text/plain", Encoding.UTF8.GetBytes("linked"), null,
            isAttachment: true, uploadedBy: "owner-1");
        await _a.ArticleService.LinkAttachmentsAsync(kept.Id, [orphan.Id], "owner-1");

        // A comment.
        await comments.CreateAsync(kept.Id, Wide("Comment"));

        return new ScriptIds(plan.Id, planTitle, revived.Id, $"/{gone}", doomedArticle.Id);
    }

    /// <summary>
    /// The protocol-3 form of a sealed event of A's log: the same ids, Lamport time, type, article and payload, signed by
    /// its author, exactly what that author wrote before the switch.
    /// </summary>
    private async Task<SyncEvent> InClearAsync(SyncEvent outer)
    {
        var envelope = SealedEnvelope.Parse(outer.Payload);
        var eventKey = _a.KeyRing.DeriveKey(envelope.KeyId, MetadataKeyCrypto.EventKeyInfo, ReadOnlySpan<byte>.Empty);
        SyncEvent view;
        try
        {
            view = MetadataEnvelope.Open(outer, envelope, eventKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(eventKey);
        }
        view.ProtocolVersion = 3;
        view.EntityId = EventEntityId.Derive(view.EventType, view.ArticleId, view.Payload);
        if (view.NodeId == _other.NodeId) return _other.Resign(view);
        var identity = (await _a.NodeRepo.GetAsync())!;
        view.Signature = NodeIdentityVault.SignWithIdentityOrGetDek(identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV,
            identity.Ed25519PrivateKeyV, identity.NodeId, _a.Session.GetMasterDek, EventSignature.BuildPayload(view));
        return view;
    }

    private static FolderService FolderServiceOf(Node node)
    {
        var scopeHolder = new CallerScopeHolder();
        var folderRepo = new FolderRepository(node.Factory, scopeHolder);
        var userRepo = new UserRepository(node.Factory);
        var folderAccess = new FolderAccessService(new ServiceCollection()
            .AddSingleton<IDbConnectionFactory>(_ => node.Factory)
            .AddScoped<IFolderAclRepository>(_ => new FolderAclRepository(node.Factory))
            .AddScoped<IRoleRepository>(_ => new RoleRepository(node.Factory))
            .AddScoped<IRoleAclRepository>(_ => new RoleAclRepository(node.Factory))
            .AddScoped<IUserRepository>(_ => userRepo)
            .AddScoped<IFolderRepository>(_ => folderRepo)
            .AddScoped(_ => scopeHolder)
            .BuildServiceProvider());
        return new FolderService(folderRepo, node.ArticleRepo, node.NodeRepo, node.Clock, node.EventLogger, folderAccess, scopeHolder);
    }

    /// <summary>Columns a receiver fills in for itself (protocol 3 as much as sealed): two receivers never agree on them.</summary>
    private static readonly string[] NodeLocalColumns =
    [
        "tbl_blob.created_at",
        "tbl_folder.id", "tbl_folder.created_at", "tbl_folder.updated_at", "tbl_article.folder_id",
        "tbl_conflict_version.id", "tbl_conflict_version.created_at", "tbl_conflict_version.expires_at",
    ];

    /// <summary>Every row of a table, its columns as text (blobs in base64).</summary>
    private static async Task<List<Dictionary<string, string>>> RowsAsync(Node node, string table)
    {
        using var conn = node.Factory.CreateConnection();
        var rows = await conn.QueryAsync($"SELECT * FROM {table}");
        return rows.Cast<IDictionary<string, object?>>()
            .Select(r => r.ToDictionary(c => c.Key, c => c.Value is byte[] bytes ? Convert.ToBase64String(bytes) : $"{c.Value}"))
            .ToList();
    }

    private static IEnumerable<string> Column(List<Dictionary<string, string>> rows, string column) =>
        rows.Select(r => r.GetValueOrDefault(column) ?? "").Order(StringComparer.Ordinal);

    /// <summary>The rows as sorted lines, without the node-local columns.</summary>
    private static List<string> Lines(string table, List<Dictionary<string, string>> rows, IReadOnlySet<string> local) =>
        rows.Select(r => string.Join("|", r.Where(c => !local.Contains($"{table}.{c.Key}"))
                .OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key}={c.Value}")))
            .Order(StringComparer.Ordinal).ToList();

    /// <summary>The rows whose id or times a receiver makes up itself, given the same values on every receiver.</summary>
    private static async Task NormaliseNodeLocalRowsAsync(Node node)
    {
        using var conn = node.Factory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_conflict_version SET id = article_id || ':' || lamport_ts, created_at = '2000-01-01', expires_at = '2000-01-08'");
        // A folder no event created (Lamport time 0): the applier made it for an article that names its path. Renamed with
        // its articles' references in one go, so the foreign key is checked off for these two statements.
        await conn.ExecuteAsync("PRAGMA foreign_keys = OFF");
        await conn.ExecuteAsync("UPDATE tbl_article SET folder_id = (SELECT 'implied:' || f.path FROM tbl_folder f WHERE f.id = folder_id) " +
                                "WHERE folder_id IN (SELECT id FROM tbl_folder WHERE lamport_ts = 0)");
        await conn.ExecuteAsync("UPDATE tbl_folder SET id = 'implied:' || path WHERE lamport_ts = 0");
        await conn.ExecuteAsync("PRAGMA foreign_keys = ON");
    }

    private static async Task<StateDigestResult> DigestAsync(Node node)
    {
        using var conn = node.Factory.CreateConnection();
        return await StateDigest.ComputeAsync(conn);
    }

    /// <summary>
    /// The columns of <c>tbl_event</c> a node serves (the wire query). The local columns (<c>local_type</c>,
    /// <c>local_article_id</c>, <c>entity_id</c>) are plaintext on a full node by design (spec 3.8) and never served.
    /// </summary>
    private static async Task<byte[]> EventLogBytesAsync(Node node)
    {
        var served = await node.EventLogRepo.GetAfterSequenceAsync(0, 10_000);
        return Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, served
            .Where(e => e.EventType is not (EventTypes.MetadataKeySet or EventTypes.MetadataActivation))
            .Select(e => $"{e.EventType} {e.ArticleId} {e.EntityId} {e.ActorName} {e.Payload}")));
    }

    private sealed class Node(StepALogCapture log) : SyncTestFixture
    {
        protected override ILogger<EventApplier> CreateApplierLogger() => log.For<EventApplier>();
    }
}
