using System.Security.Cryptography;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Crypto.Tests;

public class RecoveryBoxCryptoTests
{
    [Fact]
    public void DevicePreset_RoundTrip()
    {
        var dek = MasterKeyManager.GenerateMasterDek();

        var seal = RecoveryBoxCrypto.Wrap(dek, "correct horse", RecoveryBoxKdf.Device64);
        var opened = RecoveryBoxCrypto.Unwrap("correct horse", seal.KdfPreset, seal.Salt, seal.Wrapped, seal.Iv);

        opened.Should().Equal(dek);
        seal.KdfPreset.Should().Be("d64t3");
    }

    [Fact]
    public void WrongPassword_DoesNotOpen()
    {
        var seal = RecoveryBoxCrypto.Wrap(MasterKeyManager.GenerateMasterDek(), "right", RecoveryBoxKdf.Device64);

        RecoveryBoxCrypto.TryUnwrap("wrong", seal.KdfPreset, seal.Salt, seal.Wrapped, seal.Iv).Should().BeNull();
    }

    [Fact]
    public void DeviceBox_IsAByteCopyOfAnOrdinarySlot()
    {
        // A device publishes its own key slot unchanged; a restore must open it as a box.
        var dek = MasterKeyManager.GenerateMasterDek();
        var salt = KeyDerivation.GenerateSalt();
        var kek = KeyDerivation.DeriveKek("slot password", salt);
        var (wrapped, iv) = MasterKeyManager.WrapMasterDek(dek, kek);

        RecoveryBoxCrypto.Unwrap("slot password", RecoveryBoxKdf.Device64, salt, wrapped, iv).Should().Equal(dek);
    }

    [Theory]
    [InlineData("s4096t10")]
    [InlineData("D64T3")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownPreset_IsRefusedBeforeDerivation(string? preset)
    {
        var act = () => RecoveryBoxCrypto.Unwrap("pw", preset!, new byte[32], new byte[49], new byte[12]);

        act.Should().Throw<CryptographicException>().WithMessage("*not an allowed*");
    }

    [Theory]
    [InlineData(31, 49, 12)]
    [InlineData(32, 50, 12)]
    [InlineData(32, 49, 16)]
    [InlineData(32, 0, 12)]
    public void MalformedMaterial_IsRefused(int salt, int wrapped, int iv)
    {
        var act = () => RecoveryBoxCrypto.Unwrap("pw", RecoveryBoxKdf.Device64, new byte[salt], new byte[wrapped], new byte[iv]);

        act.Should().Throw<CryptographicException>().WithMessage("*Malformed*");
    }

    [Fact]
    public void UnknownWrapperVersion_IsRefusedBeforeAnyDerivation()
    {
        // A strong preset outside the heavy queue would throw InvalidOperationException if a derivation
        // were attempted; the structural refusal must come first.
        var wrapped = new byte[49];
        wrapped[0] = 0x02;

        var act = () => RecoveryBoxCrypto.Unwrap("pw", RecoveryBoxKdf.Strong1024, new byte[32], wrapped, new byte[12]);

        act.Should().Throw<CryptographicException>().WithMessage("*Malformed*");
    }

    [Fact]
    public void StrongBox_MustUseTheVersionedWrap_DeviceBoxMayBeLegacy()
    {
        RecoveryBoxKdf.IsWellFormed("strong", new byte[32], new byte[48], new byte[12]).Should().BeFalse();
        RecoveryBoxKdf.IsWellFormed("device", new byte[32], new byte[48], new byte[12]).Should().BeTrue();
        var v1 = new byte[49];
        v1[0] = 0x01;
        RecoveryBoxKdf.IsWellFormed("strong", new byte[32], v1, new byte[12]).Should().BeTrue();
    }

    [Fact]
    public void Budget_CountsHeavyAndLightApart()
    {
        var budget = new RecoveryAttemptBudget(maxHeavy: 1, maxLight: 2);

        budget.TryTake(RecoveryBoxKdf.Resolve("s1024t4")).Should().BeTrue();
        budget.TryTake(RecoveryBoxKdf.Resolve("s512t6")).Should().BeFalse();
        budget.TryTake(RecoveryBoxKdf.Resolve("d64t3")).Should().BeTrue();
        budget.TryTake(RecoveryBoxKdf.Resolve("d64t3")).Should().BeTrue();
        // what production reads (RecoveryKeyResolver): both classes stood at their bound
        budget.AnyLimitReached.Should().BeTrue();
    }

    [Theory]
    [InlineData("device", "d64t3", true)]
    [InlineData("device", "s512t6", false)]
    [InlineData("device", "s1024t4", false)]
    [InlineData("strong", "s512t6", true)]
    [InlineData("strong", "s1024t4", true)]
    [InlineData("strong", "d64t3", false)]
    [InlineData("other", "d64t3", false)]
    [InlineData("strong", "s2048t4", false)]
    public void KindAndPreset_MustMatch(string kind, string preset, bool allowed)
    {
        RecoveryBoxKdf.IsAllowed(kind, preset).Should().Be(allowed);
    }

    [Fact]
    public void Presets_HaveTheContractParameters()
    {
        RecoveryBoxKdf.Resolve("d64t3").Should().Be(new RecoveryBoxPreset("d64t3", 65_536, 3, 4));
        RecoveryBoxKdf.Resolve("s512t6").Should().Be(new RecoveryBoxPreset("s512t6", 524_288, 6, 4));
        RecoveryBoxKdf.Resolve("s1024t4").Should().Be(new RecoveryBoxPreset("s1024t4", 1_048_576, 4, 4));
    }

    [Fact]
    public void ExistingSlotValidator_KeepsItsCap()
    {
        // The box validator is separate on purpose: a peer's key slot must still be refused above
        // 256 MiB even though a strong box is allowed 1 GiB.
        var act = () => KeyDerivation.ValidateUntrustedParameters(1_048_576, 4, 4);

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void PresetForSlot_OnlyTheDefaults()
    {
        RecoveryBoxKdf.PresetForSlot(65_536, 3, 4).Should().Be("d64t3");
        RecoveryBoxKdf.PresetForSlot(131_072, 3, 4).Should().BeNull();
        RecoveryBoxKdf.PresetForSlot(65_536, 2, 4).Should().BeNull();
    }

    [Theory]
    [InlineData("s512t6")]
    [InlineData("s1024t4")]
    public void HeavyPreset_OutsideTheQueue_IsRefused(string preset)
    {
        var act = () => RecoveryBoxCrypto.Wrap(MasterKeyManager.GenerateMasterDek(), "pw", preset);

        act.Should().Throw<InvalidOperationException>().WithMessage("*heavy-derivation queue*");
    }
}
