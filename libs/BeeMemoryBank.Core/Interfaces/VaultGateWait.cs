using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// What a hosted loop calls at the top of an iteration (docs/design/step-b-spec.md 5.5): wait until the vault database can be used. A host with no
/// gate (a blind host) and a plain node do not wait. The gate is looked up each time, from a scope: the loops live as long as the process and the
/// gate is registered after some of them are constructed.
/// </summary>
public static class VaultGateWait
{
    /// <summary>Completes at once when there is no gate, the node is plain or the database is open; else when it opens (or <paramref name="ct"/> is cancelled).</summary>
    public static Task WaitUsableAsync(this IServiceScopeFactory scopes, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return scope.ServiceProvider.GetService<IVaultGate>().WaitUsableOrNowAsync(ct);
    }

    /// <summary>The same for a loop that holds the root provider instead of a scope factory.</summary>
    public static Task WaitUsableAsync(this IServiceProvider services, CancellationToken ct) =>
        services.GetService<IVaultGate>().WaitUsableOrNowAsync(ct);

    /// <summary>The same for a loop that was handed the gate itself (null on a host without one: no wait). Call it as <c>VaultGateWait.WaitUsableAsync(gate, ct)</c>.</summary>
    public static Task WaitUsableAsync(IVaultGate? gate, CancellationToken ct) => gate.WaitUsableOrNowAsync(ct);

    private static Task WaitUsableOrNowAsync(this IVaultGate? gate, CancellationToken ct) =>
        gate is null || gate.IsUsable ? Task.CompletedTask : gate.WaitUsableAsync(ct);
}
