using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.TestSupport;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// The fingerprint of a folder that holds a live SQLite database: its write-ahead file is listed, and the last connection that closes (any test's ClearAllPools, the pool of this process is one for
/// all of them) deletes it before the fingerprint has opened it. That is not a change of the data, and must not be an error of the check.
/// </summary>
public sealed class AccountRigFingerprintTests
{
    [Fact]
    public void AWriteAheadFileThatTheLastConnectionDeletesAfterItWasListed_IsLeftOut_NotAnError()
    {
        using var rig = new AccountRig();
        var db = Path.Combine(rig.Root, "kept.db");
        // SQLite removes a WAL when its last pooled connection closes. Model that transient sidecar directly: depending on an unrelated test not clearing
        // the process-wide SQLite pool first would make this fixture flaky itself.
        File.WriteAllText(db + "-wal", "transient");
        File.WriteAllText(Path.Combine(rig.Root, "data.txt"), "data");

        var walGone = false;
        var fingerprint = AccountRig.Fingerprint(rig.Root, beforeOpen: file =>
        {
            if (!file.EndsWith("-wal", StringComparison.Ordinal)) return;
            File.Delete(file);
            walGone = !File.Exists(file);
        });

        walGone.Should().BeTrue("the sidecar was gone between the listing and the open");
        fingerprint.Keys.Should().Contain("data.txt").And.NotContain("kept.db-wal");
    }

    [Fact]
    public void AFileThatIsGoneByTheTimeItIsOpened_IsLeftOut_AsIfTheFolderHadBeenListedAMomentLater()
    {
        using var rig = new AccountRig();
        File.WriteAllText(Path.Combine(rig.Root, "stays.txt"), "a");
        File.WriteAllText(Path.Combine(rig.Root, "goes.txt"), "b");

        var fingerprint = AccountRig.Fingerprint(rig.Root, beforeOpen: file =>
        {
            if (Path.GetFileName(file) == "goes.txt") File.Delete(file);
        });

        fingerprint.Keys.Should().Equal("stays.txt");
    }

    [WindowsOnlyFact]
    [SupportedOSPlatform("windows")]
    public void AFileThatAnswersAccessDeniedByTheTimeItIsOpened_IsLeftOut_AsIfTheFolderHadBeenListedAMomentLater()
    {
        using var rig = new AccountRig();
        File.WriteAllText(Path.Combine(rig.Root, "kept.db-wal"), "transient");

        // The race under load: a pool-clear's last close deletes the write-ahead file between the listing and the open, while a connection opened moments before still holds it —
        // the open then answers "access denied" because the deletion is under way. A denied read stands in for that answer at the same open: the same exception from the same call,
        // and a fingerprint cannot compare a file it cannot read. The rule names only the read, so the rig can still delete the folder afterwards.
        var listingNamedTheWal = false;
        var fingerprint = AccountRig.Fingerprint(rig.Root, beforeOpen: file =>
        {
            if (!file.EndsWith("-wal", StringComparison.Ordinal)) return;
            listingNamedTheWal = true;
            var info = new FileInfo(file);
            var security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny));
            info.SetAccessControl(security);
        });

        listingNamedTheWal.Should().BeTrue("the listing did name the write-ahead file: the denial is the only reason it is left out");
        fingerprint.Keys.Should().BeEmpty("the listing named the write-ahead file, but the open was denied: not data the fingerprint compares");
    }
}
