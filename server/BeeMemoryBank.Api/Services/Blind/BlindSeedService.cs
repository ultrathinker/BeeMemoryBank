using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Api.Services.BlindBackup;
using BeeMemoryBank.Api.Services.BlindLimits;
using BeeMemoryBank.Api.Startup;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Api.Services;

/// <summary>Who is seeding: the holder of the pair code's secret, or a superadmin peer reseeding.</summary>
public abstract record BlindSeedAuthority
{
    /// <summary>
    /// The seeder the pairing secret vouched for (<see cref="BlindSeederProof"/>): id and key (base64),
    /// and which code vouched (<see cref="BlindPairing.CodeIdOf"/>), so it can be asked again whether
    /// that code is still unspent.
    /// </summary>
    public sealed record PairingSecret(Guid SeederNodeId, string SeederKeyB64, string CodeId) : BlindSeedAuthority;

    public sealed record SuperadminPeer(Guid NodeId) : BlindSeedAuthority;
}

/// <summary>Where an upload stands; what the sender resumes from.</summary>
public sealed record BlindSeedProgress(Guid SeedId, long Received, long Total, bool Applied);

public sealed class BlindSeedConflictException(Guid activeSeedId)
    : InvalidOperationException($"Another seed ({activeSeedId}) is in progress.")
{
    public Guid ActiveSeedId { get; } = activeSeedId;
}

public sealed class BlindSeedOffsetException(long received)
    : InvalidOperationException($"This seed has {received} bytes; continue from there.")
{
    public long Received { get; } = received;
}

/// <summary>
/// Why a seed is refused: one code per check that can fail (BMB-306/307 fix round 2, M3/P4). The sender is told, and this node's log says, the FIXED sentence of
/// the code (<see cref="BlindSeedRefusals.Sentence"/>) - never a path, an address, a key, a pair code, a number the sender chose, or the text of a parser or an I/O
/// error. What went wrong underneath travels as the exception's inner exception, of which only the type is written down (see the endpoint).
/// </summary>
public enum BlindSeedRefusal
{
    Generic, SizeOutOfRange, HashMalformed, PartTooLarge, ResumeMismatch, HashMismatch, ActivatedTakesFormat2, ManifestMissing, PackageInvalid, WrongSeed,
    NotSuperadminPeer, NotBuiltByPairedNode, BlindCannotSeed, CannotSeedItself, UnknownAuthority, SignatureInvalid, SchemaMismatch, DrainTimeout, ReplayFailed,
    SwitchNotUndone, SwitchFailed, LamportNoRoom, PackageV2Invalid, ControlDatabaseInvalid, BaselineCleanupIncomplete, BaselineRefused, FirstSeedNeedsFullPackage,
    NotBuiltBySeedingNode, BlobMismatch, DeltaNotStored, CutoverInterrupted, CutoverUnresolved, CutoverPathOutside, CutoverLink,
}

/// <summary>
/// A seed this node refuses. <see cref="Exception.Message"/> is the fixed sentence of <see cref="Reason"/> and nothing else: there is no way to build one from a
/// free sentence, so no sentence somebody later writes with an interpolated message or path can leave this node.
/// </summary>
public sealed class BlindSeedRejectedException(BlindSeedRefusal reason, Exception? cause = null)
    : InvalidOperationException(BlindSeedRefusals.Sentence(reason), cause)
{
    public BlindSeedRefusal Reason { get; } = reason;
}

/// <summary>The sentence of each refusal. The reason of a seed leaves this node in a log line and in the answer to the sender.</summary>
public static class BlindSeedRefusals
{
    public const string Generic = "The seed was refused. Nothing was changed.";

    /// <summary>
    /// The code whose fixed sentence another blind node answered, or null. What a blind node says about a refusal is one of its fixed sentences
    /// (<see cref="Sentence"/>); an answer that is not exactly one of them is that node's own text and is not shown.
    /// </summary>
    public static BlindSeedRefusal? FromSentence(string? text) =>
        text is null ? null : Enum.GetValues<BlindSeedRefusal>().Cast<BlindSeedRefusal?>().FirstOrDefault(c => Sentence(c!.Value) == text && c != BlindSeedRefusal.Generic);

    public static string Sentence(BlindSeedRefusal reason) => reason switch
    {
        BlindSeedRefusal.SizeOutOfRange => "The package size is outside what this node accepts.",
        BlindSeedRefusal.HashMalformed => "The package's SHA-256 must be 64 hex characters.",
        BlindSeedRefusal.PartTooLarge => "Part is larger than announced.",
        BlindSeedRefusal.ResumeMismatch => "Resume does not match the seed it continues.",
        BlindSeedRefusal.HashMismatch => "SHA-256 of the received package does not match.",
        BlindSeedRefusal.ActivatedTakesFormat2 =>
            "This blind node is activated (metadata encryption): it takes format-2 packages only. Update the sending device to 2.6 or newer.",
        BlindSeedRefusal.ManifestMissing => "Not a blind package: blind-manifest.json is missing.",
        BlindSeedRefusal.PackageInvalid => "Not a valid blind package.",
        BlindSeedRefusal.WrongSeed => "The package was built for another seed.",
        BlindSeedRefusal.NotSuperadminPeer => "Only a superadmin peer may reseed this node.",
        BlindSeedRefusal.NotBuiltByPairedNode => "The package was not built by the node that paired.",
        BlindSeedRefusal.BlindCannotSeed => "A blind node cannot seed a blind node: pair it from a full node.",
        BlindSeedRefusal.CannotSeedItself => "This node cannot seed itself.",
        BlindSeedRefusal.UnknownAuthority => "Unknown seed authority.",
        BlindSeedRefusal.SignatureInvalid => "The package signature does not verify.",
        BlindSeedRefusal.SchemaMismatch => "The package is on a newer schema than this node. Update this node first.",
        BlindSeedRefusal.DrainTimeout =>
            "The database stayed in use (a blind backup job, a wipe or a connection was still running); nothing was changed. Send the seed again when it finishes.",
        BlindSeedRefusal.ReplayFailed => "Could not replay this node's own events on top of the package; nothing was changed. Send the seed again.",
        BlindSeedRefusal.SwitchNotUndone =>
            "The switch failed and could not be undone in place; restart this node — it rolls back to its previous database at start.",
        BlindSeedRefusal.SwitchFailed => "The switch failed; this node is back on its previous database and media. Send the seed again.",
        BlindSeedRefusal.LamportNoRoom =>
            "The package carries a Lamport time that leaves no value for a restorable first-seed grant; this node cannot take it.",
        BlindSeedRefusal.PackageV2Invalid => "Not a valid format-2 package.",
        BlindSeedRefusal.ControlDatabaseInvalid => "The package's control database cannot be imported here.",
        BlindSeedRefusal.BaselineCleanupIncomplete =>
            "The baseline is adopted, but removing the earlier readable content from this node's database did not finish. Send the seed again.",
        BlindSeedRefusal.BaselineRefused => "The baseline was refused.",
        BlindSeedRefusal.FirstSeedNeedsFullPackage => "A first seed needs a full package.",
        BlindSeedRefusal.NotBuiltBySeedingNode => "The package was not built by the node that is seeding.",
        BlindSeedRefusal.BlobMismatch => "A blob of the package does not match its name.",
        BlindSeedRefusal.DeltaNotStored => "A delta event could not be stored yet; the baseline is adopted. Send the seed again.",
        BlindSeedRefusal.CutoverInterrupted =>
            "An earlier seed's switch was interrupted and is not resolved; this node rolls it back at its next start. Restart it, then send the seed again.",
        BlindSeedRefusal.CutoverUnresolved =>
            "An earlier seed's cutover is unresolved: its marker is missing or unreadable and the database it replaced is still kept in blind-cutover. Nothing was discarded. Restart this node, which keeps or restores what is there; resolve blind-cutover by hand if it stays unresolved. No seed is accepted until then.",
        BlindSeedRefusal.CutoverPathOutside =>
            "Refusing to change a file that is not directly under the cutover's own folder. Nothing was changed; resolve it by hand.",
        BlindSeedRefusal.CutoverLink =>
            "Refusing to touch a link (or another reparse point) where the cutover's own file should be. Nothing was changed; resolve it by hand.",
        _ => Generic,
    };
}

/// <summary>The node is being wiped: the seed was stopped, or not started. Nothing of it is kept.</summary>
public sealed class BlindSeedWipingException()
    : InvalidOperationException("This node is being wiped; the seed was stopped.");

/// <summary>The pair code this upload came under is spent, renewed or expired by the time it is admitted.</summary>
public sealed class BlindSeedAuthorityGoneException()
    : InvalidOperationException("The pair code this seed was sent under is no longer valid.");

/// <summary>
/// Receives a blind package and makes it this blind node's database (plan 4.2, 4.3, 5.2):
/// in parts with resume, checked by SHA-256 and by the producer's signature, with free space
/// verified before the first byte, one seed at a time, under its own size limit. The new database
/// is assembled next to the live one and swapped in atomically; the previous one is kept.
///
/// <para>Step A (docs/design/step-a-spec.md 8, 9): a format-2 package is not a database. It is verified and adopted into the
/// blind store instead (<c>BlindSeedService.Format2.cs</c>); once this node is activated a format-1 package is refused, so
/// no plaintext metadata reaches it again.</para>
/// </summary>
public sealed partial class BlindSeedService(
    string dataPath,
    SnapshotService snapshots,
    MaintenanceModeService maintenance,
    BlindJobManager jobs,
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<BlindSeedService> logger,
    // The node's own limits (a server blind node; null where there are none): the storage limit is judged here, part by part, for the staging a seed makes.
    BlindUsageGuard? limits = null,
    TimeProvider? time = null) : IDisposable
{
    /// <summary>Default ceiling for a whole package; BMB_BLIND_SEED_MAX_BYTES overrides it.</summary>
    public const long DefaultMaxPackageBytes = 64L * 1024 * 1024 * 1024;

    /// <summary>A single part; the sender splits the package into parts no larger than this.</summary>
    public const int MaxChunkBytes = 64 * 1024 * 1024;

    // Package on disk, extracted copy, new database: roughly three times the package at the peak. This is the free space of the VOLUME the apply needs (checked before the first
    // byte); the node's own storage limit judges the announced total against the room it leaves (see ReceiveAsync).
    private const double SpaceFactor = 3.0;

    // An upload nobody has touched for this long no longer blocks a new one, and its staging goes (on a timer, not only when a new seed arrives).
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(1);
    internal static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private Upload? _active;
    private ITimer? _sweep;
    private volatile bool _disposed;

    // The wipe's barrier (see QuiesceAsync): _stop cancels the part that is being received; apply is never cut in the middle.
    private readonly object _barrier = new();
    private CancellationTokenSource _stop = new();
    private bool _quiesced;

    private sealed class Upload(Guid seedId, long total, string sha256, BlindSeedAuthority authority, string path, DateTime touched)
    {
        /// <summary>While the upload lives, its staging is left out of the stored-size walk the door's decisions rest on: the seed judges itself.</summary>
        public IDisposable? Exclusion { get; set; }

        public Guid SeedId { get; } = seedId;
        public long Total { get; } = total;
        public string Sha256 { get; } = sha256;
        public BlindSeedAuthority Authority { get; } = authority;
        public string Path { get; } = path;
        public long Received { get; set; }
        public DateTime LastTouched { get; set; } = touched;
    }

    /// <summary>The upload is over (applied, refused, dropped): out of the way of the door's walk, and its staging goes unless the caller keeps it.</summary>
    private void EndUpload(Upload upload, bool deleteStaging = true)
    {
        if (ReferenceEquals(_active, upload)) _active = null;
        if (deleteStaging) TryDeleteTree(SeedDir(upload.SeedId));
        upload.Exclusion?.Dispose();
        upload.Exclusion = null;
    }

    /// <summary>
    /// Drops the upload nobody has continued for <see cref="AbandonedAfter"/>, with its staging. Runs on a timer: an interrupted seed (the sender's computer went to sleep) must not
    /// keep disk the node's owner limited, until a NEW seed happens to arrive. A part that is arriving holds the gate, so it is never an idle upload.
    /// </summary>
    internal void SweepAbandoned()
    {
        if (!_gate.Wait(0)) return;
        try
        {
            if (_active is { } idle && _time.GetUtcNow().UtcDateTime - idle.LastTouched >= AbandonedAfter)
            {
                logger.LogWarning("Blind seed {SeedId} was not continued for {Hours} hour(s); the {Staged} bytes it had staged were removed", idle.SeedId, AbandonedAfter.TotalHours, idle.Received);
                EndUpload(idle);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _sweep?.Dispose();
    }

    /// <summary>
    /// The sweep timer's callback. A callback that throws ends the process, and the sweep is a courtesy to the disk of a node whose sender went away: whatever goes wrong in it (a log
    /// sink that fails, a folder that cannot be removed) is logged and the next tick tries again. After <see cref="Dispose"/> a tick that was already on its way does nothing.
    /// </summary>
    private void RunSweep()
    {
        try
        {
            if (!_disposed) SweepAbandoned();
        }
        catch (Exception ex)
        {
            try { logger.LogError(ex, "The sweep of abandoned blind seed uploads failed; it is tried again in {Minutes} minutes", SweepEvery.TotalMinutes); }
            catch (Exception) { /* a callback must not throw, whatever the log does */ }
        }
    }

    /// <summary>
    /// The wipe's half: a seed that is being received is stopped (the part is cancelled, its file closed), the one that is being applied is awaited - never cut in the middle of a
    /// switch - and no seed starts until the returned handle is disposed. Throws <see cref="TimeoutException"/> when the seed does not leave within <paramref name="timeout"/> (the
    /// service is open again then). A seed whose authority was checked before the wipe cannot be applied to the emptied node: it either ends before the wipe starts to delete, or does
    /// not run at all (the authority is checked again under the gate, by a wiped node's cleared secret).
    /// </summary>
    public async Task<IDisposable> QuiesceAsync(TimeSpan timeout, CancellationToken ct)
    {
        CancellationTokenSource stopping;
        lock (_barrier)
        {
            _quiesced = true;
            stopping = _stop;
        }
        stopping.Cancel();
        bool entered;
        try
        {
            entered = await _gate.WaitAsync(timeout, ct);
        }
        catch
        {
            Reopen();
            throw;
        }
        if (!entered)
        {
            Reopen();
            throw new TimeoutException("A seed did not stop in time.");
        }
        return new SeedBarrier(this);
    }

    private void Reopen()
    {
        lock (_barrier)
        {
            _quiesced = false;
            if (_stop.IsCancellationRequested) _stop = new CancellationTokenSource();
        }
    }

    private sealed class SeedBarrier(BlindSeedService service) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            service.Reopen();
            service._gate.Release();
        }
    }

    private long MaxPackageBytes =>
        long.TryParse(config["BMB_BLIND_SEED_MAX_BYTES"], out var max) && max > 0 ? max : DefaultMaxPackageBytes;

    public BlindSeedProgress? GetProgress(Guid seedId) =>
        _active is { } a && a.SeedId == seedId ? new BlindSeedProgress(a.SeedId, a.Received, a.Total, false) : null;

    /// <summary>
    /// Appends one part at <paramref name="offset"/>. Returns the progress; when the last part
    /// completes the package, it is verified and applied before this returns.
    /// </summary>
    /// <param name="announcedBytes">The announced length of this part, or null (a chunked body).</param>
    /// <exception cref="BlindStoragePausedException">
    /// The node's storage limit is reached by what it holds plus what this seed has staged (or will with this part): the whole staging of the seed is removed, as for any refusal.
    /// </exception>
    public async Task<BlindSeedProgress> ReceiveAsync(
        Guid seedId, long offset, long total, string sha256, BlindSeedAuthority authority,
        Stream body, CancellationToken ct, long? announcedBytes = null)
    {
        if (total <= 0 || total > MaxPackageBytes)
            throw new BlindSeedRejectedException(BlindSeedRefusal.SizeOutOfRange);
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new BlindSeedRejectedException(BlindSeedRefusal.HashMalformed);

        CancellationTokenSource stop;
        lock (_barrier)
        {
            if (_quiesced) throw new BlindSeedWipingException();
            stop = _stop;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        try
        {
            await _gate.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new BlindSeedWipingException();
        }
        Upload? staging = null;
        try
        {
            // The pair code is checked again here, under the gate every part and the switch run
            // under: the endpoint checked the proof before waiting for the gate, and an upload with the
            // same proof that got the gate first may have seeded and spent the code meanwhile. Only the
            // first one switches the database and grants authority (review l-root2 #1).
            if (authority is BlindSeedAuthority.PairingSecret pairing)
            {
                using var scope = scopeFactory.CreateScope();
                if (!await scope.ServiceProvider.GetRequiredService<BlindPairing>().IsCurrentCodeAsync(pairing.CodeId))
                    throw new BlindSeedAuthorityGoneException();
            }
            var upload = staging = Admit(seedId, total, sha256.ToLowerInvariant(), authority);
            if (offset != upload.Received)
                throw new BlindSeedOffsetException(upload.Received);

            // The storage limit, with exact numbers: what the node holds without this seed's own staging, against the WHOLE package it announced (it is known from the first part: a
            // seed that cannot fit is refused before any byte is staged, not after the owner's disk has taken most of it), the bytes staged so far and this part. A part that does
            // not announce its length is watched as it arrives (below), for what the node grows by meanwhile. The room a seed needs is the old data AND the new: the data the node
            // holds now is counted until the switch replaces it. (The volume's free space for the apply is a separate check, SpaceFactor, in Admit.)
            var room = limits?.StagingRoom(SeedDir(upload.SeedId)) ?? BlindStagingRoom.Unlimited;
            if (room.Judge(Math.Max(total, upload.Received + Math.Max(0, announcedBytes ?? 0))) is { } before)
                throw new BlindStoragePausedException(before);

            // At the recorded offset, not at the end of the file: a part that failed half-way (a dropped
            // connection, a cancel) left bytes on disk that were never counted, and its retry comes
            // again from upload.Received. Appending would put it after them (review L-merge #1).
            await using (var file = new FileStream(upload.Path, FileMode.OpenOrCreate, FileAccess.Write))
            {
                file.SetLength(upload.Received);
                file.Seek(upload.Received, SeekOrigin.Begin);
                var buffer = new byte[81920];
                long chunk = 0;
                int read;
                while ((read = await body.ReadAsync(buffer, linked.Token)) > 0)
                {
                    chunk += read;
                    if (chunk > MaxChunkBytes || upload.Received + chunk > upload.Total)
                    {
                        file.SetLength(upload.Received);
                        throw new BlindSeedRejectedException(BlindSeedRefusal.PartTooLarge);
                    }
                    if (room.Judge(upload.Received + chunk) is { } reached)
                        throw new BlindStoragePausedException(reached);
                    await file.WriteAsync(buffer.AsMemory(0, read), linked.Token);
                }
                upload.Received += chunk;
            }
            upload.LastTouched = _time.GetUtcNow().UtcDateTime;

            if (upload.Received < upload.Total)
                return new BlindSeedProgress(upload.SeedId, upload.Received, upload.Total, false);

            try
            {
                // The apply's peak (the unpacked package, the new database, the staged media) is the seed's own work: the door's walk leaves it out until the apply is over.
                using var applying = limits?.ExcludeFromDoor(BlindSeedCutover.DirOf(dataPath));
                await ApplyAsync(upload, ct);
            }
            finally
            {
                // Applied or refused, this upload is over: a refused package is not retried
                // byte-for-byte, the sender starts a new seed.
                EndUpload(upload);
            }
            return new BlindSeedProgress(upload.SeedId, upload.Total, upload.Total, true);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The wipe stopped the part that was arriving (its file is closed by here); the wipe removes what was staged.
            throw new BlindSeedWipingException();
        }
        catch (BlindStoragePausedException) when (staging is not null)
        {
            // Held back by the node's own limit: what the seed staged is the very thing that would fill the disk, so it goes now (the file of the part is closed by here). The full
            // node reads the 503 as a pause and sends the seed again from its start when it can.
            EndUpload(staging);
            logger.LogInformation("Blind seed {SeedId} was held back by the storage limit; the {Staged} bytes it had staged were removed", staging.SeedId, staging.Received);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Upload Admit(Guid seedId, long total, string sha256, BlindSeedAuthority authority)
    {
        // An upload whose staging is gone (a wipe removed it) is over: it must not hold the node for an hour.
        if (_active is { } gone && !File.Exists(gone.Path)) EndUpload(gone, deleteStaging: false);
        if (_active is { } active)
        {
            if (active.SeedId == seedId)
            {
                // Resuming must not change what is being uploaded, or by whom.
                if (active.Total != total || active.Sha256 != sha256 || active.Authority != authority)
                    throw new BlindSeedRejectedException(BlindSeedRefusal.ResumeMismatch);
                return active;
            }
            if (_time.GetUtcNow().UtcDateTime - active.LastTouched < AbandonedAfter)
                throw new BlindSeedConflictException(active.SeedId);
            logger.LogWarning("Blind seed {SeedId} abandoned; a new seed {NewSeedId} replaces it", active.SeedId, seedId);
            EndUpload(active);
        }

        var dir = SeedDir(seedId);
        Directory.CreateDirectory(dir);
        var needed = (long)(total * SpaceFactor);
        // The data volume's own file system — on Linux usually a mount of its own, not "/".
        var full = Path.GetFullPath(dir);
        var free = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(full)! : full).AvailableFreeSpace;
        if (free < needed)
            throw new InsufficientStorageException(needed, free);

        var path = Path.Combine(dir, "package.tar.gz");
        File.WriteAllBytes(path, []);
        var upload = _active = new Upload(seedId, total, sha256, authority, path, _time.GetUtcNow().UtcDateTime)
        {
            // Out of the door's walk for as long as the upload lives (and an idle one is dropped by the sweep): the seed judges itself, part by part.
            Exclusion = limits?.ExcludeFromDoor(dir),
        };
        if (!_disposed) _sweep ??= _time.CreateTimer(static state => ((BlindSeedService)state!).RunSweep(), this, SweepEvery, SweepEvery);
        return upload;
    }

    private string SeedDir(Guid seedId) => Path.Combine(BlindRoleStartup.TempDir(dataPath), $"seed-{seedId:N}");

    private async Task ApplyAsync(Upload upload, CancellationToken ct)
    {
        string actual;
        await using (var file = File.OpenRead(upload.Path))
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
        if (actual != upload.Sha256)
            throw new BlindSeedRejectedException(BlindSeedRefusal.HashMismatch);

        // Step A: the package's format decides the path. Once activated, only format 2 (spec 2, 8.7).
        if (await PackageFormatAsync(upload.Path, ct) == BlindManifestV2.CurrentFormat)
        {
            await ApplyFormat2Async(upload, ct);
            return;
        }
        using (var policyScope = scopeFactory.CreateScope())
            if (policyScope.ServiceProvider.GetService<ISyncProtocolPolicy>() is { SealsMetadata: true })
                throw new BlindSeedRejectedException(BlindSeedRefusal.ActivatedTakesFormat2);

        var dir = SeedDir(upload.SeedId);
        VerifiedSnapshot package;
        BlindManifest manifest;
        try
        {
            package = await snapshots.ExtractVerifiedAsync(upload.Path, Path.Combine(dir, "package"));
            var manifestPath = Path.Combine(package.Directory, BlindManifest.FileName);
            if (!File.Exists(manifestPath))
                throw new BlindSeedRejectedException(BlindSeedRefusal.ManifestMissing);
            manifest = BlindManifest.Parse(await File.ReadAllBytesAsync(manifestPath, ct));
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException
                                       or InvalidOperationException and not BlindSeedRejectedException)
        {
            // Whatever the sender uploaded is not a package this node can take: bad archive, a file
            // that does not match its manifest hash, an unknown format. The sender's problem, not ours.
            throw new BlindSeedRejectedException(BlindSeedRefusal.PackageInvalid, ex);
        }
        if (manifest.SeedId != upload.SeedId)
            throw new BlindSeedRejectedException(BlindSeedRefusal.WrongSeed);

        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        await CheckProducerAsync(upload.Authority, manifest, package, services);
        await CheckSchemaAsync(package, services);

        var self = await services.GetRequiredService<INodeIdentityRepository>().GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");
        var liveEvents = services.GetRequiredService<IEventLogRepository>();

        // Stage everything the switch needs, and check it, before anything live is touched.
        var cutover = new BlindSeedCutover(dataPath);
        cutover.Prepare();
        // The producer's standing on this node is never its own claim in the manifest (review L-merge
        // round 2 #1): a reseed comes from a peer this node ALREADY holds as superadmin, which it stays;
        // a first seed's producer gets no row here at all — the pair code makes it superadmin after the
        // switch, on top of the signed whitelist events about it (manifest.Standing; review r1-merge #3).
        var producerStanding = upload.Authority is BlindSeedAuthority.SuperadminPeer;
        await BuildDatabaseAsync(cutover.StagedDbPath, package, manifest, self, await liveEvents.GetMaxSequenceAsync(), producerStanding);
        // A first seed's grant is versioned after everything the package brought; a package whose
        // times leave no room after them is refused here, before the switch (review l-root3).
        long? grantAt = null;
        if (upload.Authority is BlindSeedAuthority.PairingSecret)
            using (var staged = new DbConnectionFactory(cutover.StagedDbPath))
                grantAt = GrantTime(await MaxLamportHeldAsync(staged), manifest.Standing);
        cutover.StageMedia(Path.Combine(package.Directory, "media"));
        cutover.WriteMarker(manifest.SeedId, BlindSeedCutover.Staged);

        await CutOverAsync(cutover, manifest, liveEvents,
            upload.Authority is BlindSeedAuthority.PairingSecret seeder ? (seeder.SeederKeyB64, grantAt!.Value) : null, ct);

        // The new database starts with an empty tbl_blind_state, so the code is spent already;
        // said explicitly, so it stays spent whatever the swap carries over in the future.
        if (upload.Authority is BlindSeedAuthority.PairingSecret)
            await services.GetRequiredService<BlindPairing>().ConsumeAsync();
    }

    /// <summary>
    /// The package must be signed by the node that is seeding, with a key this node knew BEFORE it
    /// read the package. For a first seed that is the key the pairing secret vouched for; for a
    /// reseed it is the key this node already has for the superadmin peer the token belongs to. The
    /// manifest never supplies the key it is checked against — a package could vouch for itself.
    /// </summary>
    private static async Task CheckProducerAsync(
        BlindSeedAuthority authority, BlindManifest manifest, VerifiedSnapshot package, IServiceProvider services)
    {
        byte[] producerKey;
        switch (authority)
        {
            case BlindSeedAuthority.SuperadminPeer peer:
                // Verified against the SENDER's key, so a package some other node built — even a
                // superadmin — cannot be relayed in under this token.
                var row = await services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(peer.NodeId);
                if (row is not { IsSuperadmin: true })
                    throw new BlindSeedRejectedException(BlindSeedRefusal.NotSuperadminPeer);
                producerKey = row.Ed25519PublicKey;
                break;
            case BlindSeedAuthority.PairingSecret seeder:
                if (manifest.ProducerNodeId != seeder.SeederNodeId)
                    throw new BlindSeedRejectedException(BlindSeedRefusal.NotBuiltByPairedNode);
                // The seeder becomes this node's superadmin: only a full node can be one. A blind node
                // holds no key and authors nothing, and this node cannot seed itself (review l-root4 #2).
                if (BlindNodeId.IsBlind(seeder.SeederNodeId))
                    throw new BlindSeedRejectedException(BlindSeedRefusal.BlindCannotSeed);
                if ((await services.GetRequiredService<INodeIdentityRepository>().GetAsync())?.NodeId == seeder.SeederNodeId)
                    throw new BlindSeedRejectedException(BlindSeedRefusal.CannotSeedItself);
                producerKey = Convert.FromBase64String(seeder.SeederKeyB64);
                break;
            default:
                throw new BlindSeedRejectedException(BlindSeedRefusal.UnknownAuthority);
        }
        if (!package.IsSignedBy(producerKey))
            throw new BlindSeedRejectedException(BlindSeedRefusal.SignatureInvalid);
    }

    private static async Task CheckSchemaAsync(VerifiedSnapshot package, IServiceProvider services)
    {
        using var conn = services.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        var local = await conn.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(version), 0) FROM tbl_migration");
        if (package.MigrationVersion > local)
            throw new BlindSeedRejectedException(BlindSeedRefusal.SchemaMismatch);
    }

    private static async Task<List<SyncEvent>> ReadAllAfterAsync(IEventLogRepository events, long after)
    {
        var all = new List<SyncEvent>();
        while (true)
        {
            var page = await events.GetAllAfterSequenceAsync(after, 1000);
            if (page.Count == 0) return all;
            all.AddRange(page);
            after = page[^1].SequenceNum;
        }
    }

    /// <summary>
    /// A fresh database at the current schema holding this node's own identity, the package's
    /// replicated content, the producer's whitelist and positions. Its tbl_blind_state starts empty:
    /// that spends the pair code's secret (one seed per code) and clears a reseed request. Its event log starts empty, but
    /// its sequence continues above the old head, and a compaction record at the cut-off tells a
    /// peer that pulled from the old log below it to take a snapshot instead of silently missing
    /// events (410).
    /// </summary>
    private static async Task BuildDatabaseAsync(
        string newDbPath, VerifiedSnapshot package, BlindManifest manifest, NodeIdentity self, long oldHead,
        bool producerStanding)
    {
        using (var factory = new DbConnectionFactory(newDbPath))
        {
            await new MigrationRunner(factory).RunMigrationsAsync();
            self.InitialSyncCompleted = true;
            await new NodeIdentityRepository(factory).CreateAsync(self);

            using (var conn = (SqliteConnection)factory.CreateConnection())
            {
                using (var fkOff = conn.CreateCommand())
                {
                    fkOff.CommandText = "PRAGMA foreign_keys = OFF";
                    fkOff.ExecuteNonQuery();
                }
                using (var attach = conn.CreateCommand())
                {
                    attach.CommandText = $"ATTACH DATABASE '{package.DatabasePath.Replace("'", "''")}' AS snap";
                    attach.ExecuteNonQuery();
                }
                using (var tx = conn.BeginTransaction())
                {
                    foreach (var table in SnapshotTables.Replicated.Concat(BlindPackageBuilder.ExtraTables))
                    {
                        if (SnapshotTableImport.SnapshotHasTable(conn, tx, table))
                            SnapshotTableImport.CopyTable(conn, tx, table, orIgnore: true);
                    }
                    SnapshotTableImport.AdoptLegacyInlineCiphertext(conn, tx);

                    var cutoff = manifest.IncludesUpTo ?? oldHead;
                    if (oldHead > 0)
                        conn.Execute("INSERT INTO sqlite_sequence (name, seq) VALUES ('tbl_event', @oldHead)",
                            new { oldHead }, tx);
                    if (cutoff > 0)
                        conn.Execute(
                            @"INSERT INTO tbl_compaction_log (compacted_at, cp_before, cp_after, events_removed, reason)
                              VALUES (@at, NULL, @cutoff, 0, @reason)",
                            new { at = DateTime.UtcNow.ToString("O"), cutoff, reason = $"blind seed {manifest.SeedId}" }, tx);
                    tx.Commit();
                }
                using var detach = conn.CreateCommand();
                detach.CommandText = "DETACH DATABASE snap";
                detach.ExecuteNonQuery();
            }

            var whitelist = new WhitelistRepository(factory);
            foreach (var peer in manifest.Whitelist.Where(p => p.NodeId != self.NodeId
                         && (producerStanding || p.NodeId != manifest.ProducerNodeId)))
            {
                await whitelist.CreateAsync(new WhitelistEntry
                {
                    NodeId = peer.NodeId,
                    DisplayName = peer.DisplayName,
                    Ed25519PublicKey = Convert.FromBase64String(peer.PublicKeyB64),
                    ApiAddress = BeeMemoryBank.Sync.PeerAddressPolicy.ForStorage(peer.ApiAddress),
                    IsSuperadmin = peer.NodeId == manifest.ProducerNodeId || peer.IsSuperadmin,
                    TlsSpki = BlindTrust.PinOf(peer.TlsTrust, peer.TlsSpki),
                    TlsTrust = BlindTrust.Effective(peer.TlsTrust, peer.TlsSpki),
                    Status = "A",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    LamportTs = peer.LamportTs,
                    SourceNodeId = peer.SourceNodeId
                });
            }

            var positions = new SyncPositionRepository(factory);
            foreach (var p in manifest.Positions.Where(p => p.RemoteNodeId != self.NodeId))
                await positions.UpsertAsync(new SyncPosition
                {
                    RemoteNodeId = p.RemoteNodeId, LastSequenceNum = p.LastSequence, UpdatedAt = DateTime.UtcNow
                });
        }
        // The factory's Dispose cleared its pool; nothing holds the new file open now.
    }

    /// <summary>
    /// The switch (review L-stage1 #4). Event writes are held off and the ones running drained first
    /// (EventWriteGate), requests answer 503, and only then is the final tail read — so no event can
    /// arrive between reading it and moving the file. The old database stays whole beside the new one
    /// until the replay has succeeded; a replay that cannot finish (the event may apply later, e.g. its
    /// author is not in the new whitelist yet) rolls the node back to it and refuses the seed, which the
    /// sender can send again. The durable marker lets a start after a crash finish or roll back.
    /// </summary>
    private async Task CutOverAsync(
        BlindSeedCutover cutover, BlindManifest manifest, IEventLogRepository liveEvents,
        (string KeyB64, long GrantAt)? paired, CancellationToken ct)
    {
        var livePath = Path.Combine(dataPath, "beememorybank.db");
        using (await EventWriteGate.Instance.QuiesceAsync(ct))
        {
            // This flow replays into the new database while everyone else waits; the mark lasts as
            // long as the gate is held, and no longer.
            using var ownerFlow = EventWriteGate.EnterOwnerFlow();
            maintenance.Enter("Blind node is taking in a seed");
            await HeavyOperationLock.Instance.WaitAsync();
            try
            {
                // The media tree this cutover is about to rename aside, move into and later delete is
                // also what a restic backup reads (Codex round 2, security #7): block new jobs and wait
                // for the one running, so no reader holds files in it while the directories move — on
                // Windows the rename fails under an open handle, on Linux the reader captures a
                // half-moved tree. Taken with the wipe's own gate, so a wipe and a cutover serialize.
                using var exclusive = await jobs.TryBeginExclusiveAsync(_cutoverDrainWait)
                    ?? throw new BlindSeedRejectedException(BlindSeedRefusal.DrainTimeout);

                // Every connection to the live database takes part in the switch (review l-root4 #1):
                // new ones wait, open ones are waited for, and the switch goes ahead only once none is
                // left, so no reader or writer keeps the moved file open. This flow's own connections
                // are the exception — they are the switch. A connection still open after the wait makes
                // the seed fail here, before anything moved; the sender sends it again.
                using var liveScope = scopeFactory.CreateScope();
                var live = liveScope.ServiceProvider.GetRequiredService<DbConnectionFactory>();
                using var quiesced = live.BeginQuiesce();
                try
                {
                    await live.WaitDrainedAsync(CutoverDrainWait, ct);
                }
                catch (TimeoutException)
                {
                    throw new BlindSeedRejectedException(BlindSeedRefusal.DrainTimeout);
                }

                // Plan 5.2: the reseeding node pulled everything up to IncludesUpTo before cutting the
                // package; what this node received after that is in no package and is replayed below.
                var finalHead = await liveEvents.GetMaxSequenceAsync();
                var tail = manifest.IncludesUpTo is { } upTo ? await ReadAllAfterAsync(liveEvents, upTo) : [];
                RaiseSequence(cutover.StagedDbPath, finalHead);
                await CarryExposureAsync(cutover.StagedDbPath);

                using (var scope = scopeFactory.CreateScope())
                using (var conn = (SqliteConnection)scope.ServiceProvider
                           .GetRequiredService<IDbConnectionFactory>().CreateConnection())
                {
                    using var checkpoint = conn.CreateCommand();
                    checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                    checkpoint.ExecuteNonQuery();
                }

                cutover.WriteMarker(manifest.SeedId, BlindSeedCutover.Switching);
                // From here until "done", ANY failure puts the old database and media back before the
                // gate opens again (review L-stage1 round 2 #3); if even that fails, the marker stays
                // "switching" and the next start rolls back (and no new seed discards it meanwhile).
                try
                {
                    await SnapshotService.SwapDbFileWithRetryAsync(cutover.SwitchFiles, ClearLivePool, logger);
                    FolderAccessService.InvalidateAll();
                    if (paired is { } p)
                        await ApplyFirstSeedStandingAsync(manifest.ProducerNodeId,
                            manifest.Whitelist.Single(peer => peer.NodeId == manifest.ProducerNodeId), manifest.Standing,
                            p.KeyB64, p.GrantAt);
                    // The replay says whether it failed, never why: what an event's apply threw is logged by the replay itself (the exception object, not its text).
                    if (!await ReplayAsync(tail))
                        throw new BlindSeedRejectedException(BlindSeedRefusal.ReplayFailed);
                    cutover.MarkDone(manifest.SeedId);
                }
                catch (Exception ex)
                {
                    try
                    {
                        await SnapshotService.SwapDbFileWithRetryAsync(cutover.RollBack, ClearLivePool, logger);
                        FolderAccessService.InvalidateAll();
                    }
                    catch (Exception rollbackFailure)
                    {
                        logger.LogCritical(rollbackFailure,
                            "Blind seed {SeedId}: rolling back the switch failed; the next start rolls it back", manifest.SeedId);
                        throw new BlindSeedRejectedException(BlindSeedRefusal.SwitchNotUndone);
                    }
                    if (ex is BlindSeedRejectedException) throw;
                    logger.LogWarning(ex, "Blind seed {SeedId}: the switch failed; rolled back", manifest.SeedId);
                    throw new BlindSeedRejectedException(BlindSeedRefusal.SwitchFailed, ex);
                }

                // Done is durable: tidying up the old copies may fail without undoing anything — the
                // next start finishes it.
                try { cutover.FinishDone(); }
                catch (Exception ex) { logger.LogWarning(ex, "Blind seed {SeedId}: tidying up after the switch failed; the next start finishes it", manifest.SeedId); }
                logger.LogInformation(
                    "Blind seed {SeedId} from {Producer} applied (cp {Cp}, includes up to {UpTo}); {Tail} own events replayed",
                    manifest.SeedId, manifest.ProducerNodeId, manifest.CpSequence, manifest.IncludesUpTo, tail.Count);
            }
            finally
            {
                HeavyOperationLock.Instance.Release();
                maintenance.Exit();
            }
        }
        snapshots.CleanupOrphanMediaFiles();
    }

    /// <summary>
    /// The DEK-exposure alarm (BlindState.DekExposureKey) is cleared by an operator only. A reseed
    /// replaces the database, not the fact that copies of the old one hold an envelope for this node's
    /// key — so the alarm goes into the new database (review L-stage1 round 2 #2).
    /// </summary>
    private async Task CarryExposureAsync(string stagedDbPath)
    {
        string? exposure;
        using (var scope = scopeFactory.CreateScope())
            exposure = await new BlindState(scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>()).GetDekExposureAsync();
        if (exposure is null) return;
        using var conn = new SqliteConnection($"Data Source={stagedDbPath};Pooling=False");
        conn.Open();
        conn.Execute(
            @"INSERT INTO tbl_blind_state (key, value, updated_at) VALUES (@key, @exposure, @now)
              ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at",
            new { key = BlindState.DekExposureKey, exposure, now = DateTime.UtcNow.ToString("O") });
        conn.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
    }

    /// <summary>
    /// The new log continues above the FINAL head of the old one, read under the gate: a peer that
    /// pulled an event which arrived while the package was being built must not find a replayed event
    /// at a sequence it has already passed.
    /// </summary>
    private static void RaiseSequence(string dbPath, long finalHead)
    {
        if (finalHead <= 0) return;
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        conn.Open();
        conn.Execute("UPDATE sqlite_sequence SET seq = MAX(seq, @finalHead) WHERE name = 'tbl_event'", new { finalHead });
        conn.Execute(
            "INSERT INTO sqlite_sequence (name, seq) SELECT 'tbl_event', @finalHead WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'tbl_event')",
            new { finalHead });
        // Only the main file is moved into place: nothing may stay behind in a WAL next to it.
        conn.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
    }

    /// <summary>
    /// Plan 5.2: events this node received after the reseeding node's cut-off, applied on top of the
    /// new database. Each is still signed by its author, so it goes through the ordinary applier. One
    /// that can never apply is quarantined, as ordinary sync would; the first that could apply later
    /// stops the replay and is returned — the caller rolls back rather than drop it.
    /// </summary>
    /// <summary>
    /// A first seed's producer is this node's authority because the operator made it so (review r1-merge
    /// #3): the pair code was issued at this node's console for the device that uses it to manage this
    /// node (<see cref="BlindPairing.AuthorityNotice"/>), and the seeder proof showed that this producer,
    /// with the key it signs with, used it. So it becomes superadmin here, bound to that key, whatever its
    /// manifest says about itself or about its network.
    ///
    /// <para>The whitelist events about it that the manifest carries — its admission, later updates and
    /// revokes, in order (review L-merge round 3 #1, #2) — still go through the ordinary applier first.
    /// Each counts only if its author is superadmin here and its signature holds; an admission for another
    /// key than the paired one is not applied. They leave the row at the version the network last signed
    /// for it, and the grant keeps that version, never one from the manifest: from here on only a signed
    /// standing event newer than it — a demotion, a revoke — changes the producer's authority, through the
    /// same applier.</para>
    ///
    /// <para>The grant is a decision this node takes at the switch, and the row carries it as its version
    /// (review l-root2 #2): <paramref name="grantAt"/>, the Lamport time right after everything this node
    /// holds once the package is in — every versioned row the package brought (a package's log starts
    /// empty, its rows do not) and the standing stream (<see cref="GrantTime"/>) — stamped with this
    /// node's id. It is taken from the raw maximum, not through the Lamport clock, whose update caps a
    /// jump (review l-root3); the clock is not moved: this node authors no events. A signed event about the producer that the
    /// package did not carry and that is not newer than what the package reflected (a demotion no reachable
    /// peer had passed on) loses against it; a newer one wins, as everywhere. Lamport time cannot tell
    /// "issued before the code" from "issued after" by a node that has seen none of the producer's recent
    /// events, so an event with a higher clock still counts as newer.</para>
    /// </summary>
    private async Task ApplyFirstSeedStandingAsync(
        Guid producerNodeId, BlindManifestPeer claimed, IReadOnlyList<SyncEvent>? standing, string pairedKeyB64, long grantAt)
    {
        using var scope = scopeFactory.CreateScope();
        var applier = scope.ServiceProvider.GetRequiredService<EventApplier>();
        foreach (var evt in standing ?? [])
        {
            if (evt.EventType == EventTypes.WhitelistAdd
                && JsonSerializer.Deserialize<WhitelistAddPayload>(evt.Payload)?.PublicKeyB64 != pairedKeyB64)
            {
                logger.LogWarning("Blind seed: standing event {EventId} admits another key than the paired one; not applied", evt.EventId);
                continue;
            }
            try
            {
                await applier.ApplyAsync(evt);
            }
            catch (Exception ex)
            {
                logger.LogInformation(ex, "Blind seed: standing event {EventId} ({Type}) not accepted; no authority from it",
                    evt.EventId, evt.EventType);
            }
        }

        var whitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        // CheckProducerAsync refused a blind seeder already; the grant keeps the invariant on its own.
        if (BlindNodeId.IsBlind(producerNodeId))
            throw new InvalidOperationException("A blind node is never a superadmin; nothing is switched.");
        var pairedKey = Convert.FromBase64String(pairedKeyB64);
        var now = DateTime.UtcNow;
        var self = await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");
        // Every row the standing stream left is in the staged database the grant time was taken from,
        // so the grant is newer than any of them.
        var grant = new RowVersion(grantAt, self.NodeId);
        if (await whitelist.GetByNodeIdAsync(producerNodeId, includeDeleted: true) is { } row)
        {
            // Where to reach it is not standing: the manifest's address and pin stand.
            if (!ConflictResolver.IncomingWins(row.Version, grant))
                throw new InvalidOperationException(
                    $"The producer's row ({row.LamportTs}) is not older than the grant ({grantAt}); nothing is switched.");
            row.LamportTs = grant.LamportTs;
            row.SourceNodeId = grant.SourceNodeId;
            row.Status = "A";
            row.IsSuperadmin = true;
            row.Ed25519PublicKey = pairedKey;
            row.ApiAddress = BeeMemoryBank.Sync.PeerAddressPolicy.ForStorage(claimed.ApiAddress) ?? row.ApiAddress;
            ApplyClaimedTrust(row, claimed);
            row.UpdatedAt = now;
            await whitelist.UpdateAsync(row);
        }
        else
        {
            await whitelist.CreateAsync(new WhitelistEntry
            {
                NodeId = claimed.NodeId,
                DisplayName = claimed.DisplayName,
                Ed25519PublicKey = pairedKey,
                ApiAddress = BeeMemoryBank.Sync.PeerAddressPolicy.ForStorage(claimed.ApiAddress),
                IsSuperadmin = true,
                TlsSpki = BlindTrust.PinOf(claimed.TlsTrust, claimed.TlsSpki),
                TlsTrust = BlindTrust.Effective(claimed.TlsTrust, claimed.TlsSpki),
                Status = "A",
                CreatedAt = now,
                UpdatedAt = now,
                LamportTs = grant.LamportTs,
                SourceNodeId = grant.SourceNodeId
            });
        }
        logger.LogInformation("Blind seed: {Producer} used this node's pair code; it manages this node from now on (superadmin)",
            producerNodeId);
    }

    /// <summary>
    /// How the manifest says the producer's endpoint is trusted, onto the producer's row. The mode the manifest names is the
    /// authority: the row takes it, and the pin only if that mode is <c>pin</c> — so a row moved to the normal certificate (or to a
    /// mode this build does not know) does not keep the old key behind it. A manifest that says nothing usable (no mode and no
    /// pin, or <c>pin</c> with no key) leaves the row's own trust as it is.
    /// </summary>
    internal static void ApplyClaimedTrust(WhitelistEntry row, BlindManifestPeer claimed)
    {
        var trust = BlindTrust.Effective(claimed.TlsTrust, claimed.TlsSpki);
        if (trust is null || (trust == BlindTrust.Pin && string.IsNullOrEmpty(claimed.TlsSpki))) return;
        row.TlsTrust = trust;
        row.TlsSpki = BlindTrust.PinOf(trust, claimed.TlsSpki);
    }

    /// <summary>
    /// The grant's Lamport time: one after the highest time the package holds (its rows) or carries (its
    /// standing stream), in checked arithmetic. It must remain below the exclusive restore ceiling, because
    /// a seeded state must be recoverable through the normal restore path as well.
    /// </summary>
    private static long GrantTime(long maxHeld, IReadOnlyList<SyncEvent>? standing)
    {
        var max = Math.Max(maxHeld, (standing ?? []).Select(e => e.LamportTs).DefaultIfEmpty(0).Max());
        if (max >= BlindManifestV2.MaximumFirstSeedGrant)
            throw new BlindSeedRejectedException(BlindSeedRefusal.LamportNoRoom);
        return checked(max + 1);
    }

    /// <summary>
    /// How long a switch waits for the connections to the live database to close, unless
    /// <c>BeeMemoryBank:BlindSeed:CutoverDrainWaitSeconds</c> says otherwise. Configurable because
    /// the timeout path is the one that must fail closed, and proving that in a test should cost
    /// seconds rather than a minute of wall clock and a real stuck reader.
    /// </summary>
    public static readonly TimeSpan DefaultCutoverDrainWait = TimeSpan.FromSeconds(60);

    /// <summary>Configuration key for <see cref="DefaultCutoverDrainWait"/>.</summary>
    public const string CutoverDrainWaitSecondsKey = "BeeMemoryBank:BlindSeed:CutoverDrainWaitSeconds";

    private readonly TimeSpan _cutoverDrainWait =
        TimeSpan.FromSeconds(Math.Max(1, config.GetValue(CutoverDrainWaitSecondsKey, (int)DefaultCutoverDrainWait.TotalSeconds)));

    /// <summary>The drain wait this instance actually uses.</summary>
    public TimeSpan CutoverDrainWait => _cutoverDrainWait;

    /// <summary>
    /// Closes the pooled connections to the live database, so its files can be switched. Only that
    /// pool: <see cref="SqliteConnection.ClearAllPools"/> would also close every other database's idle
    /// connections in the process, and one being handed out at that moment fails with
    /// ObjectDisposedException (seen in the integration tests, where many nodes share a process).
    /// </summary>
    private void ClearLivePool()
    {
        using var scope = scopeFactory.CreateScope();
        using var live = new SqliteConnection(scope.ServiceProvider.GetRequiredService<DbConnectionFactory>().DatabaseId);
        SqliteConnection.ClearPool(live);
    }

    /// <summary>
    /// The highest Lamport time of any row this node holds, in every table that versions its rows. With
    /// <paramref name="afterPurge"/> only of the rows the first adoption's purge leaves (spec 9.3): the operational tables, and
    /// of the log everything but its protocol-1..3 content rows.
    /// </summary>
    private static async Task<long> MaxLamportHeldAsync(IDbConnectionFactory db, bool afterPurge = false)
    {
        using var conn = db.CreateConnection();
        var tables = await conn.QueryAsync<string>(
            @"SELECT m.name FROM sqlite_master m
              WHERE m.type = 'table' AND EXISTS (SELECT 1 FROM pragma_table_info(m.name) WHERE name = 'lamport_ts')");
        long max = 0;
        foreach (var table in tables.Where(t => !afterPurge || BaselineStore.OperationalTables.Contains(t)))
        {
            var where = afterPurge && table == "tbl_event" ? " WHERE NOT (protocol_version < @p AND event_type IN @sealable)" : "";
            max = Math.Max(max, await conn.ExecuteScalarAsync<long?>(
                $"SELECT MAX(lamport_ts) FROM \"{table.Replace("\"", "\"\"")}\"{where}",
                new { p = EventClasses.SealedProtocol, sealable = EventClasses.Sealable.ToArray() }) ?? 0);
        }
        return max;
    }

    /// <summary>Replays this node's own events on top of the new database. True when every one applied or was quarantined; false when one did not apply (the caller rolls the switch back).</summary>
    private async Task<bool> ReplayAsync(List<SyncEvent> tail)
    {
        if (tail.Count == 0) return true;
        using var scope = scopeFactory.CreateScope();
        var applier = scope.ServiceProvider.GetRequiredService<EventApplier>();
        var quarantine = scope.ServiceProvider.GetRequiredService<ISyncQuarantineRepository>();
        var scopeHolder = scope.ServiceProvider.GetRequiredService<CallerScopeHolder>();

        // The replay is a sync apply and runs under the system scope, exactly as the batch handler's
        // is (SyncEndpoints: RunAsSystemAsync around its apply loop). Without it the apply runs under
        // the *seeding peer's* caller scope, which allows no folder path at all -- so an event whose
        // article sits in a folder this blind node does not have yet (a phone push during the reseed
        // window, anything in a new folder) is refused by FolderRepository.ThrowIfWriteDenied,
        // classified permanent and quarantined: what the blind node held beyond the package, and in
        // no package, is then lost on it for good.
        return await scopeHolder.RunAsSystemAsync(async () =>
        {
            foreach (var evt in tail)
            {
                try
                {
                    await applier.ApplyAsync(evt);
                }
                catch (Exception ex) when (SyncFailureClassifier.Classify(ex) == SyncFailureKind.Permanent)
                {
                    // The quarantine list is shown on a screen: the kind of failure, never the exception's own text.
                    await quarantine.RecordFailureAsync(evt.EventId, evt.EventType, evt.NodeId,
                        $"Blind reseed replay: {ex.GetType().Name}", SyncFailureKind.Permanent);
                    logger.LogWarning(ex, "Blind reseed: own event {EventId} ({Type}) can never apply; quarantined", evt.EventId, evt.EventType);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Blind reseed: own event {EventId} ({Type}) did not apply; rolling back", evt.EventId, evt.EventType);
                    return false;
                }
            }
            return true;
        });
    }

    private void TryDeleteTree(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not remove seed working directory {Dir}", dir); }
    }
}

/// <summary>Not enough free space for a seed of this size (plan 4.3): answered before the upload.</summary>
public sealed class InsufficientStorageException(long needed, long free)
    : IOException($"A seed of this size needs about {needed / (1024 * 1024)} MB free here; {free / (1024 * 1024)} MB are.")
{
    public long Needed { get; } = needed;
    public long Free { get; } = free;
}
