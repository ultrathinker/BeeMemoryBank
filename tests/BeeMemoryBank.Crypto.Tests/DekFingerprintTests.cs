using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Crypto.Tests;

public class DekFingerprintTests
{
    [Fact]
    public void Of_MatchesContractFormula()
    {
        // CONTRACTS §2: lowercase hex(SHA256(UTF8("bmb-dek-verify") || DEK)). Every node and the
        // blind node's recovery material depend on this exact byte layout.
        var dek = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var expected = Convert.ToHexStringLower(
            SHA256.HashData([.. Encoding.UTF8.GetBytes("bmb-dek-verify"), .. dek]));

        DekFingerprint.Of(dek).Should().Be(expected);
    }

    [Fact]
    public void Of_KnownVector()
    {
        // Pinned so an accidental change to the prefix or encoding breaks here, not at a restore.
        var dek = new byte[32];

        DekFingerprint.Of(dek).Should().Be(
            "3bfcd8c36903d234ab2eccd02ed0ac28c6eded5ba0859cd60e31f142e96b6b66");
    }

    [Fact]
    public void Of_IsLowercaseHex64()
    {
        var fp = DekFingerprint.Of(SecureRandom.GetBytes(32));

        fp.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Of_DiffersFromPlainSha256()
    {
        var dek = SecureRandom.GetBytes(32);

        DekFingerprint.Of(dek).Should().NotBe(Convert.ToHexStringLower(SHA256.HashData(dek)));
    }

    [Fact]
    public void Of_DifferentKeys_DifferentFingerprints()
    {
        DekFingerprint.Of(SecureRandom.GetBytes(32)).Should().NotBe(DekFingerprint.Of(SecureRandom.GetBytes(32)));
    }

    [Fact]
    public void Of_DoesNotModifyInput()
    {
        var dek = SecureRandom.GetBytes(32);
        var copy = (byte[])dek.Clone();

        DekFingerprint.Of(dek);

        dek.Should().Equal(copy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    public void Of_WrongLength_Throws(int length)
    {
        var act = () => DekFingerprint.Of(new byte[length]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Of_Null_Throws()
    {
        var act = () => DekFingerprint.Of(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
