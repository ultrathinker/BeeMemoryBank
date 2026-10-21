using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// "The process died after every step" of adding an account from an invitation and of the owner's confirmation. Each flow is run to the end once to list its steps, then again with a crash
/// after step 1, step 2 ... step N, each time over a fresh folder; the next start (a new host over what the dead run left, with no memory of the invitation's secret) must come up whole:
/// an account that is there is complete and consistent (waiting for the owner with the code that is on his page, or active with a call code), an account that is gone is gone entirely
/// (folder, keys, state, entry), and the hub never ends up with a device that the app has forgotten without a way to learn what became of it.
/// </summary>
public sealed class BlindInvitationCrashTests
{
    private static async Task DieAsync(BlindAccountHost host)
    {
        await host.DisposeAsync();
        SqliteConnection.ClearAllPools();
    }

    private static async Task<List<string>> StepsOfAddAsync()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        var recorder = new CrashAt();
        await using var host = rig.Host(crash: recorder);
        await host.StartAsync();
        var before = recorder.Steps.Count;
        (await host.AddFromInvitationAsync(hub.IssueText())).Ok.Should().BeTrue();
        return recorder.Steps.Skip(before).ToList();
    }

    [Fact]
    public async Task TheAdd_HasTheStepsItIsKnownBy()
    {
        var steps = await StepsOfAddAsync();

        steps.Should().ContainInOrder("invite:before-registry", "registry:renamed", "invite:registered", "invite:context-saved", "invite:started", "invite:probed",
            "invite:redeem-answered", "invite:awaiting");
        steps.Count.Should().BeGreaterThan(10);
    }

    /// <summary>Nothing of the account is left anywhere: no entry, no folder, no key, no state.</summary>
    private static void AssertGone(InviteRig rig, BlindAccountHost host, string because)
    {
        host.GetSnapshot().Accounts.Should().BeEmpty(because);
        var accounts = Path.Combine(rig.Accounts.Root, "accounts");
        if (Directory.Exists(accounts)) Directory.EnumerateFileSystemEntries(accounts).Should().BeEmpty(because);
    }

    [Fact]
    public async Task ACrashAfterEveryStepOfTheAdd_ResumesOrCleansUp_AtTheNextStart()
    {
        var steps = await StepsOfAddAsync();
        var resumed = 0;
        var cleaned = 0;

        for (var i = 0; i < steps.Count; i++)
        {
            using var rig = new InviteRig();
            var hub = rig.Hub();
            var invitation = hub.Issue("Alex");

            var crash = new ArmedCrashAtIndex(i);
            var dying = rig.Host(crash: crash);
            await dying.StartAsync();
            crash.Armed = true;
            var act = () => dying.AddFromInvitationAsync(invitation.ToWireText());
            (await act.Should().ThrowAsync<SimulatedCrash>($"crash at '{steps[i]}' (step {i})")).Which.Step.Should().Be(steps[i]);
            await DieAsync(dying);
            var leftIds = IdsOnDisk(rig);

            // The next start: a new host, which knows nothing of the invitation's secret.
            await using var next = rig.Host();
            await next.StartAsync();
            await next.PollInvitationsAsync(force: true);
            // a second pass changes nothing that is not already settled
            await next.PollInvitationsAsync(force: true);

            var accounts = next.GetSnapshot().Accounts;
            var redeemed = hub.StateOf(invitation) == "redeemed";
            if (redeemed && accounts.Count == 1)
            {
                // The redemption was delivered and the account has it: waiting for the owner with the code that is on his page.
                var entry = accounts[0].Entry;
                entry.Phase.Should().Be(BlindAccountPhases.AwaitingConfirmation, $"after a crash at '{steps[i]}'");
                entry.MatchCode.Should().Be(hub.MatchCodeOf(invitation));
                accounts[0].Status!.NodeId.Should().Be(hub.RedeemerOf(invitation), "the same identity the hub knows");
                accounts[0].Problem.Should().BeNull();
                new BlindPhoneState(rig.Accounts.States.For(entry)).CallCode.Should().BeNull();

                // and the owner's decision still arrives
                hub.Confirm(invitation);
                await next.PollInvitationsAsync(force: true);
                next.EntryOf(entry.Id).Phase.Should().Be(BlindAccountPhases.Active);
                new BlindPhoneState(rig.Accounts.States.For(next.EntryOf(entry.Id))).CallCode!.NodeId.Should().Be(hub.NodeId);
                resumed++;
            }
            else
            {
                // Nothing was delivered: the account is cleaned up, whatever it had got to, and the invitation is untouched (the person pastes it again).
                redeemed.Should().BeFalse($"a delivered redemption must not be forgotten (crash at '{steps[i]}')");
                AssertGone(rig, next, $"after a crash at '{steps[i]}'");
                hub.StateOf(invitation).Should().Be("issued");
                foreach (var left in leftIds)
                {
                    var entry = new BlindAccountEntry(left, BlindAccountLayouts.Dir, "x");
                    rig.Accounts.Secrets.BlobCount(entry).Should().Be(0, $"the keys of {left} after a crash at '{steps[i]}'");
                    rig.Accounts.States.HasState(entry).Should().BeFalse($"the state of {left} after a crash at '{steps[i]}'");
                }
                cleaned++;
            }
        }

        resumed.Should().BeGreaterThan(0, "the later steps resume");
        cleaned.Should().BeGreaterThan(0, "the earlier steps clean up");
    }

    [Fact]
    public async Task APendingAccountThatNeverGotAnAnswer_AndWhoseInvitationIsLongOver_IsGivenUp()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        var invitation = hub.Issue(lifetime: TimeSpan.FromMinutes(15));
        hub.Enqueue("identity", new HubFault.Unreachable());
        var dying = rig.Host();
        await dying.StartAsync();
        var id = (await dying.AddFromInvitationAsync(invitation.ToWireText())).AccountId!;
        dying.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Pending);
        await DieAsync(dying);

        // the hub is not reachable for a long time after the owner's window could have ended
        hub.Locked = true;
        rig.Clock.Advance(TimeSpan.FromMinutes(15) + BlindAccountHost.ConfirmWindow + TimeSpan.FromMinutes(1));
        await using var next = rig.Host();
        await next.StartAsync();
        await next.PollInvitationsAsync(force: true);

        AssertGone(rig, next, "nothing can still be decided about it");
        hub.Count("status").Should().Be(0, "it was not even asked");
    }

    // ---- the owner's confirmation ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACrashAfterEveryStepOfTheConfirmation_FinishesAtTheNextStart_WithTheAccountActiveAndPaired()
    {
        // the steps of a confirmation: what a poll that learns "confirmed" passes
        List<string> steps;
        using (var probe = new InviteRig())
        {
            var hub = probe.Hub();
            var recorder = new CrashAt();
            await using var host = probe.Host(crash: recorder);
            await host.StartAsync();
            var invitation = hub.Issue();
            (await host.AddFromInvitationAsync(invitation.ToWireText())).Ok.Should().BeTrue();
            hub.Confirm(invitation);
            var before = recorder.Steps.Count;
            await host.PollInvitationsAsync(force: true);
            steps = recorder.Steps.Skip(before).ToList();
        }
        steps.Should().ContainInOrder("invite:call-code-saved", "registry:renamed", "invite:activated");

        for (var i = 0; i < steps.Count; i++)
        {
            using var rig = new InviteRig();
            var hub = rig.Hub();
            var invitation = hub.Issue("Alex");
            var crash = new ArmedCrashAtIndex(i);
            var dying = rig.Host(crash: crash);
            await dying.StartAsync();
            var id = (await dying.AddFromInvitationAsync(invitation.ToWireText())).AccountId!;
            hub.Confirm(invitation);
            crash.Armed = true;
            var act = () => dying.PollInvitationsAsync(force: true);
            (await act.Should().ThrowAsync<SimulatedCrash>($"crash at '{steps[i]}'")).Which.Step.Should().Be(steps[i]);
            await DieAsync(dying);

            await using var next = rig.Host();
            await next.StartAsync();
            await next.PollInvitationsAsync(force: true);

            var entry = next.EntryOf(id);
            entry.Phase.Should().Be(BlindAccountPhases.Active, $"after a crash at '{steps[i]}'");
            var state = new BlindPhoneState(rig.Accounts.States.For(entry));
            state.CallCode.Should().NotBeNull();
            state.CallCode!.NodeId.Should().Be(hub.NodeId);
            entry.MatchCode.Should().BeNull();
            next.CardOf(id).Problem.Should().BeNull();
            next.For(id).GetStatus().IsPaired.Should().BeTrue();
            rig.Accounts.Secrets.For(entry).LoadPairingSecret().Should().BeNull();
        }
    }

    [Fact]
    public async Task ACrashBetweenTheCallCodeAndThePhase_NeedsNoNetworkToFinish()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        var invitation = hub.Issue();
        var crash = new ArmedCrashAtName("invite:call-code-saved");
        var dying = rig.Host(crash: crash);
        await dying.StartAsync();
        var id = (await dying.AddFromInvitationAsync(invitation.ToWireText())).AccountId!;
        hub.Confirm(invitation);
        crash.Armed = true;
        var act = () => dying.PollInvitationsAsync(force: true);
        await act.Should().ThrowAsync<SimulatedCrash>();
        await DieAsync(dying);
        var asked = hub.Count("status");
        hub.Locked = true;           // the hub could not answer now, and it does not have to

        await using var next = rig.Host();
        await next.StartAsync();
        await next.PollInvitationsAsync(force: true);

        next.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Active);
        hub.Count("status").Should().Be(asked);
    }

    /// <summary>The ids of the accounts the registry file names on disk now (what a dead run left).</summary>
    private static List<string> IdsOnDisk(InviteRig rig)
    {
        var file = Path.Combine(rig.Accounts.Root, "accounts.json");
        if (!File.Exists(file)) return [];
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(file));
        return json.RootElement.TryGetProperty("accounts", out var list)
            ? list.EnumerateArray().Select(a => a.GetProperty("id").GetString()!).Where(id => id != "legacy").ToList()
            : [];
    }

    /// <summary>Throws <see cref="SimulatedCrash"/> at the Nth step (0 = the first) reached after <see cref="Armed"/> is set; the steps before belong to starting the host.</summary>
    private sealed class ArmedCrashAtIndex(int index) : IBlindCrashPoints
    {
        private int _count;

        public volatile bool Armed;

        public void Reach(string reached)
        {
            if (Armed && Interlocked.Increment(ref _count) - 1 == index) throw new SimulatedCrash(reached);
        }
    }

    /// <summary>Throws <see cref="SimulatedCrash"/> the first time the named step is reached after <see cref="Armed"/> is set.</summary>
    private sealed class ArmedCrashAtName(string step) : IBlindCrashPoints
    {
        private int _fired;

        public volatile bool Armed;

        public void Reach(string reached)
        {
            if (Armed && reached == step && Interlocked.Exchange(ref _fired, 1) == 0) throw new SimulatedCrash(reached);
        }
    }
}
