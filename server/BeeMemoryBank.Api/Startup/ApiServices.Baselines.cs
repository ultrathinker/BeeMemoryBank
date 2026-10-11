using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;

namespace BeeMemoryBank.Api.Startup;

// Hook H4 (docs/design/step-a-spec.md 15.2): the baseline producer, scheduler and full-node source, and the full node's activation observer.
public static partial class ApiServices
{
    static partial void AddBaselineServices(IServiceCollection services, INodeRole role, string dataPath)
    {
        // A blind host never produces a baseline: it adopts one (track 2's store registers its own source and observer).
        if (role.IsBlind) return;

        services.AddScoped<BaselineProducer>();
        services.AddSingleton<BaselineProducerRunner>();
        services.AddSingleton<IBaselineProducer>(sp => sp.GetRequiredService<BaselineProducerRunner>());
        services.AddSingleton<FullNodeBaselineSource>();
        services.AddSingleton<IBaselineSource>(sp => sp.GetRequiredService<FullNodeBaselineSource>());
        services.AddSingleton<IBaselineLogHead, ScopedBaselineLogHead>();
        services.AddSingleton<BaselineScheduler>();
        services.AddSingleton<IBaselineScheduler>(sp => sp.GetRequiredService<BaselineScheduler>());
        services.AddHostedService(sp => sp.GetRequiredService<BaselineScheduler>());
        services.AddSingleton<IMetadataActivationObserver, FullNodeActivationObserver>();
    }
}
