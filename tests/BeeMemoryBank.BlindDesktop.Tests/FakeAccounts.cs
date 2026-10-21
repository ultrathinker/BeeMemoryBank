using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>
/// The accounts of a device, scripted: each account is an entry with a <see cref="FakeApp"/> behind it, and every call a window makes on <see cref="IBlindAccountsController"/> is recorded
/// and answered as the test says (a refusal sentence, a result, an exception). It computes no sentence of its own: the window's text comes from the view records over its snapshot.
/// </summary>
internal sealed class FakeAccounts : IBlindAccountsController
{
    private readonly object _gate = new();
    private int _next;

    public event Action? Changed;

    public List<BlindAccountEntry> Entries { get; } = [];
    public Dictionary<string, FakeApp> Apps { get; } = [];
    public Dictionary<string, BlindAccountUsageView> Usage { get; } = [];
    public Dictionary<string, BlindPause> Pauses { get; } = [];
    public Dictionary<string, string> Notes { get; } = [];
    public Dictionary<string, string> Problems { get; } = [];
    public List<string> Calls { get; } = [];
    public BlindDeviceLimits Device { get; set; } = BlindDeviceLimits.None;
    public string? RegistryProblem { get; set; }
    public List<BlindRepairIssue> Repairs { get; } = [];
    public string? DeviceUsageProblem { get; set; }
    public int Max { get; set; } = 8;

    /// <summary>What a call throws instead of answering: the key is the call's name ("Preview", "AddAsync", "AddFromInvitation", "Snapshot", or any call that goes through <c>Answer</c>).</summary>
    public Dictionary<string, Exception> Throws { get; } = [];

    /// <summary>What a call answers instead of success: the key is the call's name ("Rename", "SetLimits", ...).</summary>
    public Dictionary<string, string> Refusals { get; } = [];

    public Dictionary<string, BlindInvitationPreview> Previews { get; } = [];
    public Func<string?, BlindAccountSettings?, Task<BlindAddResult>>? OnAddFromInvitation { get; set; }
    public Func<string, Task>? OnRemove { get; set; }
    public int Polls { get; private set; }
    public int MaxAccounts => Max;

    public static BlindAccountEntry NewEntry(string name, string? id = null, string phase = BlindAccountPhases.Active, int order = 0, string? sourceOrigin = "https://hub.example.org:5610") =>
        new(id ?? BlindAccountEntry.NewId(), BlindAccountLayouts.Dir, name, phase, SourceOrigin: sourceOrigin, Order: order, Established: true);

    /// <summary>Adds an account that is listed and up (its app answers).</summary>
    public (BlindAccountEntry Entry, FakeApp App) AddAccount(string name, string? id = null, string phase = BlindAccountPhases.Active, BlindAppStatus? status = null)
    {
        lock (_gate)
        {
            var entry = NewEntry(name, id, phase, _next++);
            var app = new FakeApp { Status = status ?? FakeApp.StatusOf(name: name) };
            Entries.Add(entry);
            Apps[entry.Id] = app;
            return (entry, app);
        }
    }

    public void RaiseChanged() => Changed?.Invoke();

    public IReadOnlyList<string> CallsSoFar
    {
        get { lock (_gate) return [.. Calls]; }
    }

    private void Record(string what)
    {
        lock (_gate) Calls.Add(what);
    }

    private string? Answer(string call)
    {
        Record(call);
        var name = call.Contains(':') ? call[..call.IndexOf(':')] : call;
        if (Throws.TryGetValue(name, out var failure)) throw failure;
        return Refusals.GetValueOrDefault(name);
    }

    private void Replace(string id, Func<BlindAccountEntry, BlindAccountEntry> change)
    {
        lock (_gate)
        {
            var at = Entries.FindIndex(e => e.Id == id);
            if (at >= 0) Entries[at] = change(Entries[at]);
        }
    }

    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

    public BlindAccountsSnapshot GetSnapshot()
    {
        if (Throws.TryGetValue("Snapshot", out var failure)) throw failure;
        lock (_gate)
        {
            var cards = Entries.Select(e => new BlindAccountSnapshot(
                e, Apps.TryGetValue(e.Id, out var app) ? app.GetStatus() : null, Problems.GetValueOrDefault(e.Id), Usage.GetValueOrDefault(e.Id),
                Pauses.GetValueOrDefault(e.Id), Notes.GetValueOrDefault(e.Id))).ToList();
            return new BlindAccountsSnapshot(cards, Device, RegistryProblem, RegistryProblem is null && Entries.Count < Max, Max,
                DeviceUsageProblem is null ? null : new BlindDeviceUsageView(0, 0, 0, Entries.Count, DeviceUsageProblem), [.. Repairs]);
        }
    }

    public IBlindAppController For(string accountId) =>
        Apps.TryGetValue(accountId, out var app) ? app : throw new KeyNotFoundException("There is no such account on this device (any more).");

    public IServiceProvider ServicesFor(string accountId) => throw new NotSupportedException();

    public BlindPause? PauseOf(string accountId) => Pauses.GetValueOrDefault(accountId);

    public Task<BlindAddResult> AddAsync(string? name, CancellationToken ct = default)
    {
        Record("AddAsync:" + (name ?? "<null>"));
        if (Throws.TryGetValue("AddAsync", out var failure)) return Task.FromException<BlindAddResult>(failure);
        if (Refusals.TryGetValue("AddAsync", out var refusal)) return Task.FromResult(new BlindAddResult(null, refusal));
        var (entry, app) = AddAccount(name ?? "Account " + (Entries.Count + 1), status: FakeApp.StatusOf(paired: false, loaded: false, awaiting: true));
        app.PairingCodeValue = "bmb-blind-phone:?n=" + entry.Id[..4];
        return Task.FromResult(new BlindAddResult(entry.Id));
    }

    public BlindInvitationPreview PreviewInvitation(string? text)
    {
        Record("Preview:" + text);
        if (Throws.TryGetValue("Preview", out var failure)) throw failure;
        if (text is not null && Previews.TryGetValue(text, out var preview)) return preview;
        return new BlindInvitationPreview("", "", BlindAccountDefaults.ForInvited(false), null,
            "This is not an invitation.", BlindInvitationRefusals.Malformed);
    }

    public Task<BlindAddResult> AddFromInvitationAsync(string? text, BlindAccountSettings? settings = null, CancellationToken ct = default)
    {
        Record("AddFromInvitation:" + text);
        if (Throws.TryGetValue("AddFromInvitation", out var failure)) return Task.FromException<BlindAddResult>(failure);
        if (OnAddFromInvitation is { } handler) return handler(text, settings);
        var (entry, _) = AddAccount("Invited", phase: BlindAccountPhases.AwaitingConfirmation);
        return Task.FromResult(new BlindAddResult(entry.Id));
    }

    /// <summary>When set, an ask about waiting invitations is answered by this (a test holds it open); <see cref="Polls"/> counts the asks either way.</summary>
    public Func<int, CancellationToken, Task<int>>? OnPoll { get; set; }

    public Task<int> PollInvitationsAsync(bool force = false, CancellationToken ct = default)
    {
        var number = ++Polls;
        if (OnPoll is { } handler) return handler(number, ct);
        return Task.FromResult(Entries.Count(e => e.Phase is BlindAccountPhases.Pending or BlindAccountPhases.AwaitingConfirmation));
    }

    public string? Reconnect(string accountId)
    {
        var refusal = Answer("Reconnect");
        if (refusal is null) Replace(accountId, e => e with { Phase = BlindAccountPhases.Active });
        return refusal;
    }

    public string? AcceptCallCode(string text) => Answer("AcceptCallCode");

    public string? Rename(string accountId, string name)
    {
        var refusal = Answer("Rename:" + name);
        if (refusal is null) Replace(accountId, e => e with { Name = name.Trim() });
        return refusal;
    }

    public string? SetLimits(string accountId, BlindAccountLimits limits)
    {
        var refusal = Answer("SetLimits:" + limits.MaxStoredBytes + "/" + limits.MaxTrafficBytesPerDay + "/" + limits.MaxRequestsPerDay);
        if (refusal is null) Replace(accountId, e => e with { Limits = limits });
        return refusal;
    }

    public string? SetSwitches(string accountId, bool unmeteredOnly, bool pauseOnPowerSaving)
    {
        var refusal = Answer($"SetSwitches:{unmeteredOnly}/{pauseOnPowerSaving}");
        if (refusal is null) Replace(accountId, e => e with { UnmeteredOnly = unmeteredOnly, PauseOnPowerSaving = pauseOnPowerSaving });
        return refusal;
    }

    public string? SetPaused(string accountId, bool paused)
    {
        var refusal = Answer("SetPaused:" + paused);
        if (refusal is null) Replace(accountId, e => e with { PausedByUser = paused });
        return refusal;
    }

    public string? SetDeviceLimits(BlindDeviceLimits limits)
    {
        var refusal = Answer("SetDeviceLimits:" + limits.MaxStoredBytes + "/" + limits.MaxTrafficBytesPerDay);
        if (refusal is null) Device = limits;
        return refusal;
    }

    public async Task RemoveAsync(string accountId, CancellationToken ct = default)
    {
        Record("Remove:" + accountId);
        if (OnRemove is { } handler) await handler(accountId);
        lock (_gate)
        {
            Entries.RemoveAll(e => e.Id == accountId);
            Apps.Remove(accountId);
        }
    }

    public string? StartNewUsageLedger(string accountId) => Answer("StartNewUsageLedger");

    public string? StartNewDeviceUsageLedger()
    {
        var refusal = Answer("StartNewDeviceUsageLedger");
        if (refusal is null) DeviceUsageProblem = null;
        return refusal;
    }

    public Task<string?> AdoptLegacyRootAsync(CancellationToken ct = default) => Task.FromResult(Answer("AdoptLegacyRoot"));

    public Task<string?> AdoptFolderAsync(string accountId, string? name = null, CancellationToken ct = default) => Task.FromResult(Answer("AdoptFolder:" + accountId));

    public Task<string?> ForgetMissingAccountAsync(string accountId, CancellationToken ct = default) => Task.FromResult(Answer("ForgetMissing:" + accountId));
}

/// <summary>Conditions a test sets by hand.</summary>
internal sealed class FakeConditions : IBlindDeviceConditions
{
    public bool? IsMetered { get; set; }
    public bool? IsPowerSaving { get; set; }
}
