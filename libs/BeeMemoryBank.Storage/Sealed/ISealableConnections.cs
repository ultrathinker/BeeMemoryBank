namespace BeeMemoryBank.Storage.Sealed;

/// <summary>
/// What the vault gate (spec 5.4) needs from a connection factory whose database can be sealed: stop new connections, wait for the open
/// ones, and flush everything that holds the key. Implemented by <c>DbConnectionFactory</c> (vault and staging) and by the Api's
/// <c>ChatDbConnectionFactory</c>, which lives in a project the Vault library cannot reference.
/// </summary>
public interface ISealableConnections
{
    /// <summary>From now on a new connection waits until the handle is disposed, except in the flow that called this (spec 5.4 step 3).</summary>
    IDisposable BeginQuiesce();

    /// <summary>Waits until every connection opened outside the calling flow is closed; <see cref="TimeoutException"/> after <paramref name="timeout"/>.</summary>
    Task WaitDrainedAsync(TimeSpan timeout, CancellationToken ct = default);

    /// <summary>
    /// Ends the open generation (spec 7.2): no connection is handed out for it any more, the live ones are waited for (up to
    /// <paramref name="timeout"/>), the provider's pool is flushed and the key-bearing connection string is dropped. Returns only after
    /// all of that. The result is false when a connection was still in use at the limit (a straggler: closed when its owner returns it,
    /// and the one bounded exception to "no key-bearing string after close", spec 5.4 step 3); the caller logs it and goes on.
    /// </summary>
    Task<bool> CloseForSealAsync(TimeSpan timeout, CancellationToken ct = default);
}
