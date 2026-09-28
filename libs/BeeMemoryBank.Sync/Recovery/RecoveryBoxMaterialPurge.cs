using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Sync.Recovery;

/// <summary>Placeholder for the F7 floor tests (red run); the purge lands in the next commit.</summary>
public static class RecoveryBoxMaterialPurge
{
    public static Task<int> RunAsync(IDbConnectionFactory db) => Task.FromResult(0);
}
