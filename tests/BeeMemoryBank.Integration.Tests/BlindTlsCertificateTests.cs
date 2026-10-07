using BeeMemoryBank.Core.IO;
using System.Security.Cryptography;
using BeeMemoryBank.Api.Services;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The blind node's TLS certificate file (review release-a #8, PFX half): written whole or not at all, never
/// silently replaced, and a damaged one is named in a clear error instead of a raw CryptographicException.
/// </summary>
public class BlindTlsCertificateTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), $"bmb_blindtls_{Guid.NewGuid():N}");

    public BlindTlsCertificateTests() => Directory.CreateDirectory(_data);

    public void Dispose()
    {
        try { Directory.Delete(_data, recursive: true); } catch { /* best-effort */ }
    }

    private string TlsDir => Path.GetDirectoryName(BlindTlsCertificate.PathIn(_data))!;

    [Fact]
    public void LoadOrCreate_CreatesOnce_AndTheNextStartGetsTheSameKey()
    {
        using var first = BlindTlsCertificate.LoadOrCreate(_data);
        using var second = BlindTlsCertificate.LoadOrCreate(_data);

        first.HasPrivateKey.Should().BeTrue();
        second.HasPrivateKey.Should().BeTrue();
        second.Thumbprint.Should().Be(first.Thumbprint);
        second.PublicKey.ExportSubjectPublicKeyInfo().Should().Equal(first.PublicKey.ExportSubjectPublicKeyInfo());
        Directory.GetFiles(TlsDir).Should().ContainSingle().Which.Should().Be(BlindTlsCertificate.PathIn(_data));
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(BlindTlsCertificate.PathIn(_data)).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void AStartThatDiesBeforeTheRename_LeavesNoFileUnderTheFinalName_AndNoTemp()
    {
        string? seenTemp = null;
        var act = () => BlindTlsCertificate.LoadOrCreate(_data, beforeRename: temp =>
        {
            seenTemp = temp;
            File.Exists(temp).Should().BeTrue();
            File.Exists(BlindTlsCertificate.PathIn(_data)).Should().BeFalse("the final name must not exist before the rename");
            throw new IOException("simulated crash between the temp write and the rename");
        });

        act.Should().Throw<IOException>().WithMessage("simulated crash*");
        seenTemp.Should().NotBeNull();
        File.Exists(BlindTlsCertificate.PathIn(_data)).Should().BeFalse();
        Directory.GetFiles(TlsDir).Should().BeEmpty("the failed write's temp file is removed");

        using var created = BlindTlsCertificate.LoadOrCreate(_data);
        created.HasPrivateKey.Should().BeTrue("the next start simply creates the certificate");
    }

    [Fact]
    public void ATruncatedCertificate_IsNamedInAClearError_AndIsNotReplaced()
    {
        using (BlindTlsCertificate.LoadOrCreate(_data)) { }
        var path = BlindTlsCertificate.PathIn(_data);
        var truncated = File.ReadAllBytes(path)[..40];
        File.WriteAllBytes(path, truncated);

        var act = () => BlindTlsCertificate.LoadOrCreate(_data);

        act.Should().Throw<InvalidDataException>()
            .WithMessage($"*{path}*identity pin*")
            .WithInnerException<CryptographicException>();
        File.ReadAllBytes(path).Should().Equal(truncated, "a damaged certificate is never silently replaced with a new key");
    }

    [Fact]
    public void ACertificateCreatedByAnotherStartMeanwhile_IsTheOneUsed()
    {
        // Two starts at once: the second one's rename finds the first one's file there, and takes that one
        // instead of failing (or replacing it).
        string? winnerThumbprint = null;
        using var loser = BlindTlsCertificate.LoadOrCreate(_data, beforeRename: _ =>
        {
            using var winner = BlindTlsCertificate.LoadOrCreate(_data);
            winnerThumbprint = winner.Thumbprint;
        });

        loser.Thumbprint.Should().Be(winnerThumbprint);
        Directory.GetFiles(TlsDir).Should().ContainSingle();
    }

    [Fact]
    public void ALinkAtTheCertificatePath_IsRefused_AndNothingIsWrittenThroughIt()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"bmb_blindtls_outside_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(TlsDir);
        var path = BlindTlsCertificate.PathIn(_data);
        try
        {
            if (!TryCreateDirectoryLink(path, outside)) return; // no link, no test

            var act = () => BlindTlsCertificate.LoadOrCreate(_data);

            act.Should().Throw<InvalidOperationException>().WithMessage("*symlink or junction*");
            Directory.GetFileSystemEntries(outside).Should().BeEmpty("nothing is written through the link");
            Directory.GetFiles(TlsDir).Should().BeEmpty("no temp file is left behind");
        }
        finally
        {
            try { if (Directory.Exists(path)) Directory.Delete(path); } catch { /* the test's own link */ }
            try { Directory.Delete(outside, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>A junction on Windows (no privilege needed), a symbolic link elsewhere; false when refused.</summary>
    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var cmd = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "cmd", $"/c mklink /J \"{link}\" \"{target}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                })!;
                cmd.WaitForExit();
                return cmd.ExitCode == 0 && Directory.Exists(link);
            }
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
