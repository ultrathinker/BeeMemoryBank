using System.Diagnostics;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Rekey;

namespace BeeMemoryBank.Rekey.Tests;

/// <summary>
/// Review release-b R1-7 (sec#10): the re-key never follows a junction or a symbolic link out of D. A link under a
/// carried-over entry (or the entry itself) is not copied into the new vault and is named instead; deleting a leftover
/// copy never reaches through a link. Links are created as the platform allows: symbolic links everywhere, a junction
/// on Windows too. Where the account may not create them, the test has nothing to check and returns.
/// </summary>
public sealed class RekeyNoFollowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bmb-nofollow-" + Guid.NewGuid().ToString("N"));
    private readonly string _d, _new, _outside;

    public RekeyNoFollowTests()
    {
        _d = Path.Combine(_root, "vault");
        _new = RekeySwapJournal.NewDirFor(_d);
        _outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(Path.Combine(_d, "certs"));
        File.WriteAllText(Path.Combine(_d, "certs", "ca.pem"), "ca");
        Directory.CreateDirectory(_new);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "private.key"), "someone else's secret");
    }

    public void Dispose()
    {
        try { NoFollow.DeleteTree(_root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static bool TryLinkDir(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool TryLinkFile(string link, string target)
    {
        try { File.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private bool NewVaultHolds(string text) =>
        Directory.EnumerateFiles(_new, "*", SearchOption.AllDirectories).Any(f => File.ReadAllText(f) == text);

    [Fact]
    public void ALinkedDirectoryUnderACarriedOverEntry_IsNotCopied_AndIsNamed()
    {
        if (!TryLinkDir(Path.Combine(_d, "certs", "private"), _outside)) return;
        var skipped = new List<string>();

        RekeySwap.CarryOver(_d, _new, skipped);

        File.ReadAllText(Path.Combine(_new, "certs", "ca.pem")).Should().Be("ca", "regular files inside D are carried over");
        NewVaultHolds("someone else's secret").Should().BeFalse("nothing outside D comes in through a link");
        skipped.Should().Equal("certs/private");
    }

    [Fact]
    public void ALinkedFileUnderACarriedOverEntry_IsNotCopied()
    {
        if (!TryLinkFile(Path.Combine(_d, "certs", "key.pem"), Path.Combine(_outside, "private.key"))) return;
        var skipped = new List<string>();

        RekeySwap.CarryOver(_d, _new, skipped);

        File.Exists(Path.Combine(_new, "certs", "key.pem")).Should().BeFalse();
        skipped.Should().Equal("certs/key.pem");
    }

    [Fact]
    public void ACarriedOverEntryThatIsItselfALink_IsNotCopied()
    {
        if (!TryLinkDir(Path.Combine(_d, "tls"), _outside)) return;
        var skipped = new List<string>();

        RekeySwap.CarryOver(_d, _new, skipped);

        Directory.Exists(Path.Combine(_new, "tls")).Should().BeFalse();
        skipped.Should().Contain("tls");
    }

    /// <summary>A leftover D.rekey-new that is a link goes as a link: the directory it points at is untouched.</summary>
    [Fact]
    public void DeletingALinkedLeftover_NeverReachesItsTarget()
    {
        Directory.Delete(_new);
        if (!TryLinkDir(_new, _outside)) return;

        NoFollow.DeleteTree(_new);

        Directory.Exists(_new).Should().BeFalse();
        File.ReadAllText(Path.Combine(_outside, "private.key")).Should().Be("someone else's secret");
    }

    /// <summary>Windows junctions are reparse points too, and are skipped the same way.</summary>
    [Fact]
    public void AJunctionUnderACarriedOverEntry_IsNotCopied()
    {
        if (!OperatingSystem.IsWindows()) return;
        var junction = Path.Combine(_d, "certs", "junction");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{_outside}\"")
               { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
            mklink.WaitForExit();
        if (!Directory.Exists(junction)) return;
        var skipped = new List<string>();

        RekeySwap.CarryOver(_d, _new, skipped);

        NewVaultHolds("someone else's secret").Should().BeFalse();
        skipped.Should().Equal("certs/junction");
    }
}
