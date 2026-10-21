namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// The accounts screen's own rhythm for an invitation that waits for its owner: while the screen is open the hub is asked every 15 seconds, for at most 10 minutes (the friend is reading
/// the code to the owner now); after that the device scheduler asks at its own passes (<see cref="IBlindAccountsController.PollInvitationsAsync"/>). A head starts one when its accounts screen
/// appears and stops it when the screen goes. It ends by itself when nothing waits any more, when the time is up, or when it is stopped; it never throws.
/// Every ask goes through the host, which keeps the back-off the hub demanded (429, locked): a watch cannot be made to storm a hub.
/// </summary>
public sealed class BlindInvitationWatch : IAsyncDisposable
{
    public static readonly TimeSpan DefaultEvery = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DefaultFor = TimeSpan.FromMinutes(10);

    private readonly Func<bool, CancellationToken, Task<int>> _poll;
    private readonly TimeProvider _time;
    private readonly TimeSpan _every;
    private readonly TimeSpan _for;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public BlindInvitationWatch(IBlindAccountsController accounts, TimeProvider? time = null, TimeSpan? every = null, TimeSpan? upTo = null)
        : this((force, ct) => accounts.PollInvitationsAsync(force, ct), time, every, upTo)
    {
    }

    /// <summary>What a test (or a host that asks some other way) gives the watch to ask with: it returns how many accounts still wait.</summary>
    internal BlindInvitationWatch(Func<bool, CancellationToken, Task<int>> poll, TimeProvider? time = null, TimeSpan? every = null, TimeSpan? upTo = null)
    {
        _poll = poll;
        _time = time ?? TimeProvider.System;
        _every = every ?? DefaultEvery;
        _for = upTo ?? DefaultFor;
    }

    /// <summary>Tests only: awaited by <see cref="StopAsync"/> between taking the loop it stops and cancelling it, so that a second stop can be made to come late.</summary>
    internal Func<Task>? BeforeCancel { get; set; }

    /// <summary>True while the loop runs.</summary>
    public bool IsRunning { get { lock (_gate) return _loop is { IsCompleted: false }; } }

    /// <summary>Starts watching (or starts the 10 minutes again when it already does).</summary>
    public void Start()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            var previous = _loop;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            // One loop at a time: a loop that is being replaced has ended before the new one asks.
            _loop = Task.Run(async () =>
            {
                if (previous is not null)
                {
                    try { await previous.ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { }
                }
                await RunAsync(token).ConfigureAwait(false);
            });
        }
    }

    /// <summary>
    /// Cancels the loop and waits until it has ended. The loop stays the watch's loop until then (<see cref="IsRunning"/> says so), so that a <see cref="Start"/> that comes while the stop is in
    /// flight - a window shown again at once - waits for it like any restart does: never a second loop asking beside the old one, whose ask may be slow to honour the cancellation.
    /// </summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
        }
        if (cts is null) return;
        if (BeforeCancel is { } hook) await hook().ConfigureAwait(false);
        // A second stop that read the same source may come here after the first has finished and disposed it: the loop it would cancel has ended, and there is nothing left to cancel.
        try { await cts.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        lock (_gate)
        {
            // Only what this stop stopped: a Start that came meanwhile has made a loop of its own.
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
                _loop = null;
            }
        }
        cts.Dispose();
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task RunAsync(CancellationToken ct)
    {
        var started = _time.GetUtcNow();
        try
        {
            while (!ct.IsCancellationRequested && _time.GetUtcNow() - started < _for)
            {
                int waiting;
                try { waiting = await _poll(true, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException) { waiting = 1; }
                if (waiting == 0) return;
                await Task.Delay(_every, _time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
