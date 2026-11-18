using System.Collections.Concurrent;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// One lock per vault for the operations that change a key of the whole vault (release 2.12, plan WS-A6): a DEK rotation (proposal, accept,
/// a peer's apply), an identity re-key, and the in-process side of an offline re-key. They exclude each other: two of them interleaved would
/// re-seal under a key the other is replacing. The offline re-key also holds its on-disk lock (<c>&lt;D&gt;.rekey.lock</c>), which a running
/// node checks before a rotation. Never waited on for long: a second operation is refused (or, for a peer's apply, deferred) and names the
/// one that holds the lock.
/// <para>
/// Keyed by the vault's <see cref="Interfaces.IDbConnectionFactory.DatabaseId"/>: several vaults open in one process (a test stand,
/// the tests) do not refuse each other's operations. The id never contains key material. Compared without case, so the same file named in
/// another case is the same vault: a lock shared too widely refuses more, never less.
/// </para>
/// </summary>
public static class KeyOperationLock
{
    private sealed class Gate
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public volatile string? Holder;
    }

    private static readonly ConcurrentDictionary<string, Gate> Gates = new(StringComparer.OrdinalIgnoreCase);

    private static Gate GateOf(string databaseId)
    {
        ArgumentException.ThrowIfNullOrEmpty(databaseId);
        return Gates.GetOrAdd(databaseId, _ => new Gate());
    }

    /// <summary>What holds the lock of this vault now ("a key rotation", ...), or null.</summary>
    public static string? Holder(string databaseId) => Gates.TryGetValue(databaseId, out var gate) ? gate.Holder : null;

    /// <summary>Takes the lock of this vault for <paramref name="operation"/> without waiting; null when another operation holds it.</summary>
    public static IDisposable? TryEnter(string databaseId, string operation)
    {
        var gate = GateOf(databaseId);
        if (!gate.Semaphore.Wait(TimeSpan.Zero)) return null;
        gate.Holder = operation;
        return new Release(gate);
    }

    /// <summary>The refusal a caller shows when <see cref="TryEnter"/> returned null.</summary>
    public static string Busy(string databaseId, string wanted) =>
        $"{wanted} cannot start now: {Holder(databaseId) ?? "another key operation"} is running on this node. Try again when it has finished.";

    private sealed class Release(Gate gate) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            gate.Holder = null;
            gate.Semaphore.Release();
        }
    }
}
