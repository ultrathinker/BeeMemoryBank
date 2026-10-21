using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>The smaller parts of the invitation client: what an account keeps of the invitation, the call code built from it, the values the redemption sends, the screen's watch, the scheduler's pass.</summary>
public sealed class BlindInvitationPartsTests
{
    private sealed class MemoryStore : IBlindPhoneStore
    {
        private readonly Dictionary<string, string> _values = [];
        public string? Get(string key) => _values.GetValueOrDefault(key);

        public void Set(string key, string? value)
        {
            if (value is null) _values.Remove(key);
            else _values[key] = value;
        }

        public IReadOnlyDictionary<string, string> Values => _values;
    }

    private static BlindInvitationTarget Target(string trust = BlindTrust.Pin)
    {
        var (publicKey, seed) = Ed25519Signer.GenerateKeyPair();
        var invitation = BlindInvitation.CreateSigned("https://mem.example.org:5300", Guid.NewGuid(), trust, trust == BlindTrust.Pin ? new string('A', 43) : null, publicKey,
            new byte[16], new byte[32], DateTimeOffset.UtcNow.AddMinutes(30), "Alex", payload => Ed25519Signer.Sign(seed, payload));
        return BlindInvitationTarget.From(invitation);
    }

    // ---- what an account keeps of the invitation -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(BlindTrust.Pin)]
    [InlineData(BlindTrust.PublicCa)]
    public void WhatAnAccountKeepsOfTheInvitation_ComesBackAsItWas(string trust)
    {
        var target = Target(trust);
        var store = new MemoryStore();
        var kept = new BlindInvitationState(store);
        kept.Exists.Should().BeFalse();
        kept.Target.Should().BeNull();

        // What the hub says: the time of the redemption plus 72 hours, and a redemption is made before the invitation ends.
        var confirmBy = target.ExpiresAtUnix + 72 * 3600 - 600;
        kept.Save(target);
        kept.SetConfirmBy(confirmBy).Should().BeTrue();

        kept.Exists.Should().BeTrue();
        kept.ConfirmByUnix.Should().Be(confirmBy);
        var read = new BlindInvitationState(store).Target!;
        read.Address.Should().Be(target.Address);
        read.HubNodeId.Should().Be(target.HubNodeId);
        read.Trust.Should().Be(trust);
        read.SpkiPin.Should().Be(target.SpkiPin);
        read.HubPublicKey.Should().Equal(target.HubPublicKey);
        read.InviteId.Should().Equal(target.InviteId);
        read.ExpiresAtUnix.Should().Be(target.ExpiresAtUnix);
        read.SourceName.Should().Be("Alex");
        store.Values.Should().ContainSingle("it is one value, written whole: a crash leaves all of it or none");

        kept.Clear();
        kept.Exists.Should().BeFalse();
        kept.Target.Should().BeNull();
        store.Values.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"v\":2,\"a\":\"https://x.example\"}")]
    [InlineData("{\"v\":1,\"a\":\"https://x.example\",\"n\":\"nope\",\"t\":\"pin\",\"s\":\"\",\"k\":\"AA\",\"i\":\"AA\",\"e\":1,\"d\":\"x\"}")]
    public void ADamagedValue_IsNeverHalfUsed(string value)
    {
        var store = new MemoryStore();
        store.Set("bmb.blind.invite", value);

        new BlindInvitationState(store).Target.Should().BeNull();
        new BlindInvitationState(store).SetConfirmBy(1).Should().BeFalse();
    }

    [Fact]
    public void TheStateHoldsNoSecret_AndNothingThatCouldBeOne()
    {
        var store = new MemoryStore();
        new BlindInvitationState(store).Save(Target());

        store.Values.Values.Single().Should().NotContain("secret").And.NotContain("\"x\"");
    }

    // ---- the call code built from the invitation -------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePinnedCallCode_IsTheOneTheOldPairingMakes()
    {
        var target = Target();
        var secret = new byte[32];

        var code = target.CallCode(secret);

        code.Should().BeEquivalentTo(BlindCallCode.Create(target.Address, target.HubNodeId, target.SpkiPin, target.HubPublicKey, secret));
        code.Trust.Should().Be(BlindTrust.Pin);
        BlindCallCode.TryParse(code.ToString(), out var parsed).Should().BeTrue("it survives the state store's text form");
        parsed!.IsAuthenticBy(secret).Should().BeTrue();
        parsed.IsAuthenticBy(RandomNumberGeneratorBytes()).Should().BeFalse("another secret does not authenticate it");
    }

    [Fact]
    public void TheCallCodeForAHubOnAPublicCertificate_HasNoPin()
    {
        var target = Target(BlindTrust.PublicCa);

        var code = target.CallCode(new byte[32]);

        code.Trust.Should().Be(BlindTrust.PublicCa);
        code.SpkiPin.Should().BeEmpty();
        BlindCallCode.TryParse(code.ToString(), out var parsed).Should().BeTrue();
        parsed!.Trust.Should().Be(BlindTrust.PublicCa);
    }

    private static byte[] RandomNumberGeneratorBytes() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void TheHost_AndTheMatchCode_AreReadFromTheTarget()
    {
        var target = Target();
        var nodeId = BlindNodeId.NewId();
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();

        target.Host.Should().Be("mem.example.org");
        target.MatchCodeFor(nodeId, publicKey).Should().Be(BlindInviteMatchCode.Compute(target.InviteId, nodeId, publicKey));
    }

    // ---- the values the redemption sends ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("android", 16, "android")]
    [InlineData("2.7.0", 32, "2.7.0")]
    [InlineData("2.7.0-preview+abc_1", 32, "2.7.0-preview+abc_1")]
    [InlineData("2.7.0 (debug build)", 32, "2.7.0debugbuild")]
    [InlineData("ünïcode/ver", 32, "ncodever")]
    [InlineData("a-very-long-version-string-that-does-not-fit-anywhere", 16, "a-very-long-vers")]
    [InlineData(null, 16, "")]
    public void APlatformOrAVersion_IsSentInTheFormTheHubTakes(string? value, int max, string sent)
    {
        BlindInvitationClient.Token(value, max).Should().Be(sent);

        // what is sent is what the hub's strict reader takes: a request with exactly these values parses
        var (publicKey, seed) = Ed25519Signer.GenerateKeyPair();
        var asPlatform = max == BlindInviteRedeemRequest.MaxPlatformLength;
        var request = new BlindInviteRedeemRequest(new byte[16], new byte[32], BlindNodeId.NewId(), publicKey, "Test device",
            asPlatform ? sent : "android", asPlatform ? "2.7.0" : sent, 4, new byte[32], new byte[64]);
        BlindInviteRedeemRequest.TryParse(request.ToJson(), out _).Should().BeTrue("the hub takes it");
        _ = seed;
    }

    // ---- the screen's watch -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheWatch_AsksEveryFifteenSeconds_ForTenMinutes_ThenStops()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
        var forced = new List<bool>();
        await using var watch = new BlindInvitationWatch((force, _) =>
        {
            forced.Add(force);
            clock.Advance(BlindInvitationWatch.DefaultEvery);      // the time the real delay between two asks would be
            return Task.FromResult(1);
        }, clock, every: TimeSpan.FromMilliseconds(1));

        watch.Start();
        await EventuallyAsync(() => !watch.IsRunning, "the watch ends by itself");

        forced.Should().OnlyContain(f => f, "a screen asks for an answer now");
        forced.Count.Should().Be(40, "ten minutes in steps of fifteen seconds");
        BlindInvitationWatch.DefaultEvery.Should().Be(TimeSpan.FromSeconds(15));
        BlindInvitationWatch.DefaultFor.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task TheWatch_EndsWhenNothingWaitsAnyMore()
    {
        var asked = 0;
        await using var watch = new BlindInvitationWatch((_, _) => Task.FromResult(Interlocked.Increment(ref asked) < 3 ? 1 : 0), every: TimeSpan.FromMilliseconds(1));

        watch.Start();
        await EventuallyAsync(() => !watch.IsRunning, "the watch ends by itself");

        asked.Should().Be(3);
    }

    [Fact]
    public async Task TheWatch_StopsWhenItIsStopped_AndAFailedAskDoesNotEndIt()
    {
        var asked = 0;
        await using var watch = new BlindInvitationWatch((_, _) =>
        {
            if (Interlocked.Increment(ref asked) == 1) throw new IOException("the hub could not be reached");
            return Task.FromResult(1);
        }, every: TimeSpan.FromMilliseconds(1));

        watch.Start();
        await EventuallyAsync(() => Volatile.Read(ref asked) >= 3, "it asked again after the failure");
        await watch.StopAsync();

        watch.IsRunning.Should().BeFalse();
        var after = Volatile.Read(ref asked);
        await Task.Delay(30);
        Volatile.Read(ref asked).Should().Be(after, "a stopped watch does not ask");
    }

    [Fact]
    public async Task StartingTheWatchAgain_DoesNotMakeASecondLoop()
    {
        var concurrent = 0;
        var most = 0;
        await using var watch = new BlindInvitationWatch(async (_, ct) =>
        {
            most = Math.Max(most, Interlocked.Increment(ref concurrent));
            await Task.Delay(5, ct);
            Interlocked.Decrement(ref concurrent);
            return 1;
        }, every: TimeSpan.FromMilliseconds(1));

        watch.Start();
        watch.Start();
        watch.Start();
        await Task.Delay(100);
        await watch.StopAsync();

        most.Should().Be(1);
    }

    [Fact]
    public async Task StartingTheWatch_WhileAStopIsInFlight_WaitsForTheOldLoop_ThereIsNeverASecondAsk()
    {
        var concurrent = 0;
        var most = 0;
        var asks = 0;
        var entered = new SemaphoreSlim(0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var watch = new BlindInvitationWatch(async (_, _) =>
        {
            // The first ask is held and takes no notice of the cancellation (a socket whose bytes are already out): the stop is in flight until the test lets it go.
            var number = Interlocked.Increment(ref asks);
            most = Math.Max(most, Interlocked.Increment(ref concurrent));
            entered.Release();
            try { if (number == 1) await release.Task; else await Task.Delay(1); }
            finally { Interlocked.Decrement(ref concurrent); }
            return 1;
        }, every: TimeSpan.FromMilliseconds(1));
        watch.Start();
        (await entered.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue("the first ask was made");

        var stop = watch.StopAsync();
        watch.Start();   // the window is shown again before the old ask has ended
        await Task.Delay(150);

        stop.IsCompleted.Should().BeFalse("the stop waits for the ask that is in flight");
        watch.IsRunning.Should().BeTrue("the old loop is still there until it has ended");
        Volatile.Read(ref asks).Should().Be(1, "the new loop waits for the old one: no second ask beside it");
        most.Should().Be(1);
        release.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        (await entered.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue("the new loop asks once the old one has ended");
        most.Should().Be(1, "at no time were two asks in flight");
        watch.IsRunning.Should().BeTrue("the watch that was started after the stop goes on");

        await watch.StopAsync();
        watch.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task StoppingTheWatchTwice_WhileTheFirstStopIsInFlight_BothEnd_AndTheWatchCanBeStartedAfterwards()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new SemaphoreSlim(0);
        var held = true;
        var asks = 0;
        await using var watch = new BlindInvitationWatch(async (_, _) =>
        {
            Interlocked.Increment(ref asks);
            entered.Release();
            if (Volatile.Read(ref held)) await release.Task;
            return 1;
        }, every: TimeSpan.FromMilliseconds(1));
        watch.Start();
        (await entered.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        var first = watch.StopAsync();
        var second = watch.StopAsync();
        Volatile.Write(ref held, false);
        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        watch.IsRunning.Should().BeFalse();
        var before = Volatile.Read(ref asks);
        watch.Start();
        await EventuallyAsync(() => Volatile.Read(ref asks) > before, "a watch started after both stops asks again");
        await watch.StopAsync();
    }

    [Fact]
    public async Task ASecondStopThatComesLate_AfterTheFirstHasEndedTheLoopAndDisposedItsSource_EndsWithoutAnException()
    {
        // Two stops (the window hidden and the app exiting) both took the same source; the second is held between taking it and cancelling it until the first has ended the loop and disposed the source.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new SemaphoreSlim(0);
        var firstHasEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stops = 0;
        var watch = new BlindInvitationWatch(async (_, _) =>
        {
            entered.Release();
            await release.Task;     // takes no notice of the cancellation until the test lets it go: the first stop is in flight
            return 1;
        }, every: TimeSpan.FromMilliseconds(1));
        watch.BeforeCancel = () => Interlocked.Increment(ref stops) == 1 ? Task.CompletedTask : firstHasEnded.Task;
        watch.Start();
        (await entered.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        var first = watch.StopAsync();
        var second = watch.StopAsync();             // reads the same source, and waits in the hook
        release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        firstHasEnded.SetResult();                  // the second goes on to cancel a source that has been disposed
        var act = async () => await second.WaitAsync(TimeSpan.FromSeconds(10));

        await act.Should().NotThrowAsync("there is nothing left to cancel: the loop it would stop has ended");
        watch.IsRunning.Should().BeFalse();
        await watch.DisposeAsync();
    }

    private static async Task EventuallyAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(5);
        condition().Should().BeTrue(what);
    }
}
