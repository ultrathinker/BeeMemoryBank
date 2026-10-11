using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;

namespace BeeMemoryBank.Api.Startup;

public static partial class BlindRoleServices
{
    /// <summary>
    /// Hook H2 (docs/design/step-a-spec.md 15.2), the blind role of the Api and the BlindNode host (this file is linked into
    /// it): the format-2 writer a blind node serves its replica and restore package with, signed with its external key
    /// (9.7, no unlock needed), and the activation observer that asks for a format-2 reseed (5.5).
    /// </summary>
    static partial void AddBlindRoleFormat2Services(IServiceCollection services, string dataPath)
    {
        var temp = BlindRoleStartup.TempDir(dataPath);
        services.AddScoped<IBlindPackageV2Writer>(sp => BlindPackageV2Builder.Create(sp,
            (identity, payload) => SignWithExternalKey(sp.GetRequiredService<IExternalNodeKey>(), identity, payload),
            Path.Combine(temp, "packages"), Path.Combine(temp, "staging"), servingIsSuperadmin: null));
        services.AddSingleton<IMetadataActivationObserver, BlindActivationObserver>();
    }

    /// <summary>A blind node signs only with its external (v=2) identity key; it holds no other.</summary>
    private static byte[] SignWithExternalKey(IExternalNodeKey key, NodeIdentity identity, byte[] payload)
    {
        if (identity.Ed25519PrivateKeyV != NodeIdentityCrypto.ExternalKeyVersion)
            throw new InvalidOperationException(
                $"A blind node signs only with its external identity key (v={NodeIdentityCrypto.ExternalKeyVersion}); the identity row has v={identity.Ed25519PrivateKeyV}.");
        return NodeIdentityCrypto.SignWithExternalSeed(key.ReadSeed, payload);
    }
}
