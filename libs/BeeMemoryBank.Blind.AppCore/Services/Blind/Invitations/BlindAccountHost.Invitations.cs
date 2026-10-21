using System.Security.Cryptography;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// Adding an account from an invitation (release 2.7), the device's side. A new account is made the way the old pairing makes one (its own folder, identity, keys, state and HTTP client);
/// what differs is how it learns where to call: instead of a code that the person carries to a computer and back, the invitation names the hub, and the hub's owner confirms the
/// device by reading a short match code that both screens show. The steps, and what a crash at each of them leaves:
/// <list type="number">
/// <item>The account is listed, <see cref="BlindAccountPhases.Pending"/>, and what the invitation says about the hub (never its secret) is kept in the account's state. A crash here: the next start
/// finds a pending account with nothing to redeem with and cleans it up.</item>
/// <item>The account's runtime starts and makes its identity (the pairing path's own <c>CreateIdentityAsync</c>); the pairing secret that identity comes with is spent (this account is not paired the
/// old way).</item>
/// <item><b>Identity probe, before any secret is sent</b>: the hub's identity page, through the client the invitation's trust selects, must give the invitation's node id AND key.</item>
/// <item>The redemption. The invitation's secret is held in memory only, until the hub has answered; it is written nowhere (not the registry, not the state, not the log, no exception).
/// A lost answer is retried with the same identity: the hub answers the same pending 202 to the same device and key, but only while the invitation is still <c>redeemed</c> and its confirmation window is open; once the owner has moved it on, a redemption is refused and the device uses the signed status request instead. A hub that is locked or busy is retried with a back-off until the invitation runs out.</item>
/// <item>The hub's 202 makes the account <see cref="BlindAccountPhases.AwaitingConfirmation"/> with its match code. A crash before that is written, with the redemption already delivered: the next
/// start has no secret but asks the hub (a signed status request), finds the redemption and goes on. A crash with nothing delivered: the hub does not know the device, and the account is removed.</item>
/// <item>The owner's decision arrives by status polling: confirmed (the account keeps a call code built from the invitation, spent at once, and becomes active), refused, or expired.</item>
/// </list>
/// Every call to the hub goes through the account's own metered client: an account over its limits says so instead of redeeming.
/// </summary>
public sealed partial class BlindAccountHost
{
    /// <summary>An account that waits for the owner is asked this often when nobody is watching (the rhythm of a sync round).</summary>
    public static readonly TimeSpan BackgroundAsk = TimeSpan.FromMinutes(15);

    /// <summary>How long after an invitation ran out the owner can still decide about a redemption that was delivered just before it did (the hub's window to confirm).</summary>
    public static readonly TimeSpan ConfirmWindow = TimeSpan.FromHours(72);

    private static readonly TimeSpan MinWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim _inviteGate = new(1, 1);
    private readonly Dictionary<string, SemaphoreSlim> _askGates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AccountWork> _work = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingRedemption> _redemptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InviteRhythm> _rhythm = new(StringComparer.Ordinal);

    /// <summary>The hub's clock and this device's may differ by this much before a "not valid" from the hub is doubted (a status proof is accepted within ten minutes of the hub's clock).</summary>
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);

    /// <summary>While the clocks disagree the hub is asked again this seldom (even for an ask the person makes): the person has to correct the clock, and nothing else will change.</summary>
    private static readonly TimeSpan ClockRecheck = TimeSpan.FromMinutes(5);

    /// <summary>The invitation's secret while the redemption is not answered, and nothing else: held here, zeroed when the account has an answer, is removed or the host ends. Never persisted.</summary>
    private sealed class PendingRedemption(BlindInvitationTarget target, byte[] secret) : IDisposable
    {
        private byte[]? _secret = secret;

        public BlindInvitationTarget Target { get; } = target;

        public byte[]? Secret => Volatile.Read(ref _secret);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _secret, null) is { } held) CryptographicOperations.ZeroMemory(held);
        }
    }

    /// <summary>
    /// What one account's invitation work shares with the end of that account: a token every ask of the hub for it runs under (cancelled when the account is removed, so that a call that waits
    /// on the network stops), and, while an add is still setting the account up, the moment that add ends - a removal waits for it, so that nothing the add does comes after the cleanup.
    /// </summary>
    private sealed class AccountWork(TaskCompletionSource addEnded, bool setUp)
    {
        private readonly CancellationTokenSource _cts = new();
        private bool _setUp = setUp;
        private bool _cutShort;

        public static AccountWork ForAdd() => new(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), setUp: false);

        public static AccountWork Idle()
        {
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ended.SetResult();
            return new AccountWork(ended, setUp: true);
        }

        /// <summary>
        /// False while the add that made the account is still giving it a runtime and an identity: an ask of the hub from anywhere else (the scheduler, the screen's watch) leaves it alone until
        /// then - it would find no identity yet, take that for a failure, and remove an account that is being made.
        /// </summary>
        public bool IsSetUp => Volatile.Read(ref _setUp);

        public void MarkSetUp() => Volatile.Write(ref _setUp, true);

        /// <summary>
        /// True once the add that made the account has ended because it saw this work cancelled, before it finished setting the account up: whatever the removal that cancelled it does next, nothing
        /// of the add is left to carry the account on (see <see cref="BlindAccountHost.AfterCutShortAddAsync"/>).
        /// </summary>
        public bool CutShort => Volatile.Read(ref _cutShort);

        public void NoteCutShort() => Volatile.Write(ref _cutShort, true);

        public CancellationToken Token => _cts.Token;

        public bool IsCancelled => _cts.IsCancellationRequested;

        public Task AddEnded => addEnded.Task;

        public void EndAdd() => addEnded.TrySetResult();

        public void Cancel()
        {
            try { _cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>The same account after a removal that did not happen: a token that is not cancelled, the same add.</summary>
        public AccountWork Reopened() => new(addEnded, IsSetUp);
    }

    /// <summary>When the hub is asked about an account next, and what the card says while it waits to try again. Memory only: a restart asks at once.</summary>
    private sealed class InviteRhythm
    {
        public DateTimeOffset? DueAt;
        public DateTimeOffset? HoldUntil;
        public int Failures;
        public string? Note;

        /// <summary>This run of the app tried to redeem (so it knows what its tries came to).</summary>
        public bool Attempted;

        /// <summary>A try of this run may have reached the hub's decision (an answer lost on the way, one not understood, a 202, a 410 that can be the answer to a retry).</summary>
        public bool MaybeDelivered;

        /// <summary>Every try of this run was refused or never left before the hub could have taken anything: after the invitation ran out there is nothing to ask the hub about.</summary>
        public bool KnownNotDelivered => Attempted && !MaybeDelivered;
    }

    private enum InviteStepKind { Waiting, Retrying, Failed, Done, Gone }

    private sealed record InviteStep(InviteStepKind Kind, string? Message = null)
    {
        public static readonly InviteStep Waiting = new(InviteStepKind.Waiting);
        public static readonly InviteStep Done = new(InviteStepKind.Done);
        public static readonly InviteStep Gone = new(InviteStepKind.Gone);
    }

    /// <summary>How many redemptions hold a secret now (tests read it: after an account ends, or the host does, it is zero).</summary>
    internal int HeldRedemptions { get { lock (_lock) return _redemptions.Count; } }

    /// <summary>Tests only: until when the hub is not asked about this account (a back-off or a Retry-After that is held), or null.</summary>
    internal DateTimeOffset? HoldOf(string id)
    {
        lock (_lock) return _rhythm.TryGetValue(id, out var rhythm) ? rhythm.HoldUntil : null;
    }

    /// <summary>Tests only: given the array of every secret a parse of an invitation text produces, so that they can see it wiped on every way out.</summary>
    internal Action<byte[]>? ParsedSecretObserver { get; set; }

    // ---- preview ----------------------------------------------------------------------------------------------------------------------------

    public BlindInvitationPreview PreviewInvitation(string? text)
    {
        var preview = Judge(text, out var invitation);
        // A preview keeps nothing: the secret of the parsed copy is wiped before the verdict is handed back.
        if (invitation is not null) CryptographicOperations.ZeroMemory(invitation.Secret);
        return preview;
    }

    /// <summary>
    /// The invitation in <paramref name="text"/> and whether this device takes it now, from the text and the accounts alone: nothing is sent and nothing is written. A refusal carries its
    /// sentence and kind; a version or a trust mode this app does not know is refused here, before anything is guessed about where to send a secret.
    ///
    /// <para>Ownership of the secret: the parsed invitation holds it. Every way out of this method wipes it EXCEPT the one that hands the invitation to the caller (<paramref name="invitation"/>
    /// is then not null), who owns that copy and wipes it when it is done. (The text itself is a string, and a string cannot be wiped: what holds it is the caller's.)</para>
    /// </summary>
    private BlindInvitationPreview Judge(string? text, out BlindInvitation? invitation)
    {
        invitation = null;
        var settings = BlindAccountDefaults.ForInvited(_options.IsPhone);
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !BlindInvitation.TryParse(trimmed, out var parsed))
            return RefusedText(BlindInvitationText.WhyNot(trimmed ?? ""), settings);

        var handedOver = false;
        try
        {
            ParsedSecretObserver?.Invoke(parsed.Secret);
            var host = new Uri(parsed.Address).Host;
            BlindInvitationPreview Refuse(string kind, string? sentence = null) =>
                new(parsed.SourceName, host, settings, parsed.ExpiresAt, sentence ?? BlindInvitationWords.For(kind, host, _options.MaxAccounts), kind);

            if (parsed.IsExpired(_options.Time.GetUtcNow())) return Refuse(BlindInvitationRefusals.Expired);

            // Nothing is added over a damaged list, a repair that waits, another copy holding the folder, or an app that has not finished starting.
            if (Blocked() is { } blocked) return Refuse(BlindInvitationRefusals.Blocked, blocked);
            if (!_started) return Refuse(BlindInvitationRefusals.Blocked, "The app is still starting. Try again in a moment.");

            var entries = _registry.Current.Entries.Where(e => e.Phase != BlindAccountPhases.Deleting).ToList();
            if (entries.Count >= _options.MaxAccounts) return Refuse(BlindInvitationRefusals.TooMany);

            switch (HubAlreadyHere(entries, parsed.HubNodeId))
            {
                case true: return Refuse(BlindInvitationRefusals.AlreadyHere);
                case null: return Refuse(BlindInvitationRefusals.Unreadable);
            }

            invitation = parsed;
            handedOver = true;
            return new BlindInvitationPreview(parsed.SourceName, host, settings, parsed.ExpiresAt);
        }
        finally
        {
            if (!handedOver) CryptographicOperations.ZeroMemory(parsed.Secret);
        }
    }

    private static BlindInvitationPreview RefusedText(string kind, BlindAccountSettings settings) =>
        new("", "", settings, null, BlindInvitationWords.For(kind), kind);

    /// <summary>
    /// True when an account on this device already calls (or was invited to call) the hub with this node id. Null when an account's state cannot be read: it cannot be said, and adding is refused
    /// (fail closed) rather than guessed.
    /// </summary>
    private bool? HubAlreadyHere(IEnumerable<BlindAccountEntry> entries, Guid hubNodeId)
    {
        foreach (var entry in entries)
        {
            try
            {
                var store = _options.States.For(entry);
                if (new BlindPhoneState(store).CallCode is { } call && call.NodeId == hubNodeId) return true;
                if (new BlindInvitationState(store).Target is { } target && target.HubNodeId == hubNodeId) return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                return null;
            }
        }
        return false;
    }

    // ---- add --------------------------------------------------------------------------------------------------------------------------------

    public async Task<BlindAddResult> AddFromInvitationAsync(string? text, BlindAccountSettings? settings = null, CancellationToken ct = default)
    {
        // The whole add - judging the text, writing the list, composing the runtime, the first ask of the hub - is ONE operation of the host: a quiesce and a disposal wait for it, and a removal of
        // the account it makes waits for its end (see StopInvitationWorkAsync). A host that is closing takes no new account.
        using var operation = TryBeginOperation();
        if (operation is null) return new BlindAddResult(null, "The app is closing. Add the account again when it has started.");

        // The parsed invitation holds the secret: this method owns it and wipes it on every way out. What outlives the call is the target (no secret) and the one copy the redemption owns.
        var preview = Judge(text, out var invitation);
        AccountWork? work = null;
        try
        {
            if (!preview.CanAdd || invitation is null) return new BlindAddResult(null, preview.Refusal ?? BlindInvitationWords.Malformed);

            var chosen = settings ?? preview.Settings;
            if (chosen.Limits is { MaxStoredBytes: < 0 } or { MaxTrafficBytesPerDay: < 0 } or { MaxRequestsPerDay: < 0 })
                return new BlindAddResult(null, "A limit cannot be negative. Zero pauses the account; leave a limit empty for none.");

            var target = BlindInvitationTarget.From(invitation);
            BlindAccountEntry entry;
            await _inviteGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Judged again inside the gate, and the cap and the entry are one step with the old way's own add (it holds the same lock): another add may have taken the last place,
                // or the same hub, since the preview.
                lock (_lock)
                {
                    var again = Judge(text, out var second);
                    // Only its verdict is used: the second parse's secret is wiped at once, whatever the verdict is.
                    if (second is not null) CryptographicOperations.ZeroMemory(second.Secret);
                    if (!again.CanAdd) return new BlindAddResult(null, again.Refusal);

                    var order = _registry.Current.Entries.Select(e => e.Order).DefaultIfEmpty(0).Max() + 1;
                    entry = new BlindAccountEntry(BlindAccountEntry.NewId(), BlindAccountLayouts.Dir, target.SourceName, BlindAccountPhases.Pending,
                        SourceOrigin: target.Address, InviteId: target.InviteIdText, AddedAt: _options.Time.GetUtcNow(), Order: order,
                        Limits: chosen.Limits.IsEmpty ? null : chosen.Limits, UnmeteredOnly: chosen.UnmeteredOnly, PauseOnPowerSaving: chosen.PauseOnPowerSaving);

                    // The entry first, then what the invitation says about the hub: from here a crash leaves a pending account that the next start resumes or cleans up.
                    _crash.Reach("invite:before-registry");
                    try { _registry.Update(file => file with { Accounts = [.. file.Entries, entry] }); }
                    catch (BlindRegistryDamagedException ex) { return new BlindAddResult(null, ex.Message); }
                    // From the moment the account is listed a removal of it may come: it waits for this add to end, and cancels what this add asks of the hub.
                    work = BeginAddWork(entry.Id);
                    _crash.Reach("invite:registered");
                }

                try { new BlindInvitationState(_options.States.For(entry)).Save(target); }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    await CleanupAsync(entry.Id, ownAdd: true).ConfigureAwait(false);
                    return new BlindAddResult(null, "The invitation could not be kept on this device (" + FailureWords(ex) + "). Nothing was sent.");
                }
                _crash.Reach("invite:context-saved");
                lock (_lock) _redemptions[entry.Id] = new PendingRedemption(target, [.. invitation.Secret]);
            }
            finally
            {
                _inviteGate.Release();
            }

            // What follows runs under the account's work token as well as the caller's: a removal cancels it, and the account is not made again behind the removal.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, work.Token);
            bool started;
            try { started = await StartRuntimeAsync(entry, linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                work.NoteCutShort();
                return new BlindAddResult(null, "The account was removed before it was set up.");
            }
            _crash.Reach("invite:started");
            if (!started || AccountIsGoing(entry.Id) || work.IsCancelled)
            {
                work.NoteCutShort();
                return new BlindAddResult(null, "The account was removed before it was set up.");
            }

            if (!AccountIsUp(entry.Id, out var startProblem))
            {
                await CleanupAsync(entry.Id, ownAdd: true).ConfigureAwait(false);
                return new BlindAddResult(null, "The account could not be set up: " + startProblem);
            }
            SpendPairingSecret(entry.Id);
            work.MarkSetUp();
            RaiseChanged();

            // The first try is made now, while the person waits; what it cannot finish (no network, a locked hub) goes on in the background, bounded.
            var step = await AdvanceAsync(entry.Id, force: true, ct, ownAdd: true).ConfigureAwait(false);
            switch (step.Kind)
            {
                case InviteStepKind.Failed:
                    await CleanupAsync(entry.Id, ownAdd: true).ConfigureAwait(false);
                    return new BlindAddResult(null, step.Message);
                case InviteStepKind.Gone:
                    return new BlindAddResult(null, "The account is gone.");
                default:
                    RaiseChanged();
                    return new BlindAddResult(entry.Id);
            }
        }
        finally
        {
            if (invitation is not null) CryptographicOperations.ZeroMemory(invitation.Secret);
            work?.EndAdd();
        }
    }

    /// <summary>True when the account is no longer listed, or is being removed.</summary>
    private bool AccountIsGoing(string id) => _registry.Current.Find(id) is not { } entry || entry.Phase == BlindAccountPhases.Deleting;

    /// <summary>True when the account's runtime is up and has its identity; otherwise false with a sentence.</summary>
    private bool AccountIsUp(string id, out string problem)
    {
        problem = "";
        BlindAccountRuntime? runtime;
        string? remembered;
        lock (_lock)
        {
            _runtimes.TryGetValue(id, out runtime);
            _problems.TryGetValue(id, out remembered);
        }
        if (runtime is null || runtime.Closing)
        {
            problem = remembered ?? "its start did not complete.";
            return false;
        }
        try
        {
            if (runtime.Controller.GetStatus().StartError is { } error)
            {
                problem = error.ReplaceLineEndings(" ").Trim();
                return false;
            }
            if (!runtime.Services.GetRequiredService<BlindMobilePairing>().HasIdentity)
            {
                problem = "its identity was not made.";
                return false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            problem = FailureWords(ex);
            return false;
        }
        return true;
    }

    /// <summary>The identity comes with a one-time pairing secret for the old way; an account added from an invitation is not paired that way, so it is spent. Best effort: nobody has seen it.</summary>
    private void SpendPairingSecret(string id)
    {
        try { Runtime(id).Services.GetRequiredService<IBlindSecretStore>().ClearPairingSecret(); }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException) { }
    }

    // ---- asking the hub ---------------------------------------------------------------------------------------------------------------------

    public async Task<int> PollInvitationsAsync(bool force = false, CancellationToken ct = default)
    {
        var waiting = _registry.Current.Entries.Where(e => e.Phase is BlindAccountPhases.Pending or BlindAccountPhases.AwaitingConfirmation).Select(e => e.Id).ToList();
        if (waiting.Count == 0 || !_started || Blocked() is not null) return waiting.Count;

        // Every ask of the hub is an operation of the host: a quiesce and a disposal wait for the asks in flight, and a host that is closing asks nothing.
        using var operation = TryBeginOperation();
        if (operation is null) return waiting.Count;

        var stillWaiting = 0;
        foreach (var id in waiting)
        {
            ct.ThrowIfCancellationRequested();
            var step = await AdvanceAsync(id, force, ct).ConfigureAwait(false);
            switch (step.Kind)
            {
                case InviteStepKind.Waiting:
                case InviteStepKind.Retrying:
                    stillWaiting++;
                    break;
                case InviteStepKind.Failed:
                    // What cannot go on (the hub does not know the device, an old hub, a hub that is not the one named) is taken away again: nothing is left half made.
                    await CleanupAsync(id).ConfigureAwait(false);
                    break;
            }
        }
        return stillWaiting;
    }

    /// <summary>
    /// One step for one account, one at a time for that account (the screen's watch and the scheduler may ask together), under the account's work token and the host's lifetime: an account
    /// that is being removed, or a host that is closing, ends the ask in flight and the step says <see cref="InviteStepKind.Gone"/>.
    /// </summary>
    private async Task<InviteStep> AdvanceAsync(string id, bool force, CancellationToken ct, bool ownAdd = false)
    {
        if (AccountIsGoing(id)) return InviteStep.Gone;
        var work = WorkOf(id);
        // An account that its add is still setting up is not asked about by anyone else (see AccountWork.IsSetUp).
        if (!ownAdd && !work.IsSetUp) return InviteStep.Waiting;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, work.Token, _lifetime.Token);
        var gate = AskGateOf(id);
        try { await gate.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return InviteStep.Gone; }
        try { return await AdvanceCoreAsync(id, force, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return InviteStep.Gone; }
        finally { gate.Release(); }
    }

    private async Task<InviteStep> AdvanceCoreAsync(string id, bool force, CancellationToken ct)
    {
        BlindAccountRuntime runtime;
        try { runtime = Runtime(id); }
        catch (KeyNotFoundException) { return InviteStep.Gone; }
        if (_registry.Current.Find(id) is not { Phase: BlindAccountPhases.Pending or BlindAccountPhases.AwaitingConfirmation } entry) return InviteStep.Done;

        var now = _options.Time.GetUtcNow();
        var services = runtime.Services;
        var phone = services.GetRequiredService<BlindPhoneState>();
        var context = new BlindInvitationState(services.GetRequiredService<IBlindPhoneStore>());

        // The owner's confirmation was applied up to the call code and the entry has not caught up (a crash between): finish it, no network needed.
        if (phone.CallCode is not null)
        {
            Activate(entry, runtime, context, target: null);
            return InviteStep.Done;
        }

        var target = context.Target;
        if (target is null)
        {
            // Nothing kept: a crash between the list and the state (the account was never completed) - taken away. Something kept that does not read back (damaged, or changed: the
            // store is not ours alone) is another thing: no call is made on it, and the account says so and waits for the person to remove it. It is never "started again from nothing".
            if (!context.Exists) return new InviteStep(InviteStepKind.Failed, "This account was never completed.");
            DropRedemption(id);
            ForgetRhythm(id);
            bool known;
            lock (_lock) known = _problems.ContainsKey(id);
            if (!known)
            {
                Remember(id, NeedsRepair + "the invitation this account was made from cannot be read any more (it was damaged or changed). Nothing was sent. Remove this account and ask for a new invitation.");
                RaiseChanged();
            }
            return InviteStep.Done;
        }

        PendingRedemption? redemption;
        lock (_lock) _redemptions.TryGetValue(id, out redemption);
        var redeeming = redemption is not null && entry.Phase == BlindAccountPhases.Pending;
        var expired = now >= target.ExpiresAt;

        lock (_lock)
        {
            var rhythm = RhythmOf(id);
            // What the hub asked for (or the back-off after a failure) is kept even for an ask the person makes: a screen cannot be made to storm a hub. But a hold never postpones what the
            // invitation running out means: the secret is let go and the account judged when the invitation ends, not when a hold does.
            if (rhythm.HoldUntil is { } hold && now < hold && !(redeeming && expired)) return new InviteStep(InviteStepKind.Retrying);
            if (!force && rhythm.DueAt is { } due && now < due) return InviteStep.Waiting;
        }

        var client = new BlindInvitationClient(
            services.GetRequiredService<BlindHttpClientProvider>(), services.GetRequiredService<IBlindSecretStore>(), phone, _options.Time, services.GetService<ISyncProtocolPolicy>());

        // Where a held back-off stops: a redemption in progress ends with the invitation; the owner's decision (or the question whether an earlier try landed) with the window the hub gives.
        DateTimeOffset RedeemCap() => target.ExpiresAt;
        DateTimeOffset StatusCap() => entry.Phase == BlindAccountPhases.AwaitingConfirmation && context.ConfirmByUnix is { } by ? DateTimeOffset.FromUnixTimeSeconds(by) : target.ExpiresAt + ConfirmWindow;

        if (redeeming)
        {
            if (expired)
            {
                // The invitation ran out before the redemption was delivered: it cannot be redeemed any more. If a try of this run may have landed (or this run knows nothing of the tries before it),
                // the hub is asked what became of it; if every try was refused before the hub could have taken anything, there is nothing to ask and the account ends here.
                DropRedemption(id);
                bool nothingDelivered;
                lock (_lock) nothingDelivered = RhythmOf(id).KnownNotDelivered;
                if (nothingDelivered) return new InviteStep(InviteStepKind.Failed, BlindInvitationWords.NotValid(target.SourceName));
            }
            else
            {
                lock (_lock) RhythmOf(id).Attempted = true;
                ct.ThrowIfCancellationRequested();   // an account that is being removed, or a host that is closing, starts no new request
                // Before any secret is sent: the hub at this address must be the one the invitation names (node id AND key), through the client its trust selects. (The signature of the invitation,
                // not this look, binds the address to the key; this is a sanity check that the computer answering is the one that was meant.)
                var probe = await client.ProbeAsync(target, ct).ConfigureAwait(false);
                if (!probe.Ok) return AfterFailure(id, target, probe.Outcome, probe.RetryAfter, probe.Pause, awaiting: false, RedeemCap());
                _crash.Reach("invite:probed");
                ct.ThrowIfCancellationRequested();   // the secret is not sent after the work was cancelled, whatever the transport does with a cancelled token

                var secret = redemption!.Secret;
                if (secret is null) return AfterFailure(id, target, BlindInvitationOutcome.KeysUnavailable, null, null, awaiting: false, RedeemCap());
                var redeemed = await client.RedeemAsync(target, secret, ct).ConfigureAwait(false);
                NoteRedeemTry(id, redeemed.Outcome);
                _crash.Reach("invite:redeem-answered");
                if (redeemed.Ok) return AcceptRedemption(entry, runtime, context, target, redeemed.Value!);
                // A 410 may be the answer to a retry of a redemption that did land (and was decided since): the status says which.
                if (redeemed.Outcome != BlindInvitationOutcome.NotValid)
                    return AfterFailure(id, target, redeemed.Outcome, redeemed.RetryAfter, redeemed.Pause, awaiting: false, RedeemCap());
            }
        }

        // An account that never got an answer, long after the invitation and any decision the owner could still make are over, is not waited for any longer.
        if (entry.Phase == BlindAccountPhases.Pending && now >= target.ExpiresAt + ConfirmWindow)
            return new InviteStep(InviteStepKind.Failed, BlindInvitationWords.NotValid(target.SourceName));

        // No secret to redeem with (the app restarted, or the invitation ran out), or the owner's decision is what is asked: the signed status request.
        var awaiting = entry.Phase == BlindAccountPhases.AwaitingConfirmation;
        ct.ThrowIfCancellationRequested();
        var status = await client.StatusAsync(target, ct).ConfigureAwait(false);
        if (!status.Ok)
        {
            // A 410 is "this invitation is not valid" ONLY when the clocks agree: the proof of a status request carries this device's time, and the hub takes it within minutes of its own. A device whose
            // clock is off gets the same 410 for an invitation that is perfectly alive, and must not conclude "expired" or "refused" from it.
            if (status.Outcome == BlindInvitationOutcome.NotValid && ClockDisagrees(status.HubDate, out var hubMinusDevice))
                return ClockOff(id, target, hubMinusDevice);
            return AfterFailure(id, target, status.Outcome, status.RetryAfter, status.Pause, awaiting, StatusCap());
        }

        switch (status.Value)
        {
            case BlindInviteStates.Awaiting:
                // A redemption that was delivered before the app stopped (or whose answer was lost): the account goes on as if the 202 had come.
                if (entry.Phase == BlindAccountPhases.Pending) return AcceptRedemption(entry, runtime, context, target, answer: null);
                SetWaiting(id, note: null, BackgroundAsk);
                return InviteStep.Waiting;
            case BlindInviteStates.Confirmed:
                Activate(entry, runtime, context, target);
                return InviteStep.Done;
            case BlindInviteStates.Refused:
                Conclude(entry, runtime, context, BlindAccountPhases.Refused, "The owner refused this device.");
                return InviteStep.Done;
            default:
                Conclude(entry, runtime, context, BlindAccountPhases.Expired, "The invitation expired before the owner confirmed.");
                return InviteStep.Done;
        }
    }

    /// <summary>A try of the redemption came to this: whether the hub may have taken it (so that a status request after the invitation ran out still makes sense).</summary>
    private void NoteRedeemTry(string id, BlindInvitationOutcome outcome)
    {
        if (outcome is BlindInvitationOutcome.Ok or BlindInvitationOutcome.Unreachable or BlindInvitationOutcome.NotUnderstood or BlindInvitationOutcome.NotValid)
            lock (_lock) RhythmOf(id).MaybeDelivered = true;
    }

    /// <summary>True when the hub's own clock (the <c>Date</c> of its answer) and this device's differ by more than <see cref="ClockTolerance"/>. A hub that sent no date cannot be compared: false.</summary>
    private bool ClockDisagrees(DateTimeOffset? hubDate, out TimeSpan hubMinusDevice)
    {
        hubMinusDevice = TimeSpan.Zero;
        if (hubDate is not { } hub) return false;
        hubMinusDevice = hub - _options.Time.GetUtcNow();
        return hubMinusDevice.Duration() > ClockTolerance;
    }

    /// <summary>
    /// The hub said "not valid" to a question this device cannot trust its own time for: the card says that the clock is off and by how much - not that the owner refused or that the invitation
    /// expired - and the question is asked again at a low rate, so that correcting the clock is noticed. Not final: the account stays as it is.
    /// </summary>
    private InviteStep ClockOff(string id, BlindInvitationTarget target, TimeSpan hubMinusDevice)
    {
        var now = _options.Time.GetUtcNow();
        lock (_lock)
        {
            var rhythm = RhythmOf(id);
            rhythm.Failures = 0;
            rhythm.HoldUntil = now + ClockRecheck;
            rhythm.DueAt = null;
            rhythm.Note = BlindInvitationWords.ClockOff(hubMinusDevice, target.Host);
        }
        RaiseChanged();
        return new InviteStep(InviteStepKind.Retrying);
    }

    /// <summary>The hub took the redemption (a 202, or a status that shows it did earlier): the account waits for the owner, with the code to read to him, and the secret is let go.</summary>
    private InviteStep AcceptRedemption(BlindAccountEntry entry, BlindAccountRuntime runtime, BlindInvitationState context, BlindInvitationTarget target, BlindInviteRedeemAnswer? answer)
    {
        var phone = runtime.Services.GetRequiredService<BlindPhoneState>();
        if (phone.NodeId is not { } nodeId || phone.PublicKey is not { } publicKey)
            return new InviteStep(InviteStepKind.Failed, BlindInvitationWords.KeysMissing);

        // Computed here from this device's own identity: what the owner's page shows for this redemption must be this. A hub that answers another code did not see what this device sent.
        var code = target.MatchCodeFor(nodeId, publicKey);
        if (answer is not null && BlindInviteMatchCode.Normalize(answer.MatchCode) != code)
            return new InviteStep(InviteStepKind.Failed, BlindInvitationWords.NotUnderstood(target.Host));

        // A deadline the hub could not have given is an answer this app does not understand: it is not written (a value that would not read back as a target would make the account unreadable).
        if (answer is not null && !BlindInvitationState.ConfirmByIsSane(target.ExpiresAtUnix, answer.ConfirmByUnix))
            return new InviteStep(InviteStepKind.Failed, BlindInvitationWords.NotUnderstood(target.Host));
        if (answer is not null) context.SetConfirmBy(answer.ConfirmByUnix);
        if (!SetPhase(entry.Id, BlindAccountPhases.AwaitingConfirmation, code))
            return new InviteStep(InviteStepKind.Retrying);   // not recorded: the secret is kept and the redemption is repeated (the same answer comes)
        _crash.Reach("invite:awaiting");

        DropRedemption(entry.Id);
        SetWaiting(entry.Id, note: null, BackgroundAsk);
        TryLog(runtime, $"The invitation was accepted by {target.Host}. Waiting for the owner to confirm this device (code {code}).");
        RaiseChanged();
        return InviteStep.Waiting;
    }

    /// <summary>
    /// The owner confirmed. The account keeps a <see cref="BlindCallCode"/> built from the invitation - authenticated under a fresh secret of its own that is spent at once - so that everything after it
    /// (the call code in the state, the pinned or public-CA client, the pull client, the first load, backups) is the path the old pairing already uses, unchanged. The call code is stored first and the
    /// phase second: a crash between leaves a call code under a phase that is not active yet, which the next ask finishes without any network.
    /// </summary>
    private void Activate(BlindAccountEntry entry, BlindAccountRuntime runtime, BlindInvitationState context, BlindInvitationTarget? target)
    {
        var phone = runtime.Services.GetRequiredService<BlindPhoneState>();
        if (phone.CallCode is null && target is not null)
        {
            var secret = RandomNumberGenerator.GetBytes(BlindPairingSecret.Size);
            try { phone.CallCode = target.CallCode(secret); }
            finally { CryptographicOperations.ZeroMemory(secret); }
            _crash.Reach("invite:call-code-saved");
        }
        SpendPairingSecret(entry.Id);

        if (!SetPhase(entry.Id, BlindAccountPhases.Active, null)) return;   // not recorded: asked again (the call code is there, so no network is needed)
        _crash.Reach("invite:activated");
        try { context.Clear(); }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException) { /* what is left is not secret and goes with the account */ }

        DropRedemption(entry.Id);
        ForgetRhythm(entry.Id);
        TryLog(runtime, "The owner confirmed this device. The first load starts now.");
        RaiseChanged();
    }

    /// <summary>The owner said no, or the time ran out: the account says so and keeps nothing of the invitation.</summary>
    private void Conclude(BlindAccountEntry entry, BlindAccountRuntime runtime, BlindInvitationState context, string phase, string logLine)
    {
        if (!SetPhase(entry.Id, phase, null)) return;
        try { context.Clear(); }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException) { }
        DropRedemption(entry.Id);
        ForgetRhythm(entry.Id);
        TryLog(runtime, logLine);
        RaiseChanged();
    }

    /// <summary>What a call that did not come to an answer means for the account: try again later (with a back-off), or end it.</summary>
    private InviteStep AfterFailure(string id, BlindInvitationTarget target, BlindInvitationOutcome outcome, TimeSpan? retryAfter, BlindPause? pause, bool awaiting, DateTimeOffset holdCap)
    {
        var host = target.Host;
        var now = _options.Time.GetUtcNow();
        switch (outcome)
        {
            case BlindInvitationOutcome.Unreachable:
                return Retry(id, BlindInvitationWords.Unreachable(host), null, awaiting, holdCap);
            case BlindInvitationOutcome.Locked:
                return Retry(id, BlindInvitationWords.Locked, retryAfter, awaiting, holdCap);
            case BlindInvitationOutcome.RateLimited:
                return Retry(id, BlindInvitationWords.AskedToWait(host), retryAfter ?? TimeSpan.FromMinutes(1), awaiting, holdCap);
            case BlindInvitationOutcome.Paused when pause is not null:
                // Nothing went out (the meter said so before the request), so looking again is free: the usual back-off, and a change of the limits is noticed within it.
                return Retry(id, BlindPauseText.Job(pause, _options.Time.LocalTimeZone, now), null, awaiting, holdCap);
            case BlindInvitationOutcome.CertificateRefused when awaiting:
                // The redemption is delivered; only the owner's answer is missing. A hub whose certificate changed may come back right: asked again at the usual rhythm.
                return Retry(id, BlindInvitationWords.CertificateRefused(host), BackgroundAsk, awaiting, holdCap);
            case BlindInvitationOutcome.CertificateRefused:
                return new InviteStep(InviteStepKind.Failed, BlindInvitationWords.CertificateRefused(host));
            case BlindInvitationOutcome.HubMismatch:
                return new InviteStep(InviteStepKind.Failed, BlindInvitationWords.HubMismatch(host));
            case BlindInvitationOutcome.OlderHub:
                return new InviteStep(InviteStepKind.Failed, BlindInvitationWords.OlderHub);
            case BlindInvitationOutcome.KnownUnderOtherKey:
                return new InviteStep(InviteStepKind.Failed, BlindInvitationWords.KnownUnderOtherKey(host));
            case BlindInvitationOutcome.KeysUnavailable:
                return new InviteStep(InviteStepKind.Failed, BlindInvitationWords.KeysMissing);
            case BlindInvitationOutcome.NotValid when awaiting:
                // The hub has no such redemption any more: the decision cannot come. The account says so (and offers Remove).
                SetPhase(id, BlindAccountPhases.Expired, null);
                DropRedemption(id);
                ForgetRhythm(id);
                RaiseChanged();
                return InviteStep.Done;
            case BlindInvitationOutcome.NotValid:
                return new InviteStep(InviteStepKind.Failed, BlindInvitationWords.NotValid(target.SourceName));
            default:
                return new InviteStep(InviteStepKind.Failed, BlindInvitationWords.NotUnderstood(host));
        }
    }

    /// <summary>
    /// Tries again later: after what the hub asked for (at most 15 minutes), or a back-off that grows (15 s, 30 s, 1 min, 2 min, then 5 min) - and never later than <paramref name="holdCap"/>
    /// (the invitation's end for a redemption in progress), so that what the invitation running out means is not put off by a hold that outlives it.
    /// </summary>
    private InviteStep Retry(string id, string note, TimeSpan? wait, bool awaiting, DateTimeOffset holdCap)
    {
        var now = _options.Time.GetUtcNow();
        lock (_lock)
        {
            var rhythm = RhythmOf(id);
            rhythm.Failures++;
            var delay = wait is { } asked ? asked : rhythm.Failures switch { <= 1 => TimeSpan.FromSeconds(15), 2 => TimeSpan.FromSeconds(30), 3 => TimeSpan.FromMinutes(1), 4 => TimeSpan.FromMinutes(2), _ => TimeSpan.FromMinutes(5) };
            delay = delay < MinWait ? MinWait : delay > MaxWait ? MaxWait : delay;
            rhythm.HoldUntil = now + delay;
            // A cap that has already passed is not a hold at all: the invitation's end is judged on the very next ask (it is the first thing that ask looks at).
            if (holdCap > now && rhythm.HoldUntil > holdCap) rhythm.HoldUntil = holdCap;
            rhythm.DueAt = null;
            rhythm.Note = awaiting ? null : note;
        }
        RaiseChanged();
        return new InviteStep(InviteStepKind.Retrying);
    }

    private void SetWaiting(string id, string? note, TimeSpan every)
    {
        var now = _options.Time.GetUtcNow();
        lock (_lock)
        {
            var rhythm = RhythmOf(id);
            rhythm.Failures = 0;
            rhythm.HoldUntil = null;
            rhythm.DueAt = every > TimeSpan.Zero ? now + every : null;
            rhythm.Note = note;
        }
    }

    private InviteRhythm RhythmOf(string id)
    {
        lock (_lock)
        {
            if (!_rhythm.TryGetValue(id, out var rhythm)) _rhythm[id] = rhythm = new InviteRhythm();
            return rhythm;
        }
    }

    private void ForgetRhythm(string id)
    {
        lock (_lock) _rhythm.Remove(id);
    }

    private void DropRedemption(string id)
    {
        PendingRedemption? held;
        lock (_lock)
        {
            if (!_redemptions.Remove(id, out held)) return;
        }
        held.Dispose();
    }

    /// <summary>What a pending account's card says while it waits to try again (null when it says nothing special).</summary>
    private string? InviteNoteOf(string id)
    {
        lock (_lock) return _rhythm.TryGetValue(id, out var rhythm) ? rhythm.Note : null;
    }

    /// <summary>Writes the account's phase (and its match code) in one atomic write of the list. False when it could not be written (a damaged list, another copy holding the folder).</summary>
    private bool SetPhase(string id, string phase, string? matchCode)
    {
        try
        {
            _registry.Update(file => file.Find(id) is { Phase: not BlindAccountPhases.Deleting } entry
                ? file with { Accounts = file.Entries.Select(e => e.Id == id ? entry with { Phase = phase, MatchCode = matchCode } : e).ToList() }
                : file);
            return true;
        }
        catch (Exception ex) when (ex is BlindRegistryDamagedException or BlindRegistryBusyException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes an account that cannot go on (nothing was left half made): the pairing path's own removal, which stops its work, clears its keys and state and deletes its folder, and lists it
    /// no more. A removal that is already running does the work and is not joined: the add that calls this (<paramref name="ownAdd"/>) is what that removal may be waiting for.
    /// </summary>
    private async Task CleanupAsync(string id, bool ownAdd = false)
    {
        DropRedemption(id);
        ForgetRhythm(id);
        try
        {
            if (StartRemoval(id, joinRunning: false, ownAdd, CancellationToken.None) is { } removal) await removal.ConfigureAwait(false);
        }
        catch (KeyNotFoundException) { }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Remember(id, "It could not be removed: " + FailureWords(ex));
        }
    }

    private SemaphoreSlim AskGateOf(string id)
    {
        lock (_lock)
        {
            if (!_askGates.TryGetValue(id, out var gate)) _askGates[id] = gate = new SemaphoreSlim(1, 1);
            return gate;
        }
    }

    /// <summary>The work of an account that is being added: always a new one, so that a cancelled one of an earlier life is never reused.</summary>
    private AccountWork BeginAddWork(string id)
    {
        lock (_lock)
        {
            var work = AccountWork.ForAdd();
            _work[id] = work;
            return work;
        }
    }

    private AccountWork WorkOf(string id)
    {
        lock (_lock)
        {
            if (!_work.TryGetValue(id, out var work)) _work[id] = work = AccountWork.Idle();
            return work;
        }
    }

    /// <summary>A removal that did not happen (its drain failed) leaves the account as it was: its invitation work may go on, under a token that is not cancelled.</summary>
    private void ReopenWork(string id)
    {
        lock (_lock)
        {
            if (_work.TryGetValue(id, out var work) && work.IsCancelled) _work[id] = work.Reopened();
        }
    }

    private void ForgetWork(string id)
    {
        lock (_lock)
        {
            _work.Remove(id);
            _askGates.Remove(id);
        }
    }

    /// <summary>
    /// Ends everything an account asked of the hub and lets go of what it holds, BEFORE anything of the account is cleared: the running ask is cancelled (a redemption in flight, a status, a
    /// back-off that is pending), an add that is still setting the account up is waited for (unless it is the caller's own cleanup), and an ask of the scheduler or the screen that was under way
    /// has ended - so that no later step writes a state, a folder or a key for an account that is going. Then the secret is wiped.
    /// </summary>
    private async Task StopInvitationWorkAsync(string id, bool ownAdd, CancellationToken ct)
    {
        AccountWork? work;
        lock (_lock) _work.TryGetValue(id, out work);
        work?.Cancel();
        if (work is not null && !ownAdd) await work.AddEnded.WaitAsync(ct).ConfigureAwait(false);
        var gate = AskGateOf(id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        gate.Release();
        DropRedemption(id);
        ForgetRhythm(id);
    }

    private static void TryLog(BlindAccountRuntime runtime, string line)
    {
        try { runtime.Services.GetRequiredService<BlindPhoneLog>().Add("invite", line); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or InvalidOperationException) { }
    }

    // ---- the source removed the device; the person tries again ------------------------------------------------------------------------------

    /// <summary>The source refused the device a second time (<see cref="BlindSourceRemoval.Judge"/>): the card says so and the scheduler leaves the account alone.</summary>
    private void MarkRemovedByOwner(string id)
    {
        try
        {
            _registry.Update(file => file.Find(id) is { Phase: BlindAccountPhases.Active } entry
                ? file with { Accounts = file.Entries.Select(e => e.Id == id ? entry with { Phase = BlindAccountPhases.RemovedByOwner } : e).ToList() }
                : file);
        }
        catch (Exception ex) when (ex is BlindRegistryDamagedException or BlindRegistryBusyException or IOException or UnauthorizedAccessException) { return; }
        RaiseChanged();
    }

    public string? Reconnect(string accountId)
    {
        if (_registry.Current.Find(accountId) is not { } entry) return "There is no such account on this device (any more).";
        if (entry.Phase != BlindAccountPhases.RemovedByOwner) return "This account was not removed by its owner.";
        // Before the account counts refusals again: it starts from nothing (a refusal that was remembered for it is not one of the new try).
        ForgetRefusal(entry);
        if (!SetPhase(accountId, BlindAccountPhases.Active, null)) return "The list of accounts could not be written. Try again in a moment.";
        RaiseChanged();
        return null;
    }

    /// <summary>A computer's answer was accepted by an account the source had removed: it is paired again, so it is active again.</summary>
    private void ReactivateAfterPairing(string id)
    {
        if (_registry.Current.Find(id) is not { Phase: BlindAccountPhases.RemovedByOwner } entry) return;
        ForgetRefusal(entry);
        if (SetPhase(id, BlindAccountPhases.Active, null)) RaiseChanged();
    }

    /// <summary>
    /// Forgets the first refusal the account remembers, in its own state. Called while the account is not counting (before it is made active again), so that no refusal of the old try is
    /// counted with the first of the new one. Best effort: a state that cannot be written is left as it is, and the account is made active all the same.
    /// </summary>
    private void ForgetRefusal(BlindAccountEntry entry)
    {
        try
        {
            var state = new BlindPhoneState(_options.States.For(entry));
            if (state.SourceRefusedAt is not null) state.SourceRefusedAt = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    /// <summary>After "Disconnect and wipe" of one account: nothing of an invitation is left (its keys, state and folder are gone) and the account starts again as one paired the old way.</summary>
    private void ForgetInvitation(string id)
    {
        DropRedemption(id);
        ForgetRhythm(id);
        if (_registry.Current.Find(id) is { Phase: not (BlindAccountPhases.Active or BlindAccountPhases.Deleting) })
            SetPhase(id, BlindAccountPhases.Active, null);
    }

    /// <summary>The host ends: no invitation secret outlives it.</summary>
    private void ZeroInvitationSecrets()
    {
        List<PendingRedemption> held;
        lock (_lock)
        {
            held = [.. _redemptions.Values];
            _redemptions.Clear();
        }
        foreach (var redemption in held) redemption.Dispose();
    }

    /// <summary>
    /// Registered per account: the controller's way of telling the host what the source answered. The first refusal is only written down (in the account's own state, with the time); the
    /// second, on a later try within a day, marks the account removed. A state that cannot be read or written never removes an account: the refusal then counts as a first one.
    /// </summary>
    private sealed class SourceRemovalSink(BlindAccountHost host, string accountId, BlindPhoneState state, TimeProvider time) : IBlindSourceRemovalSink
    {
        // A sync round and a long job of one account may be refused together: reading the remembered time and writing the next one is one step.
        private readonly object _gate = new();

        public BlindRefusalOutcome SourceRefusedThisDevice()
        {
            // Only an account that is going on counts refusals. One the source has already removed has nothing left to count: a refusal that reaches it now (the long job of a pass whose sync round
            // removed the account, the round that follows a long job that did) must not leave a "first" refusal behind, or Try again and a new pairing would start from it. The sentence it
            // gets is the true one; one that is going away or not an active account says nothing more than a first refusal.
            var phase = host._registry.Current.Find(accountId)?.Phase;
            if (phase != BlindAccountPhases.Active)
                return phase == BlindAccountPhases.RemovedByOwner ? BlindRefusalOutcome.Removed : BlindRefusalOutcome.FirstNoted;

            BlindRefusalOutcome outcome;
            lock (_gate)
            {
                try
                {
                    outcome = BlindSourceRemoval.Judge(state.SourceRefusedAt, time.GetUtcNow(), out var remember);
                    if (remember != state.SourceRefusedAt) state.SourceRefusedAt = remember;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    return BlindRefusalOutcome.FirstNoted;
                }
            }
            if (outcome == BlindRefusalOutcome.Removed) host.MarkRemovedByOwner(accountId);
            return outcome;
        }

        public void SourceAnsweredThisDevice()
        {
            lock (_gate)
            {
                try { if (state.SourceRefusedAt is not null) state.SourceRefusedAt = null; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
            }
        }
    }
}
