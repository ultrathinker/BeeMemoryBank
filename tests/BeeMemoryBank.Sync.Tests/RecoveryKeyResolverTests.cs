using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Recovery;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>The key side of a restore: boxes, chain unwinding and the choice of the current key.</summary>
public class RecoveryKeyResolverTests
{
    private const string Password = "MasterPass1";
    private readonly byte[] _dek1 = MasterKeyManager.GenerateMasterDek();
    private readonly byte[] _dek2 = MasterKeyManager.GenerateMasterDek();
    private readonly byte[] _dek3 = MasterKeyManager.GenerateMasterDek();

    private static RecoverySetBox DeviceBox(byte[] dek, string password, long epoch, string? claimedFp = null, string preset = "d64t3", string kind = "device")
    {
        var seal = RecoveryBoxCrypto.Wrap(dek, password, RecoveryBoxKdf.Device64);
        return new RecoverySetBox(Guid.NewGuid().ToString(), kind, Guid.NewGuid().ToString(), claimedFp ?? DekFingerprint.Of(dek),
            epoch, preset, Convert.ToBase64String(seal.Salt), Convert.ToBase64String(seal.Wrapped), Convert.ToBase64String(seal.Iv),
            DateTime.UtcNow.ToString("O"), epoch, null);
    }

    private static RecoverySetLink Link(byte[] older, byte[] newer, byte[]? wrapUnder = null)
    {
        var (wrapped, iv) = MasterKeyManager.WrapMasterDek(older, wrapUnder ?? newer);
        return new RecoverySetLink(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), DekFingerprint.Of(older), DekFingerprint.Of(newer),
            Convert.ToBase64String(wrapped), Convert.ToBase64String(iv), DateTime.UtcNow.ToString("O"));
    }

    private static RecoverySetAnchor Anchor(byte[] dek, string createdAt, bool forge = false)
    {
        var vector = new Dictionary<string, long> { ["AAAAAAAA-0000-0000-0000-000000000001"] = 1 };
        var fp = DekFingerprint.Of(dek);
        var id = Guid.NewGuid().ToString();
        var hmac = forge ? new string('0', 64) : StateAnchorCrypto.ComputeHmac(dek, id, fp, vector, new string('d', 64), createdAt);
        return new RecoverySetAnchor(id, Guid.NewGuid().ToString(), fp, vector, new string('d', 64), hmac, createdAt, 1);
    }

    private static RecoverySet Set(IEnumerable<RecoverySetBox> boxes, IEnumerable<RecoverySetLink>? links = null, IEnumerable<RecoverySetAnchor>? anchors = null) =>
        new(RecoverySet.FormatV1, boxes.ToList(), (links ?? []).ToList(), (anchors ?? []).ToList(), [], DateTime.UtcNow.ToString("O"));

    [Fact]
    public async Task NewestBox_ThenTheChainDownwards_OpensEveryKey()
    {
        var set = Set([DeviceBox(_dek3, Password, 3)], [Link(_dek2, _dek3), Link(_dek1, _dek2)]);

        using var keys = (await RecoveryKeyResolver.ResolveAsync(set, Password))!;

        keys.Current.Should().Equal(_dek3);
        keys.Retired.Values.Should().BeEquivalentTo([_dek1, _dek2]);
    }

    [Fact]
    public async Task WrongPassword_OpensNothing()
    {
        (await RecoveryKeyResolver.ResolveAsync(Set([DeviceBox(_dek3, Password, 3)]), "Other1234")).Should().BeNull();
    }

    [Fact]
    public async Task ForgedLink_WithTheRightFingerprints_IsIgnored()
    {
        // Wrapped under the wrong key: it claims dek1 → dek2 but opens to nothing under dek2.
        var forged = Link(_dek1, _dek2, wrapUnder: MasterKeyManager.GenerateMasterDek());
        var set = Set([DeviceBox(_dek2, Password, 2)], [forged]);

        using var keys = (await RecoveryKeyResolver.ResolveAsync(set, Password))!;

        keys.Retired.Should().BeEmpty();
    }

    [Fact]
    public async Task LinkThatOpensToAnotherKeyThanItClaims_IsIgnored()
    {
        // Correctly wrapped under dek2, but what is inside is not dek1: a peer that knew dek2 planting
        // a key of its own choosing as "the old one".
        var planted = MasterKeyManager.GenerateMasterDek();
        var (wrapped, iv) = MasterKeyManager.WrapMasterDek(planted, _dek2);
        var lying = new RecoverySetLink(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), DekFingerprint.Of(_dek1),
            DekFingerprint.Of(_dek2), Convert.ToBase64String(wrapped), Convert.ToBase64String(iv), DateTime.UtcNow.ToString("O"));

        using var keys = (await RecoveryKeyResolver.ResolveAsync(Set([DeviceBox(_dek2, Password, 2)], [lying]), Password))!;

        keys.Retired.Should().BeEmpty();
    }

    [Fact]
    public async Task BoxThatOpensToAnotherKeyThanItClaims_IsIgnored()
    {
        var lying = DeviceBox(_dek1, Password, 9, claimedFp: DekFingerprint.Of(_dek3));

        (await RecoveryKeyResolver.ResolveAsync(Set([lying]), Password)).Should().BeNull();
    }

    [Fact]
    public async Task BoxWithAPresetOutsideTheList_IsNeverDerived()
    {
        // A crafted box naming a preset the validator does not know would otherwise make the restoring
        // PC commit to whatever it asks for (here: throw, since the preset cannot even be resolved).
        var crafted = DeviceBox(_dek3, Password, 3, preset: "s4096t8", kind: "strong");
        var honest = DeviceBox(_dek2, Password, 2);

        using var keys = (await RecoveryKeyResolver.ResolveAsync(Set([crafted, honest]), Password))!;

        keys.Current.Should().Equal(_dek2);
    }

    [Fact]
    public async Task PasswordOpensOldAndNewBoxes_TheLinkProvesTheNewOneIsTheHead()
    {
        // The old box even claims the higher epoch: epochs are hints, the link is proof.
        var set = Set([DeviceBox(_dek1, Password, 9), DeviceBox(_dek2, Password, 2)], [Link(_dek1, _dek2)]);

        using var keys = (await RecoveryKeyResolver.ResolveAsync(set, Password))!;

        keys.HeadProven.Should().BeTrue();
        keys.Current.Should().Equal(_dek2);
    }

    [Fact]
    public async Task DroppedLink_LeavesTheHeadUnproven_WhateverTheAnchorsSay()
    {
        // The package kept the old box and a genuine old anchor and removed the link to the new key.
        var set = Set([DeviceBox(_dek1, Password, 9), DeviceBox(_dek2, Password, 2)], anchors: [Anchor(_dek1, "2026-09-27T10:00:00.0000000Z")]);

        using var keys = (await RecoveryKeyResolver.ResolveAsync(set, Password))!;

        keys.HeadProven.Should().BeFalse();
        keys.Heads.Should().HaveCount(2);
    }

    [Fact]
    public async Task AnchorUnderAnOlderKey_DoesNotChooseTheKey()
    {
        var set = Set([DeviceBox(_dek1, Password, 1), DeviceBox(_dek2, Password, 2)], [Link(_dek1, _dek2)],
            [Anchor(_dek1, "2099-01-01T00:00:00.0000000Z")]);

        using var keys = (await RecoveryKeyResolver.ResolveAsync(set, Password))!;

        keys.Current.Should().Equal(_dek2);
        keys.HeadProven.Should().BeTrue();
    }

    private static RecoverySetBox JunkBox(string kind, string preset, long epoch, byte version = 0x01)
    {
        var wrapped = SecureRandom.GetBytes(49);
        wrapped[0] = version;
        return new RecoverySetBox(Guid.NewGuid().ToString(), kind, Guid.NewGuid().ToString(),
            DekFingerprint.Of(MasterKeyManager.GenerateMasterDek()), epoch, preset,
            Convert.ToBase64String(SecureRandom.GetBytes(32)), Convert.ToBase64String(wrapped),
            Convert.ToBase64String(SecureRandom.GetBytes(12)), DateTime.UtcNow.ToString("O"), epoch, null);
    }

    [Fact]
    public async Task StrongBoxWithAnUnknownWrapperVersion_CostsNoDerivation()
    {
        var budget = new RecoveryAttemptBudget();
        var set = Set([JunkBox("strong", "s1024t4", 9, version: 0x02), DeviceBox(_dek2, Password, 2)]);

        using var keys = (await RecoveryKeyResolver.ResolveAsync(set, Password, budget: budget))!;

        keys.Current.Should().Equal(_dek2);
        budget.HeavyUsed.Should().Be(0);
    }

    [Fact]
    public async Task ManyJunkBoxes_StopAtTheBudget()
    {
        var budget = new RecoveryAttemptBudget(maxHeavy: 1, maxLight: 3);
        var set = Set([
            JunkBox("strong", "s512t6", 9), JunkBox("strong", "s512t6", 8),
            .. Enumerable.Range(0, 6).Select(i => JunkBox("device", "d64t3", 7))]);

        var act = () => RecoveryKeyResolver.ResolveAsync(set, Password, budget: budget);

        (await act.Should().ThrowAsync<RecoveryBoxesRemainingException>()).Which.Remaining.Should().Be(4, "1 strong and 3 device boxes left untried");
        budget.HeavyUsed.Should().Be(1);
        budget.LightUsed.Should().Be(3);
    }

    [Fact]
    public async Task FakeAnchorAndJunkBoxesForItsKey_CannotExhaustTheBudgetBeforeTheRealBox()
    {
        // A crafted set: a future-dated fake anchor naming a fake key, plenty of valid-shape junk boxes
        // claiming that key with high epochs, and the real box last.
        var fakeKey = MasterKeyManager.GenerateMasterDek();
        var fakeFp = DekFingerprint.Of(fakeKey);
        var junk = Enumerable.Range(0, 6).Select(_ => JunkBox("device", "d64t3", 99) with { DekFingerprint = fakeFp }).ToList();
        var budget = new RecoveryAttemptBudget(maxHeavy: 0, maxLight: 3, maxPerKey: 2);
        var set = Set([.. junk, DeviceBox(_dek2, Password, 1)], anchors: [Anchor(fakeKey, "2099-01-01T00:00:00.0000000Z")]);

        using var keys = (await RecoveryKeyResolver.ResolveAsync(set, Password, budget: budget))!;

        keys.Should().NotBeNull();
        keys.Current.Should().Equal(_dek2);
        budget.LightUsed.Should().BeLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task JunkForOneKey_CostsAtMostThePerKeyCap()
    {
        var fakeFp = DekFingerprint.Of(MasterKeyManager.GenerateMasterDek());
        var budget = new RecoveryAttemptBudget(maxHeavy: 0, maxLight: 10, maxPerKey: 2);
        var set = Set(Enumerable.Range(0, 6).Select(_ => JunkBox("device", "d64t3", 5) with { DekFingerprint = fakeFp }));

        var act = () => RecoveryKeyResolver.ResolveAsync(set, Password, budget: budget);

        (await act.Should().ThrowAsync<RecoveryBoxesRemainingException>()).Which.Remaining.Should().Be(4);
        budget.LightUsed.Should().Be(2);
    }

    // Box ids sort the junk before the genuine box within a key.
    private static RecoverySetBox Early(RecoverySetBox box, int i) => box with { BoxId = $"00000000-0000-0000-0000-{i:D12}" };
    private static RecoverySetBox Late(RecoverySetBox box) => box with { BoxId = "ffffffff-ffff-ffff-ffff-ffffffffffff" };

    [Fact]
    public async Task JunkUnderTheGenuineFingerprint_LeavesTheNewestBoxUntried_SoTheOldKeyIsNotAProvenHead()
    {
        var newFp = DekFingerprint.Of(_dek2);
        var set = Set([
            DeviceBox(_dek1, Password, 1),
            .. Enumerable.Range(0, RecoveryAttemptBudget.DefaultPerKey).Select(i => Early(JunkBox("device", "d64t3", 2) with { DekFingerprint = newFp }, i)),
            Late(DeviceBox(_dek2, Password, 2))]);

        using var keys = (await RecoveryKeyResolver.ResolveAsync(set, Password))!;

        keys.Current.Should().Equal(_dek1, "the genuine newest box was never tried");
        keys.RemainingBoxes.Should().Be(1);
        keys.HeadProven.Should().BeFalse();
        using var all = (await RecoveryKeyResolver.ResolveAsync(set, Password, budget: RecoveryAttemptBudget.Unlimited()))!;
        all.Current.Should().Equal(_dek2, "trying the remaining boxes finds it");
    }

    [Fact]
    public async Task ManyFakeStrongFingerprints_ExhaustTheBudget_SoTheOldKeyIsNotAProvenHead()
    {
        var seal = await HeavyDerivationQueue.RunAsync(() => RecoveryBoxCrypto.Wrap(_dek2, Password, RecoveryBoxKdf.Strong512));
        var realStrong = new RecoverySetBox(Guid.NewGuid().ToString(), "strong", Guid.NewGuid().ToString(), DekFingerprint.Of(_dek2), 2, "s512t6",
            Convert.ToBase64String(seal.Salt), Convert.ToBase64String(seal.Wrapped), Convert.ToBase64String(seal.Iv), DateTime.UtcNow.ToString("O"), 2, null);
        var fakes = Enumerable.Range(1, 2).Select(i => JunkBox("strong", "s512t6", 9) with { DekFingerprint = new string('0', 63) + i });
        var budget = new RecoveryAttemptBudget(maxHeavy: 2);

        using var keys = (await RecoveryKeyResolver.ResolveAsync(Set([.. fakes, realStrong, DeviceBox(_dek1, Password, 1)]), Password, budget: budget))!;

        keys.Current.Should().Equal(_dek1);
        keys.RemainingBoxes.Should().Be(1);
        keys.HeadProven.Should().BeFalse();
    }

    [Fact]
    public async Task ExactDuplicates_AreTriedOnce_AndLeaveNothingUntried()
    {
        var junk = JunkBox("device", "d64t3", 1);
        var budget = new RecoveryAttemptBudget(maxHeavy: 0, maxLight: 10);

        (await RecoveryKeyResolver.ResolveAsync(Set([junk, junk with { BoxId = Guid.NewGuid().ToString() }, junk]), Password, budget: budget))
            .Should().BeNull("every box was tried: the password opens none");

        budget.LightUsed.Should().Be(1);
    }

    [Fact]
    public async Task ACopyWithTheSameCiphertextButAnotherSalt_IsNotDroppedAsADuplicate()
    {
        var real = Late(DeviceBox(_dek2, Password, 2));
        var forged = Early(real with { Salt = Convert.ToBase64String(SecureRandom.GetBytes(32)) }, 1);

        using var keys = (await RecoveryKeyResolver.ResolveAsync(Set([forged, real]), Password))!;

        keys.Current.Should().Equal(_dek2);
        keys.HeadProven.Should().BeTrue();
    }

    [Fact]
    public async Task CancelDuringAHeavyDerivation_ReturnsAtOnce_AndStartsNoFurtherAttempt()
    {
        var budget = new RecoveryAttemptBudget(maxHeavy: 6, maxLight: 0);
        var set = Set([JunkBox("strong", "s1024t4", 1), JunkBox("strong", "s1024t4", 1), JunkBox("strong", "s1024t4", 1)]);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var act = () => RecoveryKeyResolver.ResolveAsync(set, Password, cts.Token, budget);

        await act.Should().ThrowAsync<OperationCanceledException>();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "cancelling must not wait for the running derivation");
        budget.HeavyUsed.Should().Be(1, "no further attempt starts after cancellation");
    }

    [Fact]
    public async Task CancelAfterAKeyWasOpened_WipesThatKey()
    {
        // The real key opens first (strong boxes go first), then the next attempt is cancelled.
        var seal = await HeavyDerivationQueue.RunAsync(() => RecoveryBoxCrypto.Wrap(_dek2, Password, RecoveryBoxKdf.Strong512));
        var real = new RecoverySetBox(Guid.NewGuid().ToString(), "strong", Guid.NewGuid().ToString(), DekFingerprint.Of(_dek2), 2, "s512t6",
            Convert.ToBase64String(seal.Salt), Convert.ToBase64String(seal.Wrapped), Convert.ToBase64String(seal.Iv), DateTime.UtcNow.ToString("O"), 2, null);
        using var cts = new CancellationTokenSource();
        var opened = new List<byte[]>();
        RecoveryKeyResolver.KeyOpened.Value = key => { opened.Add(key); cts.Cancel(); };

        var act = () => RecoveryKeyResolver.ResolveAsync(Set([real, JunkBox("device", "d64t3", 1)]), Password, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        opened.Should().ContainSingle();
        opened[0].Should().OnlyContain(b => b == 0, "a key opened before the cancellation must not outlive it");
    }

    [Fact]
    public async Task Cancellation_StopsTheResolve()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => RecoveryKeyResolver.ResolveAsync(Set([DeviceBox(_dek2, Password, 2)]), Password, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("{\"format\":\"other\",\"boxes\":[],\"links\":[],\"anchors\":[],\"sealed_secrets\":[],\"created_at\":\"x\"}")]
    [InlineData("{\"format\":\"bmb-recovery-set-v1\",\"links\":[],\"anchors\":[],\"sealed_secrets\":[],\"created_at\":\"x\"}")]
    public void Parse_RejectsWhatIsNotAV1Set(string json)
    {
        var act = () => RecoverySet.Parse(json);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Parse_RejectsAnOversizedSet()
    {
        var boxes = string.Join(",", Enumerable.Repeat("{}", 1025));
        var act = () => RecoverySet.Parse($"{{\"format\":\"bmb-recovery-set-v1\",\"boxes\":[{boxes}],\"links\":[],\"anchors\":[],\"sealed_secrets\":[],\"created_at\":\"x\"}}");

        act.Should().Throw<InvalidDataException>();
    }
}
