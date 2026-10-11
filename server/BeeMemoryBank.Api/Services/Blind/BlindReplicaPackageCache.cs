using BeeMemoryBank.Hosting.AspNetCore;
using BeeMemoryBank.Sync.Blind.Format2;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Keeps one immutable replica package available long enough for an Android download to resume.
/// A range retry has to refer to the exact same bytes and detached signature as its first request;
/// rebuilding on every request makes a correct Range client append a different archive.
///
/// <para>Step A (docs/design/step-a-spec.md 8.7): once activated a node serves format 2, a <c>full</c> package and, for a
/// phone that names the baseline it holds, a <c>baseline</c> package. Each kind of each baseline is its own cached entry,
/// with the same lifetime and leases as the format-1 one; a new baseline is a new key, so a resume never gets other bytes.</para>
/// </summary>
public sealed class BlindReplicaPackageCache(
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<BlindReplicaPackageCache>? logger = null) : IAsyncDisposable, IHostedService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    // Its own directory next to snapshots/: listing, retention, delete and compaction only read the
    // snapshots directory, so a leased replica package can never be counted, pruned or listed there.
    private const string DirectoryName = "blind-replica";
    // Who may ask for the package how often. A hub is reachable from the internet (ADR 0007): the package is cached and
    // built once per Lifetime, so what a valid blind token can still cost is bandwidth, and that is bounded per peer. A
    // first load with a few resumes of a flaky connection stays far below it.
    private readonly SlidingWindowRateLimiter _admission = new(AdmissionsPerWindow, AdmissionWindow);
    public const int AdmissionsPerWindow = 20;
    public static readonly TimeSpan AdmissionWindow = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<Entry> _retired = [];
    private Entry? _entry;
    private readonly Dictionary<string, Entry> _v2 = new(StringComparer.Ordinal);
    private ITimer? _expiryTimer;
    private int _disposed;

    /// <summary>True if <paramref name="peer"/> may start another package download now; a refused ask is not counted against it.</summary>
    public bool TryAdmit(Guid peer) => _admission.TryAcquire(peer.ToString("N"));

    public async Task StartAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        CleanupAbandonedPackages();
        var timer = time.CreateTimer(static state => ((BlindReplicaPackageCache)state!).OnExpiryTimer(), this,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (Interlocked.CompareExchange(ref _expiryTimer, timer, null) is not null)
            timer.Dispose();
        await _gate.WaitAsync(ct);
        try
        {
            if (_entry is { } entry) ScheduleExpiryLocked(entry);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct) => await DisposeAsync();

    public Task<BlindPackage> GetAsync(bool producerIsSuperadmin, CancellationToken ct) =>
        GetAsync(_ => Task.FromResult(producerIsSuperadmin), ct);

    /// <param name="producerIsSuperadmin">Asked only when a package is built. A cached package is served
    /// for its whole life without asking: the answer can flip between a download and its Range resume
    /// (a peer briefly unreachable), and the resume must continue the same signed bytes.</param>
    public async Task<BlindPackage> GetAsync(Func<CancellationToken, Task<bool>> producerIsSuperadmin, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return (await GetCurrentLockedAsync(producerIsSuperadmin, ct)).Package!;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Acquires a package for an HTTP response. The lease remains live until the response completes,
    /// so replacing the cache never removes a file while Kestrel is still streaming it.
    /// </summary>
    public Task<Lease> AcquireAsync(bool producerIsSuperadmin, CancellationToken ct) =>
        AcquireAsync(_ => Task.FromResult(producerIsSuperadmin), ct);

    /// <inheritdoc cref="GetAsync(Func{CancellationToken, Task{bool}}, CancellationToken)"/>
    public async Task<Lease> AcquireAsync(Func<CancellationToken, Task<bool>> producerIsSuperadmin, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var entry = await GetCurrentLockedAsync(producerIsSuperadmin, ct);
            entry.Readers++;
            return new Lease(this, entry);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _expiryTimer, null)?.Dispose();
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _gate.WaitAsync();
        try
        {
            if (_entry is { } entry)
            {
                _entry = null;
                RetireLocked(entry);
            }
            foreach (var v2 in _v2.Values.ToList())
                RetireLocked(v2);
            _v2.Clear();
            foreach (var retiredEntry in _retired)
                DeletePackageFiles(retiredEntry.Served);
            _retired.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Retires the format-1 package, the readable copy a phone may have asked for in the last <see cref="Lifetime"/> (docs/design/step-a-spec.md 5.5, 9.3):
    /// once the metadata activation is applied a node serves format 2 only, and nothing readable may stay on the disk until the entry would expire.
    /// An HTTP response that still leases it finishes first; the files go with the last lease.
    /// </summary>
    public async Task RetireFormat1Async(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_entry is not { } entry) return;
            _entry = null;
            RetireLocked(entry);
            ScheduleEarliestLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Retires an expired package without interrupting an HTTP response that still leases it.</summary>
    public async Task SweepExpiredAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var (key, v2) in _v2.ToList())
                if (time.GetUtcNow() - v2.CreatedAt >= Lifetime)
                {
                    _v2.Remove(key);
                    RetireLocked(v2);
                }
            if (_entry is not { } entry)
            {
                ScheduleEarliestLocked();
                return;
            }
            if (time.GetUtcNow() - entry.CreatedAt >= Lifetime)
            {
                _entry = null;
                RetireLocked(entry);
            }
            ScheduleEarliestLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Entry> GetCurrentLockedAsync(Func<CancellationToken, Task<bool>> producerIsSuperadmin, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (_entry is { } cached
            && now - cached.CreatedAt < Lifetime
            && File.Exists(cached.Served.FilePath)
            && File.Exists(cached.Served.FilePath + ".sig"))
        {
            return cached;
        }

        var isSuperadmin = await producerIsSuperadmin(ct);
        using var scope = scopes.CreateScope();
        var package = await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
            .BuildAsync(Guid.NewGuid(), includesUpTo: null, isSuperadmin, ct,
                outputDirectory: PackageDirectory(scope.ServiceProvider.GetRequiredService<SnapshotService>()));
        var replacement = new Entry(ServedPackage.Of(package), now, package);
        var superseded = _entry;
        _entry = replacement;
        if (superseded is not null)
            RetireLocked(superseded);
        ScheduleExpiryLocked(replacement);
        return replacement;
    }

    /// <summary>
    /// A format-2 package of <paramref name="kind"/> for <paramref name="baseline"/> (spec 8.7), leased like the format-1 one:
    /// built once per baseline and kind and lifetime, the same signed bytes for every request and resume until it expires.
    /// </summary>
    public async Task<Lease> AcquireV2Async(string kind, ServableBaseline baseline, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var key = kind + ":" + baseline.Files.Manifest.BaselineId.ToString("N");
            var now = time.GetUtcNow();
            if (!_v2.TryGetValue(key, out var entry)
                || now - entry.CreatedAt >= Lifetime
                || !File.Exists(entry.Served.FilePath) || !File.Exists(entry.Served.FilePath + ".sig"))
            {
                using var scope = scopes.CreateScope();
                var package = await scope.ServiceProvider.GetRequiredService<IBlindPackageV2Writer>().WriteAsync(
                    new BlindPackageV2Request(kind, Guid.NewGuid(), baseline, IncludesUpTo: null,
                        IncludeBlobs: kind == BlindPackageKinds.Full,
                        OutputDirectory: PackageDirectory(scope.ServiceProvider.GetRequiredService<SnapshotService>())), ct);
                var replacement = new Entry(new ServedPackage(package.FilePath, package.Sha256, package.Manifest.SeedId), now);
                if (entry is not null) RetireLocked(entry);
                _v2[key] = replacement;
                entry = replacement;
                ScheduleEarliestLocked();
            }
            entry.Readers++;
            return new Lease(this, entry);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ScheduleEarliestLocked()
    {
        var earliest = _v2.Values.Select(e => (Entry?)e).Append(_entry).Where(e => e is not null).MinBy(e => e!.CreatedAt);
        if (earliest is not null) ScheduleExpiryLocked(earliest);
    }

    private async ValueTask ReleaseAsync(Entry entry)
    {
        await _gate.WaitAsync();
        try
        {
            entry.Readers--;
            if (entry.Retired && entry.Readers == 0)
            {
                _retired.Remove(entry);
                DeletePackageFiles(entry.Served);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RetireLocked(Entry entry)
    {
        entry.Retired = true;
        if (entry.Readers == 0) DeletePackageFiles(entry.Served);
        else _retired.Add(entry);
    }

    private void ScheduleExpiryLocked(Entry entry)
    {
        var timer = Volatile.Read(ref _expiryTimer);
        if (timer is null) return;
        var due = entry.CreatedAt + Lifetime - time.GetUtcNow();
        timer.Change(due <= TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
    }

    private void OnExpiryTimer() => _ = SweepFromTimerAsync();

    private void CleanupAbandonedPackages()
    {
        try
        {
            using var scope = scopes.CreateScope();
            var directory = PackageDirectory(scope.ServiceProvider.GetRequiredService<SnapshotService>());
            if (!Directory.Exists(directory)) return;
            var now = time.GetUtcNow();
            foreach (var file in Directory.GetFiles(directory, "*.tar.gz"))
            {
                // Another process on the same data directory may be serving this file right now: only a
                // package older than a lease can be nobody's.
                if (now - File.GetLastWriteTimeUtc(file) < Lifetime) continue;
                File.Delete(file);
                var signature = file + ".sig";
                if (File.Exists(signature)) File.Delete(signature);
            }
        }
        catch (IOException ex)
        {
            logger?.LogWarning(ex, "Could not remove abandoned blind replica package files");
        }
        catch (UnauthorizedAccessException ex)
        {
            logger?.LogWarning(ex, "Could not remove abandoned blind replica package files");
        }
    }

    private async Task SweepFromTimerAsync()
    {
        try
        {
            await SweepExpiredAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not retire an expired blind replica package");
        }
    }

    private static string PackageDirectory(SnapshotService snapshots) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(snapshots.SnapshotsDir))!, DirectoryName);

    private static void DeletePackageFiles(ServedPackage package)
    {
        if (File.Exists(package.FilePath)) File.Delete(package.FilePath);
        var signaturePath = package.FilePath + ".sig";
        if (File.Exists(signaturePath)) File.Delete(signaturePath);
    }

    public sealed class Lease : IAsyncDisposable
    {
        private BlindReplicaPackageCache? _owner;
        private readonly Entry _entry;

        internal Lease(BlindReplicaPackageCache owner, Entry entry)
        {
            _owner = owner;
            _entry = entry;
        }

        /// <summary>The format-1 package of a format-1 lease.</summary>
        public BlindPackage Package => _entry.Package ?? throw new InvalidOperationException("A format-2 lease has no format-1 package; use Served.");

        /// <summary>What every response needs, either format: the file (its detached signature beside it), its hash, its seed id.</summary>
        public ServedPackage Served => _entry.Served;

        public ValueTask DisposeAsync()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            return owner is null ? ValueTask.CompletedTask : owner.ReleaseAsync(_entry);
        }
    }

    internal sealed class Entry(ServedPackage served, DateTimeOffset createdAt, BlindPackage? package = null)
    {
        public ServedPackage Served { get; } = served;
        public BlindPackage? Package { get; } = package;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public int Readers { get; set; }
        public bool Retired { get; set; }
    }
}

/// <summary>A package on disk as a response serves it: its detached signature is <c>FilePath + ".sig"</c>.</summary>
public sealed record ServedPackage(string FilePath, string Sha256, Guid SeedId)
{
    public static ServedPackage Of(BlindPackage package) => new(package.FilePath, package.Sha256, package.Manifest.SeedId);
}
