using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;
using Dapper;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Hand-made Step A material for the format-2 and blind-store tests (docs/design/step-a-spec.md 7, 8, 9): a baseline signed
/// by a test producer key (its container has the framing of spec 7.1 around random bytes, as ciphertext would), sealed events with a well-formed envelope
/// (the blind side never opens one), and the rows a node holds. Nothing here can encrypt or decrypt: the blind side never does.
/// </summary>
public static class Format2TestKit
{
    public sealed record Producer(Guid NodeId, byte[] PublicKey, byte[] PrivateKey)
    {
        public static Producer Create() =>
            Ed25519Signer.GenerateKeyPair() is var (pub, priv) ? new Producer(Guid.NewGuid(), pub, priv) : throw new InvalidOperationException();

        public Func<Guid, (byte[] PublicKey, bool IsSuperadmin)?> TrustedAs(bool superadmin) =>
            id => id == NodeId ? (PublicKey, superadmin) : null;
    }

    public static readonly string KeyId = new('a', 32);

    /// <summary>
    /// Writes a baseline directory (manifest.json, manifest.json.sig, blobs.txt, baseline.bmbch) signed by
    /// <paramref name="producer"/>. <paramref name="tamper"/> runs after signing (a test that breaks one file).
    /// </summary>
    public static BaselineFiles WriteBaseline(
        string directory, Producer producer, IEnumerable<string>? blobs = null, long lamportAtCut = 100, int keyGeneration = 1,
        IReadOnlyList<BlindManifestPosition>? pullPositions = null, byte[]? container = null, Action<string>? tamper = null,
        DateTime? createdAt = null)
    {
        Directory.CreateDirectory(directory);
        container ??= OpaqueContainer(KeyId);
        var blobList = Format2Package.FormatBlobList(blobs ?? []);
        File.WriteAllBytes(Path.Combine(directory, BaselineManifest.ContainerFileName), container);
        File.WriteAllBytes(Path.Combine(directory, BaselineManifest.BlobListFileName), blobList);
        var manifest = new BaselineManifest(BaselineManifest.CurrentFormat, Guid.NewGuid(), producer.NodeId,
            createdAt ?? DateTime.UtcNow, ProducerLogHead: 1000, LamportAtCut: lamportAtCut, pullPositions ?? [],
            KeyId, keyGeneration, SchemaVersion: 39,
            Convert.ToHexStringLower(SHA256.HashData(container)), container.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(blobList)), Format2Package.ParseBlobList(blobList).Count, BlobBytes: 0);
        var bytes = manifest.ToBytes();
        var signature = Ed25519Signer.Sign(producer.PrivateKey, BaselineManifest.SignaturePayload(bytes));
        File.WriteAllBytes(Path.Combine(directory, BaselineManifest.FileName), bytes);
        File.WriteAllBytes(Path.Combine(directory, BaselineManifest.SignatureFileName), signature);
        tamper?.Invoke(directory);
        return new BaselineFiles(directory, manifest, bytes, signature);
    }

    /// <summary>
    /// A container with the framing of spec 7.1 (magic, header, length-prefixed chunks) around random bytes: what the blind side
    /// can check without a key. <paramref name="plaintextBytes"/> decides how many chunks of <paramref name="chunk"/> it holds.
    /// </summary>
    public static byte[] OpaqueContainer(string keyId, int chunk = 4096, long plaintextBytes = 6000, string purpose = "baseline")
    {
        var header = System.Text.Encoding.UTF8.GetBytes(
            $"{{\"v\":1,\"purpose\":\"{purpose}\",\"key\":\"mek:{keyId}\",\"salt\":\"{Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))}\"," +
            $"\"nonce_prefix\":\"{Convert.ToBase64String(RandomNumberGenerator.GetBytes(7))}\",\"chunk\":{chunk}}}");
        using var output = new MemoryStream();
        output.Write("BMBCH1\0\0"u8);
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

    /// <summary>The names of <see cref="NotAContainer"/>: bytes a baseline must never be, each wrong in one way.</summary>
    public static readonly string[] NotAContainerCases =
    [
        "json text", "zip archive", "random bytes", "wrong magic", "header length zero", "header length too long", "header not json",
        "header unknown property", "purpose join", "another key", "chunk size too small", "no chunks", "truncated chunk",
        "partial length at end", "short chunk before the last", "chunk longer than the header allows",
    ];

    /// <summary>Plaintext of other formats, and containers broken in one way each (spec 7.1): all with a matching size and hash once signed.</summary>
    public static byte[] NotAContainer(string what)
    {
        var good = OpaqueContainer(KeyId, plaintextBytes: 3 * 4096 + 5);
        var headerLength = (int)BitConverter.ToUInt32(good, 8);
        var firstChunk = 12 + headerLength;
        switch (what)
        {
            case "json text": return System.Text.Encoding.UTF8.GetBytes("{\"folders\":[{\"name\":\"" + new string('x', 5000) + "\"}]}");
            case "zip archive": return [0x50, 0x4B, 3, 4, .. RandomNumberGenerator.GetBytes(5000)];
            case "random bytes": return RandomNumberGenerator.GetBytes(5000);
            case "wrong magic": good[5] ^= 1; return good;
            case "header length zero": BitConverter.GetBytes(0u).CopyTo(good, 8); return good;
            case "header length too long": BitConverter.GetBytes(4097u).CopyTo(good, 8); return good;
            case "header not json":
                for (var i = 12; i < firstChunk; i++) good[i] = (byte)'x';
                return good;
            case "header unknown property":
                return Reframe(good, headerLength, h => h.Replace("\"chunk\":4096", "\"chunk\":4096,\"extra\":1"));
            case "purpose join": return OpaqueContainer(KeyId, plaintextBytes: 6000, purpose: "join");
            case "another key": return OpaqueContainer(new string('b', 32));
            case "chunk size too small": return OpaqueContainer(KeyId, chunk: 100, plaintextBytes: 300);
            case "no chunks": return good[..firstChunk];
            case "truncated chunk": return good[..^10];
            case "partial length at end": return [.. good, 1, 2];
            case "short chunk before the last":
            {
                // The first chunk loses a byte and says so: well formed in itself, but a chunk before the last must be full.
                var shortened = (byte[])good.Clone();
                BitConverter.GetBytes(4096u + 15).CopyTo(shortened, firstChunk);
                return [.. shortened[..(firstChunk + 4 + 4096 + 15)], .. shortened[(firstChunk + 4 + 4096 + 16)..]];
            }
            case "chunk longer than the header allows":
            {
                var longer = (byte[])good.Clone();
                BitConverter.GetBytes(4096u + 17).CopyTo(longer, firstChunk);
                return longer;
            }
            default: throw new ArgumentException(what);
        }
    }

    /// <summary>The container with its header text rewritten and its length prefix corrected.</summary>
    private static byte[] Reframe(byte[] container, int headerLength, Func<string, string> change)
    {
        var header = System.Text.Encoding.UTF8.GetBytes(change(System.Text.Encoding.UTF8.GetString(container, 12, headerLength)));
        return [.. container[..8], .. BitConverter.GetBytes((uint)header.Length), .. header, .. container[(12 + headerLength)..]];
    }

    /// <summary>A sealed event as an activated author writes it (spec 3.2): opaque payload, protocol 4, no article id.</summary>
    public static SyncEvent SealedEvent(Guid author, byte[] authorPrivateKey, long lamport, IReadOnlyList<string>? blobs = null)
    {
        var envelope = new BeeMemoryBank.Sync.Metadata.SealedEnvelope(KeyId, RandomNumberGenerator.GetBytes(12), (blobs ?? []).Order(StringComparer.Ordinal).ToList(),
            RandomNumberGenerator.GetBytes(16 + 64));
        return Signed(new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = author, LamportTs = lamport, EventType = EventClasses.Sealed,
            ArticleId = null, Payload = envelope.ToPayload(), ProtocolVersion = EventClasses.SealedProtocol,
            CreatedAt = DateTime.UtcNow,
        }, authorPrivateKey);
    }

    /// <summary>A protocol-3 article_create with <paramref name="title"/> in clear: what a blind host must never get after activation.</summary>
    public static SyncEvent ClearArticleEvent(Guid author, byte[] authorPrivateKey, long lamport, string title, string folder)
    {
        var id = Guid.NewGuid();
        var payload = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = id.ToString(), ["title"] = title, ["tree_path"] = folder, ["status"] = "A",
            ["content_type"] = "text/markdown",
        });
        return Signed(new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = author, LamportTs = lamport, EventType = EventTypes.ArticleCreate,
            ArticleId = id, Payload = payload, ProtocolVersion = 3, CreatedAt = DateTime.UtcNow,
            ActorName = title,
        }, authorPrivateKey);
    }

    public static SyncEvent Signed(SyncEvent evt, byte[] privateKey)
    {
        evt.Signature = Ed25519Signer.Sign(privateKey, EventSignature.BuildPayload(evt));
        return evt;
    }

    /// <summary>A blob in the store; returns its hash.</summary>
    public static async Task<string> StoreBlobAsync(DbConnectionFactory factory, byte[]? data = null) =>
        await new BlobRepository(factory).StoreAsync(data ?? RandomNumberGenerator.GetBytes(300));

    /// <summary>One metadata key row and one activation row: how a test makes a node activated (spec 14).</summary>
    public static async Task ActivateAsync(Core.Interfaces.IDbConnectionFactory factory, Guid author)
    {
        using var conn = factory.CreateConnection();
        var now = DateTime.UtcNow.ToString("O");
        await conn.ExecuteAsync(
            @"INSERT OR IGNORE INTO tbl_metadata_key (key_id, generation, dek_fingerprint, epoch_hint, wrapped, iv, author_node_id, created_at, lamport_ts, event_id)
              VALUES (@KeyId, 1, @fp, 0, @wrapped, @iv, @author, @now, 1, @eventId)",
            new { KeyId, fp = new string('f', 64), wrapped = RandomNumberGenerator.GetBytes(48), iv = RandomNumberGenerator.GetBytes(12),
                  author = author.ToString(), now, eventId = Guid.NewGuid().ToString() });
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_metadata_activation (activation_id, key_id, generation, activated_at, author_node_id, event_id, lamport_ts, applied_at)
              VALUES (@id, @KeyId, 1, @now, @author, @eventId, 2, @now)",
            new { id = Guid.NewGuid().ToString(), KeyId, now, author = author.ToString(), eventId = Guid.NewGuid().ToString() });
    }

    /// <summary>
    /// The control tables of a database copied row for row into a new plain SQLite file, as the writer makes <c>control.db</c>
    /// (spec 8.4): what the rows the appliers wrote look like once they travel.
    /// </summary>
    public static void CopyControlTables(DbConnectionFactory factory, string path)
    {
        using (var created = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
            created.Open();
        using var conn = factory.CreateConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        conn.Execute($"ATTACH DATABASE '{path.Replace("'", "''")}' AS ctl");
        try
        {
            foreach (var table in SnapshotTables.Control)
                conn.Execute($"CREATE TABLE ctl.\"{table}\" AS SELECT * FROM main.\"{table}\"");
        }
        finally
        {
            conn.Execute("DETACH DATABASE ctl");
        }
    }

    /// <summary>Every entry of a .tar.gz, in order (a tamper test rewrites one and packs them again).</summary>
    public static List<(string Name, byte[] Data)> ReadEntries(string archive)
    {
        var list = new List<(string, byte[])>();
        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            using var memory = new MemoryStream();
            entry.DataStream?.CopyTo(memory);
            list.Add((entry.Name, memory.ToArray()));
        }
        return list;
    }

    public static void WriteEntries(string archive, IEnumerable<(string Name, byte[] Data)> entries)
    {
        using var file = File.Create(archive);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        using var tar = new TarWriter(gzip, TarEntryFormat.Pax);
        foreach (var (name, data) in entries)
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) });
    }
}
