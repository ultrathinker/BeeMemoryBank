using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Storage.Boot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Infrastructure.AutoUnlock;

/// <summary>
/// What the auto-unlock services (OS auto-unlock, the update hand-off, the server checkbox) need from a sealed node
/// (docs/design/step-b-spec.md 4.7, 5.2 step 6): the doors in the boot store, which are readable while the database is closed, and the
/// unlock with a master DEK that checks the door and the sentinel (<c>SessionService.TryUnlockWithDekAsync</c>, track 3). On a plain
/// node <see cref="IsSealed"/> is false and nothing here is used.
/// </summary>
public interface ISealedAutoUnlockHost
{
    /// <summary>True when this node's database is sealed (any state).</summary>
    bool IsSealed { get; }

    /// <summary>True when the database can be read now (Open or Importing); false while it is closed.</summary>
    bool IsUsable { get; }

    Task<IReadOnlyList<BootDoor>> ListDoorsAsync(CancellationToken ct = default);

    /// <summary>
    /// Opens the database with <paramref name="masterDek"/> and unlocks the session when the sentinel and the door check pass; false and
    /// closed again on any refusal (spec 4.7). The caller keeps and clears <paramref name="masterDek"/>.
    /// </summary>
    Task<bool> TryUnlockWithDekAsync(byte[] masterDek, BootDoor? door, CancellationToken ct = default);

    /// <summary>Records which master DEK the door yielded (spec 3.4), after a successful unlock.</summary>
    Task SetDekFingerprintAsync(long doorId, string dekFingerprint, CancellationToken ct = default);
}

/// <summary>
/// The unlock with a master DEK on a sealed node, as a delegate the sealed-vault composition (track 3) registers from
/// <c>SessionService.TryUnlockWithDekAsync</c>. Without one registered nothing unlocks a sealed node by itself: fail closed.
/// </summary>
public delegate Task<bool> SealedDekUnlock(byte[] masterDek, BootDoor? door, CancellationToken ct);

/// <summary>The default <see cref="ISealedAutoUnlockHost"/>: the gate says whether the node is sealed and open, the door store lists the doors.</summary>
public sealed class SealedAutoUnlockHost(IVaultGate? gate, IBootDoorStore? doors, SealedDekUnlock? unlock) : ISealedAutoUnlockHost
{
    public bool IsSealed => gate is { IsSealed: true };

    public bool IsUsable => gate is null || gate.IsUsable;

    public async Task<IReadOnlyList<BootDoor>> ListDoorsAsync(CancellationToken ct = default) =>
        doors is null ? [] : await doors.ListAsync(ct).ConfigureAwait(false);

    public Task<bool> TryUnlockWithDekAsync(byte[] masterDek, BootDoor? door, CancellationToken ct = default) =>
        unlock is null ? Task.FromResult(false) : unlock(masterDek, door, ct);

    public Task SetDekFingerprintAsync(long doorId, string dekFingerprint, CancellationToken ct = default) =>
        doors is null ? Task.CompletedTask : doors.SetDekFingerprintAsync(doorId, dekFingerprint, ct);
}

public static class SealedAutoUnlockServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ISealedAutoUnlockHost"/> over the gate, the door store and the <see cref="SealedDekUnlock"/> delegate (all
    /// optional: absent ones leave a sealed node locked after a restart, never open). Called by the sealed-vault composition (track 3).
    /// </summary>
    public static IServiceCollection AddSealedAutoUnlockHost(this IServiceCollection services)
    {
        services.TryAddSingleton<ISealedAutoUnlockHost>(sp => new SealedAutoUnlockHost(
            sp.GetService<IVaultGate>(), sp.GetService<IBootDoorStore>(), sp.GetService<SealedDekUnlock>()));
        return services;
    }
}
