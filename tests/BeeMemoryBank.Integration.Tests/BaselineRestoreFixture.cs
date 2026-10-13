using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Sync.Recovery;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Metadata;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The source network of the baseline restore tests, built once (docs/design/step-a-spec.md 16.4 item 3): a full node with recovery boxes
/// that rotated its master key once BEFORE the baseline and once AFTER it, a vault with folders, notes, tags, comments and media, the
/// activation, the baseline it produced, and then every kind of change as a delta: create, edit, move, a subtree rename, a folder delete,
/// a soft delete, a hard delete of a note the baseline still holds, a tag rename and merge, a comment, media, a new metadata key after the
/// second rotation, and a state anchor last. From it the package a blind node would hand out is assembled by hand (the writer is track 2's):
/// the baseline, <c>control.db</c>, <c>deltas.jsonl</c> with every content event SEALED under the metadata key in force, and the blobs.
/// </summary>
public sealed class BaselineRestoreFixture : IAsyncLifetime
{
    public const string Password = "BaselineRestore1";

    public RecoveryTestFactory Source { get; }
    public string Work { get; } = Path.Combine(Path.GetTempPath(), "bmb_baseline_restore_" + Guid.NewGuid().ToString("N"));
    public List<string> Canaries { get; } = [];
    public ControllableHeldEventStatus Held { get; } = new();

    public Guid SourceNodeId { get; private set; }
    public byte[] SourcePublicKey { get; private set; } = [];
    public BaselineFiles Baseline { get; private set; } = null!;
    public string PackageDirectory { get; private set; } = "";

    /// <summary>The same package as one signed <c>.tar.gz</c> a blind node would serve, with its detached signature and the serving node's key.</summary>
    public string ArchivePath { get; private set; } = "";
    public byte[] ArchiveSignature { get; private set; } = [];
    public byte[] ServingPublicKey { get; private set; } = [];
    private byte[] _servingPrivateKey = [];
    public VerifiedPackageV2 Package { get; private set; } = null!;
    public List<SyncEvent> Deltas { get; private set; } = [];
    public Guid ServingNodeId { get; } = BlindNodeId.NewId();
    public string FirstKeyId { get; private set; } = "";
    public string SecondKeyId { get; private set; } = "";
    public Guid HardDeletedArticle { get; private set; }
    public Guid SurvivingArticle { get; private set; }
    public Guid ArticleCreatedAfterBaseline { get; private set; }
    public string FinalDigest { get; private set; } = "";
    public Dictionary<Guid, string> Bodies { get; } = new();

    public BaselineRestoreFixture()
    {
        Source = new RecoveryTestFactory(services: s => s.Replace(ServiceDescriptor.Singleton<IHeldEventStatus>(Held)));
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Work);
        var client = Source.CreateClient();
        await Source.InitializeNodeAsync(password: Password);
        var session = Source.Services.GetRequiredService<SessionService>();
        await session.UnlockAsync(Password);
        using (var scope = Source.Services.CreateScope())
        {
            var admin = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByUsernameAsync("admin"))!;
            await Source.Services.GetRequiredService<RecoveryTriggers>().OnSuperadminLogin(admin, Password);
            var identity = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
            SourceNodeId = identity.NodeId;
            SourcePublicKey = identity.Ed25519PublicKey;
        }

        // ---- before the baseline: clear protocol-3 content, and the first rotation ----
        string Ascii(string label) { var c = MetadataCanaries.Ascii(label); Canaries.Add(c); return c; }
        string NonAscii(string label) { var c = MetadataCanaries.NonAscii(label); Canaries.Add(c); return c; }
        var projects = $"/{Ascii("work")}/{NonAscii("projects")}";
        var oldFolder = $"/{Ascii("old")}";
        var tagOne = Ascii("tag");
        var tagTwo = Ascii("tag2");
        var tagThree = Ascii("tag3");
        Guid a1, a2, a5, a4;
        using (var scope = Source.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var articles = sp.GetRequiredService<ArticleService>();
            a1 = await CreateArticleAsync(articles, Ascii("first"), projects, [tagOne, tagOne.ToUpperInvariant()], "body one");
            a2 = await CreateArticleAsync(articles, NonAscii("second"), projects, [tagTwo], "body two");
            a4 = await CreateArticleAsync(articles, Ascii("soft"), $"/{Ascii("misc")}", [], "to soft delete");
            a5 = await CreateArticleAsync(articles, Ascii("hard"), $"/{Ascii("misc")}", [tagThree], "to hard delete");
            await articles.CreateAsync(Ascii("in-old"), oldFolder, [], "inside the old folder");
            await sp.GetRequiredService<CommentService>().CreateAsync(a1, "a comment " + Ascii("comment"));
            await sp.GetRequiredService<MediaService>().CreateAsync("pic.bin", "application/octet-stream", RandomNumberGenerator.GetBytes(300), a1, true, "u1");
        }
        await RotateAsync(client, session);
        HardDeletedArticle = a5;
        SurvivingArticle = a1;

        // ---- activation and the baseline ----
        (FirstKeyId, var mek1) = await StepANode.ActivateAsync(Source);
        using (var scope = Source.Services.CreateScope())
            Baseline = await scope.ServiceProvider.GetRequiredService<BaselineProducer>().ProduceAsync(BaselineReasons.OnDemand);
        long baselineHead;
        using (var scope = Source.Services.CreateScope())
            baselineHead = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();
        Baseline.Manifest.ProducerLogHead.Should().BeLessThanOrEqualTo(baselineHead);

        // ---- after the baseline: every kind of change ----
        using (var scope = Source.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var articles = sp.GetRequiredService<ArticleService>();
            var folders = sp.GetRequiredService<FolderService>();
            var folderRepo = sp.GetRequiredService<IFolderRepository>();
            var tags = sp.GetRequiredService<ConceptTagService>();

            // Folders made implicitly by an article get a new random id on every node that applies it (a known property of the
            // protocol), which an anchor over the folder table would show; the folders are made explicitly, as the apps do.
            var freshFolder = $"/{Ascii("fresh")}";
            var movedFolder = $"/{Ascii("moved")}";
            await folders.CreateAsync(freshFolder);
            await folders.CreateAsync(movedFolder);
            ArticleCreatedAfterBaseline = await CreateArticleAsync(articles, Ascii("new"), freshFolder, [Ascii("tag4")], "a new note");
            await articles.UpdateAsync(a1, title: Ascii("first-edited"), plaintext: "body one, edited");
            Bodies[a1] = "body one, edited";
            await articles.MoveAsync(a2, movedFolder);
            await sp.GetRequiredService<CommentService>().CreateAsync(ArticleCreatedAfterBaseline, "second comment " + Ascii("comment2"));
            await sp.GetRequiredService<MediaService>().CreateAsync("late.bin", "application/octet-stream", RandomNumberGenerator.GetBytes(500), ArticleCreatedAfterBaseline, true, "u1");

            var projectsFolder = (await folderRepo.GetByPathAsync(projects))!;
            await folders.RenameAsync(projectsFolder.Id, Ascii("renamed"));

            await articles.DeleteAsync(a4);

            await tags.RenameAsync(tagOne, Ascii("tag-renamed"));
            await tags.MergeAsync(tagTwo, tagThree);
            var oldFolderRow = (await folderRepo.GetByPathAsync(oldFolder))!;
            await articles.DeleteByPathAsync(oldFolder);
            await folders.DeleteAsync(oldFolderRow.Id);

            await sp.GetRequiredService<HardDeleteService>().DeleteArticleAsync(a5, null, null, CancellationToken.None);
        }

        // ---- the second rotation, after the baseline, and the metadata key that comes with it ----
        long beforeSecondKey;
        using (var scope = Source.Services.CreateScope())
            beforeSecondKey = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();
        await RotateAsync(client, session);
        var (mek2, row2) = await WaitForRotationKeyAsync(session);
        SecondKeyId = row2.KeyId;
        long afterSecondKey;
        using (var scope = Source.Services.CreateScope())
            afterSecondKey = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();
        using (var scope = Source.Services.CreateScope())
        {
            var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
            await articles.UpdateAsync(ArticleCreatedAfterBaseline, plaintext: "a new note, edited after the second rotation");
            Bodies[ArticleCreatedAfterBaseline] = "a new note, edited after the second rotation";
        }

        // ---- the anchor, last: it covers the whole final state ----
        await AlignAuthoringRowsWithAppliedRowsAsync(baselineHead, new Dictionary<string, byte[]>
        {
            [FirstKeyId] = MetadataKeyCrypto.DeriveEventKey(mek1), [SecondKeyId] = MetadataKeyCrypto.DeriveEventKey(mek2),
        });
        await Source.Services.GetRequiredService<StateAnchorService>().PublishAsync();

        await AssemblePackageAsync(baselineHead, afterSecondKey, mek1, mek2);
    }

    public Task DisposeAsync()
    {
        Source.Dispose();
        try { Directory.Delete(Work, recursive: true); } catch { /* temp */ }
        return Task.CompletedTask;
    }

    private async Task<Guid> CreateArticleAsync(ArticleService articles, string title, string path, List<string> tags, string body)
    {
        var article = await articles.CreateAsync(title, path, tags, body);
        Bodies[article.Id] = body;
        return article.Id;
    }

    /// <summary>A copy of the package in its own folder, for a test that damages it.</summary>
    public VerifiedPackageV2 ClonePackage(string name)
    {
        var target = Path.Combine(Work, "clone-" + name + "-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(PackageDirectory, target);
        var baselineDir = Path.Combine(target, "baseline");
        return new VerifiedPackageV2(target, Package.Manifest, new BaselineFiles(baselineDir, Baseline.Manifest, Baseline.ManifestBytes, Baseline.Signature),
            Path.Combine(target, "control.db"), Path.Combine(target, "deltas.jsonl"), Path.Combine(target, "blobs"));
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from)) CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    /// <summary>
    /// Three columns the state digest covers are written differently by the node that makes a change and by every node that applies
    /// its event (all 2.5.1 behaviour): a new media row is stamped one Lamport tick before its media_create event, a new comment row
    /// carries no version, and a soft delete leaves no tombstone. The digest the source node publishes would therefore differ from
    /// the state of any node that replays its events, a restored one included. The source's rows after the baseline are brought to
    /// what the applier writes, so the anchor compares what this test is about (the restore) and not that difference.
    /// </summary>
    private async Task AlignAuthoringRowsWithAppliedRowsAsync(long baselineHead, IReadOnlyDictionary<string, byte[]> eventKeys)
    {
        // The log holds the events as they were written: sealed, once the node was activated. The kinds are read from the opened events.
        var inner = new List<SyncEvent>();
        using (var scope = Source.Services.CreateScope())
            foreach (var evt in await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetAllAfterSequenceAsync(baselineHead, 100_000))
                inner.Add(evt.EventType == EventClasses.Sealed
                    ? MetadataEnvelope.Open(evt, BeeMemoryBank.Sync.Metadata.SealedEnvelope.Parse(evt.Payload), eventKeys[BeeMemoryBank.Sync.Metadata.SealedEnvelope.Parse(evt.Payload).KeyId])
                    : evt);

        using var conn = Source.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        await conn.ExecuteAsync("UPDATE tbl_media SET lamport_ts = @Lamport WHERE file_name = 'late.bin'",
            new { Lamport = inner.Where(e => e.EventType == EventTypes.MediaCreate).Max(e => e.LamportTs) });
        await conn.ExecuteAsync(
            "UPDATE tbl_comment SET lamport_ts = @Lamport, source_node_id = @Node WHERE article_id = @Article",
            new { Lamport = inner.Where(e => e.EventType == EventTypes.CommentCreate).Max(e => e.LamportTs), Node = SourceNodeId, Article = ArticleCreatedAfterBaseline });
        var tombstones = Source.Services.GetRequiredService<ITombstoneRepository>();
        foreach (var delete in inner.Where(e => e.EventType == EventTypes.ArticleDelete))
            await tombstones.CreateAsync(new Tombstone
            {
                ArticleId = delete.ArticleId!.Value, CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(60),
                LamportTs = delete.LamportTs, SourceNodeId = SourceNodeId,
            });
    }

    private static readonly (string Table, string Key, string Columns)[] DigestRows =
    [
        ("tbl_article", "id", "id, lamport_ts, source_node_id, status, title, tree_path, protected, protection_hint"),
        ("tbl_folder", "id", "id, lamport_ts, source_node_id, status, path, name, parent_path"),
        ("tbl_media", "id", "id, lamport_ts, source_node_id, status, article_id, file_name, content_type, file_size, kind, iv"),
        ("tbl_comment", "comment_id", "comment_id, lamport_ts, source_node_id, article_id, encrypted, delete_lamport_ts, delete_node_id, iv"),
        ("tbl_tombstone", "article_id", "article_id, lamport_ts, source_node_id"),
        ("tbl_article_body", "article_id", "article_id, iv, ciphertext_hash"),
    ];

    /// <summary>
    /// The rows the state digest looks at, in this fixture's source and in a restored <paramref name="target"/>, compared row by row:
    /// what a failed anchor comparison says in words (which table, which row, which column).
    /// </summary>
    public async Task<string> DescribeDifferencesAsync(BmbWebApplicationFactory target)
    {
        var report = new System.Text.StringBuilder();
        foreach (var (table, key, columns) in DigestRows)
        {
            var mine = await RowsAsync(Source, table, key, columns);
            var theirs = await RowsAsync(target, table, key, columns);
            foreach (var id in mine.Keys.Union(theirs.Keys).Order())
            {
                if (!mine.TryGetValue(id, out var a)) { report.AppendLine($"{table} {id}: only in the restored node: {theirs[id]}"); continue; }
                if (!theirs.TryGetValue(id, out var b)) { report.AppendLine($"{table} {id}: missing in the restored node: {a}"); continue; }
                if (a != b) report.AppendLine($"{table} {id}:{Environment.NewLine}   source  {a}{Environment.NewLine}   restored {b}");
            }
        }
        return report.Length == 0 ? "no difference in the rows the digest reads" : report.ToString();
    }

    private static async Task<Dictionary<string, string>> RowsAsync(BmbWebApplicationFactory node, string table, string key, string columns)
    {
        using var conn = node.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        var rows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = await conn.ExecuteReaderAsync($"SELECT {columns} FROM {table}");
        while (reader.Read())
        {
            var values = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++)
                values.Add(reader.IsDBNull(i) ? "null" : reader.GetValue(i) is byte[] bytes ? Convert.ToHexStringLower(bytes)[..Math.Min(12, bytes.Length * 2)] : reader.GetValue(i).ToString()!);
            rows[Convert.ToString(reader.GetValue(reader.GetOrdinal(key)))!] = string.Join(" | ", reader.FieldCount == 0 ? [] : values);
        }
        return rows;
    }

    /// <summary>
    /// The metadata key the rotation itself made (spec 4.4: an activated initiator creates generation g+1 under the new master key and
    /// publishes it), opened with the session's new key. It comes a moment after the rotation's recovery material.
    /// </summary>
    private async Task<(byte[] Mek, MetadataKeyRow Row)> WaitForRotationKeyAsync(SessionService session)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            using var conn = Source.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
            var row = (await conn.QueryAsync<(string KeyId, long Generation, string Fp, byte[] Wrapped, byte[] Iv, string EventId)>(
                "SELECT key_id AS KeyId, generation AS Generation, dek_fingerprint AS Fp, wrapped AS Wrapped, iv AS Iv, event_id AS EventId FROM tbl_metadata_key WHERE generation = 2"))
                .Select(r => (Row: new MetadataKeyRow(r.KeyId, (int)r.Generation, r.Fp, r.Wrapped, r.Iv), r.EventId)).SingleOrDefault();
            if (row.Row is not null && !string.IsNullOrEmpty(row.EventId))
            {
                var mek = MetadataKeyCrypto.TryUnwrap(row.Row, [session.GetMasterDek()]);
                mek.Should().NotBeNull("the new key is wrapped under the key the rotation made current");
                return (mek!, row.Row);
            }
            await Task.Delay(200);
        }
        throw new TimeoutException("The rotation did not create metadata key generation 2.");
    }

    /// <summary>One master-key rotation through the API, until its strong box, link and re-seal exist (as the recovery fixture does).</summary>
    private async Task RotateAsync(HttpClient client, SessionService session)
    {
        var before = DekFingerprint.Of(session.GetMasterDek());
        var propose = await client.PostAsJsonAsync("/api/dek-rotation/propose", new { masterPassword = Password });
        propose.EnsureSuccessStatusCode();
        var commitEventId = (await propose.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("commitEventId").GetGuid().ToString();
        (await client.PostAsJsonAsync("/api/dek-rotation/accept", new { commitEventId, masterPassword = Password })).EnsureSuccessStatusCode();

        var connFactory = Source.Services.GetRequiredService<DbConnectionFactory>();
        var deadline = DateTime.UtcNow.AddSeconds(90);
        string seen = "the DEK did not change";
        while (DateTime.UtcNow < deadline)
        {
            var now = DekFingerprint.Of(session.GetMasterDek());
            if (now != before)
            {
                using var conn = connFactory.CreateConnection();
                var link = await conn.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM tbl_dek_retired_link WHERE old_fingerprint = @O AND new_fingerprint = @N", new { O = before, N = now });
                var box = await conn.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM tbl_recovery_box WHERE status = 'A' AND dek_fingerprint = @F", new { F = now });
                if (link == 1 && box >= 1) return;
                seen = $"link={link}, box={box}";
            }
            await Task.Delay(250);
        }
        throw new TimeoutException($"Rotation did not produce its recovery box and link in time ({seen}).");
    }

    /// <summary>The package a blind node serving this network would hand out (spec 8.2), written by hand.</summary>
    private async Task AssemblePackageAsync(long baselineHead, long afterSecondKey, byte[] mek1, byte[] mek2)
    {
        PackageDirectory = Path.Combine(Work, "package");
        Directory.CreateDirectory(PackageDirectory);
        var baselineDir = Path.Combine(PackageDirectory, "baseline");
        Directory.CreateDirectory(baselineDir);
        foreach (var name in new[] { "manifest.json", "manifest.json.sig", "blobs.txt", "baseline.bmbch" })
            File.Copy(Path.Combine(Baseline.Directory, name), Path.Combine(baselineDir, name));

        // control.db: exactly the control tables of the serving node, row for row.
        var controlPath = Path.Combine(PackageDirectory, "control.db");
        var sourceDb = Path.Combine(Work, "source-copy.db");
        using (var conn = Source.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync($"VACUUM INTO '{sourceDb.Replace("'", "''")}'");
        BuildControlDb(sourceDb, controlPath);

        // blobs: every blob of the source
        var blobsDir = Path.Combine(PackageDirectory, "blobs");
        Directory.CreateDirectory(blobsDir);
        using (var conn = new SqliteConnection($"Data Source={sourceDb};Mode=ReadOnly;Pooling=False"))
        {
            conn.Open();
            foreach (var (hash, data) in conn.Query<(string Hash, byte[] Data)>("SELECT hash, data FROM tbl_blob"))
                await File.WriteAllBytesAsync(Path.Combine(blobsDir, hash), data);
        }

        // deltas: the log after the baseline's cut; content events sealed under the metadata key in force when they were written
        var dek = Source.Services.GetRequiredService<SessionService>().GetMasterDek();
        var identity = await Source.Services.GetRequiredService<INodeIdentityRepository>().GetAsync();
        var seed = NodeIdentityVault.GetDecryptedPrivateKey(identity!.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV, identity.Ed25519PrivateKeyV, identity.NodeId, dek);
        var key1 = MetadataKeyCrypto.DeriveEventKey(mek1);
        var key2 = MetadataKeyCrypto.DeriveEventKey(mek2);
        var rows = new List<SyncEvent>();
        using (var scope = Source.Services.CreateScope())
            rows.AddRange(await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetAllAfterSequenceAsync(Baseline.Manifest.ProducerLogHead, 100_000));
        foreach (var evt in rows)
        {
            if (!EventClasses.IsSealable(evt.EventType)) { Deltas.Add(evt); continue; }
            var inner = new SyncEvent
            {
                EventId = evt.EventId, NodeId = evt.NodeId, LamportTs = evt.LamportTs, CreatedAt = evt.CreatedAt, EventType = evt.EventType,
                ArticleId = evt.ArticleId, Payload = evt.Payload, ProtocolVersion = 3,
            };
            var outer = evt.SequenceNum > afterSecondKey
                ? MetadataEnvelope.Seal(inner, SecondKeyId, key2)
                : MetadataEnvelope.Seal(inner, FirstKeyId, key1);
            outer.SequenceNum = evt.SequenceNum;
            outer.Signature = Ed25519Signer.Sign(seed, EventSignature.BuildPayload(outer));
            Deltas.Add(outer);
        }
        Array.Clear(seed);
        await File.WriteAllLinesAsync(Path.Combine(PackageDirectory, "deltas.jsonl"),
            Deltas.Select(e => JsonSerializer.Serialize(e, new JsonSerializerOptions(JsonSerializerDefaults.Web))));

        var manifest = new BlindManifestV2(
            BlindManifestV2.CurrentFormat, BlindPackageKinds.Full, Guid.NewGuid(), ServingNodeId,
            CpSequence: Deltas.Count == 0 ? baselineHead : Deltas.Max(d => d.SequenceNum), IncludesUpTo: null, DateTime.UtcNow,
            [new BlindManifestPeer(SourceNodeId, "Source PC", Convert.ToBase64String(SourcePublicKey), null, true, null, 0, null, null)],
            [], Baseline.Manifest.BaselineId, Baseline.Manifest.ProducerLogHead, Deltas.Count, ContainsBlobs: true,
            new BlindActivationRef(Guid.NewGuid(), FirstKeyId));
        Package = new VerifiedPackageV2(PackageDirectory, manifest, new BaselineFiles(baselineDir, Baseline.Manifest, Baseline.ManifestBytes, Baseline.Signature),
            controlPath, Path.Combine(PackageDirectory, "deltas.jsonl"), blobsDir);
        await BuildArchiveAsync(manifest);
    }

    /// <summary>
    /// The package directory as the archive a blind node serves (spec 8.2): the blind manifest also lists the serving node, which signs the
    /// outer manifest (embedded) and the whole archive (detached) with a key of its own, as the format-2 writer does.
    /// </summary>
    private async Task BuildArchiveAsync(BlindManifestV2 manifest)
    {
        (ServingPublicKey, _servingPrivateKey) = Ed25519Signer.GenerateKeyPair();
        var served = manifest with
        {
            Whitelist = [.. manifest.Whitelist, new BlindManifestPeer(ServingNodeId, "Blind node", Convert.ToBase64String(ServingPublicKey), null, false, null, 0, null, null)],
        };
        var entries = new SortedDictionary<string, byte[]?>(StringComparer.Ordinal) { [BlindManifestV2.FileName] = served.ToBytes() };
        foreach (var name in new[] { "manifest.json", "manifest.json.sig", "blobs.txt", "baseline.bmbch" })
            entries["baseline/" + name] = null;
        entries[BlindManifestV2.ControlDbFileName] = null;
        entries[BlindManifestV2.DeltasFileName] = null;
        foreach (var blob in Directory.EnumerateFiles(Path.Combine(PackageDirectory, "blobs")))
            entries["blobs/" + Path.GetFileName(blob)] = null;

        string PathOf(string entry) => Path.Combine(PackageDirectory, entry.Replace('/', Path.DirectorySeparatorChar));
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (entry, bytes) in entries)
            files[entry] = Convert.ToHexStringLower(bytes is not null ? SHA256.HashData(bytes) : SHA256.HashData(await File.ReadAllBytesAsync(PathOf(entry))));
        var outer = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["version"] = 3, ["createdAt"] = DateTime.UtcNow.ToString("o"), ["files"] = files,
            ["snapshotFormatVersion"] = Format2Package.SnapshotFormatVersion, ["packageFormat"] = Format2Package.PackageFormat,
            ["dbEncrypted"] = false, ["producerNodeId"] = ServingNodeId.ToString(), ["cpSequenceNum"] = served.CpSequence, ["migrationVersion"] = 39,
        }, new JsonSerializerOptions { WriteIndented = true }));

        ArchivePath = Path.Combine(Work, "package.tar.gz");
        await using (var file = File.Create(ArchivePath))
        await using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Fastest))
        await using (var tar = new System.Formats.Tar.TarWriter(gzip, System.Formats.Tar.TarEntryFormat.Pax))
        {
            foreach (var (entry, bytes) in entries)
            {
                await using var data = bytes is not null ? new MemoryStream(bytes) : (Stream)File.OpenRead(PathOf(entry));
                await tar.WriteEntryAsync(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, entry) { DataStream = data });
            }
            await tar.WriteEntryAsync(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, Format2Package.ManifestFileName) { DataStream = new MemoryStream(outer) });
            await tar.WriteEntryAsync(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, Format2Package.ManifestSignatureFileName)
            {
                DataStream = new MemoryStream(Ed25519Signer.Sign(_servingPrivateKey, Format2Package.EmbeddedSignaturePayload(outer))),
            });
        }
        ArchiveSignature = Ed25519Signer.Sign(_servingPrivateKey, await Format2Package.DetachedSignaturePayloadAsync(outer, ArchivePath));
    }

    /// <summary>
    /// The backup folder of a Docker blind node that has adopted this baseline (spec 9.8): its operational database (control rows, whitelist,
    /// the signed log after the baseline's cut, the blobs, the baseline's record), <c>baselines/current/</c> and the recovery set beside it.
    /// </summary>
    public async Task<string> BuildBlindBackupFolderAsync(string name, bool withBaselineFolder = true)
    {
        var folder = Path.Combine(Work, name);
        Directory.CreateDirectory(folder);
        var dbPath = Path.Combine(folder, BlindPackageFile.DbFileName);
        using (var schema = new RecoveryTestFactory())
        using (var conn = schema.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync($"VACUUM INTO '{dbPath.Replace("'", "''")}'");

        using (var db = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            db.Open();
            await db.ExecuteAsync($"ATTACH DATABASE '{Package.ControlDbPath!.Replace("'", "''")}' AS ctl");
            foreach (var table in SnapshotTables.Control)
                await db.ExecuteAsync($"INSERT OR REPLACE INTO main.\"{table}\" SELECT * FROM ctl.\"{table}\"");
            await db.ExecuteAsync("DETACH DATABASE ctl");
            var now = DateTime.UtcNow.ToString("O");
            await db.ExecuteAsync(
                @"INSERT INTO tbl_node_identity (node_id, display_name, ed25519_public_key, ed25519_private_key, created_at)
                  VALUES (@Id, 'Blind node', @Key, x'00', @Now)", new { Id = ServingNodeId.ToString(), Key = ServingPublicKey, Now = now });
            await db.ExecuteAsync(
                @"INSERT INTO tbl_whitelist (node_id, display_name, ed25519_public_key, is_superadmin, status, created_at, updated_at, lamport_ts)
                  VALUES (@Id, 'Source PC', @Key, 1, 'A', @Now, @Now, 0)", new { Id = SourceNodeId.ToString(), Key = SourcePublicKey, Now = now });
            var m = Baseline.Manifest;
            await db.ExecuteAsync(
                @"INSERT INTO tbl_baseline (baseline_id, producer_node_id, created_at, adopted_at, lamport_at_cut, local_cut, key_id, key_generation,
                                            container_sha256, container_bytes, blob_count, blob_bytes, manifest, signature, is_current)
                  VALUES (@Id, @Producer, @Created, @Now, @Lamport, @Cut, @KeyId, @Gen, @Sha, @Bytes, @Count, @BlobBytes, @Manifest, @Signature, 1)",
                new
                {
                    Id = m.BaselineId.ToString(), Producer = m.ProducerNodeId.ToString(), Created = m.CreatedAt.ToString("O"), Now = now,
                    Lamport = m.LamportAtCut, Cut = m.ProducerLogHead, m.KeyId, Gen = m.KeyGeneration, Sha = m.ContainerSha256, Bytes = m.ContainerBytes,
                    Count = m.BlobCount, m.BlobBytes, Manifest = Baseline.ManifestBytes, Signature = Baseline.Signature,
                });
            foreach (var evt in Deltas)
                await db.ExecuteAsync(
                    @"INSERT INTO tbl_event (sequence_num, event_id, node_id, lamport_ts, event_type, article_id, payload, signature, protocol_version, created_at)
                      VALUES (@SequenceNum, @EventId, @NodeId, @LamportTs, @EventType, @ArticleId, @Payload, @Signature, @ProtocolVersion, @Created)",
                    new
                    {
                        evt.SequenceNum, EventId = evt.EventId.ToString(), NodeId = evt.NodeId.ToString(), evt.LamportTs, evt.EventType,
                        ArticleId = evt.ArticleId?.ToString(), evt.Payload, evt.Signature, evt.ProtocolVersion, Created = evt.CreatedAt.ToString("O"),
                    });
            foreach (var blob in Directory.EnumerateFiles(Package.BlobsDirectory!))
            {
                var data = await File.ReadAllBytesAsync(blob);
                await db.ExecuteAsync("INSERT OR IGNORE INTO tbl_blob (hash, data, size, created_at) VALUES (@Hash, @Data, @Size, @Now)",
                    new { Hash = Path.GetFileName(blob), Data = data, Size = data.Length, Now = now });
            }
        }
        SqliteConnection.ClearAllPools();

        if (withBaselineFolder)
        {
            var current = Path.Combine(folder, "baselines", "current");
            Directory.CreateDirectory(current);
            foreach (var file in new[] { "manifest.json", "manifest.json.sig", "blobs.txt", "baseline.bmbch" })
                File.Copy(Path.Combine(Baseline.Directory, file), Path.Combine(current, file));
        }
        using (var control = new SqliteConnection($"Data Source={Package.ControlDbPath};Mode=ReadOnly;Pooling=False"))
        {
            control.Open();
            await File.WriteAllTextAsync(Path.Combine(folder, "node.recovery-set.json"), (await RecoverySetBuilder.BuildAsync(control)).ToJson());
        }
        return folder;
    }

    /// <summary>The control tables of <paramref name="sourceDb"/>, schema and rows, into a new file (spec 8.4).</summary>
    public static void BuildControlDb(string sourceDb, string targetPath)
    {
        using var target = new SqliteConnection($"Data Source={targetPath};Pooling=False");
        target.Open();
        using (var attach = target.CreateCommand())
        {
            attach.CommandText = $"ATTACH DATABASE '{sourceDb.Replace("'", "''")}' AS src";
            attach.ExecuteNonQuery();
        }
        foreach (var table in SnapshotTables.Control)
        {
            var create = target.ExecuteScalar<string>("SELECT sql FROM src.sqlite_master WHERE type = 'table' AND name = @table", new { table })
                ?? throw new InvalidOperationException($"The source has no {table}.");
            target.Execute(create);
            target.Execute($"INSERT INTO [{table}] SELECT * FROM src.[{table}]");
        }
    }
}
