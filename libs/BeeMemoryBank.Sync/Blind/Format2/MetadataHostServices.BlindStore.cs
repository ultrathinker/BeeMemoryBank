using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Blind.Format2;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync.Metadata;

public static partial class MetadataHostServices
{
    /// <summary>
    /// Hook H1 (docs/design/step-a-spec.md 15.2), every blind composition (the BlindNode host, the Api in the blind role,
    /// AppCore): the baseline store under <paramref name="dataPath"/>, the format-2 reader, the store as the baseline this
    /// host serves (9.7) and the summary its status and my-standing show (10.1). None of them can open anything.
    /// </summary>
    static partial void AddBlindStoreServices(IServiceCollection services, string dataPath)
    {
        services.TryAddSingleton<IBaselineStore>(sp => new BaselineStore(
            sp.GetRequiredService<DbConnectionFactory>(), dataPath, sp.GetRequiredService<ISyncProtocolPolicy>(),
            sp.GetService<ILogger<BaselineStore>>()));
        services.TryAddSingleton<IBlindPackageV2Reader>(_ => new BlindPackageV2Reader());
        services.TryAddSingleton<IBaselineSource, BlindBaselineSource>();
        services.TryAddSingleton<IBlindStoreSummarySource>(sp => new BlindStoreSummarySource(
            sp.GetRequiredService<DbConnectionFactory>(), sp.GetRequiredService<IBaselineStore>(), dataPath));
    }
}
