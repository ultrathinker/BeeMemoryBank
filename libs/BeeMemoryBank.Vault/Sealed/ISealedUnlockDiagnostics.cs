namespace BeeMemoryBank.Vault.Sealed;

/// <summary>
/// What the unlock of a sealed node asks the gate besides opening it (docs/design/step-b-spec.md 4.4 step 5, 6.7): to remember the code of a
/// refused door for the status, and the pair diagnosis. Implemented by <see cref="VaultGate"/>; absent on a plain node.
/// </summary>
public interface ISealedUnlockDiagnostics
{
    /// <summary>Records the code of a refused unlock (never a name or a key); null clears it after a successful unlock.</summary>
    Task RecordRefusalAsync(string? code, CancellationToken ct = default);

    /// <summary>True when the open database's <c>unlock_material_version</c> is lower than the boot store's (a database restored without its boot store of the same moment).</summary>
    Task<bool> IsDatabaseOlderThanBootStoreAsync(CancellationToken ct = default);
}
