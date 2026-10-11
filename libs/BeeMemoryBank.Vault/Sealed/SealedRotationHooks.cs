using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Vault.Sealed;

/// <summary>
/// The local, never-backed-up state of the server checkbox (docs/design/step-b-spec.md 11.2) as the DEK rotation sees it: after a rotation
/// the <c>server_auto_unlock</c> slot is gone, so the enable record and a key file the node created go with it. Implemented by
/// <c>ServerAutoUnlockService</c> (Infrastructure); defined here because <c>DekRewrapper</c> lives below it.
/// </summary>
public interface IServerAutoUnlockLocalState
{
    /// <summary>Removes the enable record first, then a node-created key file (never an operator's). A failure is the caller's to log: the stale rule removes what is left at the next start.</summary>
    Task DisableLocalAsync(CancellationToken ct = default);
}

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
