using BeeMemoryBank.AppPaths;

namespace BeeMemoryBank.Rekey.Tests;

/// <summary>
/// Review release-b R1-6 (sec#3): the journal is untrusted. Nothing it names is moved unless it is exactly the sibling
/// the swap itself creates. A directory that is a link is no vault. A journal that does not parse is decided like a lost
/// one, so D stays startable and nothing outside the vault's siblings is ever touched.
/// </summary>
[Collection(nameof(DurableFsEvents))]
public sealed class RekeySwapJournalTrustTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bmb-trust-" + Guid.NewGuid().ToString("N"));
    private readonly string _d, _elsewhere, _old;
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 21, 30, 0, TimeSpan.Zero);

    public RekeySwapJournalTrustTests()
    {
        _d = Path.Combine(_root, "vault");
        _elsewhere = Path.Combine(_root, "someone-elses-dir");
        _old = RekeySwapJournal.OldDirFor(_d, Now);
        Directory.CreateDirectory(_elsewhere);
        File.WriteAllText(Path.Combine(_elsewhere, "precious"), "not a vault");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void Vault(string dir, string db)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "beememorybank.db"), db);
    }

    private string Db => File.ReadAllText(Path.Combine(_d, "beememorybank.db"));
    private void AssertElsewhereUntouched() =>
        File.ReadAllText(Path.Combine(_elsewhere, "precious")).Should().Be("not a vault", "nothing outside the vault's siblings moves");

    /// <summary>"new" names an unrelated directory: it is never promoted to D; with no real new vault the old one returns.</summary>
    [Fact]
    public void AJournalNamingAForeignDirAsNew_NeverPromotesIt()
    {
        Vault(_old, "old-db");
        RekeySwapJournal.Write(_d, new RekeySwapJournal(_elsewhere, _old, RekeySwapJournal.OldMoved));

        RekeySwapResolver.Resolve(_d);

        Db.Should().Be("old-db");
        AssertElsewhereUntouched();
    }

    /// <summary>"old" names an unrelated directory: D is parked under the proper sibling name, not moved onto it.</summary>
    [Fact]
    public void AJournalNamingAForeignDirAsOld_ParksDUnderItsOwnName()
    {
        Vault(_d, "old-db");
        Vault(RekeySwapJournal.NewDirFor(_d), "new-db");
        RekeySwapJournal.Write(_d, new RekeySwapJournal(RekeySwapJournal.NewDirFor(_d), _elsewhere, RekeySwapJournal.Prepared));

        RekeySwapResolver.Resolve(_d);

        Db.Should().Be("new-db");
        AssertElsewhereUntouched();
        RekeySwapJournal.ParkedOldDirs(_d).Should().ContainSingle()
            .Which.Should().Match(p => File.ReadAllText(Path.Combine(p, "beememorybank.db")) == "old-db");
    }

    /// <summary>A journal that does not parse: set aside, and the siblings decide (here: the parked old vault returns).</summary>
    [Fact]
    public void AnUnreadableJournal_IsSetAside_AndDStaysStartable()
    {
        Vault(_old, "old-db");
        File.WriteAllText(RekeySwapJournal.PathFor(_d), "{ \"new\": \"/etc\", torn");

        var resolved = RekeySwapResolver.Resolve(_d);

        resolved.FirstStartAfterSwap.Should().BeFalse();
        Db.Should().Be("old-db");
        File.Exists(RekeySwapJournal.PathFor(_d)).Should().BeFalse("set aside, so a later re-key is not stopped by it");
        Directory.EnumerateFiles(_root, "vault.rekey-journal.json.unreadable-*").Should().ContainSingle("kept for whoever looks");
    }

    /// <summary>A new-vault sibling that is a link (to somewhere else) is no vault: it is not promoted.</summary>
    [Fact]
    public void ANewVaultThatIsALink_IsNotPromoted()
    {
        Vault(_old, "old-db");
        Vault(_elsewhere, "foreign-db");
        try { Directory.CreateSymbolicLink(RekeySwapJournal.NewDirFor(_d), _elsewhere); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // no right to create links on this machine; the check is platform code, covered where it can run
        }
        RekeySwapJournal.Write(_d, new RekeySwapJournal(RekeySwapJournal.NewDirFor(_d), _old, RekeySwapJournal.OldMoved));

        RekeySwapResolver.Resolve(_d);

        Db.Should().Be("old-db", "the link is treated as no new vault, so the old one returns");
        File.ReadAllText(Path.Combine(_elsewhere, "beememorybank.db")).Should().Be("foreign-db");
    }
}
