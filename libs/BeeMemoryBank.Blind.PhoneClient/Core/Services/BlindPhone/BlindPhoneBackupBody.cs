using System.Formats.Tar;

namespace BeeMemoryBank.Core.Services.BlindPhone;

/// <summary>The parts of a backup body, as files in a work folder.</summary>
/// <param name="PackagePath">The blind package (a snapshot archive with its signed blind-manifest.json).</param>
/// <param name="Signature">The package's detached signature, by the node that built it.</param>
/// <param name="EventsPath">The signed events the phone held: a JSON array of events, read one by one.</param>
public sealed record BlindPhoneBackupParts(string PackagePath, byte[] Signature, string EventsPath);

/// <summary>
/// How much of a backup body a restore takes: the package, the events file and their count. The decrypted body
/// is bounded as it streams (<see cref="MaxBodyBytes"/>), each entry by its declared size before a byte of it is
/// written, and the events as they are read — an oversized body is refused, never staged whole.
/// </summary>
public sealed record BlindPhoneBackupLimits(long MaxPackageBytes, long MaxEventsBytes, int MaxEvents)
{
    /// <summary>A whole vault with its media, the event log a restore replays (as the network restore route).</summary>
    public static readonly BlindPhoneBackupLimits Default = new(64L << 30, 1L << 30, 200_000);

    /// <summary>The tar around the three files: a header (with its PAX record) and padding each, generously.</summary>
    private const long ArchiveOverheadBytes = 16 * 1024;

    public long MaxBodyBytes => MaxPackageBytes + MaxEventsBytes + ArchiveOverheadBytes;
}

/// <summary>
/// The body of an Android blind node's backup (inside <see cref="Crypto.AndroidBackupFile"/>'s encryption): a
/// plain tar of exactly three files — the blind package the listening node built and signed, its detached
/// signature, and the phone's signed events. It is the same material a restore from a blind node gets over the
/// network (package, signature, events), so a restore from the file goes through the same package restore: the
/// signature checked against the producer the pairing sealed, the manifest's whitelist and positions, the media.
/// </summary>
public static class BlindPhoneBackupBody
{
    public const string PackageEntry = "package.tar.gz";
    public const string SignatureEntry = "package.tar.gz.sig";
    public const string EventsEntry = "events.json";
    private const int MaxSignatureBytes = 1024;

    public static async Task WriteAsync(string destination, string packagePath, byte[] signature, string eventsJsonPath,
        CancellationToken ct = default)
    {
        await using var file = File.Create(destination);
        await using var tar = new TarWriter(file, TarEntryFormat.Pax, leaveOpen: false);
        await tar.WriteEntryAsync(packagePath, PackageEntry, ct);
        await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, SignatureEntry) { DataStream = new MemoryStream(signature) }, ct);
        await tar.WriteEntryAsync(eventsJsonPath, EventsEntry, ct);
    }

    /// <summary>
    /// The three parts, written under <paramref name="workDirectory"/> by fixed names (never by a name from the
    /// archive). Anything else in the archive — another name, a second copy, a link or folder — refuses it.
    /// </summary>
    public static async Task<BlindPhoneBackupParts> ReadAsync(string bodyPath, string workDirectory,
        BlindPhoneBackupLimits limits, CancellationToken ct = default)
    {
        Directory.CreateDirectory(workDirectory);
        var package = Path.Combine(workDirectory, PackageEntry);
        var events = Path.Combine(workDirectory, EventsEntry);
        byte[]? signature = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        await using var file = File.OpenRead(bodyPath);
        await using var tar = new TarReader(file);
        try
        {
            while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || !seen.Add(entry.Name))
                    throw new InvalidDataException("The backup body holds something other than its three files.");
                switch (entry.Name)
                {
                    case PackageEntry:
                        if (entry.Length > limits.MaxPackageBytes)
                            throw new InvalidDataException("The backup body's package is larger than a restore takes.");
                        await entry.ExtractToFileAsync(package, overwrite: true, ct);
                        break;
                    case EventsEntry:
                        if (entry.Length > limits.MaxEventsBytes)
                            throw new InvalidDataException("The backup body's events are larger than a restore takes.");
                        await entry.ExtractToFileAsync(events, overwrite: true, ct);
                        break;
                    case SignatureEntry:
                        if (entry.Length > MaxSignatureBytes || entry.DataStream == null)
                            throw new InvalidDataException("The backup body's signature is malformed.");
                        using (var ms = new MemoryStream())
                        {
                            await entry.DataStream.CopyToAsync(ms, ct);
                            signature = ms.ToArray();
                        }
                        break;
                    default:
                        throw new InvalidDataException($"The backup body holds an unexpected file: {entry.Name}.");
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or EndOfStreamException)
        {
            throw new InvalidDataException("The backup body is not a readable archive.", ex);
        }
        if (seen.Count != 3 || signature == null)
            throw new InvalidDataException("The backup body is missing its package, signature or events.");
        return new BlindPhoneBackupParts(package, signature, events);
    }
}
