namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Abstraction for the post-apply health check step.
/// In this task "apply" is simulated — inject a test double that can be
/// configured to succeed or fail a given number of times.
/// The real implementation (checking actual running process health) comes
/// in the later Velopack-integration task once binary-swap/restart exists.
/// </summary>
public interface IUpdateHealthCheck
{
    /// <summary>
    /// Perform one health check attempt.
    /// </summary>
    /// <returns><c>true</c> if the node appears healthy after the update; <c>false</c> otherwise.</returns>
    Task<bool> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Always-passing health check stub — used in the happy-path tests.
/// </summary>
public sealed class AlwaysHealthyHealthCheck : IUpdateHealthCheck
{
    public Task<bool> CheckAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(true);
}

/// <summary>
/// Configurable stub that fails the first <paramref name="failCount"/> calls, then passes.
/// Useful for testing the 3-failures → Failed state transition.
/// </summary>
public sealed class FlakyHealthCheck : IUpdateHealthCheck
{
    private int _remaining;
    public FlakyHealthCheck(int failCount) => _remaining = failCount;

    public Task<bool> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (_remaining > 0)
        {
            _remaining--;
            return Task.FromResult(false);
        }
        return Task.FromResult(true);
    }
}
