namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// Opens the phone's database once: migrations and pragmas. Everything that reads or writes it awaits
/// <see cref="EnsureReadyAsync"/> first. On the first start <c>App.OnStart</c> (migrations, an async void) and the
/// page's <c>OnAppearing</c> (creates the identity) ran at the same time, the identity insert found no tables, the
/// error was swallowed and the screen said "No identity. Wipe and set the phone up again." until the next start.
/// </summary>
public sealed class BlindStartup(Func<CancellationToken, Task> initialize)
{
    private readonly object _gate = new();
    private Task? _run;

    /// <summary>
    /// Completes when the database is open. Callers during a run share it; a finished run is not repeated; a
    /// failed run reaches the callers that waited for it and the next caller starts a new one.
    /// </summary>
    public Task EnsureReadyAsync()
    {
        lock (_gate)
        {
            if (_run is { IsFaulted: false, IsCanceled: false }) return _run;
            return _run = Task.Run(() => initialize(CancellationToken.None));
        }
    }

    /// <summary>
    /// Waits, for at most <paramref name="timeout"/>, for a run that is under way to end however it ends (a run is never started here). A caller that gave up waiting (a cancelled
    /// start) leaves the run going, and the services under it must not be disposed beneath it: whoever ends an account settles this first. True when no run is under way any more (it ended,
    /// by success or by failure, or there was none); false when it is still going after the timeout: the services must then be left alone.
    /// </summary>
    internal async Task<bool> SettleAsync(TimeSpan timeout)
    {
        Task? run;
        lock (_gate) run = _run;
        if (run is null || run.IsCompleted) return true;
        try
        {
            await run.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return true; // it failed: it has ended
        }
    }
}
