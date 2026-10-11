using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Sync.Blind.Format2;

namespace BeeMemoryBank.Api.Startup;

public static partial class ApiServices
{
    /// <summary>
    /// Hook H3 (docs/design/step-a-spec.md 15.2): the format-2 writer a full hub serves replicas with once activated (8.7),
    /// signed with its session, so a locked hub cannot build a new package (7.5). The blind role registers its own (hook H2).
    /// </summary>
    static partial void AddFormat2ApiServices(IServiceCollection services, INodeRole role, string dataPath)
    {
        if (role.IsBlind) return;
        services.AddScoped<IBlindPackageV2Writer>(sp => BlindPackageV2Builder.Create(sp,
            (identity, payload) => new SessionSnapshotKeyOperations(
                sp.GetRequiredService<BeeMemoryBank.Core.Services.SessionService>(), sp.GetService<IExternalNodeKey>())
                .SignWithIdentity(identity, payload),
            Path.Combine(dataPath, "blind-replica"), SnapshotStaging.DirIn(dataPath),
            ct => sp.GetService<IReplicaProducerAuthority>() is { } authority ? authority.IsSuperadminAsync(ct) : Task.FromResult(false)));
    }
}
