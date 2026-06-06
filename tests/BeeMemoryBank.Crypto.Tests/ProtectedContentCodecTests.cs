using System.Security.Cryptography;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Crypto.Tests;

public class ProtectedContentCodecTests
{
    [Fact]
    public void WrapUnwrap_Roundtrip()
    {
        var plaintext = "my super secret password: hunter2 — Привет, мир!";
        var wrapped = ProtectedContentCodec.Wrap(plaintext, "correct horse");

        ProtectedContentCodec.IsProtected(wrapped).Should().BeTrue();
        wrapped.Should().StartWith(ProtectedContentCodec.Prefix);

        var unwrapped = ProtectedContentCodec.Unwrap(wrapped, "correct horse");
        unwrapped.Should().Be(plaintext);
    }

    [Fact]
    public void Unwrap_WrongPassphrase_Throws()
    {
        var wrapped = ProtectedContentCodec.Wrap("secret", "right");
        var act = () => ProtectedContentCodec.Unwrap(wrapped, "wrong");
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Wrap_DifferentSaltAndCiphertextEachTime()
    {
        var a = ProtectedContentCodec.Wrap("same", "pass");
        var b = ProtectedContentCodec.Wrap("same", "pass");
        a.Should().NotBe(b); // random salt + IV per wrap
    }

    [Fact]
    public void IsProtected_PlainText_False()
    {
        ProtectedContentCodec.IsProtected("just normal markdown").Should().BeFalse();
        ProtectedContentCodec.IsProtected(null).Should().BeFalse();
        ProtectedContentCodec.IsProtected("").Should().BeFalse();
    }

    [Fact]
    public void IsProtected_PrefixButNotARealBlob_False()
    {
        // A user whose markdown merely starts with the sentinel must NOT be mis-flagged as protected.
        ProtectedContentCodec.IsProtected("BMBENC1: my notes on the BMBENC1 format").Should().BeFalse();
        ProtectedContentCodec.IsProtected("BMBENC1:not-valid-base64!!!").Should().BeFalse();
        ProtectedContentCodec.IsProtected("BMBENC1:aGVsbG8=").Should().BeFalse(); // valid base64, wrong structure
    }

    [Fact]
    public void Unwrap_NonProtected_Throws()
    {
        var act = () => ProtectedContentCodec.Unwrap("not a blob", "pass");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Wrap_EmptyPassphrase_Throws()
    {
        var act = () => ProtectedContentCodec.Wrap("x", "");
        act.Should().Throw<ArgumentException>();
    }
}
