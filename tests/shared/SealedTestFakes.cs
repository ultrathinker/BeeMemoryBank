using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Vault.Sealed;

namespace BeeMemoryBank.TestSupport;

/// <summary>A gate that only answers the questions the mirror and its watcher ask (the real gate is track 3's).</summary>
internal sealed class FakeGate(VaultState state = VaultState.Open, bool isSealed = true) : IVaultGate
{
    public VaultState State { get; set; } = state;
    public bool IsSealed { get; set; } = isSealed;
    public bool IsUsable => State is VaultState.Plain or VaultState.Importing or VaultState.Open;
    public bool IsHeld => false;
    public event Action<VaultState>? StateChanged { add { } remove { } }
    public Task<VaultOpenResult> OpenAsync(byte[] masterDek, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IAsyncDisposable> OpenTemporarilyAsync(byte[] masterDek, CancellationToken ct = default) => throw new NotSupportedException();
    public Task CompleteOpenAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IAsyncDisposable> HoldClosedAsync(string reason, CancellationToken ct = default) => throw new NotSupportedException();
    public Task CloseAsync(string reason, CancellationToken ct = default) => throw new NotSupportedException();
    public Task WaitUsableAsync(CancellationToken ct) => Task.CompletedTask;
}

internal sealed class FakeVaultKeys(byte[] dbk, int generation = 1) : ISealedVaultKeys
{
    public bool Closed { get; set; }
    public byte[] GetDatabaseKey() => Closed ? throw new VaultSealedException() : (byte[])dbk.Clone();
    public int DbkGeneration => generation;
}
