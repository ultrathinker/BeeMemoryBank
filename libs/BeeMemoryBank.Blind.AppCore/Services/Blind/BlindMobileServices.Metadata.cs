using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

public static partial class BlindMobileServices
{
    /// <summary>
    /// Hook H5 (docs/design/step-a-spec.md 15.2): what a blind phone, a Windows or a macOS copy and an iPhone add for Step A: the store summary
    /// its home screen shows instead of a note count (10.2), and the effect of the activation on this host (5.5).
    /// </summary>
    static partial void AddPhoneMetadataServices(IServiceCollection services, string dataDir)
    {
        services.AddSingleton(sp => new BlindStoreStats(
            sp.GetRequiredService<BlindStartup>(), sp.GetRequiredService<IDbConnectionFactory>(), sp.GetRequiredService<IBaselineStore>(), dataDir));
        services.AddSingleton<IMetadataActivationObserver>(sp => new BlindPhoneActivationObserver(sp));
    }
}

/// <summary>
/// The activation reaches a copy that is already loaded as an event, and what it holds is a format-1 replica: marking the first load "not done"
/// sends the next heavy pass to download a format-2 one (the path a 410 takes today, <c>BlindPhoneSync</c>), which replaces what the copy held in clear
/// at its first adoption (docs/design/step-a-spec.md 5.5, 9.3). Setting a flag is all it does, so a retry changes nothing.
/// </summary>
public sealed class BlindPhoneActivationObserver(IServiceProvider services) : IMetadataActivationObserver
{
    public Task OnActivatedAsync(MetadataActivation activation, CancellationToken ct = default)
    {
        // Resolved when the activation arrives, not when the applier is built: a composition that has no phone state (a check of the relay
        // path alone) still builds the applier.
        var state = services.GetRequiredService<BlindPhoneState>();
        var log = services.GetRequiredService<BlindPhoneLog>();
        if (state.InitialLoadDone)
        {
            state.InitialLoadDone = false;
            log.Add("load", "The network turned on sealed metadata: this copy loads again, sealed.");
        }
        return Task.CompletedTask;
    }
}
