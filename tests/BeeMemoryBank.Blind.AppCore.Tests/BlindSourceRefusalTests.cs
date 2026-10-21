using System.Buffers.Text;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// Two refusals in a row mean "removed by the owner" (2.7.0): the FIRST 401 or 403 is remembered in the account's own state with its time, and the account goes on asking at its
/// normal rhythm; a SECOND one that comes after at least one normal retry interval (not in the same burst) and within 24 hours marks the account; a contact that goes through forgets the first;
/// an older first one is a first one again. A reverse proxy or a firewall in front of a healthy source can answer 401 or 403 for a moment.
/// </summary>
public sealed class BlindSourceRefusalTests
{
    private const string StateKey = "bmb.blind.source_refused_at";
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static IBlindPhoneStore StateOf(InviteRig rig, BlindAccountHost host, string id) => rig.Accounts.States.For(host.EntryOf(id));

    private static Task<string> ActiveAsync(BlindAccountHost host, FakeInviteHub hub, string name = "Alex") => BlindInvitationRevocationTests.ActiveAsync(host, hub, name);

    private static Guid NodeOf(InviteRig rig, BlindAccountHost host, string id) => BlindInvitationRevocationTests.NodeOf(rig, host, id);

    // ---- the rule, as a pure function --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, BlindRefusalOutcome.FirstNoted, 0)]
    [InlineData(0, BlindRefusalOutcome.SameBurst, 0)]
    [InlineData(60, BlindRefusalOutcome.SameBurst, -60)]
    [InlineData(899, BlindRefusalOutcome.SameBurst, -899)]
    [InlineData(900, BlindRefusalOutcome.Removed, null)]
    [InlineData(3600, BlindRefusalOutcome.Removed, null)]
    [InlineData(24 * 3600 - 60, BlindRefusalOutcome.Removed, null)]
    [InlineData(24 * 3600, BlindRefusalOutcome.Removed, null)]
    [InlineData(24 * 3600 + 1, BlindRefusalOutcome.FirstNoted, 0)]
    [InlineData(3 * 24 * 3600, BlindRefusalOutcome.FirstNoted, 0)]
    [InlineData(-300, BlindRefusalOutcome.FirstNoted, 0)]
    public void Judge_ReadsTheRememberedTimeAgainstTheClock(int? rememberedSecondsAgo, BlindRefusalOutcome expected, int? keptSecondsFromNow)
    {
        DateTimeOffset? remembered = rememberedSecondsAgo is { } ago ? T0.AddSeconds(-ago) : null;

        var outcome = BlindSourceRemoval.Judge(remembered, T0, out var remember);

        outcome.Should().Be(expected);
        remember.Should().Be(keptSecondsFromNow is { } at ? T0.AddSeconds(at) : null, "what is kept: the new first time, the old one in a burst, nothing once the account is removed");
    }

    [Fact]
    public void ARefusalIsOnlyShownWhileItCounts()
    {
        BlindSourceRemoval.RefusalIsRecent(null, T0).Should().BeFalse();
        BlindSourceRemoval.RefusalIsRecent(T0.AddHours(-23), T0).Should().BeTrue();
        BlindSourceRemoval.RefusalIsRecent(T0.AddHours(-25), T0).Should().BeFalse("a refusal older than a day is forgotten");
        BlindSourceRemoval.RefusalIsRecent(T0.AddMinutes(5), T0).Should().BeFalse("a time in the future is not trusted");
    }

    // ---- the first refusal removes nothing ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheFirstRefusal_DoesNotRemoveTheAccount_IsRememberedWithItsTime_AndSaysSoNeutrally()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);
        (await host.For(id).RequestSyncAsync()).Should().Be("Synced.");
        StateOf(rig, host, id).Get(StateKey).Should().BeNull("nothing is written until the source refuses");
        hub.Revoke(NodeOf(rig, host, id));

        var result = await host.For(id).RequestSyncAsync();

        result.Should().Be("The source refused this device once; asking again.");
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Active);
        new BlindPhoneState(StateOf(rig, host, id)).SourceRefusedAt.Should().Be(rig.Clock.GetUtcNow());
        host.For(id).GetStatus().LastFailure.Should().BeNull();
        host.For(id).GetStatus().SourceRefusedAt.Should().Be(rig.Clock.GetUtcNow());
        var card = BlindAccountCard.Build(host.CardOf(id), TimeZoneInfo.Utc, rig.Clock.GetUtcNow());
        card.State.Should().Be("The source refused this device once; asking again.");
        card.Kind.Should().Be(BlindCardKind.Waiting, "neutral, not a problem");
        card.CanSyncNow.Should().BeTrue("the account goes on");
        host.For(id).GetStatus().RecentLog.Should().Contain(e => e.Kind == "refusal" && e.Message == "The source refused this device once; asking again.");
        BlindHomeView.ProblemText(host.For(id).GetStatus()).Should().BeNull("a refusal heard once is not a problem line");
    }

    [Fact]
    public async Task AfterTheFirstRefusal_TheSchedulerGoesOnAtItsNormalRhythm_AndTheSecondRoundDecides()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);
        await using var scheduler = new BlindDeviceScheduler(host, rig.Clock);
        hub.Revoke(NodeOf(rig, host, id));

        await scheduler.TickAsync();
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Active);
        var asked = hub.Count("authenticate");

        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        await scheduler.TickAsync();
        hub.Count("authenticate").Should().Be(asked, "the rhythm is a round every 15 minutes");
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Active);

        rig.Clock.Advance(TimeSpan.FromMinutes(11));
        await scheduler.TickAsync();

        hub.Count("authenticate").Should().BeGreaterThan(asked, "it asked again");
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.RemovedByOwner);
        new BlindPhoneState(StateOf(rig, host, id)).SourceRefusedAt.Should().BeNull("a removed account starts from nothing if the person tries again");
    }

    [Fact]
    public async Task ASecondRefusal_AfterOneRetryInterval_RemovesTheAccount_ExactlyAtTheInterval()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);
        hub.Revoke(NodeOf(rig, host, id));

        await host.For(id).RequestSyncAsync();
        rig.Clock.Advance(BlindSourceRemoval.MinimumGap - TimeSpan.FromSeconds(1));
        (await host.For(id).RequestSyncAsync()).Should().Be(BlindSourceRemoval.FirstRefusalSentence, "one second short of the interval is still the same burst");
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Active);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        var result = await host.For(id).RequestSyncAsync();

        result.Should().Be("The source no longer accepts this device. The copy stays until you remove it.");
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.RemovedByOwner);
        host.StateLineOf(id, rig.Clock).Should().Be("Alex removed this device from the network. The copy stays until you remove it.");
    }

    // ---- a burst is one refusal --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TwoRefusalsOfOneBurst_AreOneRefusal()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);
        hub.Revoke(NodeOf(rig, host, id));
        var first = rig.Clock.GetUtcNow();

        for (var i = 0; i < 5; i++)
        {
            (await host.For(id).RequestSyncAsync()).Should().Be(BlindSourceRemoval.FirstRefusalSentence);
            rig.Clock.Advance(TimeSpan.FromSeconds(30));
        }

        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Active, "the same request asked again at once is not a second opinion of the source");
        new BlindPhoneState(StateOf(rig, host, id)).SourceRefusedAt.Should().Be(first, "the time of the FIRST one stays: the interval is counted from it");
    }

    [Fact]
    public async Task ARoundAndALongJobRefusedTogether_AreOneRefusal()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);
        hub.Revoke(NodeOf(rig, host, id));

        (await host.For(id).RequestSyncAsync()).Should().Be(BlindSourceRemoval.FirstRefusalSentence);
        (await host.For(id).RunHeavyAsync(forceBackup: true)).Should().NotContain("no longer accepts");

        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Active);
    }

    // ---- a contact that goes through forgets the refusal -------------------------------------------------------------------------------------

    [Fact]
    public async Task ASuccessfulRoundBetween_ForgetsTheFirstRefusal()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);
        var node = NodeOf(rig, host, id);

        hub.Revoke(node);
        await host.For(id).RequestSyncAsync();
        new BlindPhoneState(StateOf(rig, host, id)).SourceRefusedAt.Should().NotBeNull();
        hub.Unrevoke(node);
        rig.Clock.Advance(TimeSpan.FromMinutes(20));
        (await host.For(id).RequestSyncAsync()).Should().Be("Synced.");

        new BlindPhoneState(StateOf(rig, host, id)).SourceRefusedAt.Should().BeNull();
        host.For(id).GetStatus().SourceRefusedAt.Should().BeNull();
        BlindAccountCard.Build(host.CardOf(id), TimeZoneInfo.Utc, rig.Clock.GetUtcNow()).State.Should().StartWith("Synced");

        // and the next refusal is a first one again: one more does not remove the account
        hub.Revoke(node);
        rig.Clock.Advance(TimeSpan.FromMinutes(20));
        (await host.For(id).RequestSyncAsync()).Should().Be(BlindSourceRemoval.FirstRefusalSentence);
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Active);
    }

    /// <summary>A source of the replica that refuses (401) as many times as it is told and then loads: the first load without a real hub.</summary>
    private sealed class ScriptedReplica : IBlindReplicaSource
    {
        public int Refusals { get; set; }

        /// <summary>How many of the next baseline checks the source refuses (401); the ones after it say "nothing newer" without having asked.</summary>
        public int UpdateRefusals { get; set; }

        public int UpdateCalls;

        /// <summary>What a baseline check that is not refused comes to: nothing asked (a copy without a baseline), a 304, or a newer baseline.</summary>
        public BlindBaselineCheck Answer { get; set; } = BlindBaselineCheck.NotAsked;

        public async Task<bool> UpdateBaselineAsync(BlindCallCode target, string workDirectory, IProgress<double>? progress, CancellationToken ct) =>
            await CheckBaselineAsync(target, workDirectory, progress, ct) == BlindBaselineCheck.Installed;

        public Task<BlindBaselineCheck> CheckBaselineAsync(BlindCallCode target, string workDirectory, IProgress<double>? progress, CancellationToken ct)
        {
            Interlocked.Increment(ref UpdateCalls);
            if (UpdateRefusals > 0)
            {
                UpdateRefusals--;
                return Task.FromException<BlindBaselineCheck>(new HttpRequestException("refused", null, System.Net.HttpStatusCode.Unauthorized));
            }
            return Task.FromResult(Answer);
        }

        /// <summary>When set, a first load that is not refused fails with it (a network error, a 5xx).</summary>
        public Exception? LoadFailure { get; set; }

        public Task FetchAndInstallAsync(BlindCallCode target, string workDirectory, IProgress<double>? progress, CancellationToken ct)
        {
            if (Refusals > 0)
            {
                Refusals--;
                return Task.FromException(new HttpRequestException("refused", null, System.Net.HttpStatusCode.Unauthorized));
            }
            return LoadFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ASuccessfulFirstLoad_ForgetsTheFirstRefusal()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        var replica = new ScriptedReplica { Refusals = 1 };
        await using var host = rig.Host(overrides: (services, _) => services.AddSingleton<IBlindReplicaSource>(replica));
        await host.StartAsync();
        var id = await BlindInvitationRevocationTests.ConfirmedBeforeTheFirstLoadAsync(host, hub);

        (await host.For(id).RunHeavyAsync(forceBackup: false)).Should().Be(BlindSourceRemoval.FirstRefusalSentence);
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Active);
        host.For(id).GetStatus().LastFailure.Should().BeNull("not a first load that failed");
        new BlindPhoneState(StateOf(rig, host, id)).SourceRefusedAt.Should().NotBeNull();

        rig.Clock.Advance(TimeSpan.FromMinutes(20));
        await host.For(id).RunHeavyAsync(forceBackup: false);

        new BlindPhoneState(StateOf(rig, host, id)).InitialLoadDone.Should().BeTrue("the load went through");
        new BlindPhoneState(StateOf(rig, host, id)).SourceRefusedAt.Should().BeNull("a contact that went through forgets the refusal");
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Active);
    }

    // ---- a state that cannot be written never removes an account (review T3) ---------------------------------------------------------------

    /// <summary>The account's state, but a write of the remembered refusal fails while the test says so (a full disk, a store that is locked).</summary>
    private sealed class FlakyRefusalState(IBlindStateStore inner) : IBlindStateStore
    {
        public Exception? FailWith { get; set; }

        public string? Get(string key) => inner.Get(key);

        public void Set(string key, string? value)
        {
            if (FailWith is { } failure && key == StateKey) throw failure;
            inner.Set(key, value);
        }

        public void Erase() => inner.Erase();
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task AStateThatCannotBeWrittenAtTheSecondRefusal_NeverRemovesTheAccount_TheRefusalCountsAsAFirstOne_AndTheRoundDoesNotFail(Type failure)
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        FlakyRefusalState? flaky = null;
        await using var host = rig.Host(overrides: (services, entry) =>
        {
            flaky = new FlakyRefusalState(rig.Accounts.States.For(entry));
            services.AddSingleton<IBlindStateStore>(flaky);
        });
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);
        hub.Revoke(NodeOf(rig, host, id));
        (await host.For(id).RequestSyncAsync()).Should().Be(BlindSourceRemoval.FirstRefusalSentence);
        rig.Clock.Advance(BlindSourceRemoval.MinimumGap);

        flaky!.FailWith = (Exception)Activator.CreateInstance(failure, "the store is locked")!;
        var result = await host.For(id).RequestSyncAsync();        // the second refusal: judging it ends in a write that fails

        result.Should().Be(BlindSourceRemoval.FirstRefusalSentence, "what could not be written is never taken for the source's decision");
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Active);
        host.For(id).GetStatus().LastFailure.Should().BeNull("a refusal is not a failure, and the round did not fail");
        BlindAccountCard.Build(host.CardOf(id), TimeZoneInfo.Utc, rig.Clock.GetUtcNow()).State.Should().Be(BlindSourceRemoval.FirstRefusalSentence);

        // when the store works again the next refusal is the second one, as the first is still remembered
        flaky.FailWith = null;
        (await host.For(id).RequestSyncAsync()).Should().Be(BlindSourceRemoval.RemovedSentence);
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.RemovedByOwner);
    }

    // ---- the baseline check is a contact too (review D3) -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(BlindBaselineCheck.Current)]
    [InlineData(BlindBaselineCheck.Installed)]
    public async Task ABaselineCheckTheSourceAnswered_ForgetsTheFirstRefusal_SoTheNextBlipIsAFirstOneAgain(BlindBaselineCheck answer)
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        var replica = new ScriptedReplica { Answer = answer };
        await using var host = rig.Host(overrides: (services, _) => services.AddSingleton<IBlindReplicaSource>(replica));
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);
        var node = NodeOf(rig, host, id);
        hub.Revoke(node);
        await host.For(id).RequestSyncAsync();                                   // 12:00 a blip: the first refusal
        new BlindPhoneState(StateOf(rig, host, id)).SourceRefusedAt.Should().NotBeNull();
        rig.Clock.Advance(TimeSpan.FromMinutes(5));

        await host.For(id).RunHeavyAsync(forceBackup: false);                    // 12:05 the long job's baseline check goes through (a 304: the source accepted the device)

        new BlindPhoneState(StateOf(rig, host, id)).SourceRefusedAt.Should().BeNull("a contact that went through forgets the refusal, whichever job made it");
        rig.Clock.Advance(TimeSpan.FromMinutes(15));
        (await host.For(id).RequestSyncAsync()).Should().Be(BlindSourceRemoval.FirstRefusalSentence, "12:20 is a first refusal again: a contact went through between the two");
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Active);
    }

    [Fact]
    public async Task ABaselineCheckThatAskedNothing_KeepsTheFirstRefusal_AStateThatCannotTellNeverRemovesButNeverClearsEither()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        var replica = new ScriptedReplica { Answer = BlindBaselineCheck.NotAsked };
        await using var host = rig.Host(overrides: (services, _) => services.AddSingleton<IBlindReplicaSource>(replica));
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);
        hub.Revoke(NodeOf(rig, host, id));
        await host.For(id).RequestSyncAsync();
        var first = new BlindPhoneState(StateOf(rig, host, id)).SourceRefusedAt;
        rig.Clock.Advance(TimeSpan.FromMinutes(5));

        await host.For(id).RunHeavyAsync(forceBackup: false);

        replica.UpdateCalls.Should().BeGreaterThan(0);
        new BlindPhoneState(StateOf(rig, host, id)).SourceRefusedAt.Should().Be(first, "nothing was asked of the source: no contact went through");
    }

    [Fact]
    public async Task ASourceThatOnlyKnowsTheOldMethod_NeverReportsAContact()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        var replica = new OldMethodOnlyReplica();
        await using var host = rig.Host(overrides: (services, _) => services.AddSingleton<IBlindReplicaSource>(replica));
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);
        hub.Revoke(NodeOf(rig, host, id));
        await host.For(id).RequestSyncAsync();
        var first = new BlindPhoneState(StateOf(rig, host, id)).SourceRefusedAt;

        await host.For(id).RunHeavyAsync(forceBackup: false);

        replica.Calls.Should().Be(1);
        new BlindPhoneState(StateOf(rig, host, id)).SourceRefusedAt.Should().Be(first, "false from the old method cannot tell a 304 from nothing asked");
    }

    private sealed class OldMethodOnlyReplica : IBlindReplicaSource
    {
        public int Calls;

        public Task FetchAndInstallAsync(BlindCallCode target, string workDirectory, IProgress<double>? progress, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> UpdateBaselineAsync(BlindCallCode target, string workDirectory, IProgress<double>? progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(false);
        }
    }

    // ---- the card shows the newest news (review D4) ------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFirstLoadThatFailedAfterTheRefusal_IsShownOnTheCard_NotTheRefusalThatIsOlder()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        var replica = new ScriptedReplica { Refusals = 1 };
        await using var host = rig.Host(overrides: (services, _) => services.AddSingleton<IBlindReplicaSource>(replica));
        await host.StartAsync();
        var id = await BlindInvitationRevocationTests.ConfirmedBeforeTheFirstLoadAsync(host, hub);
        await host.For(id).RunHeavyAsync(forceBackup: false);
        BlindAccountCard.Build(host.CardOf(id), TimeZoneInfo.Utc, rig.Clock.GetUtcNow()).State.Should().Be(BlindSourceRemoval.FirstRefusalSentence);

        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        replica.LoadFailure = new HttpRequestException("The server said 503.", null, System.Net.HttpStatusCode.ServiceUnavailable);
        await host.For(id).RunHeavyAsync(forceBackup: false);

        new BlindPhoneState(StateOf(rig, host, id)).SourceRefusedAt.Should().NotBeNull("only a success forgets the refusal");
        var card = BlindAccountCard.Build(host.CardOf(id), TimeZoneInfo.Utc, rig.Clock.GetUtcNow());
        card.State.Should().StartWith("First load failed:", "the newer, real failure is what the card says");
        card.State.Should().NotContain("refused this device once");
    }

    [Fact]
    public async Task ARefusalThatCameAfterAFailedFirstLoad_IsShownOnTheCard_BecauseItIsTheNewerNews()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        var replica = new ScriptedReplica { LoadFailure = new HttpRequestException("The server said 503.", null, System.Net.HttpStatusCode.ServiceUnavailable) };
        await using var host = rig.Host(overrides: (services, _) => services.AddSingleton<IBlindReplicaSource>(replica));
        await host.StartAsync();
        var id = await BlindInvitationRevocationTests.ConfirmedBeforeTheFirstLoadAsync(host, hub);
        await host.For(id).RunHeavyAsync(forceBackup: false);
        BlindAccountCard.Build(host.CardOf(id), TimeZoneInfo.Utc, rig.Clock.GetUtcNow()).State.Should().StartWith("First load failed:");

        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        replica.Refusals = 1;
        await host.For(id).RunHeavyAsync(forceBackup: false);

        BlindAccountCard.Build(host.CardOf(id), TimeZoneInfo.Utc, rig.Clock.GetUtcNow()).State.Should().Be(BlindSourceRemoval.FirstRefusalSentence);
    }

    [Fact]
    public async Task AFirstLoadRefusedSeveralTimesInOneBurst_WritesTheSentenceOnce()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        var replica = new ScriptedReplica { Refusals = 4 };
        await using var host = rig.Host(overrides: (services, _) => services.AddSingleton<IBlindReplicaSource>(replica));
        await host.StartAsync();
        var id = await BlindInvitationRevocationTests.ConfirmedBeforeTheFirstLoadAsync(host, hub);

        for (var i = 0; i < 4; i++)
        {
            (await host.For(id).RunHeavyAsync(forceBackup: false)).Should().Be(BlindSourceRemoval.FirstRefusalSentence);
            rig.Clock.Advance(TimeSpan.FromSeconds(30));
        }

        host.For(id).GetStatus().RecentLog.Count(e => e.Message == BlindSourceRemoval.FirstRefusalSentence)
            .Should().Be(1, "a burst of refused retries is one line, not two lines per retry");
    }

    [Fact]
    public async Task TwoRefusedFirstLoads_OneIntervalApart_RemoveTheAccount()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        var replica = new ScriptedReplica { Refusals = 2 };
        await using var host = rig.Host(overrides: (services, _) => services.AddSingleton<IBlindReplicaSource>(replica));
        await host.StartAsync();
        var id = await BlindInvitationRevocationTests.ConfirmedBeforeTheFirstLoadAsync(host, hub);

        await host.For(id).RunHeavyAsync(forceBackup: false);
        rig.Clock.Advance(BlindSourceRemoval.MinimumGap);
        var second = await host.For(id).RunHeavyAsync(forceBackup: false);

        second.Should().Be(BlindSourceRemoval.RemovedSentence);
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.RemovedByOwner);
    }

    [Fact]
    public async Task TheFirstLoad_IsRetriedByTheSchedulerAfterAFirstRefusal_AndTheSecondRefusalRemoves()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var id = await BlindInvitationRevocationTests.ConfirmedBeforeTheFirstLoadAsync(host, hub);
        await using var scheduler = new BlindDeviceScheduler(host, rig.Clock);
        hub.Revoke(NodeOf(rig, host, id));

        await scheduler.TickAsync();
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Active);
        scheduler.ScheduleOf(id)!.HeavyRetryAt.Should().NotBeNull("the first load goes on being tried, as after any failure to load");

        // the back-off (30 s, 1 min, 2 min ... up to 15) tries again; refusals inside the first interval are one burst, the one after it is the second
        for (var pass = 0; pass < 12 && host.EntryOf(id).Phase == BlindAccountPhases.Active; pass++)
        {
            rig.Clock.Advance(TimeSpan.FromMinutes(5));
            await scheduler.TickAsync();
        }

        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.RemovedByOwner);
    }

    // ---- a refusal is never remembered for an account the source has already removed (review D1) --------------------------------------------

    /// <summary>An account that has loaded, refused once by a round and removed by the second one: the state of a pass that ran its sync round and then its long job.</summary>
    private static async Task<string> RemovedBySyncAsync(InviteRig rig, BlindAccountHost host, FakeInviteHub hub)
    {
        var id = await ActiveAsync(host, hub);
        hub.Revoke(NodeOf(rig, host, id));
        await host.For(id).RequestSyncAsync();
        rig.Clock.Advance(BlindSourceRemoval.MinimumGap);
        (await host.For(id).RequestSyncAsync()).Should().Be(BlindSourceRemoval.RemovedSentence);
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.RemovedByOwner);
        new BlindPhoneState(StateOf(rig, host, id)).SourceRefusedAt.Should().BeNull();
        return id;
    }

    [Fact]
    public async Task ALongJobRefused_AfterTheRoundThatRemovedTheAccount_RemembersNothingOnTheRemovedAccount()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        var replica = new ScriptedReplica { UpdateRefusals = 1 };
        await using var host = rig.Host(overrides: (services, _) => services.AddSingleton<IBlindReplicaSource>(replica));
        await host.StartAsync();
        var id = await RemovedBySyncAsync(rig, host, hub);

        await host.For(id).RunHeavyAsync(forceBackup: false);

        replica.UpdateCalls.Should().Be(1, "the long job did ask (this is a job that was started from a card taken before the round removed the account)");
        new BlindPhoneState(StateOf(rig, host, id)).SourceRefusedAt.Should().BeNull("the account is already removed by its owner: a refusal that arrives now is not a first one of anything");
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.RemovedByOwner);
    }

    [Fact]
    public async Task ARoundRefused_AfterTheLongJobThatRemovedTheAccount_RemembersNothingOnTheRemovedAccount_TheOrderOfTheIPhoneRound()
    {
        // The iPhone's work round runs the long job and then the sync round. Here the long job makes the second refusal; the round after it must not start a count on the removed account.
        using var rig = new InviteRig();
        var hub = rig.Hub();
        var replica = new ScriptedReplica { UpdateRefusals = 2 };
        await using var host = rig.Host(overrides: (services, _) => services.AddSingleton<IBlindReplicaSource>(replica));
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);
        hub.Revoke(NodeOf(rig, host, id));
        await host.For(id).RunHeavyAsync(forceBackup: false);
        rig.Clock.Advance(BlindSourceRemoval.MinimumGap);
        await host.For(id).RunHeavyAsync(forceBackup: false);   // the refusal of the baseline check is the second one; the job's own sentence is its backup's, the log has the refusal's
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.RemovedByOwner);
        new BlindPhoneState(StateOf(rig, host, id)).SourceRefusedAt.Should().BeNull();

        await host.For(id).RequestSyncAsync();

        new BlindPhoneState(StateOf(rig, host, id)).SourceRefusedAt.Should().BeNull();
    }

    [Fact]
    public async Task APassThatRemovedTheAccountWithItsSyncRound_DoesNotRunTheLongJobFromTheCardItTookBefore()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        var replica = new ScriptedReplica();
        await using var host = rig.Host(overrides: (services, _) => services.AddSingleton<IBlindReplicaSource>(replica));
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);
        await using var scheduler = new BlindDeviceScheduler(host, rig.Clock);
        hub.Revoke(NodeOf(rig, host, id));
        replica.UpdateRefusals = 100;

        await scheduler.TickAsync();      // the first pass: the sync round and the long job are both due; both are refused, in one burst
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Active);
        var asked = replica.UpdateCalls;
        asked.Should().BeGreaterThan(0, "the long job of the first pass did ask the source");
        rig.Clock.Advance(TimeSpan.FromMinutes(61));
        await scheduler.TickAsync();      // the sync round is refused again and removes the account; the long job is due in the same pass

        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.RemovedByOwner);
        replica.UpdateCalls.Should().Be(asked, "the account was removed by this pass's own sync round: nothing more is asked of the source for it");
        new BlindPhoneState(StateOf(rig, host, id)).SourceRefusedAt.Should().BeNull();
    }

    [Fact]
    public async Task TryAgain_StartsFromNothing_AFirstRefusalAfterItIsOnlyAFirstOne()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        var replica = new ScriptedReplica { UpdateRefusals = 1 };
        await using var host = rig.Host(overrides: (services, _) => services.AddSingleton<IBlindReplicaSource>(replica));
        await host.StartAsync();
        var id = await RemovedBySyncAsync(rig, host, hub);
        await host.For(id).RunHeavyAsync(forceBackup: false);   // the long job of the same pass, refused on the removed account

        host.Reconnect(id).Should().BeNull();

        new BlindPhoneState(StateOf(rig, host, id)).SourceRefusedAt.Should().BeNull("nothing has been asked since the person pressed Try again");
        BlindAccountCard.Build(host.CardOf(id), TimeZoneInfo.Utc, rig.Clock.GetUtcNow()).State.Should().NotBe(BlindSourceRemoval.FirstRefusalSentence);
        rig.Clock.Advance(BlindSourceRemoval.MinimumGap);
        (await host.For(id).RequestSyncAsync()).Should().Be(BlindSourceRemoval.FirstRefusalSentence, "a blip after Try again is a first refusal, not the second one");
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Active);
    }

    [Fact]
    public async Task TryAgain_ForgetsARefusalThatIsStillRemembered()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var id = await RemovedBySyncAsync(rig, host, hub);
        new BlindPhoneState(StateOf(rig, host, id)).SourceRefusedAt = rig.Clock.GetUtcNow();   // however it came to be there

        host.Reconnect(id).Should().BeNull();

        new BlindPhoneState(StateOf(rig, host, id)).SourceRefusedAt.Should().BeNull();
    }

    [Fact]
    public void ANewCallCode_ForgetsTheRefusalOfTheComputerThatGaveTheOldOne()
    {
        // An account paired the old way, refused once by computer A and then paired with computer B: B's first refusal is its first one.
        var store = new DictionaryStore();
        var state = new BlindPhoneState(store) { SourceRefusedAt = T0 };
        var code = BlindCallCode.Create("https://127.0.0.1:5610", Guid.NewGuid(), Base64Url.EncodeToString(new byte[32]), new byte[32], new byte[32]);

        state.CallCode = code;

        state.CallCode.Should().NotBeNull();
        state.SourceRefusedAt.Should().BeNull("a refusal belongs to the computer that gave it");
    }

    // ---- 24 hours ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASecondRefusal_WithinADayOfTheFirst_Removes_AndOneAfterADayIsAFirstOneAgain()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        var hubTwo = rig.Hub("hub-two.test");
        await using var host = rig.Host();
        await host.StartAsync();
        var one = await ActiveAsync(host, hub, "One");
        var two = await ActiveAsync(host, hubTwo, "Two");
        hub.Revoke(NodeOf(rig, host, one));
        hubTwo.Revoke(NodeOf(rig, host, two));

        await host.For(one).RequestSyncAsync();
        await host.For(two).RequestSyncAsync();
        rig.Clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));
        await host.For(one).RequestSyncAsync();
        host.EntryOf(one).Phase.Should().Be(BlindAccountPhases.RemovedByOwner, "23 h 59 min after the first one is within the day");

        rig.Clock.Advance(TimeSpan.FromMinutes(2));
        (await host.For(two).RequestSyncAsync()).Should().Be(BlindSourceRemoval.FirstRefusalSentence, "older than 24 hours: a first refusal again");
        host.EntryOf(two).Phase.Should().Be(BlindAccountPhases.Active);
        new BlindPhoneState(StateOf(rig, host, two)).SourceRefusedAt.Should().Be(rig.Clock.GetUtcNow(), "the day counts from the new first one");
        BlindAccountCard.Build(host.CardOf(two), TimeZoneInfo.Utc, rig.Clock.GetUtcNow()).State.Should().Be(BlindSourceRemoval.FirstRefusalSentence);

        rig.Clock.Advance(BlindSourceRemoval.MinimumGap);
        await host.For(two).RequestSyncAsync();
        host.EntryOf(two).Phase.Should().Be(BlindAccountPhases.RemovedByOwner);
    }

    [Fact]
    public async Task AStaleRefusal_IsNotShownOnTheCard()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);
        hub.Revoke(NodeOf(rig, host, id));
        await host.For(id).RequestSyncAsync();

        rig.Clock.Advance(TimeSpan.FromHours(25));

        BlindAccountCard.Build(host.CardOf(id), TimeZoneInfo.Utc, rig.Clock.GetUtcNow()).State.Should().NotBe(BlindSourceRemoval.FirstRefusalSentence);
    }

    // ---- what is stored ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AStateFileFromBeforeTheField_HasNoRefusal_AndEverythingElseReadsAsBefore()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);
        var store = StateOf(rig, host, id);
        // a state of 2.6.4 / 2.7 before this field: the key is simply not there
        store.Set(StateKey, null);
        var before = new BlindPhoneState(store);
        var node = before.NodeId;
        var call = before.CallCode;

        var state = new BlindPhoneState(store);

        state.SourceRefusedAt.Should().BeNull();
        state.NodeId.Should().Be(node);
        state.CallCode?.ToString().Should().Be(call?.ToString());
        (await host.For(id).RequestSyncAsync()).Should().Be("Synced.");
        host.For(id).GetStatus().SourceRefusedAt.Should().BeNull();
    }

    [Fact]
    public void TheField_IsOneAdditiveKey_AndNothingThe264KeysMeanIsMoved()
    {
        var store = new DictionaryStore();
        var node = Guid.NewGuid();
        var state = new BlindPhoneState(store) { NodeId = node, DisplayName = "Phone", InitialLoadDone = true, LastSyncAt = T0 };
        var keysBefore = store.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

        state.SourceRefusedAt = T0.AddMinutes(3);

        store.Keys.Except(keysBefore).Should().Equal(StateKey);
        keysBefore.Should().BeEquivalentTo(["bmb.blind.node_id", "bmb.blind.name", "bmb.blind.initial_load_done", "bmb.blind.last_sync"], "the keys 2.6.4 knows keep their names");
        new BlindPhoneState(store).NodeId.Should().Be(node, "an older reader that ignores the new key reads the rest as before");
        new BlindPhoneState(store).SourceRefusedAt.Should().Be(T0.AddMinutes(3));
        store.Get(StateKey).Should().Be(T0.AddMinutes(3).ToString("O"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a time")]
    [InlineData("2026-13-45")]
    public void ADamagedValue_IsNoRefusal(string stored)
    {
        var store = new DictionaryStore();
        store.Set(StateKey, stored);

        new BlindPhoneState(store).SourceRefusedAt.Should().BeNull();
        BlindSourceRemoval.Judge(new BlindPhoneState(store).SourceRefusedAt, T0, out _).Should().Be(BlindRefusalOutcome.FirstNoted, "what cannot be read is never taken for a first refusal that was given");
    }

    [Fact]
    public void DisconnectAndWipe_ForgetsTheRefusal()
    {
        var store = new DictionaryStore();
        var state = new BlindPhoneState(store) { SourceRefusedAt = T0 };

        state.Clear();

        store.Get(StateKey).Should().BeNull();
        state.SourceRefusedAt.Should().BeNull();
    }

    // ---- the removed account's cleanup is unchanged -----------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAccountTheSourceRemoved_IsCleanedUpAsBefore_WhenThePersonRemovesIt()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);
        hub.Revoke(NodeOf(rig, host, id));
        var entry = host.EntryOf(id);
        await host.For(id).RequestSyncAsync();
        rig.Clock.Advance(BlindSourceRemoval.MinimumGap);
        await host.For(id).RequestSyncAsync();
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.RemovedByOwner);

        await host.RemoveAsync(id);

        host.GetSnapshot().Accounts.Should().BeEmpty();
        rig.Accounts.Secrets.BlobCount(entry).Should().Be(0);
        rig.Accounts.States.HasState(entry).Should().BeFalse("the remembered refusal goes with the rest of the state");
        Directory.Exists(Path.Combine(rig.Accounts.Root, "accounts", entry.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task AFirstRefusal_DoesNotHoldUpARemoval_AndGoesWithTheAccount()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);
        hub.Revoke(NodeOf(rig, host, id));
        var entry = host.EntryOf(id);
        await host.For(id).RequestSyncAsync();

        await host.RemoveAsync(id);

        host.GetSnapshot().Accounts.Should().BeEmpty();
        rig.Accounts.States.HasState(entry).Should().BeFalse();
    }

    // ---- the single-account home -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheHomeSaysTheNeutralSentence_OnlyWhileTheRefusalCounts()
    {
        BlindAppStatus Status(DateTimeOffset? refused) =>
            new(Guid.NewGuid(), "Phone", true, true, T0.AddHours(-1), null, BlindBackupSchedule.Off, false, null, null, [], [], Endpoint: "https://a.test:5610", SourceRefusedAt: refused);

        BlindHomeView.Build(Status(null), null, false, null, T0).Sync.Should().StartWith("Last sync:").And.NotContain("refused");
        BlindHomeView.Build(Status(T0.AddMinutes(-20)), null, false, null, T0).Sync.Should().EndWith("The source refused this device once; asking again.");
        BlindHomeView.Build(Status(T0.AddHours(-30)), null, false, null, T0).Sync.Should().NotContain("refused");
    }

    private sealed class DictionaryStore : IBlindPhoneStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public IEnumerable<string> Keys => _values.Keys;

        public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;

        public void Set(string key, string? value)
        {
            if (value is null) _values.Remove(key);
            else _values[key] = value;
        }
    }
}
