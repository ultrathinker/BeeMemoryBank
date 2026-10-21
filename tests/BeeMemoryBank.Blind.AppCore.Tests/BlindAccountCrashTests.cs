using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// "The process died after every write": adopting a 2.6 install, adding an account and removing one are each run to completion once to list their steps, then run again
/// with a crash simulated after step 1, step 2 ... step N (<see cref="CrashAt"/>), each time over a fresh folder; the next start (a new host over what the dead run left)
/// must come up whole - an account that is there is complete and usable, an account that is gone is gone entirely - and the other account never notices.
/// </summary>
public sealed class BlindAccountCrashTests
{
    /// <summary>Ends a "dead" host without cleaning up after it more than disposing does: a real crash releases its file handles too.</summary>
    private static async Task DieAsync(BlindAccountHost host)
    {
        await host.DisposeAsync();
        SqliteConnection.ClearAllPools();
    }

    // ---- adopting a 2.6 install --------------------------------------------------------------------------------------------------------------

    private static async Task<List<string>> StepsOfAdoptionAsync()
    {
        using var rig = new AccountRig();
        await Legacy26.MakeAsync(rig);
        var recorder = new CrashAt();
        await using var host = rig.Host(crash: recorder);
        await host.StartAsync();
        return recorder.Steps;
    }

    [Fact]
    public async Task TheAdoption_HasTheStepsItIsKnownBy()
    {
        var steps = await StepsOfAdoptionAsync();
        steps.Should().Equal("adopt:detected", "registry:before-write", "registry:tmp-written", "registry:renamed", "adopt:done");
    }

    [Fact]
    public async Task ACrashAfterEveryStepOfTheAdoption_IsRepairedByTheNextStart_AndAccountOneIsNeverChanged()
    {
        var steps = await StepsOfAdoptionAsync();
        steps.Count.Should().BeGreaterThan(3);

        for (var i = 0; i < steps.Count; i++)
        {
            using var rig = new AccountRig();
            var install = await Legacy26.MakeAsync(rig);
            var before = AccountRig.Fingerprint(rig.Root);

            var dying = rig.Host(crash: new CrashAt(i));
            var act = () => dying.StartAsync();
            (await act.Should().ThrowAsync<SimulatedCrash>($"crash after step {i} ({steps[i]})")).Which.Step.Should().Be(steps[i]);
            await DieAsync(dying);

            // Whatever the dead run left, it wrote nothing but (part of) the registry: no 2.6 file was touched.
            var left = AccountRig.Fingerprint(rig.Root);
            foreach (var path in before.Keys) left[path].Should().Be(before[path], $"{path} after a crash at '{steps[i]}'");
            left.Keys.Except(before.Keys).Where(k => k != "accounts.json" && !(k.StartsWith("accounts.json.", StringComparison.Ordinal) && k.EndsWith(".tmp", StringComparison.Ordinal)))
                .Should().BeEmpty($"after a crash at '{steps[i]}'");

            // The next start runs and the account is listed exactly once, with its identity and its keys as 2.6 left them.
            await using (var next = rig.Host())
            {
                await next.StartAsync();
                var cards = next.GetSnapshot().Accounts;
                cards.Should().ContainSingle($"after a crash at '{steps[i]}'");
                cards[0].Entry.Id.Should().Be("legacy");
                cards[0].Status!.NodeId.Should().Be(install.NodeId);
                cards[0].Status!.IsPaired.Should().BeTrue();
                cards[0].Status!.StartError.Should().BeNull();
                cards[0].Problem.Should().BeNull();
                rig.Secrets.For(cards[0].Entry).LoadIdentitySeed().Should().Equal(install.Seed);
            }

            // And a third start changes nothing.
            var registry = File.ReadAllBytes(Path.Combine(rig.Root, "accounts.json"));
            await using (var third = rig.Host())
            {
                await third.StartAsync();
                third.GetSnapshot().Accounts.Should().ContainSingle();
            }
            File.ReadAllBytes(Path.Combine(rig.Root, "accounts.json")).Should().Equal(registry);
        }
    }

    [Fact]
    public async Task ACrashAfterEveryStepOfTheFirstEverStart_LeavesAnInstallThatStartsEmpty()
    {
        List<string> steps;
        using (var probe = new AccountRig())
        {
            var recorder = new CrashAt();
            await using var host = probe.Host(crash: recorder);
            await host.StartAsync();
            steps = recorder.Steps;
        }
        steps.Should().Contain("adopt:done");

        for (var i = 0; i < steps.Count; i++)
        {
            using var rig = new AccountRig();
            var dying = rig.Host(crash: new CrashAt(i));
            var act = () => dying.StartAsync();
            await act.Should().ThrowAsync<SimulatedCrash>();
            await DieAsync(dying);

            await using var next = rig.Host();
            await next.StartAsync();

            next.GetSnapshot().RegistryProblem.Should().BeNull($"after a crash at '{steps[i]}'");
            next.GetSnapshot().Accounts.Should().BeEmpty();
            (await next.AddAsync("first")).Ok.Should().BeTrue();
        }
    }

    // ---- adding ------------------------------------------------------------------------------------------------------------------------------

    private static async Task<List<string>> StepsOfAddAsync()
    {
        using var rig = new AccountRig();
        await using (var first = rig.Host()) await first.StartAsync();
        var recorder = new CrashAt();
        await using var host = rig.Host(crash: recorder);
        await host.StartAsync();
        recorder.Steps.Clear();
        await host.AddAsync("probe");
        return recorder.Steps;
    }

    [Fact]
    public async Task ACrashAfterEveryStepOfAdding_LeavesNoHalfAnAccount_AndTheNextStartIsWhole()
    {
        var steps = await StepsOfAddAsync();
        // The entry, then (once the identity is made) the second write that marks it established.
        steps.Should().Equal("add:before-registry", "registry:before-write", "registry:tmp-written", "registry:renamed", "add:registered",
            "registry:before-write", "registry:tmp-written", "registry:renamed", "add:started");
        var renamed = steps.IndexOf("registry:renamed");

        for (var i = 0; i < steps.Count; i++)
        {
            using var rig = new AccountRig();
            await using (var first = rig.Host()) await first.StartAsync();

            var dying = rig.Host(crash: new CrashAt(i));
            await dying.StartAsync();
            var act = () => dying.AddAsync("friend");
            await act.Should().ThrowAsync<SimulatedCrash>($"crash after step {i} ({steps[i]})");
            await DieAsync(dying);

            await using var next = rig.Host();
            await next.StartAsync();
            var cards = next.GetSnapshot().Accounts;

            if (i < renamed)
            {
                cards.Should().BeEmpty($"the account was never written ('{steps[i]}')");
                Directory.Exists(Path.Combine(rig.Root, "accounts")).Should().BeFalse("no folder without an entry");
            }
            else
            {
                // Written: the next start brings it up, whatever was or was not made before the crash.
                var card = cards.Should().ContainSingle($"after '{steps[i]}'").Subject;
                card.Problem.Should().BeNull();
                card.Status!.StartError.Should().BeNull();
                card.Status.NodeId.Should().NotBeNull("the identity is made at the start if the dead run did not get to it");
                rig.Secrets.For(card.Entry).LoadIdentitySeed().Should().NotBeNull();
                next.For(card.Entry.Id).PairingCode().Should().StartWith("bmb-blind-phone:");
                Directory.GetDirectories(Path.Combine(rig.Root, "accounts")).Should().ContainSingle();
            }

            (await next.AddAsync("another")).Ok.Should().BeTrue();
        }
    }

    // ---- removing ----------------------------------------------------------------------------------------------------------------------------

    private static async Task<List<string>> StepsOfRemovalAsync(bool legacy)
    {
        using var rig = new AccountRig();
        var target = await PrepareAsync(rig, legacy);
        var recorder = new CrashAt();
        await using var host = rig.Host(crash: recorder);
        await host.StartAsync();
        recorder.Steps.Clear();
        await host.RemoveAsync(target.Id);
        return recorder.Steps;
    }

    private sealed record Prepared(string Id, string OtherId, Guid OtherNode, byte[] OtherSeed);

    /// <summary>Two accounts, stopped: the one to remove (account 1 of a 2.6 install when <paramref name="legacy"/>) and another one that must come through untouched.</summary>
    private static async Task<Prepared> PrepareAsync(AccountRig rig, bool legacy)
    {
        if (legacy)
        {
            await Legacy26.MakeAsync(rig);
            File.WriteAllText(Path.Combine(rig.Root, "unrelated.txt"), "not the copy's");
        }
        string target, other;
        await using (var host = rig.Host())
        {
            await host.StartAsync();
            target = legacy ? BlindAccountEntry.LegacyId : (await host.AddAsync("to remove")).AccountId!;
            other = (await host.AddAsync("other")).AccountId!;
        }
        SqliteConnection.ClearAllPools();

        var targetMedia = legacy ? Path.Combine(rig.Root, "media") : Path.Combine(rig.Root, "accounts", target, "media");
        Directory.CreateDirectory(targetMedia);
        File.WriteAllBytes(Path.Combine(targetMedia, "x.bin"), [1]);
        var otherMedia = Path.Combine(rig.Root, "accounts", other, "media");
        Directory.CreateDirectory(otherMedia);
        File.WriteAllBytes(Path.Combine(otherMedia, "keep.bin"), [7, 7, 7]);

        var entry = new BlindAccountEntry(other, BlindAccountLayouts.Dir, "other");
        var node = new BlindPhoneState(rig.States.For(entry)).NodeId!.Value;
        return new Prepared(target, other, node, rig.Secrets.For(entry).LoadIdentitySeed()!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACrashAfterEveryStepOfARemoval_EndsWithTheAccountWholeOrGoneEntirely_AndTheOtherUntouched(bool legacy)
    {
        var steps = await StepsOfRemovalAsync(legacy);
        steps.Should().StartWith("remove:start").And.EndWith("remove:done");
        var marked = steps.IndexOf("registry:renamed");   // the first write of the registry is the mark "deleting"
        marked.Should().BePositive();

        for (var i = 0; i < steps.Count; i++)
        {
            using var rig = new AccountRig();
            var prepared = await PrepareAsync(rig, legacy);
            var targetEntry = legacy
                ? new BlindAccountEntry(BlindAccountEntry.LegacyId, BlindAccountLayouts.Root, "x")
                : new BlindAccountEntry(prepared.Id, BlindAccountLayouts.Dir, "x");
            var seedBefore = rig.Secrets.For(targetEntry).LoadIdentitySeed();
            seedBefore.Should().NotBeNull();

            var dying = rig.Host(crash: new CrashAt(i));
            await dying.StartAsync();
            var act = () => dying.RemoveAsync(prepared.Id);
            (await act.Should().ThrowAsync<SimulatedCrash>($"crash after step {i} ({steps[i]})")).Which.Step.Should().Be(steps[i]);
            await DieAsync(dying);

            await using var next = rig.Host();
            await next.StartAsync();
            var cards = next.GetSnapshot().Accounts;

            if (i < marked)
            {
                var card = cards.Should().Contain(c => c.Entry.Id == prepared.Id, $"the removal never got to write its mark ('{steps[i]}')").Subject;
                card.Entry.Phase.Should().Be(BlindAccountPhases.Active);
                card.Problem.Should().BeNull();
                card.Status!.StartError.Should().BeNull();
                rig.Secrets.For(targetEntry).LoadIdentitySeed().Should().Equal(seedBefore, "nothing of the account was touched");
            }
            else
            {
                cards.Should().NotContain(c => c.Entry.Id == prepared.Id, $"the mark was written, so the next start finishes the removal ('{steps[i]}')");
                rig.Secrets.BlobCount(targetEntry).Should().Be(0, $"its keys are cleared ('{steps[i]}')");
                rig.States.HasState(targetEntry).Should().BeFalse("its state is erased");
                var gone = () => next.For(prepared.Id);
                gone.Should().Throw<KeyNotFoundException>();
                if (legacy)
                {
                    File.Exists(rig.LegacyDb).Should().BeFalse();
                    Directory.Exists(Path.Combine(rig.Root, "media")).Should().BeFalse();
                    Directory.Exists(Path.Combine(rig.Root, "blind-backups")).Should().BeFalse();
                    File.Exists(Path.Combine(rig.Root, "unrelated.txt")).Should().BeTrue("only what the copy owns is deleted from the app's root");
                    File.Exists(Path.Combine(rig.Root, "accounts.json")).Should().BeTrue("the device's registry is not part of any account");
                }
                else
                {
                    Directory.Exists(Path.Combine(rig.Root, "accounts", prepared.Id)).Should().BeFalse($"its folder is gone ('{steps[i]}')");
                }
            }

            // The other account never noticed.
            var other = cards.Single(c => c.Entry.Id == prepared.OtherId);
            other.Problem.Should().BeNull();
            other.Status!.NodeId.Should().Be(prepared.OtherNode);
            rig.Secrets.For(other.Entry).LoadIdentitySeed().Should().Equal(prepared.OtherSeed);
            File.ReadAllBytes(Path.Combine(rig.Root, "accounts", prepared.OtherId, "media", "keep.bin")).Should().Equal([7, 7, 7]);

            // A removal that was cut off at any point can simply be asked for again.
            if (cards.Any(c => c.Entry.Id == prepared.Id))
            {
                await next.RemoveAsync(prepared.Id);
                next.GetSnapshot().Accounts.Should().NotContain(c => c.Entry.Id == prepared.Id);
            }
        }
    }
}
