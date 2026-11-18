using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Vault.Sealed;

/// <summary>
/// The local, never-backed-up state of the server checkbox (docs/design/step-b-spec.md 11.2) as the DEK rotation sees it. Since 2.12 a rotation
/// re-seals a v2 <c>server_auto_unlock</c> door to the new DEK, and the enable record follows it: before the commit the record names the door
/// before and after (<see cref="PrepareRotationAsync"/>), after it only the new door and the new DEK (<see cref="OnDekRotatedAsync"/>), so a
/// crash at any point leaves the server armed. Only a v1 door, which nobody can re-seal, is deleted by the rotation; then the enable record
/// and a key file the node created go with it (<see cref="DisableLocalAsync"/>). Implemented by <c>ServerAutoUnlockService</c>
/// (Infrastructure); defined here because <c>DekRewrapper</c> lives below it.
/// </summary>
public interface IServerAutoUnlockLocalState
{
    /// <summary>Removes the enable record first, then a node-created key file (never an operator's). A failure is the caller's to log: the stale rule removes what is left at the next start.</summary>
    Task DisableLocalAsync(CancellationToken ct = default);

    /// <summary>
    /// Before the rotation commits: when the enable record names <see cref="ServerRotationDoor.OldSlotSha256"/>, it is rewritten to name that
    /// door and the re-sealed one, and the old DEK and the new one. A record that names another door is left alone (that slot is stale anyway).
    /// </summary>
    Task PrepareRotationAsync(ServerRotationDoor change, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>After the rotation: <paramref name="committed"/> true settles the record on the re-sealed door and the new DEK; false on the old ones.</summary>
    Task OnDekRotatedAsync(bool committed, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>A server auto-unlock door a rotation re-seals: its content hash before and after (as the boot store names it), and the new DEK's fingerprint.</summary>
public sealed record ServerRotationDoor(string OldSlotSha256, string NewSlotSha256, string NewDekFingerprint);

/// <summary>What a rotation hands to the sealed bookkeeping: the mirror and the server-checkbox state. Either may be absent.</summary>
public sealed record SealedRotationHooks(IUnlockMaterialMirror? Mirror, IServerAutoUnlockLocalState? ServerAutoUnlock)
{
    public static readonly SealedRotationHooks None = new(null, null);

    private static readonly AsyncLocal<SealedRotationHooks?> Captured = new();

    /// <summary>
    /// The hooks the current logical flow captured with <see cref="Capture"/>, or <see cref="None"/>. The two places that run a rotation
    /// (the initiator's accept path and the peer's auto-accept) call <c>DekRewrapper.RunPreRewrapHooksAsync(services, ...)</c> and then
    /// <c>RewrapAllAsync(...)</c> in the same flow; the first call picks the hooks out of <c>services</c>, the second uses them, so neither caller
    /// had to change.
    /// </summary>
    public static SealedRotationHooks Current => Captured.Value ?? None;

    /// <summary>
    /// Resolves the hooks from <paramref name="services"/> and keeps them for the rest of the logical flow. MUST run synchronously in the caller's
    /// flow (not inside an async method of its own): a value set inside an async method does not reach its caller.
    /// </summary>
    public static void Capture(IServiceProvider services)
    {
        var mirror = (IUnlockMaterialMirror?)services.GetService(typeof(IUnlockMaterialMirror));
        var server = (IServerAutoUnlockLocalState?)services.GetService(typeof(IServerAutoUnlockLocalState));
        Captured.Value = mirror is null && server is null ? null : new SealedRotationHooks(mirror, server);
    }

    /// <summary>Forgets what <see cref="Capture"/> kept (tests; a flow that ends does not need it).</summary>
    public static void Release() => Captured.Value = null;
}
