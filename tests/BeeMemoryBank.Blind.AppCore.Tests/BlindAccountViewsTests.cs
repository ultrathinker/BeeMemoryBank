using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>The sentences of the accounts screen, the size and limits forms, the removal confirmation and the invitation preview: pure, worked out without a phone.</summary>
public sealed class BlindAccountViewsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly TimeZoneInfo Minus5 = TimeZoneInfo.CreateCustomTimeZone("Test/Minus5", TimeSpan.FromHours(-5), "Test UTC-5", "Test UTC-5");

    private static BlindAccountEntry Entry(string name = "Friend", string phase = BlindAccountPhases.Active, BlindAccountLimits? limits = null,
        bool unmetered = false, bool saver = false, bool paused = false, string? origin = null) =>
        new("0123456789abcdef0123456789abcdef", BlindAccountLayouts.Dir, name, phase, SourceOrigin: origin, Limits: limits,
            UnmeteredOnly: unmetered, PauseOnPowerSaving: saver, PausedByUser: paused);

    /// <summary>An account added from an invitation: it carries the invitation's id and its source's name.</summary>
    private static BlindAccountEntry Invited(string phase, string name = "Alex", string? matchCode = null) =>
        new("0123456789abcdef0123456789abcdef", BlindAccountLayouts.Dir, name, phase, SourceOrigin: "https://mem.example.org:5300", InviteId: "AAAAAAAAAAAAAAAAAAAAAA", MatchCode: matchCode);

    private static BlindAppStatus Status(bool paired = true, bool loaded = true, DateTimeOffset? lastSync = null, string? job = null, double? progress = null,
        string? endpoint = "https://mem.example.org:5300", bool awaiting = false, string? startError = null, bool keyLost = false, string? keyStore = null,
        BlindAppFailure? failure = null) =>
        new(Guid.NewGuid(), "device", paired, loaded, lastSync, null, BlindBackupSchedule.Off, keyLost, job, progress, [], [],
            Endpoint: paired ? endpoint : null, AwaitingAnswer: awaiting, StartError: startError, KeyStoreUnavailable: keyStore, LastFailure: failure);

    private static BlindAccountUsageView Usage(long stored = 1_288_490_189, long requests = 312, long inbound = 36L << 20, long outbound = 2L << 20, long? average = 40L << 20) =>
        new(stored, requests, inbound, outbound, average);

    private static BlindAccountCard Card(BlindAccountSnapshot snapshot) => BlindAccountCard.Build(snapshot, Utc, Now);

    // ---- sizes -------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1L, "1 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(512L * 1024, "512 KB")]
    [InlineData(1048575L, "1 MB")]            // 1023.999 KB rounds up to the next unit, never "1024 KB"
    [InlineData(1L << 20, "1 MB")]
    [InlineData(38L << 20, "38 MB")]
    [InlineData(200L << 20, "200 MB")]
    [InlineData(1_288_490_189L, "1.2 GB")]
    [InlineData(1_503_238_554L, "1.4 GB")]
    [InlineData(2L << 30, "2 GB")]
    [InlineData(10L << 30, "10 GB")]
    [InlineData(3L << 40, "3 TB")]
    [InlineData(-5L, "0 B")]
    public void SizesAreWrittenTheWayAPersonReadsThem(long bytes, string text) => BlindSizeFormat.Format(bytes).Should().Be(text);

    [Theory]
    [InlineData("2 GB", 2L << 30)]
    [InlineData("2GB", 2L << 30)]
    [InlineData("2 gb", 2L << 30)]
    [InlineData("200 MB", 200L << 20)]
    [InlineData("200mb", 200L << 20)]
    [InlineData("1.5 GB", 1610612736L)]
    [InlineData("1,5 GB", 1610612736L)]
    [InlineData("512 KiB", 512L * 1024)]
    [InlineData("1 GiB", 1L << 30)]
    [InlineData("  10 MB  ", 10L << 20)]
    [InlineData("0", 0L)]
    [InlineData("0 MB", 0L)]
    [InlineData("100 B", 100L)]
    [InlineData("1 TB", 1L << 40)]
    public void SizesAreReadTheWayAPersonWritesThem(string text, long bytes)
    {
        BlindSizeFormat.TryParse(text, out var parsed, out var problem).Should().BeTrue(problem);
        parsed.Should().Be(bytes);
    }

    [Theory]
    [InlineData("200", "Add a unit")]
    [InlineData("abc", "Write a size")]
    [InlineData("-5 MB", "Write a size")]
    [InlineData("2 XB", "Write a size")]
    [InlineData("2..5 GB", "Write a size")]
    [InlineData("99999999 TB", "more than a device can hold")]
    [InlineData("", "Write a size")]
    public void ASizeThatCannotBeRead_SaysWhy(string text, string reason)
    {
        BlindSizeFormat.TryParse(text, out _, out var problem).Should().BeFalse();
        problem.Should().Contain(reason);
    }

    // ---- the limits form ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AnEmptyBox_IsNoLimit_AndTheFormRoundTrips()
    {
        var empty = new BlindLimitsForm("", "", "", false, false).Validate();
        empty.Ok.Should().BeTrue();
        empty.Limits.Should().Be(BlindAccountLimits.None);

        var entry = Entry(limits: new BlindAccountLimits(2L << 30, 200L << 20, 5000), unmetered: true, saver: true);
        var form = BlindLimitsForm.From(entry);
        form.Should().Be(new BlindLimitsForm("2 GB", "200 MB", "5000", true, true));
        form.Validate().Limits.Should().Be(entry.Limits);
    }

    [Fact]
    public void ZeroIsAPause_NotAnUnlimitedBox()
    {
        var result = new BlindLimitsForm("0", "0", "0", false, false).Validate();

        result.Ok.Should().BeTrue();
        result.Limits.Should().Be(new BlindAccountLimits(0, 0, 0));
    }

    [Theory]
    [InlineData("1", "at least 200")]
    [InlineData("199", "at least 200")]
    [InlineData("abc", "whole number")]
    [InlineData("-1", "whole number")]
    [InlineData("2.5", "whole number")]
    public void FewerThan200RequestsADay_IsRefused_WithTheReason(string text, string reason)
    {
        var result = new BlindLimitsForm("", "", text, false, false).Validate();

        result.Ok.Should().BeFalse();
        result.Limits.Should().BeNull();
        result.Problems.Should().ContainSingle().Which.Should().StartWith("Daily requests:").And.Contain(reason);
        if (text is "1" or "199") result.Problems[0].Should().Contain("every 15 minutes");
    }

    [Theory]
    [InlineData("200")]
    [InlineData("5000")]
    [InlineData("0")]
    public void TwoHundredRequestsADayOrMore_AreAccepted(string text) => new BlindLimitsForm("", "", text, false, false).Validate().Ok.Should().BeTrue();

    [Theory]
    [InlineData("1 MB")]
    [InlineData("9 MB")]
    [InlineData("10239 KB")]
    public void LessThan10MBADay_IsRefused_WithTheReason(string text)
    {
        var result = new BlindLimitsForm("", text, "", false, false).Validate();

        result.Ok.Should().BeFalse();
        result.Problems.Should().ContainSingle().Which.Should().StartWith("Daily traffic:").And.Contain("10 MB").And.Contain("0 to pause");
    }

    [Theory]
    [InlineData("10 MB")]
    [InlineData("200 MB")]
    [InlineData("1 GB")]
    public void TenMBADayOrMore_IsAccepted(string text) => new BlindLimitsForm("", text, "", false, false).Validate().Limits!.MaxTrafficBytesPerDay.Should().Be(BlindSizeFormat.TryParse(text, out var b, out _) ? b : -1);

    [Fact]
    public void EveryProblemIsSaidAtOnce_NotOneAtATime()
    {
        var result = new BlindLimitsForm("lots", "3 MB", "10", false, false).Validate();

        result.Problems.Should().HaveCount(3);
        result.Problems.Select(p => p.Split(':')[0]).Should().Equal("Storage", "Daily traffic", "Daily requests");
    }

    [Fact]
    public void TheDeviceForm_IsTheSameWithoutTheRequestBox()
    {
        var ok = new BlindDeviceLimitsForm("10 GB", "1 GB").Validate();
        ok.Problems.Should().BeEmpty();
        ok.Limits.Should().Be(new BlindDeviceLimits(10L << 30, 1L << 30));
        BlindDeviceLimitsForm.From(ok.Limits!).Should().Be(new BlindDeviceLimitsForm("10 GB", "1 GB"));

        var bad = new BlindDeviceLimitsForm("x", "5 MB").Validate();
        bad.Limits.Should().BeNull();
        bad.Problems.Should().HaveCount(2);
        new BlindDeviceLimitsForm("", "0").Validate().Limits.Should().Be(new BlindDeviceLimits(null, 0), "zero pauses the whole device");
    }

    // ---- the card ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AHealthyCard_ShowsOneStateLine_AndTheNumbersTheUserAsksFor()
    {
        var snapshot = new BlindAccountSnapshot(
            Entry(limits: new BlindAccountLimits(2L << 30, 200L << 20), origin: "https://mem.example.org"), Status(lastSync: Now.AddMinutes(-4)), Usage: Usage());

        var card = Card(snapshot);

        card.Name.Should().Be("Friend");
        card.SourceHost.Should().Be("mem.example.org");
        card.State.Should().Be("Synced 4 min ago");
        card.Kind.Should().Be(BlindCardKind.Ok);
        card.Stored.Should().Be("1.2 GB of 2 GB");
        card.Today.Should().Be("312 requests, about 38 MB of 200 MB");
        card.Average.Should().Be("about 40 MB a day over the last 7 days");
        card.LastSync.Should().Be("Last sync: 4 min ago");
        card.IsPaused.Should().BeFalse();
        card.CanSyncNow.Should().BeTrue();
    }

    [Fact]
    public void ACardWithNoLimits_SaysJustWhatIsHeld_AndTheRequestLimitShowsAsOfN()
    {
        var plain = Card(new BlindAccountSnapshot(Entry(), Status(lastSync: Now), Usage: Usage()));
        plain.Stored.Should().Be("1.2 GB");
        plain.Today.Should().Be("312 requests, about 38 MB");

        var limited = Card(new BlindAccountSnapshot(Entry(limits: new BlindAccountLimits(MaxRequestsPerDay: 5000)), Status(lastSync: Now), Usage: Usage()));
        limited.Today.Should().Be("312 of 5000 requests, about 38 MB");
    }

    [Theory]
    [InlineData(30, "Synced just now")]
    [InlineData(5 * 60, "Synced 5 min ago")]
    [InlineData(3 * 3600, "Synced 3 h ago")]
    [InlineData(2 * 86400, "Synced 2 d ago")]
    public void TheLastSyncIsToldInTheLargestUnit(int secondsAgo, string text) =>
        Card(new BlindAccountSnapshot(Entry(), Status(lastSync: Now.AddSeconds(-secondsAgo)))).State.Should().Be(text);

    public static TheoryData<string, BlindAccountSnapshot, string, BlindCardKind> States() => new()
    {
        { "problem on the card", new BlindAccountSnapshot(Entry(), Status(), "It could not be started: IOException: disk full"), "It could not be started: IOException: disk full", BlindCardKind.Problem },
        { "deleting", new BlindAccountSnapshot(Entry(phase: BlindAccountPhases.Deleting), null), "Removing this account...", BlindCardKind.Removing },
        { "awaiting confirmation", new BlindAccountSnapshot(Entry(phase: BlindAccountPhases.AwaitingConfirmation), null), "Waiting for the owner to confirm.", BlindCardKind.Waiting },
        { "refused", new BlindAccountSnapshot(Entry(phase: BlindAccountPhases.Refused), null), "The owner refused this device. You can remove this account.", BlindCardKind.Problem },
        { "expired", new BlindAccountSnapshot(Entry(phase: BlindAccountPhases.Expired), null), "The invitation expired. You can remove this account.", BlindCardKind.Problem },
        { "removed by owner", new BlindAccountSnapshot(Entry(phase: BlindAccountPhases.RemovedByOwner), null), "The owner removed this device from the network. The copy stays until you remove it.", BlindCardKind.Problem },
        { "pending", new BlindAccountSnapshot(Invited(BlindAccountPhases.Pending), null), "Connecting to mem.example.org...", BlindCardKind.Waiting },
        { "pending with a note", new BlindAccountSnapshot(Invited(BlindAccountPhases.Pending), null, Note: "Could not reach mem.example.org. It will try again."), "Could not reach mem.example.org. It will try again.", BlindCardKind.Waiting },
        { "invited, waiting with the code", new BlindAccountSnapshot(Invited(BlindAccountPhases.AwaitingConfirmation, matchCode: "K7F-2QX"), null), "Waiting for Alex to confirm. Read this code out to them: K7F-2QX", BlindCardKind.Waiting },
        { "invited, refused", new BlindAccountSnapshot(Invited(BlindAccountPhases.Refused), null), "Alex refused this device. You can remove this account.", BlindCardKind.Problem },
        { "invited, removed by its owner", new BlindAccountSnapshot(Invited(BlindAccountPhases.RemovedByOwner, "Anna's copy"), null), "Anna's copy removed this device from the network. The copy stays until you remove it.", BlindCardKind.Problem },
        { "starting", new BlindAccountSnapshot(Entry(), null), "Starting...", BlindCardKind.Working },
        { "start error", new BlindAccountSnapshot(Entry(), Status(startError: "database is locked")), "Could not start: database is locked", BlindCardKind.Problem },
        { "key storage", new BlindAccountSnapshot(Entry(), Status(keyStore: "UnauthorizedAccessException: locked")), "The key storage of this device did not answer. Nothing was changed; try again in a moment.", BlindCardKind.Problem },
        { "keys missing", new BlindAccountSnapshot(Entry(), Status(keyLost: true)), "Keys missing - remove this account and add it again.", BlindCardKind.Problem },
        { "traffic", new BlindAccountSnapshot(Entry(), Status(), Pause: new BlindPause(BlindPauseReasons.Traffic, new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero), 200L << 20)), "Paused: daily traffic limit reached (200 MB). Resumes at 00:00.", BlindCardKind.Paused },
        { "requests", new BlindAccountSnapshot(Entry(), Status(), Pause: new BlindPause(BlindPauseReasons.Requests, new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero), 500)), "Paused: daily request limit reached (500 requests). Resumes at 00:00.", BlindCardKind.Paused },
        { "disk", new BlindAccountSnapshot(Entry(), Status(), Pause: new BlindPause(BlindPauseReasons.Disk, null, 2L << 30)), "Paused: storage limit reached (2 GB). Raise the limit to go on.", BlindCardKind.Paused },
        { "disk before a download", new BlindAccountSnapshot(Entry(), Status(), Pause: new BlindPause(BlindPauseReasons.Disk, null, 1L << 30, 1_503_238_554)), "Paused: not enough room: this copy needs about 1.4 GB and the limit is 1 GB.", BlindCardKind.Paused },
        { "device traffic", new BlindAccountSnapshot(Entry(), Status(), Pause: new BlindPause(BlindPauseReasons.DeviceTraffic, new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero), 1L << 30)), "Paused: the device's daily traffic limit is reached (1 GB). Resumes at 00:00.", BlindCardKind.Paused },
        { "device disk", new BlindAccountSnapshot(Entry(), Status(), Pause: new BlindPause(BlindPauseReasons.DeviceDisk, null, 10L << 30)), "Paused: the device's storage limit is reached (10 GB).", BlindCardKind.Paused },
        { "metered", new BlindAccountSnapshot(Entry(), Status(), Pause: new BlindPause(BlindPauseReasons.MeteredNetwork)), "Waiting for an unmetered network (Wi-Fi).", BlindCardKind.Waiting },
        { "metered unknown", new BlindAccountSnapshot(Entry(), Status(), Pause: new BlindPause(BlindPauseReasons.MeteredNetwork, ConditionUnknown: true)), "Waiting: this device does not say whether the network is metered.", BlindCardKind.Waiting },
        { "battery saver", new BlindAccountSnapshot(Entry(), Status(), Pause: new BlindPause(BlindPauseReasons.PowerSaving)), "Waiting: the battery saver is on.", BlindCardKind.Waiting },
        { "paused by you", new BlindAccountSnapshot(Entry(paused: true), Status(), Pause: new BlindPause(BlindPauseReasons.PausedByYou)), "Paused: paused by you.", BlindCardKind.Paused },
        { "zero traffic", new BlindAccountSnapshot(Entry(), Status(), Pause: new BlindPause(BlindPauseReasons.Traffic, null, 0)), "Paused: the daily traffic limit is set to 0.", BlindCardKind.Paused },
        { "running job", new BlindAccountSnapshot(Entry(), Status(job: "First load", progress: 0.4)), "First load: 40%", BlindCardKind.Working },
        { "job without progress", new BlindAccountSnapshot(Entry(), Status(job: "Sync")), "Sync...", BlindCardKind.Working },
        { "awaiting answer", new BlindAccountSnapshot(Entry(), Status(paired: false, awaiting: true)), "Waiting for the computer's answer: give it the code this account shows.", BlindCardKind.Waiting },
        { "not paired", new BlindAccountSnapshot(Entry(), Status(paired: false)), "Not paired yet.", BlindCardKind.Waiting },
        { "load waiting", new BlindAccountSnapshot(Entry(), Status(loaded: false)), "First load: waiting to start.", BlindCardKind.Working },
        { "load failed", new BlindAccountSnapshot(Entry(), Status(loaded: false, failure: new BlindAppFailure("First load", "no answer: connection refused", Now, 2))), "First load failed: no answer: connection refused. It tries again soon.", BlindCardKind.Working },
        { "never synced", new BlindAccountSnapshot(Entry(), Status()), "Not synced yet.", BlindCardKind.Waiting },
    };

    [Theory]
    [MemberData(nameof(States))]
    public void TheOneStateLine_SaysWhatTheAccountIsDoingOrWhyItIsNot(string what, BlindAccountSnapshot snapshot, string state, BlindCardKind kind)
    {
        _ = what;
        var card = Card(snapshot);

        card.State.Should().Be(state);
        card.Kind.Should().Be(kind);
        card.State.Should().NotContain("\n", "one line");
    }

    [Fact]
    public void APausedCard_CannotSyncNow_AndAnAccountThatIsBeingRemovedCannotBeEdited()
    {
        var paused = Card(new BlindAccountSnapshot(Entry(), Status(lastSync: Now), Pause: new BlindPause(BlindPauseReasons.PausedByYou)));
        paused.IsPaused.Should().BeTrue();
        paused.CanSyncNow.Should().BeFalse();

        var removing = Card(new BlindAccountSnapshot(Entry(phase: BlindAccountPhases.Deleting), null));
        removing.CanEdit.Should().BeFalse();
        Card(new BlindAccountSnapshot(Entry(), Status(job: "Sync"), Usage: null)).CanSyncNow.Should().BeFalse("a round is already running");
    }

    [Fact]
    public void AResumeTimeThatIsNotTodayOrTomorrow_IsToldWithItsDate()
    {
        var until = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
        var text = BlindPauseText.Say(new BlindPause(BlindPauseReasons.Traffic, until, 200L << 20), Utc, Now);

        text.Should().Be("daily traffic limit reached (200 MB). Resumes on 25 Oct at 00:00.", "a clock that was set back pushes the lift far ahead: say the day");
    }

    [Fact]
    public void TheResumeTime_IsTheDevicesLocalTime()
    {
        var until = new DateTimeOffset(2026, 10, 22, 5, 0, 0, TimeSpan.Zero);   // 00:00 at UTC-5

        BlindPauseText.Say(new BlindPause(BlindPauseReasons.Traffic, until, 1L << 20), Minus5, Now).Should().EndWith("Resumes at 00:00.");
        BlindPauseText.Say(new BlindPause(BlindPauseReasons.Traffic, until, 1L << 20), Utc, Now).Should().EndWith("Resumes at 05:00.");
    }

    // ---- the accounts screen --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheAccountsScreen_HasACardEach_AndTheDeviceLine()
    {
        var snapshot = new BlindAccountsSnapshot(
            [
                new BlindAccountSnapshot(Entry("A"), Status(lastSync: Now), Usage: Usage(stored: 1L << 30, inbound: 20L << 20, outbound: 1L << 20)),
                new BlindAccountSnapshot(Entry("B"), Status(lastSync: Now), Usage: Usage(stored: 2L << 30, inbound: 20L << 20, outbound: 0)),
                new BlindAccountSnapshot(Entry("C"), Status(lastSync: Now), Usage: Usage(stored: 1_288_490_189, inbound: 0, outbound: 0)),
            ],
            BlindDeviceLimits.None, null, CanAdd: true, 8,
            DeviceUsage: new BlindDeviceUsageView(3_650_000_000, 41L << 20, 900, 3));

        var view = BlindAccountsView.Build(snapshot, Utc, Now);

        view.Cards.Select(c => c.Name).Should().Equal("A", "B", "C");
        view.DeviceLine.Should().Be("3 accounts - 3.4 GB stored - today 41 MB");
        view.CanAdd.Should().BeTrue();
        view.Problem.Should().BeNull();
        view.AddHint.Should().BeEmpty();
    }

    [Fact]
    public void TheDeviceLine_ShowsTheDeviceLimits_WhenThereAreSome_AndSaysNoAccountsWhenThereAreNone()
    {
        var limited = new BlindAccountsSnapshot(
            [new BlindAccountSnapshot(Entry("A"), Status(lastSync: Now), Usage: Usage())],
            new BlindDeviceLimits(10L << 30, 1L << 30), null, CanAdd: true, 8, DeviceUsage: new BlindDeviceUsageView(1L << 30, 300L << 20, 5, 1));
        BlindAccountsView.Build(limited, Utc, Now).DeviceLine.Should().Be("1 account - 1 GB of 10 GB stored - today 300 MB of 1 GB");

        var none = new BlindAccountsSnapshot([], BlindDeviceLimits.None, null, CanAdd: true, 8);
        var view = BlindAccountsView.Build(none, Utc, Now);
        view.DeviceLine.Should().Be("No accounts");
        view.Cards.Should().BeEmpty();
    }

    [Fact]
    public void ADamagedRegistry_ShowsTheSentenceAndNothingElse()
    {
        var snapshot = new BlindAccountsSnapshot([], BlindDeviceLimits.None, "accounts.json is damaged and cannot be read. Nothing was started or changed, so that nothing is lost.", CanAdd: false, 8);

        var view = BlindAccountsView.Build(snapshot, Utc, Now);

        view.Problem.Should().Contain("damaged");
        view.CanAdd.Should().BeFalse();
        view.Cards.Should().BeEmpty();
        view.AddHint.Should().BeEmpty("the problem says it all");
    }

    [Fact]
    public void AtTheCap_AddIsOffAndTheHintSaysWhy()
    {
        var accounts = Enumerable.Range(0, 8).Select(i => new BlindAccountSnapshot(Entry("A" + i), Status(lastSync: Now))).ToList();
        var view = BlindAccountsView.Build(new BlindAccountsSnapshot(accounts, BlindDeviceLimits.None, null, CanAdd: false, 8), Utc, Now);

        view.CanAdd.Should().BeFalse();
        view.AddHint.Should().Be("This device holds at most 8 accounts. Remove one to add another.");
    }

    // ---- removal, defaults, invitation ----------------------------------------------------------------------------------------------------

    [Fact]
    public void TheRemovalConfirmation_SaysWhatGoesAndWhoIsTold()
    {
        var paired = BlindRemoveConfirmation.Build(new BlindAccountSnapshot(Entry("Alex Doe"), Status(), Usage: Usage()));
        paired.Title.Should().Be("Remove Alex Doe?");
        paired.Message.Should().Be("Remove the copy of Alex Doe? 1.2 GB and its keys are deleted from this device. Alex Doe is told if this device is online.");
        paired.ConfirmText.Should().Be("Remove");

        var unpaired = BlindRemoveConfirmation.Build(new BlindAccountSnapshot(Entry("A"), Status(paired: false), Usage: Usage(stored: 100)));
        unpaired.Message.Should().Be("Remove the copy of A? 100 B and its keys are deleted from this device. It never connected, so nobody is told.");
    }

    [Fact]
    public void TheDefaults_ProtectAnInvitedFriend_AndChangeNothingForAccountOne()
    {
        var phone = BlindAccountDefaults.ForInvited(isPhone: true);
        phone.Limits.Should().Be(new BlindAccountLimits(2L << 30, 200L << 20, null));
        phone.UnmeteredOnly.Should().BeTrue();
        phone.PauseOnPowerSaving.Should().BeTrue();

        var desktop = BlindAccountDefaults.ForInvited(isPhone: false);
        desktop.Limits.Should().Be(phone.Limits);
        desktop.UnmeteredOnly.Should().BeFalse("no network switch on a desktop");
        desktop.PauseOnPowerSaving.Should().BeFalse();

        var one = BlindAccountDefaults.ForUpgradedAccountOne;
        one.Limits.IsEmpty.Should().BeTrue();
        one.UnmeteredOnly.Should().BeFalse();
        one.PauseOnPowerSaving.Should().BeFalse();

        // The defaults pass the form's own checks.
        BlindLimitsForm.From(Entry(limits: phone.Limits)).Validate().Ok.Should().BeTrue();
    }

    [Fact]
    public void TheInvitationPreview_IsHonestAboutWhatThisDeviceCanReadAndWhatItWillAllow()
    {
        var preview = new BlindInvitationPreview("Alex Doe", "mem.example.org", BlindAccountDefaults.ForInvited(isPhone: true));

        preview.Description.Should().Be("A copy of Alex Doe's Bee Memory Bank, kept encrypted: this device cannot read it. It comes from mem.example.org. This invitation is a one-time code, not an encrypted one.");
        preview.Limits.Should().Be("It will use at most 2 GB of storage, 200 MB a day, only on Wi-Fi or other unmetered networks, paused on battery saver.");
        new BlindInvitationPreview("A", "h", BlindAccountDefaults.ForUpgradedAccountOne).Limits.Should().Be("No limits are set.");
    }
}
