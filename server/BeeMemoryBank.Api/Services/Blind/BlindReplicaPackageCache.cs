using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Keeps one immutable replica package available long enough for an Android download to resume.
/// A range retry has to refer to the exact same bytes and detached signature as its first request;
/// rebuilding on every request makes a correct Range client append a different archive.
/// </summary>
public sealed class BlindReplicaPackageCache(IServiceScopeFactory scopes, TimeProvider time)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Entry? _entry;

    public async Task<BlindPackage> GetAsync(bool producerIsSuperadmin, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            if (_entry is { } cached
                && cached.ProducerIsSuperadmin == producerIsSuperadmin
                && now - cached.CreatedAt < Lifetime
                && File.Exists(cached.Package.FilePath)
                && File.Exists(cached.Package.FilePath + ".sig"))
            {
                return cached.Package;
            }

            using var scope = scopes.CreateScope();
            var package = await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
                .BuildAsync(Guid.NewGuid(), includesUpTo: null, producerIsSuperadmin, ct);
            _entry = new Entry(package, producerIsSuperadmin, now);
            return package;
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record Entry(BlindPackage Package, bool ProducerIsSuperadmin, DateTimeOffset CreatedAt);
}
