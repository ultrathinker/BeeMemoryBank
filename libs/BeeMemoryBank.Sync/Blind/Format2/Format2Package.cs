using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync.Blind.Format2;

/// <summary>
/// The byte layout of a format-2 blind package (docs/design/step-a-spec.md 8.2) shared by its writer and its reader: the entry
/// names, the two outer signature framings (the same domain tags as every snapshot package, so a format-2 package is signed
/// exactly as a format-1 one), the blob list and the wire form of the deltas.
/// </summary>
public static class Format2Package
{
    /// <summary>The outer manifest (today's snapshot manifest shape) and its embedded signature.</summary>
    public const string ManifestFileName = "manifest.json";
    public const string ManifestSignatureFileName = "manifest.json.sig";

    /// <summary>The outer manifest's format markers (spec 8.2). A 2.5 reader refuses the package by the blind manifest's format.</summary>
    public const string PackageFormat = BlindManifestV2.CurrentFormat;
    public const int SnapshotFormatVersion = 5;

    /// <summary>Response header of the detached signature over manifest || archive, as for every package served today.</summary>
    public const string DetachedSignatureHeader = "X-BMB-Snapshot-Signature";

    public static readonly string BaselineManifestEntry = BlindManifestV2.BaselineDirectory + "/" + BaselineManifest.FileName;
    public static readonly string BaselineSignatureEntry = BlindManifestV2.BaselineDirectory + "/" + BaselineManifest.SignatureFileName;
    public static readonly string BaselineBlobListEntry = BlindManifestV2.BaselineDirectory + "/" + BaselineManifest.BlobListFileName;
    public static readonly string BaselineContainerEntry = BlindManifestV2.BaselineDirectory + "/" + BaselineManifest.ContainerFileName;

    // The tags of SnapshotService.Signature.cs: the embedded signature covers the manifest bytes, the detached one
    // SHA-256(tag || manifest || archive). Same tags, so a signature made for one framing never verifies for the other.
    private static readonly byte[] DomainTagEmbedded = "BMB-MANIFEST-V1\0"u8.ToArray();
    private static readonly byte[] DomainTagDetached = "BMB-MANIFEST-FILE-V1\0"u8.ToArray();

    /// <summary>Wire JSON of a delta line (spec 8.5): the SyncEvent as served, camelCase.</summary>
    public static readonly JsonSerializerOptions DeltaJson = new(JsonSerializerDefaults.Web);

    /// <summary>What the serving node signs for <c>manifest.json.sig</c>.</summary>
    public static byte[] EmbeddedSignaturePayload(byte[] manifestBytes)
    {
        var payload = new byte[DomainTagEmbedded.Length + manifestBytes.Length];
        DomainTagEmbedded.CopyTo(payload, 0);
        manifestBytes.CopyTo(payload, DomainTagEmbedded.Length);
        return payload;
    }

    /// <summary>What the serving node signs for the detached signature: SHA-256 of tag, manifest bytes and the whole archive.</summary>
    public static async Task<byte[]> DetachedSignaturePayloadAsync(byte[] manifestBytes, string archivePath, CancellationToken ct = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(DomainTagDetached);
        hash.AppendData(manifestBytes);
        await using var stream = File.OpenRead(archivePath);
        var buffer = new byte[81_920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0) hash.AppendData(buffer, 0, read);
        return hash.GetHashAndReset();
    }

    /// <summary>The entry name of a blob: <c>blobs/&lt;hash&gt;</c>.</summary>
    public static string BlobEntry(string hash) => BlindManifestV2.BlobsDirectory + "/" + hash;

    /// <summary>
    /// Whether <paramref name="name"/> is an entry a format-2 package may hold (spec 8.2). Everything else, a database entry
    /// in particular, makes the package invalid: nothing in it is unaccounted for.
    /// </summary>
    public static bool IsAllowedEntry(string name) =>
        name is ManifestFileName or ManifestSignatureFileName or BlindManifestV2.FileName
            or BlindManifestV2.ControlDbFileName or BlindManifestV2.DeltasFileName
        || name == BaselineManifestEntry || name == BaselineSignatureEntry
        || name == BaselineBlobListEntry || name == BaselineContainerEntry
        || (name.StartsWith(BlindManifestV2.BlobsDirectory + "/", StringComparison.Ordinal)
            && BlobReferences.IsWellFormedHash(name[(BlindManifestV2.BlobsDirectory.Length + 1)..]));

    /// <summary>The bytes of <c>blobs.txt</c> (spec 7.6): distinct lowercase hashes, ordinal order, each line ending in \n.</summary>
    public static byte[] FormatBlobList(IEnumerable<string> hashes)
    {
        var sb = new StringBuilder();
        foreach (var hash in hashes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            sb.Append(hash).Append('\n');
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>Reads <c>blobs.txt</c>, refusing anything but its one exact form (spec 7.6).</summary>
    public static IReadOnlyList<string> ParseBlobList(byte[] bytes)
    {
        var text = Encoding.ASCII.GetString(bytes);
        if (text.Length == 0) return [];
        if (!text.EndsWith('\n'))
            throw new InvalidDataException("blobs.txt must end with a newline.");
        var lines = text[..^1].Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!BlobReferences.IsWellFormedHash(lines[i]))
                throw new InvalidDataException("blobs.txt holds a line that is not a blob hash.");
            if (i > 0 && string.CompareOrdinal(lines[i - 1], lines[i]) >= 0)
                throw new InvalidDataException("blobs.txt is not sorted, or repeats a hash.");
        }
        return lines;
    }

    /// <summary>
    /// The blob hashes a delta needs: the envelope's list of a sealed event (spec 3.3), the payload's ciphertext hash of a
    /// clear one (<see cref="BlobReferences"/>).
    /// </summary>
    public static IEnumerable<string> BlobsOf(SyncEvent evt)
    {
        if (evt.EventType == Metadata.EventClasses.Sealed)
            return Metadata.SealedEnvelope.Parse(evt.Payload).BlobHashes;
        return BlobReferences.Collect([evt]);
    }

    /// <summary>One delta line: the event in wire form. Local columns and actor fields never travel (spec 3.2).</summary>
    public static string ToDeltaLine(SyncEvent evt) =>
        JsonSerializer.Serialize(new SyncEvent
        {
            EventId = evt.EventId,
            NodeId = evt.NodeId,
            LamportTs = evt.LamportTs,
            EventType = evt.EventType,
            ArticleId = evt.ArticleId,
            Payload = evt.Payload,
            Signature = evt.Signature,
            SequenceNum = evt.SequenceNum,
            CreatedAt = evt.CreatedAt,
            ProtocolVersion = evt.ProtocolVersion,
        }, DeltaJson);

    /// <summary>
    /// Reads <c>deltas.jsonl</c> back, one event per line, in the serving node's order. A line that is empty or is not an event
    /// is an <see cref="InvalidDataException"/>: the writer writes none.
    /// </summary>
    public static async IAsyncEnumerable<SyncEvent> ReadDeltasAsync(
        string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        var row = 0;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            row++;
            if (line.Length == 0)
                throw new InvalidDataException($"deltas.jsonl row {row} is empty.");
            SyncEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<SyncEvent>(line, DeltaJson);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"deltas.jsonl row {row} is not an event.", ex);
            }
            yield return evt ?? throw new InvalidDataException($"deltas.jsonl row {row} holds no event.");
        }
    }

    /// <summary>
    /// The receiver's side of spec 8.5: <see cref="ReadDeltasAsync"/> plus the invariants the format promises, checked on every
    /// row before the caller sees it - the event has an id and a type, its Lamport time is in the format-2 range, its sequence
    /// is after <paramref name="cut"/> and not after <paramref name="head"/>, the rows are strictly in the serving node's order,
    /// no event comes twice, and none is a protocol-1..3 content event (those never travel in a package, 5.7). A sender that
    /// breaks one is not trusted for the rest.
    /// </summary>
    public static async IAsyncEnumerable<SyncEvent> ReadVerifiedDeltasAsync(
        string path, long cut, long head, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var ids = new HashSet<Guid>();
        var before = cut;
        var row = 0;
        await foreach (var evt in ReadDeltasAsync(path, ct))
        {
            row++;
            if (evt.EventId == Guid.Empty || string.IsNullOrEmpty(evt.EventType) || evt.Payload is null)
                throw new InvalidDataException($"deltas.jsonl row {row} is not a complete event.");
            if (evt.LamportTs < 0 || evt.LamportTs > BlindManifestV2.MaximumLamport)
                throw new InvalidDataException($"deltas.jsonl row {row}: Lamport time is outside the format-2 limit.");
            if (Metadata.EventClasses.IsClearContent(evt.ProtocolVersion, evt.EventType))
                throw new InvalidDataException($"deltas.jsonl row {row} is a protocol-1..3 content event, which a package never carries.");
            if (!ids.Add(evt.EventId))
                throw new InvalidDataException($"deltas.jsonl row {row}: event {evt.EventId} is listed twice.");
            if (evt.SequenceNum <= cut)
                throw new InvalidDataException($"deltas.jsonl row {row}: sequence {evt.SequenceNum} is not after the baseline's cut ({cut}).");
            if (evt.SequenceNum > head)
                throw new InvalidDataException($"deltas.jsonl row {row}: sequence {evt.SequenceNum} is after the serving node's head ({head}).");
            if (evt.SequenceNum <= before)
                throw new InvalidDataException($"deltas.jsonl row {row}: sequence {evt.SequenceNum} does not come after the row before it ({before}).");
            before = evt.SequenceNum;
            yield return evt;
        }
    }

    /// <summary>Reads one entry of a .tar.gz into memory, at most <paramref name="maximumBytes"/>; null when it is absent.</summary>
    public static async Task<byte[]?> ReadEntryAsync(string archivePath, string name, long maximumBytes, CancellationToken ct = default)
    {
        await using var file = File.OpenRead(archivePath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (await tar.GetNextEntryAsync(cancellationToken: ct) is { } entry)
        {
            if (entry.Name != name) continue;
            if (entry.DataStream is null) return [];
            using var output = new MemoryStream();
            await CopyWithLimitAsync(entry.DataStream, output, maximumBytes, ct);
            return output.ToArray();
        }
        return null;
    }

    /// <summary>Copies at most <paramref name="maximumBytes"/>; a longer stream is refused.</summary>
    public static async Task<long> CopyWithLimitAsync(Stream input, Stream output, long maximumBytes, CancellationToken ct)
    {
        var buffer = new byte[81_920];
        long copied = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, ct);
            if (count == 0) return copied;
            copied += count;
            if (copied > maximumBytes)
                throw new InvalidDataException("A package entry is larger than allowed.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
    }

    /// <summary>Lowercase hex SHA-256 of a file.</summary>
    public static async Task<string> HashFileAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }
}
