using System.Text;
using System.Text.Json;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// The invitation's secret is a bearer capability: it goes to the hub in the one request that redeems and nowhere else. After a finished flow - however it ended - the secret, and the
/// invitation text that carries it, are in no file of the data folder (the registry, the databases, the log, the state files), in no state value, in no secret-store blob, on no card, in
/// no sentence of a result and in no exception; and the host holds no copy of it any more.
/// </summary>
public sealed class BlindInvitationSecretTests
{
    private static IEnumerable<(string Label, byte[] Needle)> NeedlesOf(BlindInvitation invitation, string text) =>
        LeakScan.Needles("secret", invitation.Secret).Concat(LeakScan.Needles("text", text)).Concat(LeakScan.Needles("secret text", Convert.ToBase64String(invitation.Secret)));

    private static int HeldRedemptions(BlindAccountHost host) =>
        ((System.Collections.IDictionary)typeof(BlindAccountHost).GetField("_redemptions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(host)!).Count;

    /// <summary>Everything a person, a log or a disk could show of the account: the files, the state values, the secret-store blobs, the card, the log lines.</summary>
    private static List<string> Leaks(InviteRig rig, BlindAccountHost host, BlindInvitation invitation, string text, string? accountId)
    {
        var needles = NeedlesOf(invitation, text).ToList();
        var found = LeakScan.InFolder(rig.Accounts.Root, needles);
        foreach (var card in host.GetSnapshot().Accounts)
        {
            var entry = card.Entry;
            found.AddRange(LeakScan.InText(JsonSerializer.Serialize(entry), needles).Select(l => $"{l} in the entry"));
            foreach (var (key, value) in rig.Accounts.States.Snapshot(entry))
                found.AddRange(LeakScan.InText(key + "=" + value, needles).Select(l => $"{l} in the state {key}"));
            foreach (BlindAccountNames.Role role in Enum.GetValues<BlindAccountNames.Role>())
                if (rig.Accounts.Secrets.ReadBlob(entry, role) is { } blob)
                    found.AddRange(LeakScan.InText(Convert.ToBase64String(blob), needles).Select(l => $"{l} in the secret {role}"));
            found.AddRange(LeakScan.InText((card.Problem ?? "") + "\n" + (card.Note ?? ""), needles).Select(l => $"{l} on the card"));
            found.AddRange(LeakScan.InText(BlindAccountCard.Build(card, TimeZoneInfo.Utc, rig.Clock.GetUtcNow()).State, needles).Select(l => $"{l} in the state line"));
            if (card.Status is { } status)
                foreach (var line in status.RecentLog)
                    found.AddRange(LeakScan.InText(line.Message, needles).Select(l => $"{l} in the log"));
        }
        return found;
    }

    [Fact]
    public async Task AfterTheRedemptionWasAnswered_TheSecretIsNowhereButInTheRequestThatSentIt()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var invitation = hub.Issue("Alex");
        var text = invitation.ToWireText();

        var added = await host.AddFromInvitationAsync(text);

        added.Ok.Should().BeTrue(added.Error);
        Leaks(rig, host, invitation, text, added.AccountId).Should().BeEmpty();
        HeldRedemptions(host).Should().Be(0, "the host let go of the secret when the hub answered");
        // the secret did go to the hub, in the redemption: that is what the request is for
        hub.Calls.Single(c => c.Route == "redeem").Body.Should().Contain(Convert.ToBase64String(invitation.Secret).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        // ... and in no other request
        hub.Calls.Where(c => c.Route != "redeem").SelectMany(c => LeakScan.InText(c.Body, NeedlesOf(invitation, text))).Should().BeEmpty();
        LeakScan.InText(hub.Sync.AllRequestText(), NeedlesOf(invitation, text)).Should().BeEmpty();
    }

    [Fact]
    public async Task WhileTheRedemptionIsNotAnswered_TheSecretIsHeldInMemoryOnly()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var invitation = hub.Issue("Alex");
        var text = invitation.ToWireText();
        hub.Enqueue("redeem", new HubFault.LoseAnswer());

        var added = await host.AddFromInvitationAsync(text);

        added.Ok.Should().BeTrue(added.Error);
        HeldRedemptions(host).Should().Be(1, "the retry needs it");
        Leaks(rig, host, invitation, text, added.AccountId).Should().BeEmpty("not even while the answer is awaited");
    }

    [Fact]
    public async Task AfterTheOwnerConfirmed_NothingOfTheInvitationIsKept()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var invitation = hub.Issue("Alex");
        var text = invitation.ToWireText();
        var added = await host.AddFromInvitationAsync(text);
        hub.Confirm(invitation);

        await host.PollInvitationsAsync(force: true);

        host.EntryOf(added.AccountId!).Phase.Should().Be(BlindAccountPhases.Active);
        Leaks(rig, host, invitation, text, added.AccountId).Should().BeEmpty();
        HeldRedemptions(host).Should().Be(0);
    }

    [Theory]
    [InlineData("gone")]
    [InlineData("locked")]
    [InlineData("older")]
    [InlineData("mismatch")]
    [InlineData("refused")]
    public async Task WhateverTheFlowCameTo_NeitherASentenceNorTheDiskHoldsTheSecret(string how)
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var invitation = hub.Issue("Alex");
        var text = invitation.ToWireText();
        switch (how)
        {
            case "gone": hub.Enqueue("redeem", new HubFault.Status(410)); break;
            case "locked": hub.Locked = true; break;
            case "older": hub.Older = true; break;
            case "mismatch": hub.IdentityNodeId = Guid.NewGuid(); break;
        }

        var added = await host.AddFromInvitationAsync(text);
        if (how == "refused")
        {
            hub.Refuse(invitation);
            await host.PollInvitationsAsync(force: true);
        }

        LeakScan.InText((added.Error ?? "") + (added.AccountId ?? ""), NeedlesOf(invitation, text)).Should().BeEmpty("the sentence of the result");
        Leaks(rig, host, invitation, text, added.AccountId).Should().BeEmpty();
        HeldRedemptions(host).Should().Be(how == "locked" ? 1 : 0);
    }

    [Fact]
    public async Task WhenTheHostEnds_ItHoldsNoSecret()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        var host = rig.Host();
        await host.StartAsync();
        hub.Locked = true;
        (await host.AddFromInvitationAsync(hub.IssueText())).Ok.Should().BeTrue();
        HeldRedemptions(host).Should().Be(1);

        await host.DisposeAsync();

        HeldRedemptions(host).Should().Be(0);
    }

    [Fact]
    public async Task ACrashAtAnyStep_LeavesNoSecretOnDisk()
    {
        // the files a dead run leaves, at the step where the secret was most exposed (after the redemption left, before the answer was recorded)
        using var rig = new InviteRig();
        var hub = rig.Hub();
        var invitation = hub.Issue("Alex");
        var text = invitation.ToWireText();
        var crash = new AtName("invite:redeem-answered");
        var dying = rig.Host(crash: crash);
        await dying.StartAsync();
        crash.Armed = true;
        var act = () => dying.AddFromInvitationAsync(text);
        await act.Should().ThrowAsync<SimulatedCrash>();
        await dying.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        LeakScan.InFolder(rig.Accounts.Root, NeedlesOf(invitation, text)).Should().BeEmpty();
        foreach (var id in Directory.EnumerateDirectories(Path.Combine(rig.Accounts.Root, "accounts")).Select(Path.GetFileName))
            rig.Accounts.States.Snapshot(new BlindAccountEntry(id!, BlindAccountLayouts.Dir, "x")).Values
                .SelectMany(v => LeakScan.InText(v ?? "", NeedlesOf(invitation, text))).Should().BeEmpty();
    }

    [Fact]
    public async Task TheAccountsOwnKeysAreNotInTheCardOrInTheLogEither()
    {
        // the redemption carries the account's backup key to the hub; it is not shown or logged anywhere on the device
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var invitation = hub.Issue();
        var added = await host.AddFromInvitationAsync(invitation.ToWireText());
        var entry = host.EntryOf(added.AccountId!);
        var backupKey = rig.Accounts.Secrets.For(entry).LoadBackupKey()!;
        var seed = rig.Accounts.Secrets.For(entry).LoadIdentitySeed()!;
        var needles = LeakScan.Needles("backup key", backupKey).Concat(LeakScan.Needles("seed", seed)).ToList();

        var card = host.CardOf(added.AccountId!);
        var shown = string.Join("\n", card.Status!.RecentLog.Select(l => l.Message)) + BlindAccountCard.Build(card, TimeZoneInfo.Utc, rig.Clock.GetUtcNow()).State
                    + JsonSerializer.Serialize(entry);

        LeakScan.InText(shown, needles).Should().BeEmpty();
        LeakScan.InFolder(rig.Accounts.Root, needles).Should().BeEmpty("the keys are in the secret store, not in a file");
        hub.Calls.Where(c => c.Route != "redeem").SelectMany(c => LeakScan.InText(c.Body, needles)).Should().BeEmpty("only the redemption carries the backup key, and no request carries the seed");
        LeakScan.InText(hub.Calls.Single(c => c.Route == "redeem").Body, LeakScan.Needles("seed", seed).ToList()).Should().BeEmpty();
    }

    private sealed class AtName(string step) : IBlindCrashPoints
    {
        private int _fired;

        public volatile bool Armed;

        public void Reach(string reached)
        {
            if (Armed && reached == step && Interlocked.Exchange(ref _fired, 1) == 0) throw new SimulatedCrash(reached);
        }
    }

    // ---- the formatters of an invitation do not carry the secret (review MAJOR-2) ------------------------------------------------------------

    [Fact]
    public async Task NothingTheHostFormats_NotTheInvitation_ThePreview_TheResults_TheTarget_NorAnException_CarriesTheSecret()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var invitation = hub.Issue("Alex");
        var text = invitation.ToWireText();
        var needles = NeedlesOf(invitation, text).ToList();
        var strings = new List<string>();

        // the invitation itself, every way a framework formats an object
        strings.Add(invitation.ToString());
        strings.Add($"{invitation}");
        strings.Add("invitation: " + invitation);
        strings.Add(string.Format("{0}", invitation));
        strings.Add(BlindInvitationTarget.From(invitation).ToString());

        // what the host hands back: the preview, the result of the add, the card
        var preview = host.PreviewInvitation(text);
        strings.Add(preview.ToString());
        strings.Add($"{preview}");
        strings.Add(preview.Description + preview.Limits + preview.Validity(rig.Clock.GetUtcNow()));
        var added = await host.AddFromInvitationAsync(text);
        strings.Add(added.ToString());
        strings.Add($"{added}" + added.Error);
        strings.Add(host.CardOf(added.AccountId!).ToString());

        // exceptions: an add that is cancelled, and a process that "dies" at each place the secret is in memory
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await host.AddFromInvitationAsync(hub.Issue("Other").ToWireText(), null, cancelled.Token); }
        catch (Exception ex) { strings.Add(ex.ToString()); }
        foreach (var step in new[] { "invite:context-saved", "invite:probed", "invite:redeem-answered" })
        {
            using var crashRig = new InviteRig();
            var crashHub = crashRig.Hub();
            await using var dying = crashRig.Host(crash: new CrashAtNamed(step));
            await dying.StartAsync();
            var crashing = crashHub.Issue("Alex");
            var crashNeedles = NeedlesOf(crashing, crashing.ToWireText()).ToList();
            try { await dying.AddFromInvitationAsync(crashing.ToWireText()); }
            catch (SimulatedCrash ex)
            {
                LeakScan.InText(ex.ToString(), crashNeedles).Should().BeEmpty($"the exception of a crash at {step}");
            }
        }

        foreach (var printed in strings)
            LeakScan.InText(printed, needles).Should().BeEmpty("nothing a person, a log or a debugger formats may hold the bearer secret: " + printed[..Math.Min(80, printed.Length)]);
        invitation.ToString().Should().Contain("x=<redacted>");
    }
}
