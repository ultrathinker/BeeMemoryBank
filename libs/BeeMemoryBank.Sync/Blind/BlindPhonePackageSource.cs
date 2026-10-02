using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services.BlindPhone;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Sync.Blind;

/// <summary>Where a backup gets its package: the listening node's current one, downloaded and verified.</summary>
public interface IBlindVerifiedPackageFetcher
{
    /// <param name="beforeDownload">Told the size once it is known, before the first byte is written; may throw to stop.</param>
    Task<VerifiedReplicaPackage> FetchAsync(Action<long> beforeDownload, IProgress<double>? progress, CancellationToken ct);
}

/// <summary>
/// The body of a phone's backup (plan section 10, <see cref="BlindPhoneBackupBody"/>): the listening node's
/// CURRENT signed package, its signature, and the signed events the phone holds.
///
/// <para>Why a fresh package and not the one the phone installed at its first load: a restore on Windows
/// accepts the phone's pairing record only if an anchor under the master key vouches for the superadmin that
/// paired it, and it finds each anchor row's signed <c>state_anchor</c> event among the phone's events. The
/// phone logs events only from its own first load on, so the anchors inside the first package never have an
/// event on the phone; an anchor that reached the phone later is in a package cut after it. So: wait until the
/// phone has received an anchor, fetch a package, and check it holds one of those anchors (else wait for the next
/// one: the listener serves a package it built up to half an hour ago).</para>
///
/// <para>Written only when a restore can use it, and fail closed otherwise: waiting for the first anchor
/// (<see cref="BlindFeaturePendingException"/>), no room, a package or event list a restore would refuse
/// (<see cref="BlindPhoneBackupLimits"/>). Nothing it made is left behind on any failure, the fetched package
/// included.</para>
/// </summary>
/// <param name="freeBytes">Free space of the volume holding the given folder. Tests inject it.</param>
public sealed class BlindPhonePackageSource(
    IBlindVerifiedPackageFetcher fetcher, IServiceScopeFactory scopes, Func<string, long>? freeBytes = null,
    BlindPhoneBackupLimits? limits = null, int pageSize = 500) : IBlindPackageSource
{
    // How many of the phone's newest anchor events are compared with the package's anchors.
    private const int AnchorEventsChecked = 200;
    // Room for the working copies beyond the archive: the download plus its checking extraction, then the body and
    // the encrypted file (each about the size of the archive), and some slack.
    private const long ExtractionFactor = 4;
    private const long Slack = 32L << 20;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly BlindPhoneBackupLimits _limits = limits ?? BlindPhoneBackupLimits.Default;
    private readonly Func<string, long> _free = freeBytes ?? DiskFreeBytes;

    public async Task CreateAsync(string destinationPath, CancellationToken ct)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(destinationPath))!;
        var eventsPath = destinationPath + ".events";
        VerifiedReplicaPackage? package = null;
        var finished = false;
        try
        {
            var received = await ReceivedAnchorIdsAsync(ct);
            if (received.Count == 0)
                throw new BlindFeaturePendingException(
                    "the network's next integrity anchor to reach this phone by sync (the computer publishes one after the data changes)");

            package = await fetcher.FetchAsync(length => EnsureRoom(folder, ExtractionFactor * length + Slack), progress: null, ct);
            if (!package.AnchorIds.Any(received.Contains))
                // The listener serves a package it built up to half an hour ago, so right after an anchor reached
                // the phone it can still be an older one: wait for the next, not a failure.
                throw new BlindFeaturePendingException(
                    "a package that includes the integrity anchor this phone received (the listening node renews its package every half hour)");
            if (package.RecoveryBoxes == 0)
                // A restore takes its keys from the boxes of the PACKAGE's database: one the listener built before the first
                // superadmin signed in holds none, and a backup made from it could never be opened.
                throw new BlindFeaturePendingException(
                    "a package that includes a recovery box (the listening node renews its package every half hour)");
            if (package.Length > _limits.MaxPackageBytes)
                throw new InvalidDataException("The listening node's package is larger than a restore takes.");

            var eventsBytes = await WriteEventsAsync(eventsPath, ct);
            EnsureRoom(folder, 2 * (package.Length + eventsBytes) + Slack);

            await BlindPhoneBackupBody.WriteAsync(destinationPath, package.ArchivePath, package.Signature, eventsPath, ct);
            finished = true;
        }
        finally
        {
            DeleteIfPresent(eventsPath);
            if (package is not null) DeleteIfPresent(package.ArchivePath);
            if (!finished) DeleteIfPresent(destinationPath);
        }
    }

    /// <summary>The ids of the state anchors whose signed events this phone holds, newest first.</summary>
    private async Task<HashSet<string>> ReceivedAnchorIdsAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var log = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var evt in await log.GetRecentAsync(AnchorEventsChecked, 0, EventTypes.StateAnchor))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (JsonSerializer.Deserialize<StateAnchorPayload>(evt.Payload) is { AnchorId: { Length: > 0 } id }) ids.Add(id);
            }
            catch (JsonException)
            {
                // The applier took it, so it parsed once; an unreadable one just does not count.
            }
        }
        return ids;
    }

    /// <summary>Every event of the log, in order, as a JSON array — the reader's own shape. Returns its size.</summary>
    private async Task<long> WriteEventsAsync(string path, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var log = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous);
        await using (var writer = new Utf8JsonWriter(file))
        {
            writer.WriteStartArray();
            long after = 0;
            var count = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = await log.GetAllAfterSequenceAsync(after, pageSize);
                if (page.Count == 0) break;
                foreach (var evt in page)
                {
                    if (++count > _limits.MaxEvents)
                        throw new InvalidDataException("The phone holds more events than a restore takes.");
                    JsonSerializer.Serialize(writer, evt, Web);
                    after = evt.SequenceNum;
                }
                await writer.FlushAsync(ct);
                if (file.Length > _limits.MaxEventsBytes)
                    throw new InvalidDataException("The phone's events are larger than a restore takes.");
            }
            writer.WriteEndArray();
        }
        return file.Length;
    }

    private void EnsureRoom(string folder, long needed)
    {
        var free = _free(folder);
        if (free < needed)
            throw new IOException($"Not enough room for a backup: about {needed >> 20} MiB are needed, {free >> 20} MiB are free.");
    }

    private static long DiskFreeBytes(string folder) => new DriveInfo(folder).AvailableFreeSpace;

    private static void DeleteIfPresent(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* the next run overwrites it; a delete failure must not hide the cause */ }
        catch (UnauthorizedAccessException) { }
    }
}
