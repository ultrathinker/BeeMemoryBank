using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// Android adapter that gives a backup its package: the listening node's current one, downloaded through the
/// call-code pinned client (the same one the first load uses) and verified, never installed.
/// </summary>
public sealed class BlindPackageFetcher(
    BlindPhoneReplicaClient client, BlindHttpClientProvider http, BlindPhoneState state, string workDirectory)
    : IBlindVerifiedPackageFetcher
{
    public Task<VerifiedReplicaPackage> FetchAsync(Action<long> beforeDownload, IProgress<double>? progress, CancellationToken ct)
    {
        var target = state.CallCode
            ?? throw new InvalidDataException("This phone is not paired with a node to fetch a package from.");
        return client.FetchVerifiedPackageAsync(http.GetClient(target.SpkiPin), target, workDirectory, progress, ct, beforeDownload);
    }
}
