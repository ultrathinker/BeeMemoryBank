using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Keeps one immutable replica package available long enough for an Android download to resume.
/// A range retry has to refer to the exact same bytes and detached signature as its first request;
/// rebuilding on every request makes a correct Range client append a different archive.
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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<Entry> _retired = [];
    private Entry? _entry;
    private ITimer? _expiryTimer;
    private int _disposed;

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

    public async Task<BlindPackage> GetAsync(bool producerIsSuperadmin, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return (await GetCurrentLockedAsync(producerIsSuperadmin, ct)).Package;
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
    public async Task<Lease> AcquireAsync(bool producerIsSuperadmin, CancellationToken ct)
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
            foreach (var retiredEntry in _retired)
                DeletePackageFiles(retiredEntry.Package);
            _retired.Clear();
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
            if (_entry is not { } entry) return;
            if (time.GetUtcNow() - entry.CreatedAt >= Lifetime)
            {
                _entry = null;
                RetireLocked(entry);
            }
            else
            {
                ScheduleExpiryLocked(entry);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Entry> GetCurrentLockedAsync(bool producerIsSuperadmin, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (_entry is { } cached
            && cached.ProducerIsSuperadmin == producerIsSuperadmin
            && now - cached.CreatedAt < Lifetime
            && File.Exists(cached.Package.FilePath)
            && File.Exists(cached.Package.FilePath + ".sig"))
        {
            return cached;
        }

        using var scope = scopes.CreateScope();
        var package = await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
            .BuildAsync(Guid.NewGuid(), includesUpTo: null, producerIsSuperadmin, ct,
                outputDirectory: PackageDirectory(scope.ServiceProvider.GetRequiredService<SnapshotService>()));
        var replacement = new Entry(package, producerIsSuperadmin, now);
        var superseded = _entry;
        _entry = replacement;
        if (superseded is not null)
            RetireLocked(superseded);
        ScheduleExpiryLocked(replacement);
        return replacement;
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
                DeletePackageFiles(entry.Package);
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
        if (entry.Readers == 0) DeletePackageFiles(entry.Package);
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
            foreach (var file in Directory.GetFiles(directory, "*.tar.gz"))
            {
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

    private static void DeletePackageFiles(BlindPackage package)
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

        public BlindPackage Package => _entry.Package;

        public ValueTask DisposeAsync()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            return owner is null ? ValueTask.CompletedTask : owner.ReleaseAsync(_entry);
        }
    }

    internal sealed class Entry(BlindPackage package, bool producerIsSuperadmin, DateTimeOffset createdAt)
    {
        public BlindPackage Package { get; } = package;
        public bool ProducerIsSuperadmin { get; } = producerIsSuperadmin;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public int Readers { get; set; }
        public bool Retired { get; set; }
    }
}
