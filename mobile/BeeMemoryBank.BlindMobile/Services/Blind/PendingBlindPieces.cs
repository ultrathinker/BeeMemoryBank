using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

// Stand-ins for the pieces of the Android blind node not wired to the phone yet. Each says what it
// waits for; the screen shows that text instead of failing. Replace each with the real one.

/// <summary>TODO(identity row v=2): write tbl_node_identity with v=2.</summary>
public sealed class PendingBlindIdentityRecorder : IBlindIdentityRecorder
{
    // Nothing to write yet: the identity lives in BlindPhoneState and the Keystore until the v=2 row
    // exists. Creating it must still work, so the phone can show its code and be added on Windows.
    public Task RecordAsync(Guid nodeId, byte[] publicKey, string displayName, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>TODO(GET /api/blind/replica on the phone).</summary>
public sealed class PendingBlindReplicaSource : IBlindReplicaSource
{
    public Task FetchAndInstallAsync(BlindCallCode target, string workDirectory, IProgress<double>? progress, CancellationToken ct) =>
        throw new BlindFeaturePendingException("the first download of the blind package (GET /api/blind/replica)");
}

/// <summary>TODO(identity v=2 signer + protocol 3 on the phone).</summary>
public sealed class PendingBlindPhoneSync : IBlindPhoneSync
{
    public Task SyncOnceAsync(BlindCallCode target, CancellationToken ct) =>
        throw new BlindFeaturePendingException("blind sync with the v=2 identity signer");
}

/// <summary>TODO(BlindPackageBuilder on the phone).</summary>
public sealed class PendingBlindPackageSource : IBlindPackageSource
{
    public Task CreateAsync(string destinationPath, CancellationToken ct) =>
        throw new BlindFeaturePendingException("building the blind package on the phone");
}

/// <summary>TODO(RecoverySetBuilder over the phone's database).</summary>
public sealed class PendingRecoverySetSource : IRecoverySetJsonSource
{
    public Task<string> BuildJsonAsync(CancellationToken ct) =>
        throw new BlindFeaturePendingException("the recovery set of the phone's data");
}
