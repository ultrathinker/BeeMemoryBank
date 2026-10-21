using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>One account as a screen shows it: its entry in <c>accounts.json</c>, its controller's status (null when its runtime is not up), and what went wrong if it is not.</summary>
/// <param name="Problem">A sentence for the card when the account could not be started or its removal stopped; null otherwise. Never holds a secret.</param>
/// <param name="Usage">What the account holds and used today, or null when its runtime is not up.</param>
/// <param name="Pause">The pause that holds the account now (a limit, a switch, "paused by you"), or null.</param>
/// <param name="Note">What an account that was added from an invitation is doing while it has no answer yet ("Could not reach mem.example.org. It will try again."); null otherwise. Never holds a secret.</param>
public sealed record BlindAccountSnapshot(
    BlindAccountEntry Entry, BlindAppStatus? Status, string? Problem = null, BlindAccountUsageView? Usage = null, BlindPause? Pause = null, string? Note = null);

/// <summary>What an account holds and used: the numbers behind "1.2 GB of 2 GB" and "312 requests, about 38 MB of 200 MB". Bodies are exact, headers and TLS estimated: a screen says "about".</summary>
/// <param name="StoredBytes">The bytes the account holds (live); null when they cannot be measured (a folder cannot be read, or is a link): unknown is never shown as zero.</param>
/// <param name="AverageTrafficPerDay">Over the last seven days ending today (from the first day with a record); null when nothing was counted yet.</param>
/// <param name="Problem">Set when the account's usage counts exist and cannot be read or trusted (<see cref="IBlindAccountsController.StartNewUsageLedger"/> is the way on); null otherwise.</param>
public sealed record BlindAccountUsageView(long? StoredBytes, long RequestsToday, long BytesInToday, long BytesOutToday, long? AverageTrafficPerDay, string? Problem = null)
{
    public long TrafficToday => BytesInToday + BytesOutToday;
}

/// <summary>What the whole device holds and used today, over every account.</summary>
/// <param name="StoredBytes">Null when any account's cannot be measured.</param>
/// <param name="Problem">Set when the device's own usage counts exist and cannot be read or trusted (<see cref="IBlindAccountsController.StartNewDeviceUsageLedger"/> is the way on); null otherwise.</param>
public sealed record BlindDeviceUsageView(long? StoredBytes, long TrafficToday, long RequestsToday, int AccountCount, string? Problem = null);

/// <summary>The kinds of <see cref="BlindRepairIssue"/>: words of the contract, never translated.</summary>
public static class BlindRepairKinds
{
    /// <summary>The app's root holds a 2.6 copy (an identity in its state, or a database) that the list of accounts does not name.</summary>
    public const string LegacyRootNotListed = "legacy-root-not-listed";

    /// <summary>A folder under <c>accounts/</c> that is named like an account and that the list does not name.</summary>
    public const string FolderNotListed = "folder-not-listed";

    /// <summary>A listed account that has completed its identity and whose folder is gone.</summary>
    public const string FolderMissing = "folder-missing";

    /// <summary>A path that is a link (or a folder replaced by a file), or a file or folder inside an account's folder that the app opens by name and that is a link: nothing was opened through it, and it needs a person.</summary>
    public const string UnsafePath = "unsafe-path";

    /// <summary>A regular file under <c>accounts/</c> whose name is that of an account and that the list does not name: it blocks that name, and nothing can adopt it (a person moves it away).</summary>
    public const string FileInPlaceOfFolder = "file-in-place-of-folder";
}

/// <summary>
/// One thing the list of accounts and the disk do not agree on. <see cref="Description"/> says what, in a sentence for the screen. The actions that settle it are
/// explicit calls on <see cref="IBlindAccountsController"/>, each one atomic and never taken by the app on its own.
/// </summary>
/// <param name="Kind"><see cref="BlindRepairKinds"/>.</param>
/// <param name="AccountId">The account or folder it is about (the id), or null for the legacy root.</param>
public sealed record BlindRepairIssue(string Kind, string? AccountId, string Description)
{
    /// <summary>True when <see cref="IBlindAccountsController.AdoptLegacyRootAsync"/> or <see cref="IBlindAccountsController.AdoptFolderAsync"/> settles it.</summary>
    public bool CanAdopt => Kind is BlindRepairKinds.LegacyRootNotListed or BlindRepairKinds.FolderNotListed;

    /// <summary>True when <see cref="IBlindAccountsController.ForgetMissingAccountAsync"/> settles it.</summary>
    public bool CanForget => Kind == BlindRepairKinds.FolderMissing;
}

/// <summary>Everything the accounts screen needs in one read.</summary>
/// <param name="RegistryProblem">Set when <c>accounts.json</c> is damaged or the device's accounts could not be read: the app opens on this sentence and touches no account.</param>
/// <param name="Repairs">What the list of accounts and the disk disagree on (empty when they agree): the repair state, with one entry per thing to settle.</param>
public sealed record BlindAccountsSnapshot(
    IReadOnlyList<BlindAccountSnapshot> Accounts, BlindDeviceLimits Device, string? RegistryProblem, bool CanAdd, int MaxAccounts,
    BlindDeviceUsageView? DeviceUsage = null, IReadOnlyList<BlindRepairIssue>? Repairs = null)
{
    public IReadOnlyList<BlindRepairIssue> RepairIssues => Repairs ?? [];
}

/// <summary>What <see cref="IBlindAccountsController.AcceptCallCodeWithAccount"/> did: the account that took the code, or why none did.</summary>
/// <param name="AccountId">The account whose pairing secret made the code and that accepted it; null when nobody did.</param>
/// <param name="Error">The sentence that says why not; null when an account accepted the code.</param>
public sealed record BlindAcceptResult(string? AccountId, string? Error = null)
{
    public bool Ok => Error is null && AccountId is not null;
}

/// <summary>What <see cref="IBlindAccountsController.AddAsync"/> made: the new account's id, or why there is none.</summary>
/// <param name="AccountId">The account that was made (also set when it was made but could not be started: <see cref="Error"/> then says so).</param>
public sealed record BlindAddResult(string? AccountId, string? Error = null)
{
    public bool Ok => Error is null && AccountId is not null;
}

/// <summary>
/// What a host screen and scheduler use of a device that holds SEVERAL blind accounts, one per source mesh: list them, add one with the existing pairing
/// (the account shows a phone code, the computer answers with a call code), rename it, set its limits and switches, pause it, remove it, and reach one
/// account's own <see cref="IBlindAppController"/> (<see cref="For"/>) - the same controller, with the same rules and tests, that a single-account copy always had.
/// <see cref="BlindAccountHost"/> is the implementation.
/// </summary>
public interface IBlindAccountsController
{
    /// <summary>Raised when something an accounts screen shows changed (an account was added or removed or edited, or one of them reports a change). May come from any thread.</summary>
    event Action? Changed;

    /// <summary>The most accounts this device holds (8): each one is a service provider, a connection pool and an HTTP handler.</summary>
    int MaxAccounts { get; }

    /// <summary>
    /// Takes the data folder (a second host on it is refused with a sentence and may call this again later), opens the registry, adopts a 2.6 install as account 1 (once),
    /// compares the list with the disk, finishes a removal that a crash cut off, and starts every account's runtime. A damaged registry, a folder that is a link, or a list that
    /// does not agree with the disk is not an exception: <see cref="GetSnapshot"/> then carries the sentence (and <see cref="BlindAccountsSnapshot.RepairIssues"/>) and no account
    /// was touched. One account that cannot start does not stop the others.
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    BlindAccountsSnapshot GetSnapshot();

    /// <summary>The controller of one account. Throws <see cref="KeyNotFoundException"/> for an id that is not there (any more) or an account that is being removed.</summary>
    IBlindAppController For(string accountId);

    /// <summary>The account's own services (the host's pages and workers resolve what they need from them). Throws like <see cref="For"/>.</summary>
    IServiceProvider ServicesFor(string accountId);

    /// <summary>The pause that holds the account now, or null: asked by the scheduler before it spends a round on an account. Throws like <see cref="For"/>.</summary>
    BlindPause? PauseOf(string accountId);

    /// <summary>
    /// The scheduler gave up waiting for a job of this account that was asked to stop and did not (a thread cannot be killed). A long job still holds the device's gate then: the host records
    /// who holds it, so that the other accounts' long jobs say "the device is busy" and this account's card says its job did not stop. Nothing happens for an account that is not there (any
    /// more). The default does nothing: a host with no device gate has nothing to record.
    /// </summary>
    void NoteJobAbandoned(string accountId, bool longJob) { }

    /// <summary>Adds an account that is paired the old way: it makes its own identity and shows a phone code (<c>For(id).PairingCode()</c>); the computer's call code is then given to <see cref="AcceptCallCode"/> or to that account's own controller.</summary>
    /// <param name="name">What the person calls it; null or blank is "Account N".</param>
    Task<BlindAddResult> AddAsync(string? name, CancellationToken ct = default);

    /// <summary>
    /// What a person is shown before an invitation is accepted, from the pasted text alone (no network, nothing is written): whose copy it is, from which computer, the limits the
    /// account will start with, how long the invitation works - or why it is not taken (<see cref="BlindInvitationRefusals"/>: a text that is not an invitation, a version or a trust mode this app
    /// does not know, an expired one, an account for the same hub already on this device, the cap of accounts, a damaged list or a repair that blocks adding).
    /// </summary>
    BlindInvitationPreview PreviewInvitation(string? text);

    /// <summary>
    /// Adds an account from an invitation: a new folder and identity (the pairing path's own), the identity probe of the hub (its node id and key must be the invitation's, or nothing is sent), the
    /// redemption, and then the account waits for the owner (<see cref="BlindAccountPhases.AwaitingConfirmation"/>, with the match code to read to him). The invitation text is never stored and its secret
    /// lives in memory only until the hub has answered. A network error or a locked hub leaves the account <see cref="BlindAccountPhases.Pending"/> and it tries again (bounded, until the invitation runs out); every
    /// other refusal removes the account again and is the sentence of the result. A crash at any step is resumed or cleaned up at the next start.
    /// </summary>
    /// <param name="settings">The limits and switches the account starts with; null is <see cref="BlindAccountDefaults.ForInvited"/> for this device (<see cref="BlindAccountHostOptions.IsPhone"/>).</param>
    Task<BlindAddResult> AddFromInvitationAsync(string? text, BlindAccountSettings? settings = null, CancellationToken ct = default);

    /// <summary>
    /// Asks the hub about every account that waits for an answer (a redemption not delivered yet, or the owner's decision): confirmed, refused, expired. A due ask is made, the rest are left (each account keeps
    /// the rhythm the hub allows: a back-off after a 429 or a locked hub is kept even when <paramref name="force"/> is set). The device scheduler calls it at each pass with <paramref name="force"/> false (an
    /// account is then asked every 15 minutes); <see cref="BlindInvitationWatch"/> calls it every 15 seconds with it set while the accounts screen is open. Returns how many accounts still wait.
    /// </summary>
    Task<int> PollInvitationsAsync(bool force = false, CancellationToken ct = default);

    /// <summary>
    /// The source stopped accepting this device (<see cref="BlindAccountPhases.RemovedByOwner"/>) and the person wants to try again (a mistake on the source's side, a proxy that said no once): the account
    /// goes back to <see cref="BlindAccountPhases.Active"/> and the next round decides. Null on success, otherwise why not.
    /// </summary>
    string? Reconnect(string accountId);

    /// <summary>
    /// Gives a computer's call code to the account that is waiting for an answer and whose pairing secret made it. Null on success, otherwise why not. An account
    /// that does not recognise the code is not touched; no account waiting is a sentence of its own.
    /// </summary>
    string? AcceptCallCode(string text);

    /// <summary>
    /// The same as <see cref="AcceptCallCode"/>, and says WHICH account took the code (the one whose own pairing secret made it). A screen that follows an account (a re-pair of one of
    /// several that are paired and wait at once) must not guess it from their states. The default knows no account: a host that does overrides it (<see cref="BlindAccountHost"/> does).
    /// </summary>
    BlindAcceptResult AcceptCallCodeWithAccount(string text) => new(null, AcceptCallCode(text));

    /// <summary>Each setter returns null on success or the sentence that says why not (an unknown account, a damaged registry, a value that is not allowed).</summary>
    string? Rename(string accountId, string name);

    string? SetLimits(string accountId, BlindAccountLimits limits);

    string? SetSwitches(string accountId, bool unmeteredOnly, bool pauseOnPowerSaving);

    string? SetPaused(string accountId, bool paused);

    string? SetDeviceLimits(BlindDeviceLimits limits);

    /// <summary>
    /// Removes the account: its work is stopped and waited for, the source is told when this device is online, its keys, state and files are deleted, and its entry
    /// goes last. Throws <see cref="AggregateException"/> naming what is left when something could not be done (the account then stays "deleting" and the next call,
    /// or the next start, finishes it).
    /// </summary>
    Task RemoveAsync(string accountId, CancellationToken ct = default);

    /// <summary>
    /// Repair, on purpose: the account's usage counts exist and cannot be read or trusted, so its limits of traffic and requests are not checked and the account is paused. Keeps the damaged
    /// file aside as <c>usage.json.damaged-&lt;utc&gt;</c> (never over another file) and starts a new ledger. Null on success, otherwise why not. Throws like <see cref="For"/>.
    /// </summary>
    string? StartNewUsageLedger(string accountId);

    /// <summary>Repair, on purpose: the device's own usage counts (its high-water date) exist and cannot be read or trusted. Keeps the damaged file aside and starts a new one. Null on success, otherwise why not.</summary>
    string? StartNewDeviceUsageLedger();

    /// <summary>
    /// Repair: the app's root holds a 2.6 copy that the list of accounts omits (<see cref="BlindRepairKinds.LegacyRootNotListed"/>). Lists it as an account in one atomic
    /// write of the list and starts it; nothing of the copy is moved, copied or re-keyed. Null on success, otherwise why not (nothing was changed then).
    /// </summary>
    Task<string?> AdoptLegacyRootAsync(CancellationToken ct = default);

    /// <summary>
    /// Repair: a folder under <c>accounts/</c> that the list does not name (<see cref="BlindRepairKinds.FolderNotListed"/>). Lists it as an account in one atomic write and
    /// starts it. Refused for a folder that is a link. Null on success, otherwise why not.
    /// </summary>
    /// <param name="name">What to call it; null is "Recovered copy".</param>
    Task<string?> AdoptFolderAsync(string accountId, string? name = null, CancellationToken ct = default);

    /// <summary>
    /// Repair: a listed account whose folder is gone (<see cref="BlindRepairKinds.FolderMissing"/>). Takes its entry out of the list in one atomic write and then clears the keys
    /// and the state it kept; nothing else is touched. Refused while the folder is there. Null on success, otherwise why not.
    /// </summary>
    Task<string?> ForgetMissingAccountAsync(string accountId, CancellationToken ct = default);
}

/// <summary>
/// What the host's platform does when an account's work must stop (a removal, a wipe): cancel its scheduled jobs, stop its foreground service. The defaults do
/// nothing (a host whose stop is not undone, or whose scheduler asks the account host itself).
/// </summary>
public interface IBlindAccountLifecycle
{
    void StopBackgroundWork(BlindAccountEntry account) { }

    void StopBackupService(BlindAccountEntry account) { }

    /// <summary>Called when a stop was given up (the work did not end in time and nothing was deleted): put back what <see cref="StopBackgroundWork"/> took away.</summary>
    void ResumeBackgroundWork(BlindAccountEntry account) { }
}

/// <param name="Services">The account's own services, still running: its HTTP client and signer can be used for the courtesy call.</param>
public sealed record BlindAccountLeaveContext(BlindAccountEntry Account, IServiceProvider Services);

/// <summary>
/// The courtesy a removal pays the source: "this device leaves the network" (<see cref="BlindSourceLeaveHook"/>: <c>POST /api/sync/leave</c>, the default of <see cref="BlindAccountHostOptions.LeaveHook"/>).
/// Best effort and bounded: a hook that fails or is slow never holds up a removal, which completes locally whatever it does.
/// </summary>
public interface IBlindAccountLeaveHook
{
    /// <remarks>
    /// MUST be idempotent at the source. A removal that a crash cut off after this call (the registry still says <c>deleting</c>) calls it AGAIN at the next start, for the same account, with
    /// nothing remembered of the first call: leaving a network one has already left is a success, not an error and not a second event. Best effort and bounded, as above.
    /// </remarks>
    Task LeaveAsync(BlindAccountLeaveContext context, CancellationToken ct);
}
