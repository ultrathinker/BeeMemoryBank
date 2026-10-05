using System.Security.Cryptography;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Rekey;

namespace BeeMemoryBank.Rekey.Tests;

/// <summary>
/// The swap of an offline re-key and its start-up resolver (rekey-offline.md §2 step 6–7, §4 brick, §5): a fault at
/// every point of the swap leaves a startable D — the old vault or the new one, never neither — and the old vault's
/// content is never changed, wherever it ends up.
/// </summary>
public sealed class RekeySwapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bmb-swap-" + Guid.NewGuid().ToString("N"));
    private readonly string _d;
    private readonly Dictionary<string, string> _oldTree;
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 18, 0, 0, TimeSpan.Zero);

    public RekeySwapTests()
    {
        _d = Path.Combine(_root, "vault");
        Write(_d, "beememorybank.db", "old-db");
        Write(_d, "chat.db", "old-chat");
        Write(_d, "media/11111111-1111-1111-1111-111111111111.enc", "old-media");
        Write(_d, "snapshots/s1.zip", "old-snapshot");
        Write(_d, "os-auto-unlock.dat", "old-secret");
        Write(_d, "unknown.txt", "who knows");
        Write(_d, "certs/ca.pem", "ca");
        // Legacy data of the removed internet-access wizard: an upgraded installation may still hold it, and it must survive a rekey.
        Write(_d, "certs/acme/account.json", "acme");
        Write(_d, "tls/server.pfx", "tls");
        Write(_d, "internet-access/state.json", "ia");
        Write(_d, "ddns-state.json", "ddns");
        Write(_d, ".internal-key", "internal");
        Write(_d, "wipe-audit.log", "wipe");
        Write(_d, "reset-audit.log", "reset");
        _oldTree = Tree(_d);
        var newDir = RekeySwapJournal.NewDirFor(_d);
        Write(newDir, "beememorybank.db", "new-db");
        Write(newDir, "chat.db", "new-chat");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void Write(string dir, string rel, string text)
    {
        var path = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static Dictionary<string, string> Tree(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(dir, f).Replace('\\', '/'),
            f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    private string OldDir => RekeySwapJournal.OldDirFor(_d, Now);
    private string Db(string dir) => File.ReadAllText(Path.Combine(dir, "beememorybank.db"));

    /// <summary>D holds a whole vault after resolution, and the old vault is unchanged wherever it is.</summary>
    private string AssertStartable(RekeySwapResolution r)
    {
        r.DataDir.Should().Be(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_d)));
        Directory.Exists(_d).Should().BeTrue("D always exists after resolution");
        var db = Db(_d);
        db.Should().BeOneOf("old-db", "new-db");
        var oldAt = db == "old-db" ? _d : OldDir;
        Tree(oldAt).Should().BeEquivalentTo(_oldTree, "the old vault is never modified");
        if (db == "new-db")
        {
            File.ReadAllText(Path.Combine(_d, "chat.db")).Should().Be("new-chat");
            File.ReadAllText(Path.Combine(_d, "certs/acme/account.json")).Should().Be("acme", "carried over");
        }
        return db;
    }

    [Fact]
    public void TheSwap_CarriesOverTheListedFiles_KeepsTheOldVaultIntact_AndTheFirstStartClearsJournalAndLock()
    {
        var old = RekeySwap.Swap(_d, Now);

        old.Should().Be(OldDir);
        Db(_d).Should().Be("new-db");
        var tree = Tree(_d);
        tree.Keys.Should().BeEquivalentTo(
        [
            "beememorybank.db", "chat.db", "certs/ca.pem", "certs/acme/account.json", "tls/server.pfx",
            "internet-access/state.json", "ddns-state.json", "wipe-audit.log", "reset-audit.log",
        ], "only the §5 list is carried over: no media, snapshots, secrets or unknown files");
        Tree(OldDir).Should().BeEquivalentTo(_oldTree);
        Directory.Exists(RekeySwapJournal.NewDirFor(_d)).Should().BeFalse();
        RekeySwapJournal.Read(_d)!.Phase.Should().Be(RekeySwapJournal.Swapped);

        File.WriteAllText(RekeySwapJournal.LockPathFor(_d), "");
        RekeySwapResolver.Resolve(_d).FirstStartAfterSwap.Should().BeTrue();
        RekeySwapResolver.CompleteFirstStart(_d);

        File.Exists(RekeySwapJournal.PathFor(_d)).Should().BeFalse();
        File.Exists(RekeySwapJournal.LockPathFor(_d)).Should().BeFalse();
        RekeySwapResolver.Resolve(_d).Should().Be(new RekeySwapResolution(Path.GetFullPath(_d), false));
        Db(_d).Should().Be("new-db");
    }

    [Theory]
    [InlineData(RekeySwap.FaultCarryOver, "old-db")]
    [InlineData(RekeySwap.FaultBeforeRename1, "new-db")]
    [InlineData(RekeySwap.FaultBetweenRenames, "new-db")]
    [InlineData(RekeySwap.FaultAfterRename2, "new-db")]
    public void AFaultAtEveryPoint_LeavesAStartableVault_OldOrNew(string point, string expected)
    {
        var swap = () => RekeySwap.Swap(_d, Now, p => { if (p == point) throw new IOException("power cut at " + p); });
        swap.Should().Throw<IOException>();

        var resolved = RekeySwapResolver.Resolve(_d);

        AssertStartable(resolved).Should().Be(expected);
        resolved.FirstStartAfterSwap.Should().Be(expected == "new-db");
        // Resolving again (the Api after the node, or a second crash) changes nothing.
        AssertStartable(RekeySwapResolver.Resolve(_d)).Should().Be(expected);
    }

    /// <summary>The crash fell after rename 1 but before the journal said so.</summary>
    [Fact]
    public void ACrashAfterRename1BeforeItsJournalUpdate_IsFinishedFromWhatIsOnDisk()
    {
        var swap = () => RekeySwap.Swap(_d, Now, p => { if (p == RekeySwap.FaultBetweenRenames) throw new IOException("cut"); });
        swap.Should().Throw<IOException>();
        RekeySwapJournal.Write(_d, RekeySwapJournal.Read(_d)! with { Phase = RekeySwapJournal.Prepared });

        AssertStartable(RekeySwapResolver.Resolve(_d)).Should().Be("new-db");
    }

    /// <summary>The new vault vanished after rename 1: the old vault goes back to D.</summary>
    [Fact]
    public void TheNewVaultGoneBetweenTheRenames_RollsBackToTheOldVault()
    {
        var swap = () => RekeySwap.Swap(_d, Now, p => { if (p == RekeySwap.FaultBetweenRenames) throw new IOException("cut"); });
        swap.Should().Throw<IOException>();
        Directory.Delete(RekeySwapJournal.NewDirFor(_d), recursive: true);

        var resolved = RekeySwapResolver.Resolve(_d);

        AssertStartable(resolved).Should().Be("old-db");
        resolved.FirstStartAfterSwap.Should().BeFalse();
        File.Exists(RekeySwapJournal.PathFor(_d)).Should().BeFalse();
    }

    /// <summary>An older start path created an empty D between the renames: it holds nothing and gives way.</summary>
    [Fact]
    public void AnEmptyDCreatedBetweenTheRenames_GivesWayToTheNewVault()
    {
        var swap = () => RekeySwap.Swap(_d, Now, p => { if (p == RekeySwap.FaultBetweenRenames) throw new IOException("cut"); });
        swap.Should().Throw<IOException>();
        Directory.CreateDirectory(_d);

        AssertStartable(RekeySwapResolver.Resolve(_d)).Should().Be("new-db");
    }

    /// <summary>A crashed first start on the new vault: the next start is again a first start, nothing is renamed.</summary>
    [Fact]
    public void ACrashedFirstStart_KeepsTheNewVaultAndItsJournal()
    {
        RekeySwap.Swap(_d, Now);
        RekeySwapResolver.Resolve(_d).FirstStartAfterSwap.Should().BeTrue();
        // ... the start crashes before CompleteFirstStart.

        var again = RekeySwapResolver.Resolve(_d);

        again.FirstStartAfterSwap.Should().BeTrue();
        AssertStartable(again).Should().Be("new-db");
        File.Exists(RekeySwapJournal.PathFor(_d)).Should().BeTrue();
    }

    [Fact]
    public void WithNoJournal_TheResolverChangesNothing()
    {
        RekeySwapResolver.Resolve(_d).Should().Be(new RekeySwapResolution(Path.GetFullPath(_d), false));
        Tree(_d).Should().BeEquivalentTo(_oldTree);
        var missing = Path.Combine(_root, "never-created");
        RekeySwapResolver.Resolve(missing).DataDir.Should().Be(missing);
        Directory.Exists(missing).Should().BeFalse();
    }

    /// <summary>
    /// A handle still open inside D during the swap. On Windows rename 1 fails (after its retries): D stays the old
    /// vault and the journal finishes the swap at the next start, once the handle is gone. On Linux the rename
    /// succeeds under the open handle. Either way D is startable.
    /// </summary>
    [Fact]
    public void AnOpenHandleInsideD_NeverLeavesItUnstartable()
    {
        Exception? failed = null;
        using (new FileStream(Path.Combine(_d, "chat.db"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            try { RekeySwap.Swap(_d, Now); }
            catch (Exception ex) { failed = ex; }
            if (failed != null)
            {
                (failed is IOException or UnauthorizedAccessException).Should().BeTrue(failed.ToString());
                Db(_d).Should().Be("old-db", "a failed rename leaves the old vault in place");
            }
        }

        AssertStartable(RekeySwapResolver.Resolve(_d)).Should().Be("new-db");
    }

    [Fact]
    public void ASecondSwapWhileAJournalExists_IsRefused()
    {
        var swap = () => RekeySwap.Swap(_d, Now, p => { if (p == RekeySwap.FaultBeforeRename1) throw new IOException("cut"); });
        swap.Should().Throw<IOException>();

        var again = () => RekeySwap.Swap(_d, Now.AddMinutes(1));

        again.Should().Throw<InvalidOperationException>().WithMessage("*already in progress*");
        Tree(_d).Should().BeEquivalentTo(_oldTree);
    }
}
