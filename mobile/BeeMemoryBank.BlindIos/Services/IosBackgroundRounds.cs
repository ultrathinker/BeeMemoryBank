using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.BlindIos.Services;

/// <summary>How an account fared in a background wake.</summary>
public enum IosRoundKind
{
    /// <summary>A sync round or a piece of a first load / long job was asked of it and ended as it ended (the result says).</summary>
    Done,

    /// <summary>Nothing was asked of it: held by a limit, a switch or the person (<c>PauseOf</c>), or not paired yet.</summary>
    Held,

    /// <summary>It was removed between the snapshot and its turn.</summary>
    Gone,

    /// <summary>Its slice of the wake ran out, or iOS ended the wake: the job paused and continues later.</summary>
    Stopped,

    /// <summary>It was asked and failed; the result says the kind of failure (never the failure's own text).</summary>
    Failed,
}

/// <summary>What one account got out of a background wake: its id and name, the sentence for ITS OWN log and its "last background round" line, and whether anything was asked of it.</summary>
public sealed record IosAccountRound(string AccountId, string Name, string Result, bool Ran, IosRoundKind Kind = IosRoundKind.Done);

/// <summary>
/// What a whole background wake did. <see cref="EndedByIos"/>: the time was up before every account had its turn. <see cref="Skipped"/>: the wake did nothing because another round of the
/// device was on, or the app was open (the task is completed as "nothing to do").
/// </summary>
public sealed record IosRoundOutcome(IReadOnlyList<IosAccountRound> Accounts, string? Note, bool EndedByIos, int NotReached, string? Skipped = null)
{
    public static IosRoundOutcome SkippedBecause(string reason) => new([], null, EndedByIos: false, NotReached: 0, Skipped: reason);

    /// <summary>
    /// The sentence for the DEVICE's own log: what the wake did in numbers and the reason it did nothing, and nothing of any account - no name, no result, no failure. Each account's sentence is
    /// written only to that account's own log and state (<see cref="IosBackgroundRecorder"/>): one account's log never holds another account's name or fate.
    /// </summary>
    public string DeviceSummary
    {
        get
        {
            if (Skipped is not null) return Skipped;
            var parts = new List<string>();
            if (Note is not null) parts.Add(Note);
            if (Accounts.Count > 0)
            {
                var asked = Accounts.Count(a => a.Ran);
                var failed = Accounts.Count(a => a.Kind == IosRoundKind.Failed);
                var held = Accounts.Count(a => a.Kind == IosRoundKind.Held);
                parts.Add($"{Accounts.Count} account{(Accounts.Count == 1 ? "" : "s")} had a turn: {asked} asked, {held} held, {failed} failed.");
            }
            if (NotReached > 0) parts.Add($"{NotReached} more wait{(NotReached == 1 ? "s" : "")} for the next wake.");
            if (EndedByIos) parts.Add("Ended by iOS before it finished; it continues later.");
            return parts.Count == 0 ? "Nothing to do." : string.Join(" ", parts);
        }
    }
}

/// <summary>
/// The two background tasks the app asks iOS for, and what each does when iOS grants it. iOS decides when (and whether) they run: an app
/// refresh task gets about 30 seconds a few times a day at best, depending on how often the person uses the app, the battery and Low Power
/// Mode; a processing task gets minutes, typically at night on the charger. A blind copy listens on no port and keeps no socket, so these
/// rounds and the time the app is open are all it has.
///
/// <para><b>Several accounts share one wake.</b> The two task identifiers are fixed in Info.plist (they cannot be per account), so each wake serves the accounts of the
/// device itself: in the order of the in-app loop (<see cref="BlindDeviceScheduler.PassOrder"/>: the one that synced longest ago first), one at a time, each through the
/// account's own guard (<see cref="IBlindAccountsController.PauseOf"/>: a limit, a switch or the person that says "not now" costs no request). When there is more than one account
/// each gets a slice of the time the wake is expected to have, so that an account in the middle of a long first load cannot use up every wake; the one that was not reached is the
/// oldest at the next wake and goes first. One account is never cut short (the wake ends when iOS says so, as always). The honest price is in the docs: with several accounts each
/// one syncs less often in the background, and only the open app syncs them all.</para>
/// </summary>
public static class IosBackgroundRounds
{
    /// <summary>BGAppRefreshTask: one sync round of each account that fits in the time (or a piece of a first load, which resumes).</summary>
    public const string SyncTaskId = "com.beememorybank.blind.sync";

    /// <summary>BGProcessingTask with network: the first load or a due backup, then a sync round, for each account that fits in the time.</summary>
    public const string WorkTaskId = "com.beememorybank.blind.work";

    /// <summary>The earliest the next sync round is asked for: the Android worker's period. iOS treats it as "not before", never as "at".</summary>
    public static readonly TimeSpan SyncNotBefore = TimeSpan.FromMinutes(15);

    /// <summary>The earliest the next long-job window is asked for: the hourly long job of the other copies.</summary>
    public static readonly TimeSpan WorkNotBefore = TimeSpan.FromHours(1);

    /// <summary>The time an app refresh wake is planned to have (iOS gives about 30 s and ends it itself; this only divides it between accounts).</summary>
    public static readonly TimeSpan AppRefreshBudget = TimeSpan.FromSeconds(25);

    /// <summary>The least an account gets of an app refresh wake when there are several.</summary>
    public static readonly TimeSpan AppRefreshMinimumSlice = TimeSpan.FromSeconds(5);

    /// <summary>The time a processing wake is planned to have (minutes, at night; iOS ends it itself).</summary>
    public static readonly TimeSpan ProcessingBudget = TimeSpan.FromMinutes(5);

    /// <summary>The least an account gets of a processing wake when there are several.</summary>
    public static readonly TimeSpan ProcessingMinimumSlice = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The app refresh round. Starts the accounts (iOS may have started the app for this task alone), asks about invitations that wait for the owner, then for each account in turn: nothing
    /// before pairing; the first load while it is not done (it resumes where the last piece stopped); otherwise one sync round. Never throws for the cancellation iOS asks for when the time is up:
    /// the outcome says what was done and how many accounts wait for the next wake.
    /// </summary>
    public static Task<IosRoundOutcome> RunSyncAsync(IBlindAccountsController accounts, CancellationToken ct, TimeProvider? time = null) =>
        RunAsync(accounts, work: false, time ?? TimeProvider.System, ct, AppRefreshBudget, AppRefreshMinimumSlice);

    /// <summary>The processing round: for each account, the first load or a due backup, then a sync round when the copy is loaded.</summary>
    public static Task<IosRoundOutcome> RunWorkAsync(IBlindAccountsController accounts, CancellationToken ct, TimeProvider? time = null) =>
        RunAsync(accounts, work: true, time ?? TimeProvider.System, ct, ProcessingBudget, ProcessingMinimumSlice);

    /// <summary>The wake itself, with the planned time and the least slice as parameters (the public entries give the app refresh's and the processing task's; the tests give short ones).</summary>
    internal static async Task<IosRoundOutcome> RunAsync(
        IBlindAccountsController accounts, bool work, TimeProvider time, CancellationToken ct, TimeSpan budget, TimeSpan minimumSlice)
    {
        var rounds = new List<IosAccountRound>();
        try
        {
            await accounts.StartAsync(ct).ConfigureAwait(false);
            await AskAboutInvitationsAsync(accounts, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new IosRoundOutcome(rounds, null, EndedByIos: true, NotReached: 0);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new IosRoundOutcome(rounds, "The accounts could not be started: " + IosFailureText.Say(ex), EndedByIos: false, NotReached: 0);
        }

        var snapshot = accounts.GetSnapshot();
        if (snapshot.RegistryProblem is { } problem) return new IosRoundOutcome(rounds, problem, EndedByIos: false, NotReached: 0);
        var order = BlindDeviceScheduler.PassOrder(snapshot);
        if (order.Count == 0)
            return new IosRoundOutcome(rounds, snapshot.Accounts.Count == 0 ? "No account yet." : "No account is ready to sync.", EndedByIos: false, NotReached: 0);

        var began = time.GetTimestamp();
        for (var i = 0; i < order.Count; i++)
        {
            if (ct.IsCancellationRequested) return new IosRoundOutcome(rounds, null, EndedByIos: true, NotReached: order.Count - i);
            var card = order[i];

            // One account has the whole wake; several share what is left of it.
            using var slice = order.Count == 1 ? null : new CancellationTokenSource(SliceFor(budget, minimumSlice, time.GetElapsedTime(began), order.Count - i), time);
            using var linked = slice is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, slice.Token);
            var token = linked?.Token ?? ct;

            string result;
            var ran = true;
            var kind = IosRoundKind.Done;
            try
            {
                (result, ran, kind) = await RunAccountAsync(accounts, card, work, time, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                rounds.Add(new IosAccountRound(card.Entry.Id, card.Entry.Name, "Stopped by iOS; it continues where it stopped.", Ran: true, IosRoundKind.Stopped));
                return new IosRoundOutcome(rounds, null, EndedByIos: true, NotReached: order.Count - i - 1);
            }
            catch (OperationCanceledException)
            {
                result = "Stopped to give the next account its time; it continues where it stopped.";
                kind = IosRoundKind.Stopped;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // One account's trouble is its own: the others still get their turn. Told by the KIND of failure, never by its message.
                result = "Failed: " + IosFailureText.Say(ex);
                kind = IosRoundKind.Failed;
            }
            rounds.Add(new IosAccountRound(card.Entry.Id, card.Entry.Name, result, ran, kind));
        }
        return new IosRoundOutcome(rounds, null, EndedByIos: false, NotReached: 0);
    }

    /// <summary>What an account gets when several share a wake: an even share of what is left of the planned time, never less than the minimum.</summary>
    internal static TimeSpan SliceFor(TimeSpan budget, TimeSpan minimum, TimeSpan elapsed, int accountsLeft)
    {
        var left = budget - elapsed;
        var share = left <= TimeSpan.Zero ? TimeSpan.Zero : left / Math.Max(1, accountsLeft);
        return share < minimum ? minimum : share;
    }

    private static async Task AskAboutInvitationsAsync(IBlindAccountsController accounts, CancellationToken ct)
    {
        try { await accounts.PollInvitationsAsync(force: false, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An invitation that cannot be asked about now is asked about at the next pass; it must not cost the accounts their turn.
        }
    }

    private static async Task<(string Result, bool Ran, IosRoundKind Kind)> RunAccountAsync(
        IBlindAccountsController accounts, BlindAccountSnapshot card, bool work, TimeProvider time, CancellationToken ct)
    {
        var id = card.Entry.Id;
        BlindPause? pause;
        IBlindAppController app;
        try
        {
            // Through the account's own guard first: a limit, a switch or the person says "not now" and then nothing is asked of the network.
            pause = accounts.PauseOf(id);
            app = accounts.For(id);
        }
        catch (KeyNotFoundException)
        {
            return ("Removed.", false, IosRoundKind.Gone);
        }
        if (pause is not null) return (BlindPauseText.Job(pause, time.LocalTimeZone, time.GetUtcNow()), false, IosRoundKind.Held);

        await app.InitializeAsync(ct).ConfigureAwait(false);
        var status = app.GetStatus();
        if (!status.IsPaired) return ("Not paired yet.", false, IosRoundKind.Held);
        if (!work)
            return (status.InitialLoadDone ? await app.RequestSyncAsync(ct).ConfigureAwait(false) : await app.RunHeavyAsync(forceBackup: false, ct).ConfigureAwait(false), true, IosRoundKind.Done);

        var heavy = await app.RunHeavyAsync(forceBackup: false, ct).ConfigureAwait(false);
        if (!app.GetStatus().InitialLoadDone) return (heavy, true, IosRoundKind.Done);
        // The long job may have made the source's second refusal: the account is removed by its owner then, and a sync round after it would only ask a source that has just said no.
        if (!StillActive(accounts, id)) return (heavy, true, IosRoundKind.Done);
        var sync = await app.RequestSyncAsync(ct).ConfigureAwait(false);
        return ($"{heavy} {sync}", true, IosRoundKind.Done);
    }

    private static bool StillActive(IBlindAccountsController accounts, string id) =>
        accounts.GetSnapshot().Accounts.Any(a => a.Entry.Id == id && a.Entry.Phase == BlindAccountPhases.Active);
}
