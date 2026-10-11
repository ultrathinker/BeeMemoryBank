using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Sync.Metadata;

/// <summary>
/// The Step A registrations every blind host makes (docs/design/step-a-spec.md 9.4, 15.2): the BlindNode host and the Api
/// started with BMB_ROLE=blind (both through <c>BlindRoleServices.AddBlindRoleServices</c>) and AppCore's
/// <c>BlindMobileServices</c>. A blind host relays sealed events and never opens one, and it never holds an event; then
/// hook H1 registers the blind store (track 2, <c>Blind/Format2/MetadataHostServices.BlindStore.cs</c>).
/// </summary>
public static partial class MetadataHostServices
{
    public static IServiceCollection AddBlindMetadataHost(this IServiceCollection services, string dataPath)
    {
        // Replace, not TryAdd: the Api in the blind role has AddSync's full-node opener and drain registered already.
        services.Replace(ServiceDescriptor.Singleton<IEnvelopeOpener, RelayEnvelopeOpener>());
        services.Replace(ServiceDescriptor.Singleton<IHeldEventDrain, NoHeldEventDrain>());
        // AppCore does not call AddNodeSync; the other hosts have these from it already.
        services.TryAddSingleton<ISyncProtocolPolicy, SyncProtocolPolicy>();
        services.TryAddSingleton<IHeldEventStatus, HeldEventStatus>();
        AddBlindStoreServices(services, dataPath);
        return services;
    }

    /// <summary>Hook H1 (spec 15.2): the blind store, the format-2 reader, the blind baseline source and store summary.</summary>
    static partial void AddBlindStoreServices(IServiceCollection services, string dataPath);
}
