using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Keeps one immutable replica package available long enough for an Android download to resume.
/// A range retry has to refer to the exact same bytes and detached signature as its first request;
/// rebuilding on every request makes a correct Range client append a different archive.
/// </summary>
public sealed class BlindReplicaPackageCache(IServiceScopeFactory scopes, TimeProvider time) : IAsyncDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<Entry> _retired = [];
    private Entry? _entry;

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
        await _gate.WaitAsync();
        try
        {
            if (_entry is { } entry)
            {
                _entry = null;
                DeletePackageFiles(entry.Package);
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
            .BuildAsync(Guid.NewGuid(), includesUpTo: null, producerIsSuperadmin, ct);
        var replacement = new Entry(package, producerIsSuperadmin, now);
        var superseded = _entry;
        _entry = replacement;
        if (superseded is not null)
        {
            superseded.Retired = true;
            if (superseded.Readers == 0) DeletePackageFiles(superseded.Package);
            else _retired.Add(superseded);
        }
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
