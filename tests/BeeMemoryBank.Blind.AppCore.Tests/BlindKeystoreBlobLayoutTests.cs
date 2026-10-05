using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>F-04: a truncated or damaged Keystore blob is an error with a sentence, not a slicing exception.</summary>
public sealed class BlindKeystoreBlobLayoutTests
{
    [Fact]
    public void JoinThenSplit_GivesTheIvAndTheCiphertextBack()
    {
        var iv = Enumerable.Range(1, 12).Select(i => (byte)i).ToArray();
        var cipher = Enumerable.Range(100, 48).Select(i => (byte)i).ToArray();

        var (gotIv, offset, length) = BlindKeystoreBlobLayout.Split(BlindKeystoreBlobLayout.Join(iv, cipher));

        gotIv.Should().Equal(iv);
        offset.Should().Be(12);
        length.Should().Be(48);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(27)]
    public void ABlobTooShortForAnIvAndATag_IsInvalidData_NotAnIndexOrRangeException(int size)
    {
        var act = () => BlindKeystoreBlobLayout.Split(new byte[size]);

        act.Should().Throw<InvalidDataException>().Which.Message.Should().Contain("damaged");
    }

    [Fact]
    public void TheSmallestBlob_AnEmptySecretWithItsTag_IsAccepted()
    {
        var (_, offset, length) = BlindKeystoreBlobLayout.Split(new byte[BlindKeystoreBlobLayout.IvLength + BlindKeystoreBlobLayout.TagLength]);

        (offset + length).Should().Be(28);
        length.Should().Be(16);
    }
}
