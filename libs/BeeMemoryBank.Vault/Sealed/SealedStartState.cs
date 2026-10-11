using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Storage.Sealed;

namespace BeeMemoryBank.Vault.Sealed;

/// <summary>
/// What this process found when it started on its data directory (docs/design/step-b-spec.md 5.2 steps 1-2): the mode, what the seal resolver
/// did about an interrupted migration, and the hard links it unlinked. Read by the status route; never changes after the start.
/// </summary>
public sealed record SealedStartState(VaultStoreMode Mode, SealSwapResolution? Seal)
{
    /// <summary>The state of a directory the resolver has just dealt with. A broken directory has no node, so no state: see <see cref="BrokenStart"/>.</summary>
    public static SealedStartState For(string dataPath, SealSwapResolution? resolved) =>
        new(VaultStoreFiles.DetectMode(dataPath) == VaultStoreMode.Sealed ? VaultStoreMode.Sealed : VaultStoreMode.Plain, resolved);

    public IReadOnlyList<string> LinkedPlain => Seal?.LinkedPlainNames ?? [];
}
