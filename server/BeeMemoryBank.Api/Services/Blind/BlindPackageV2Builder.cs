using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Blind.Format2;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// The format-2 package writer of an Api host (docs/design/step-a-spec.md 8): the library's <see cref="BlindPackageV2Writer"/>
/// over this host's database, signing with this host's identity, naming the address and pin a receiver can dial it at. A
/// blind host signs with its external key and is never superadmin in its own package (hook H2); a full hub signs with its
/// session and says superadmin only where its full peers confirm it, asked once per package (hook H3).
/// </summary>
public static class BlindPackageV2Builder
{
    public static IBlindPackageV2Writer Create(
        IServiceProvider sp, Func<NodeIdentity, byte[], byte[]> sign, string outputDirectory, string stagingDirectory,
        Func<CancellationToken, Task<bool>>? servingIsSuperadmin)
    {
        var (address, spki) = ServingEndpoint(sp.GetRequiredService<IConfiguration>(), sp.GetService<BlindTlsIdentity>());
        return new BlindPackageV2Writer(
            sp.GetRequiredService<IDbConnectionFactory>(),
            sp.GetRequiredService<INodeIdentityRepository>(),
            sp.GetRequiredService<IWhitelistRepository>(),
            sp.GetRequiredService<ISyncPositionRepository>(),
            sp.GetRequiredService<IEventLogRepository>(),
            new BlindPackageV2WriterOptions(sign, outputDirectory, stagingDirectory, servingIsSuperadmin, address, spki));
    }

    /// <summary>Where a receiver can dial this node and the pin it checks there, as a format-1 package says it (BMB_PUBLIC_ADDRESS).</summary>
    private static (string? Address, string? Spki) ServingEndpoint(IConfiguration config, BlindTlsIdentity? tls)
    {
        var address = config["BMB_PUBLIC_ADDRESS"]?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(address)) return (null, null);
        var spki = tls?.Spki ?? config["BMB_PUBLIC_TLS_SPKI"]?.Trim();
        return (address, string.IsNullOrEmpty(spki) ? null : spki);
    }
}
