using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Vault.Metadata;
using Dapper;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// An activated full node in test form (docs/design/step-a-spec.md 3, 7, 8) for the blind-host tests of track 2: its own
/// database on disk, a legacy v=0 identity whose key the test holds, a metadata key it seals with (real envelope crypto),
/// a log of signed events, hand-made baselines it signs as their producer, and the library's format-2 writer. The blind
/// host under test only ever sees what such a node would send it.
/// </summary>
public sealed class StepASeeder : IDisposable
{
    public string DataPath { get; }
    public DbConnectionFactory Factory { get; }
    public NodeIdentity Identity { get; }
    public Guid NodeId => Identity.NodeId;
    public byte[] PublicKey => Identity.Ed25519PublicKey;
    private readonly byte[] _seed;
    private long _lamport = 100;
    private byte[] _mek = [];
    public MetadataKeyRow? KeyRow { get; private set; }

    private StepASeeder(string dataPath, DbConnectionFactory factory, NodeIdentity identity, byte[] seed)
    {
        DataPath = dataPath;
        Factory = factory;
        Identity = identity;
        _seed = seed;
    }

    public static async Task<StepASeeder> CreateAsync(string name = "PC")
    {
        DapperConfig.Configure();
        var dir = Path.Combine(Path.GetTempPath(), "bmb_seeder_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var factory = new DbConnectionFactory(Path.Combine(dir, "beememorybank.db"));
        await new MigrationRunner(factory).RunMigrationsAsync();
        var (pub, seed) = Ed25519Signer.GenerateKeyPair();
        var identity = new NodeIdentity
        {
            NodeId = Guid.NewGuid(), DisplayName = name, Ed25519PublicKey = pub, Ed25519PrivateKey = seed,
            Ed25519PrivateKeyV = 0, CreatedAt = DateTime.UtcNow, InitialSyncCompleted = true,
        };
        await new NodeIdentityRepository(factory).CreateAsync(identity);
        return new StepASeeder(dir, factory, identity, seed);
    }

    public EventLogRepository Events => new(Factory);

    public BlindPackageV2Writer Writer() =>
        new(Factory, new NodeIdentityRepository(Factory), new WhitelistRepository(Factory), new SyncPositionRepository(Factory),
            new EventLogRepository(Factory),
            new BlindPackageV2WriterOptions((_, payload) => Ed25519Signer.Sign(_seed, payload),
                Path.Combine(DataPath, "packages"), Path.Combine(DataPath, "staging"), _ => Task.FromResult(true)));

    public Task<BlindPackageV2> WriteAsync(string kind, ServableBaseline baseline, bool blobs = true, Guid? seedId = null) =>
        Writer().WriteAsync(new BlindPackageV2Request(kind, seedId ?? Guid.NewGuid(), baseline, null,
            blobs && kind == BlindPackageKinds.Full));

    /// <summary>
    /// Activates this node as the activation would (spec 4.1, 5.4): a generation-1 key under a DEK only it has, its
    /// metadata_key_set and metadata_activation in its log and their rows in its tables. Returns the two events.
    /// </summary>
    public async Task<(SyncEvent KeySet, SyncEvent Activation)> ActivateAsync()
    {
        var (mek, row) = MetadataKeyCrypto.Create(1, RandomNumberGenerator.GetBytes(32));
        (_mek, KeyRow) = (mek, row);
        var keySet = Sign(new SyncEvent
        {
            EventType = EventTypes.MetadataKeySet, ProtocolVersion = 3,
            Payload = JsonSerializer.Serialize(new MetadataKeySetPayload(row.KeyId, row.Generation, row.DekFingerprint, 0,
                Convert.ToBase64String(row.Wrapped), Convert.ToBase64String(row.Iv), DateTime.UtcNow.ToString("O"))),
        });
        var activationId = Guid.NewGuid();
        var activation = Sign(new SyncEvent
        {
            EventType = EventTypes.MetadataActivation, ProtocolVersion = 3,
            Payload = JsonSerializer.Serialize(new MetadataActivationPayload(activationId.ToString(), row.KeyId, 1, DateTime.UtcNow.ToString("O"))),
        });
        await Events.AppendAsync(keySet);
        await Events.AppendAsync(activation);
        using var conn = Factory.CreateConnection();
        var now = DateTime.UtcNow.ToString("O");
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_metadata_key (key_id, generation, dek_fingerprint, epoch_hint, wrapped, iv, author_node_id, created_at, lamport_ts, event_id)
              VALUES (@KeyId, 1, @DekFingerprint, 0, @Wrapped, @Iv, @author, @now, @lamport, @eventId)",
            new { row.KeyId, row.DekFingerprint, row.Wrapped, row.Iv, author = NodeId.ToString(), now, lamport = keySet.LamportTs, eventId = keySet.EventId.ToString() });
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_metadata_activation (activation_id, key_id, generation, activated_at, author_node_id, event_id, lamport_ts, applied_at)
              VALUES (@id, @KeyId, 1, @now, @author, @eventId, @lamport, @now)",
            new { id = activationId.ToString(), row.KeyId, now, author = NodeId.ToString(), eventId = activation.EventId.ToString(), lamport = activation.LamportTs });
        return (keySet, activation);
    }

    /// <summary>A content event in clear (protocol 3), signed and appended to this node's log.</summary>
    public async Task<SyncEvent> ClearAsync(string type, Guid? articleId, string payload)
    {
        var evt = Sign(new SyncEvent { EventType = type, ArticleId = articleId, Payload = payload, ProtocolVersion = 3 });
        await Events.AppendAsync(evt);
        return evt;
    }

    /// <summary>A content event sealed under this node's key (spec 3.2-3.5), signed as the outer event; appended unless told not to.</summary>
    public async Task<SyncEvent> SealedAsync(string type, Guid? articleId, string payload, bool append = true)
    {
        var inner = Stamp(new SyncEvent { EventType = type, ArticleId = articleId, Payload = payload, ProtocolVersion = 3 });
        var eventKey = MetadataKeyCrypto.DeriveEventKey(_mek);
        try
        {
            var outer = MetadataEnvelope.Seal(inner, KeyRow!.KeyId, eventKey);
            outer.Signature = Ed25519Signer.Sign(_seed, EventSignature.BuildPayload(outer));
            if (append) await Events.AppendAsync(outer);
            return outer;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(eventKey);
        }
    }

    /// <summary>
    /// A container with the framing of spec 7.1 (magic, header, length-prefixed chunks) around random bytes: what the blind side
    /// can check without a key.
    /// </summary>
    public static byte[] OpaqueContainer(string keyId, int chunk = 4096, long plaintextBytes = 6000)
    {
        var header = System.Text.Encoding.UTF8.GetBytes(
            $"{{\"v\":1,\"purpose\":\"baseline\",\"key\":\"mek:{keyId}\",\"salt\":\"{Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))}\"," +
            $"\"nonce_prefix\":\"{Convert.ToBase64String(RandomNumberGenerator.GetBytes(7))}\",\"chunk\":{chunk}}}");
        using var output = new MemoryStream();
        output.Write(System.Text.Encoding.ASCII.GetBytes("BMBCH1").Concat(new byte[2]).ToArray());
        output.Write(BitConverter.GetBytes((uint)header.Length));
        output.Write(header);
        var chunks = Math.Max(1, (plaintextBytes + chunk - 1) / chunk);
        for (long i = 0; i < chunks; i++)
        {
            var plain = i < chunks - 1 ? chunk : plaintextBytes - (chunks - 1) * chunk;
            output.Write(BitConverter.GetBytes((uint)(plain + 16)));
            output.Write(RandomNumberGenerator.GetBytes((int)plain + 16));
        }
        return output.ToArray();
    }

    /// <summary>A baseline produced and signed by this node (random container bytes, as ciphertext would be).</summary>
    public BaselineFiles WriteBaseline(string directory, IEnumerable<string> blobs, long lamportAtCut, int generation = 1,
        IReadOnlyList<BlindManifestPosition>? pullPositions = null)
    {
        Directory.CreateDirectory(directory);
        var container = OpaqueContainer(KeyRow?.KeyId ?? new string('a', 32));
        var blobList = Format2Package.FormatBlobList(blobs);
        File.WriteAllBytes(Path.Combine(directory, BaselineManifest.ContainerFileName), container);
        File.WriteAllBytes(Path.Combine(directory, BaselineManifest.BlobListFileName), blobList);
        var manifest = new BaselineManifest(BaselineManifest.CurrentFormat, Guid.NewGuid(), NodeId, DateTime.UtcNow,
            ProducerLogHead: 0, lamportAtCut, pullPositions ?? [], KeyRow?.KeyId ?? new string('a', 32), generation, SchemaVersion: 39,
            Convert.ToHexStringLower(SHA256.HashData(container)), container.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(blobList)), Format2Package.ParseBlobList(blobList).Count, 0);
        var bytes = manifest.ToBytes();
        var signature = Ed25519Signer.Sign(_seed, BaselineManifest.SignaturePayload(bytes));
        File.WriteAllBytes(Path.Combine(directory, BaselineManifest.FileName), bytes);
        File.WriteAllBytes(Path.Combine(directory, BaselineManifest.SignatureFileName), signature);
        return new BaselineFiles(directory, manifest, bytes, signature);
    }

    /// <summary>
    /// The package with some entries changed, as a serving node that does not check its own output would send it: the outer
    /// manifest lists the new hashes and both signatures are made again with this node's key, so only the content is wrong.
    /// </summary>
    public async Task<BlindPackageV2> ResignAsync(BlindPackageV2 package, Func<List<(string Name, byte[] Data)>, List<(string Name, byte[] Data)>> change)
    {
        var entries = new List<(string Name, byte[] Data)>();
        using (var file = File.OpenRead(package.FilePath))
        using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress))
        using (var tar = new System.Formats.Tar.TarReader(gzip))
            while (tar.GetNextEntry() is { } entry)
            {
                using var memory = new MemoryStream();
                entry.DataStream?.CopyTo(memory);
                entries.Add((entry.Name, memory.ToArray()));
            }
        var inner = change(entries.Where(e => e.Name is not (Format2Package.ManifestFileName or Format2Package.ManifestSignatureFileName)).ToList());
        var outer = System.Text.Json.Nodes.JsonNode.Parse(entries.Single(e => e.Name == Format2Package.ManifestFileName).Data)!.AsObject();
        var files = new System.Text.Json.Nodes.JsonObject();
        foreach (var (name, data) in inner.OrderBy(e => e.Name, StringComparer.Ordinal))
            files[name] = Convert.ToHexStringLower(SHA256.HashData(data));
        outer["files"] = files;
        var outerBytes = System.Text.Encoding.UTF8.GetBytes(outer.ToJsonString());
        var embedded = Ed25519Signer.Sign(_seed, Format2Package.EmbeddedSignaturePayload(outerBytes));
        var path = Path.Combine(DataPath, "packages", "resigned-" + Guid.NewGuid().ToString("N") + ".tar.gz");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = File.Create(path))
        using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Fastest))
        using (var tar = new System.Formats.Tar.TarWriter(gzip, System.Formats.Tar.TarEntryFormat.Pax))
            foreach (var (name, data) in inner.Append((Format2Package.ManifestFileName, outerBytes)).Append((Format2Package.ManifestSignatureFileName, embedded)))
                tar.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) });
        var detached = Ed25519Signer.Sign(_seed, await Format2Package.DetachedSignaturePayloadAsync(outerBytes, path));
        return package with { FilePath = path, Sha256 = await Format2Package.HashFileAsync(path), Length = new FileInfo(path).Length, DetachedSignature = detached };
    }

    public async Task<string> StoreBlobAsync() => await new BlobRepository(Factory).StoreAsync(RandomNumberGenerator.GetBytes(256));

    public static string FolderCreate(string path, string name) =>
        JsonSerializer.Serialize(new FolderCreatePayload(Guid.NewGuid(), path, name, "/", DateTime.UtcNow, DateTime.UtcNow));

    public static string ArticleCreate(string title, string path, string blobHash, string tag) =>
        JsonSerializer.Serialize(new ArticleEventPayload(title, path, [tag], null,
            Convert.ToBase64String(new byte[12]), Convert.ToBase64String(new byte[60]), Convert.ToBase64String(new byte[12]),
            "A", DateTime.UtcNow, DateTime.UtcNow, CiphertextSha256: blobHash));

    public static string FolderRename(string oldPath, string newPath, string newName) =>
        JsonSerializer.Serialize(new FolderRenamePayload(Guid.NewGuid(), oldPath, newPath, newName, "/", DateTime.UtcNow));

    public static string TagRename(string oldName, string newName) =>
        JsonSerializer.Serialize(new ConceptTagRenamePayload(oldName, newName));

    private SyncEvent Stamp(SyncEvent evt)
    {
        evt.EventId = Guid.NewGuid();
        evt.NodeId = NodeId;
        evt.LamportTs = ++_lamport;
        evt.CreatedAt = DateTime.UtcNow;
        return evt;
    }

    private SyncEvent Sign(SyncEvent evt)
    {
        Stamp(evt);
        evt.Signature = Ed25519Signer.Sign(_seed, EventSignature.BuildPayload(evt));
        return evt;
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_mek);
        Factory.Dispose();
        try { Directory.Delete(DataPath, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Every log line of a host, for the scans of spec 16.3 item 1.</summary>
public sealed class StepALogCapture : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Capture(categoryName, Lines);
    public void Dispose() { }

    private sealed class Capture(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{category} {logLevel} {formatter(state, exception)} {exception}");
    }
}
