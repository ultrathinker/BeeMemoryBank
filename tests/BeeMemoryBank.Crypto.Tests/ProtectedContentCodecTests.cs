using System.Buffers.Binary;
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

    // Security regression tests for finding M4: the memory/iterations/parallelism header fields
    // come from inside the blob, which is attacker-controlled — any writer (a folder-restricted
    // agent included) can save an article whose body is a hand-crafted "BMBENC1:" blob. Without
    // bounds, a value like memory = int.MaxValue asks Argon2id to allocate multiple terabytes the
    // instant a human later enters the correct passphrase. These mirror the exact bounds
    // SessionService.UnlockCoreAsync already enforces on key-slot KDF params.
    [Theory]
    [InlineData(int.MaxValue, 3, 4)]       // absurd memory
    [InlineData(65536, 999, 4)]            // absurd iterations
    [InlineData(65536, 3, 999)]            // absurd parallelism
    public void Unwrap_UnreasonablyLargeArgonParams_ThrowsWithoutAttemptingDerivation(
        int memory, int iterations, int parallelism)
    {
        var blob = BuildBlobWithArgonParams(memory, iterations, parallelism);

        var act = () => ProtectedContentCodec.Unwrap(blob, "whatever");

        // Throwing itself is the real assertion (a slow/OOM-inducing Argon2id call means the
        // bounds check didn't run first); the message additionally confirms it's OUR guard,
        // not some unrelated failure further down.
        act.Should().Throw<CryptographicException>().WithMessage("*unreasonable*");
    }

    [Fact]
    public void Unwrap_BelowMinimumArgonParams_Throws()
    {
        var blob = BuildBlobWithArgonParams(memory: 1024, iterations: 3, parallelism: 4);

        var act = () => ProtectedContentCodec.Unwrap(blob, "whatever");

        act.Should().Throw<CryptographicException>().WithMessage("*weakened*");
    }

    [Fact]
    public void Unwrap_ArgonParamsAtDefault_DoesNotThrowFromBoundsCheck()
    {
        // Sanity check that the new bounds don't accidentally reject the codec's own normal
        // output — WrapUnwrap_Roundtrip already covers the full happy path, but this isolates
        // the bounds check specifically against the real defaults used by Wrap().
        var blob = BuildBlobWithArgonParams(
            CryptoConstants.DefaultArgonMemory, CryptoConstants.DefaultArgonIterations, CryptoConstants.DefaultArgonParallelism);

        // Wrong passphrase, so it still throws — but from the GCM tag mismatch, not our bounds
        // check, proving the default params pass the guard.
        var act = () => ProtectedContentCodec.Unwrap(blob, "whatever");
        act.Should().Throw<CryptographicException>()
            .Which.Message.Should().NotContain("Argon", "default params must pass the bounds check cleanly");
    }

    /// <summary>
    /// Hand-builds a structurally-valid "BMBENC1:" blob (matching the format documented on
    /// <see cref="ProtectedContentCodec"/>) with attacker-chosen KDF params and a filler salt/iv/
    /// tag, bypassing <see cref="ProtectedContentCodec.Wrap"/> (which always uses safe defaults)
    /// so the bounds check under test can be exercised directly.
    /// </summary>
    private static string BuildBlobWithArgonParams(int memory, int iterations, int parallelism)
    {
        var salt = new byte[16];
        var iv = new byte[12];
        var tagSizedFiller = new byte[16]; // never decrypted — the bounds check throws first

        using var ms = new MemoryStream();
        ms.WriteByte(0x01); // format version
        WriteInt32(ms, memory);
        WriteInt32(ms, iterations);
        WriteInt32(ms, parallelism);
        ms.WriteByte((byte)salt.Length);
        ms.Write(salt);
        ms.WriteByte((byte)iv.Length);
        ms.Write(iv);
        ms.Write(tagSizedFiller);

        return ProtectedContentCodec.Prefix + Convert.ToBase64String(ms.ToArray());
    }

    private static void WriteInt32(Stream s, int value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, value);
        s.Write(buf);
    }
}
