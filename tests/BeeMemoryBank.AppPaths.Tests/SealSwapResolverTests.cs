using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.TestSupport;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.AppPaths.Tests;

/// <summary>
/// The swap resolver and the journal of "Encrypt this node" (docs/design/step-b-spec.md 10.3, 10.6; red tests 15.3.7 and 15.3.9 at the file
/// level): a start finds a half-done migration in any state and ends either plain with the old files or sealed with the new ones, never
/// neither, and no plain leftover survives it. The directories are made of real SQLite3 Multiple Ciphers files; the "plain" files carry a
/// canary so a raw byte scan shows what a wipe left.
/// </summary>
public sealed class SealSwapResolverTests : IClassFixture<SealSwapResolverTests.Corpus>
{
    public sealed class Corpus : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "bmb_sealswap_" + Guid.NewGuid().ToString("N"));
        public SealedCorpus.Pool Pool { get; }

        public Corpus()
        {
            Directory.CreateDirectory(Root);
            Pool = SealedCorpus.Pool.Create(Root);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, true); } catch { }
        }
    }

    private sealed class SimulatedKill(string at) : Exception("killed after " + at);

    private readonly Corpus _corpus;
    private readonly string _canary = MetadataCanaries.Ascii("swap");

    public SealSwapResolverTests(Corpus corpus) => _corpus = corpus;

    private SealedCorpus.Pool Pool => _corpus.Pool;

    // ---- directories ----------------------------------------------------------------------------------------------------------------

    private string NewDir()
    {
        var dir = Path.Combine(_corpus.Root, "d-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A plain SQLite file (it starts with the magic) that also holds the canary.</summary>
    private void PlainFile(string path, string source)
    {
        File.Copy(source, path, overwrite: true);
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write);
        fs.Write(Encoding.UTF8.GetBytes(_canary));
    }

    /// <summary>The directory of a plain node about to be sealed: plain database and chat with canaries, a plain WAL, no boot store.</summary>
    private string PlainNode()
    {
        var dir = NewDir();
        PlainFile(Path.Combine(dir, "beememorybank.db"), Pool.PlainMain);
        PlainFile(Path.Combine(dir, "chat.db"), Pool.PlainChat);
        File.WriteAllText(Path.Combine(dir, "beememorybank.db-wal"), _canary);
        return dir;
    }

    /// <summary>The three verified targets next to the live files, and the journal in the given phase.</summary>
    private string Migrating(string phase, bool chat = true)
    {
        var dir = PlainNode();
        File.Copy(Pool.SealedMain, Path.Combine(dir, "beememorybank.db.seal-new"));
        if (chat) File.Copy(Pool.SealedChat, Path.Combine(dir, "chat.db.seal-new"));
        else File.Delete(Path.Combine(dir, "chat.db"));
        SealedCorpus.MakeBoot(Path.Combine(dir, "beememorybank.boot.db.seal-new"));
        SealJournal.Write(dir, SealJournal.New(phase, ["beememorybank.db", "chat.db"], string.Empty));
        return dir;
    }

    /// <summary>The directory after a finished swap whose wipe has not happened: the sealed pair, and the replaced plain files as leftovers.</summary>
    private string SwappedWithLeftovers(bool journal = false)
    {
        var dir = NewDir();
        File.Copy(Pool.SealedMain, Path.Combine(dir, "beememorybank.db"));
        File.Copy(Pool.SealedChat, Path.Combine(dir, "chat.db"));
        SealedCorpus.MakeBoot(Path.Combine(dir, "beememorybank.boot.db"));
        PlainFile(Path.Combine(dir, "beememorybank.db.seal-old"), Pool.PlainMain);
        File.WriteAllText(Path.Combine(dir, "beememorybank.db.seal-old-wal"), _canary);
        PlainFile(Path.Combine(dir, "chat.db.seal-old"), Pool.PlainChat);
        if (journal) SealJournal.Write(dir, SealJournal.New(SealJournal.Swapping, ["beememorybank.db", "chat.db"], string.Empty));
        return dir;
    }

    private static Dictionary<string, string> Hashes(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f).Replace('\\', '/'), f => Convert.ToHexString(SHA256.HashData(SealedCorpus.ReadShared(f))));

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(SealedCorpus.ReadShared(path)));

    private void NoCanaryAnywhere(string dir) =>
        MetadataCanaries.ScanDirectory(dir, [_canary]).Should().BeEmpty("no plain copy of the old database may survive the start");

    private static void NoSealLeftovers(string dir)
    {
        SealSwapResolver.Leftovers(dir).Should().BeEmpty();
        Directory.EnumerateFiles(dir, "*.seal-*").Should().BeEmpty();
        File.Exists(SealJournal.PathFor(dir)).Should().BeFalse();
        File.Exists(SealJournal.UnreadablePathFor(dir)).Should().BeFalse();
    }

    // ---- the journal ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_journal_round_trips_and_holds_no_path_or_key()
    {
        var dir = NewDir();
        SealJournal.Write(dir, new SealJournal(SealJournal.Building, "2026-10-10T00:00:00.0000000Z", ["beememorybank.db", "chat.db"], "pre-encryption/snap-1.tar.gz"));

        var (state, journal) = SealJournal.Read(dir);

        state.Should().Be(SealJournalState.Valid);
        journal!.Phase.Should().Be("building");
        journal.Files.Should().Equal("beememorybank.db", "chat.db");
        journal.Snapshot.Should().Be("pre-encryption/snap-1.tar.gz");
        File.ReadAllText(SealJournal.PathFor(dir)).Should().NotContain(dir, "the journal names files, not locations");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all \u0001\u0002")]
    [InlineData("{\"phase\":\"building\",\"started_at\":\"x\",\"files\":[\"beememorybank.db\"")]
    [InlineData("{\"phase\":\"dancing\",\"started_at\":\"x\",\"files\":[],\"snapshot\":\"\"}")]
    [InlineData("{\"phase\":\"swapping\",\"started_at\":\"x\",\"files\":[\"..\\\\evil.db\"],\"snapshot\":\"\"}")]
    [InlineData("{\"phase\":\"swapping\",\"started_at\":\"x\",\"files\":[\"../evil.db\"],\"snapshot\":\"\"}")]
    [InlineData("{\"phase\":\"swapping\",\"started_at\":\"x\",\"files\":[],\"snapshot\":\"../../etc/passwd\"}")]
    [InlineData("{\"phase\":\"swapping\",\"started_at\":\"x\",\"files\":[],\"snapshot\":\"snapshots/other.tar.gz\"}")]
    [InlineData("[]")]
    [InlineData("null")]
    public void A_journal_that_does_not_have_the_shape_is_unreadable_and_nothing_it_names_is_trusted(string content)
    {
        var dir = NewDir();
        File.WriteAllText(SealJournal.PathFor(dir), content);

        SealJournal.Read(dir).State.Should().Be(SealJournalState.Unreadable);
        File.Exists(SealJournal.PathFor(dir)).Should().BeTrue("reading never changes the disk");
    }

    // ---- nothing, rolled back ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_directory_with_no_migration_in_it_is_left_alone()
    {
        var dir = PlainNode();
        var before = Hashes(dir);

        SealSwapResolver.Resolve(dir).Action.Should().Be(SealResolveAction.None);

        Hashes(dir).Should().Equal(before);
        SealSwapResolver.Resolve(Path.Combine(_corpus.Root, "missing")).Action.Should().Be(SealResolveAction.None);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_building_journal_or_targets_without_any_journal_are_rolled_back_and_the_old_files_were_never_touched(bool withJournal)
    {
        var dir = Migrating(SealJournal.Building);
        if (!withJournal) File.Delete(SealJournal.PathFor(dir));
        var live = new[] { "beememorybank.db", "chat.db", "beememorybank.db-wal" }.ToDictionary(n => n, n => Sha(Path.Combine(dir, n)));

        var result = SealSwapResolver.Resolve(dir);

        result.Action.Should().Be(SealResolveAction.RolledBack);
        Directory.EnumerateFiles(dir, "*.seal-new").Should().BeEmpty();
        File.Exists(SealJournal.PathFor(dir)).Should().BeFalse();
        foreach (var (name, hash) in live) Sha(Path.Combine(dir, name)).Should().Be(hash, name);
        VaultStoreProbe.IsSealedPair(dir).Should().BeFalse("the node stays plain");
    }

    // ---- the forward path --------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(SealJournal.Verified)]
    [InlineData(SealJournal.Swapping)]
    public void A_verified_or_swapping_journal_goes_forward_and_leaves_the_sealed_pair_and_nothing_plain(string phase)
    {
        var dir = Migrating(phase);
        var sealedMain = Sha(Pool.SealedMain);
        var sealedChat = Sha(Pool.SealedChat);

        var result = SealSwapResolver.Resolve(dir);

        result.Action.Should().Be(SealResolveAction.SwappedForward);
        result.WipePendingNames.Should().BeEmpty();
        Sha(Path.Combine(dir, "beememorybank.db")).Should().Be(sealedMain);
        Sha(Path.Combine(dir, "chat.db")).Should().Be(sealedChat);
        VaultStoreProbe.IsSealedPair(dir).Should().BeTrue();
        NoSealLeftovers(dir);
        NoCanaryAnywhere(dir);
    }

    [Fact]
    public void A_node_without_a_chat_database_is_swapped_the_same_way()
    {
        var dir = Migrating(SealJournal.Verified, chat: false);

        SealSwapResolver.Resolve(dir).Action.Should().Be(SealResolveAction.SwappedForward);

        File.Exists(Path.Combine(dir, "chat.db")).Should().BeFalse();
        VaultStoreProbe.IsSealedPair(dir).Should().BeTrue();
        NoSealLeftovers(dir);
        NoCanaryAnywhere(dir);
    }

    [Fact]
    public void A_process_killed_after_any_step_of_the_swap_ends_sealed_after_the_next_start_with_no_plain_copy_left()
    {
        // The dry run names every step; then the swap is killed after each of them in a fresh directory and a second start finishes it.
        var steps = new List<string>();
        SealSwapResolver.Resolve(Migrating(SealJournal.Verified), steps.Add);
        steps.Should().Contain("journal-swapping").And.Contain("old-aside-beememorybank.db").And.Contain("renamed-beememorybank.boot.db").And.Contain("journal-deleted");

        var sealedMain = Sha(Pool.SealedMain);
        var sealedChat = Sha(Pool.SealedChat);
        for (var kill = 0; kill < steps.Count; kill++)
        {
            var dir = Migrating(SealJournal.Verified);
            var n = 0;
            var act = () => SealSwapResolver.Resolve(dir, step => { if (n++ == kill) throw new SimulatedKill(step); });
            act.Should().Throw<SimulatedKill>($"step {kill} ({steps[kill]})");

            // Whatever step it died at, the data directory holds the plain node or the sealed one, never neither.
            (File.Exists(Path.Combine(dir, "beememorybank.db")) || File.Exists(Path.Combine(dir, "beememorybank.db.seal-old"))).Should().BeTrue(steps[kill]);

            SealSwapResolver.Resolve(dir).Action.Should().NotBe(SealResolveAction.Refused, steps[kill]);

            Sha(Path.Combine(dir, "beememorybank.db")).Should().Be(sealedMain, steps[kill]);
            Sha(Path.Combine(dir, "chat.db")).Should().Be(sealedChat, steps[kill]);
            VaultStoreProbe.IsSealedPair(dir).Should().BeTrue(steps[kill]);
            NoSealLeftovers(dir);
            NoCanaryAnywhere(dir);
        }
    }

    [Fact]
    public void A_wipe_that_fails_keeps_the_journal_at_swapping_and_lists_the_file_and_the_next_start_finishes_it()
    {
        var dir = Migrating(SealJournal.Verified);
        var failed = false;
        var result = SealSwapResolver.Resolve(dir, wipe: path =>
        {
            if (Path.GetFileName(path) == "chat.db.seal-old") { failed = true; return false; }
            return SecureFileWipe.TryWipe(path);
        });

        failed.Should().BeTrue();
        result.Action.Should().Be(SealResolveAction.SwappedForward);
        result.WipePendingNames.Should().Equal("chat.db.seal-old");
        VaultStoreProbe.IsSealedPair(dir).Should().BeTrue("the node starts sealed even while a wipe is pending");
        var (state, journal) = SealJournal.Read(dir);
        state.Should().Be(SealJournalState.Valid);
        journal!.Phase.Should().Be(SealJournal.Swapping);
        journal.Files.Should().Equal("chat.db.seal-old");
        MetadataCanaries.ScanDirectory(dir, [_canary]).Should().NotBeEmpty("the control: the unwiped leftover still holds it");

        var next = SealSwapResolver.Resolve(dir);

        next.WipePendingNames.Should().BeEmpty();
        NoSealLeftovers(dir);
        NoCanaryAnywhere(dir);
    }

    [WindowsOnlyFact]
    public void A_replaced_file_another_program_holds_open_is_a_pending_wipe_not_a_failed_start()
    {
        var dir = Migrating(SealJournal.Verified);
        var result = default(SealSwapResolution);
        var locked = Path.Combine(dir, "chat.db");
        // The old chat database is renamed to chat.db.seal-old by the swap; hold it open the moment the rename is done.
        FileStream? hold = null;
        try
        {
            result = SealSwapResolver.Resolve(dir, step =>
            {
                if (step == "old-aside-chat.db")
                    hold = new FileStream(Path.Combine(dir, "chat.db.seal-old"), FileMode.Open, FileAccess.Read, FileShare.None);
            });
        }
        finally
        {
            hold?.Dispose();
        }

        _ = locked;
        result!.WipePendingNames.Should().Contain("chat.db.seal-old");
        VaultStoreProbe.IsSealedPair(dir).Should().BeTrue();
        SealSwapResolver.Resolve(dir).WipePendingNames.Should().BeEmpty();
        NoSealLeftovers(dir);
    }

    // ---- leftovers without a usable journal ----------------------------------------------------------------------------------------------

    public static TheoryData<string, string?> UnusableJournals() => new()
    {
        { "no journal", null },
        { "garbage bytes", "\u0001\u0002 garbage ÿþ" },
        { "truncated JSON", "{\"phase\":\"swapping\",\"started_at\":\"2026-10-10T00:00:00Z\",\"files\":[\"beememorybank.db\"" },
        { "an unknown phase", "{\"phase\":\"dancing\",\"started_at\":\"x\",\"files\":[],\"snapshot\":\"\"}" },
        { "phase building", "{\"phase\":\"building\",\"started_at\":\"2026-10-10T00:00:00Z\",\"files\":[\"beememorybank.db\"],\"snapshot\":\"\"}" },
    };

    [Theory]
    [MemberData(nameof(UnusableJournals))]
    public void Leftovers_beside_a_sealed_pair_are_wiped_whatever_the_journal_says_and_the_live_files_are_untouched(string label, string? journalText)
    {
        var dir = SwappedWithLeftovers();
        if (journalText is not null) File.WriteAllText(SealJournal.PathFor(dir), journalText);
        var live = new[] { "beememorybank.db", "chat.db", "beememorybank.boot.db" }.ToDictionary(n => n, n => Sha(Path.Combine(dir, n)));
        var unreadableKeptWhileWiping = new List<bool>();

        var result = SealSwapResolver.Resolve(dir, step =>
        {
            if (step.StartsWith("wiped-", StringComparison.Ordinal)) unreadableKeptWhileWiping.Add(File.Exists(SealJournal.UnreadablePathFor(dir)));
        });

        result.Action.Should().Be(SealResolveAction.LeftoversWiped, label);
        foreach (var (name, hash) in live) Sha(Path.Combine(dir, name)).Should().Be(hash, label + " " + name);
        NoSealLeftovers(dir);
        NoCanaryAnywhere(dir);
        if (label is "garbage bytes" or "truncated JSON" or "an unknown phase")
            unreadableKeptWhileWiping.Should().NotBeEmpty().And.OnlyContain(kept => kept, "the bad journal is kept aside until the wipe is done, then removed");
    }

    [Fact]
    public void A_leftover_that_cannot_be_wiped_starts_sealed_with_a_rebuilt_swapping_journal_and_keeps_the_unreadable_one()
    {
        var dir = SwappedWithLeftovers();
        File.WriteAllText(SealJournal.PathFor(dir), "garbage");

        var result = SealSwapResolver.Resolve(dir, wipe: path => !path.EndsWith("chat.db.seal-old", StringComparison.Ordinal) && SecureFileWipe.TryWipe(path));

        result.WipePendingNames.Should().Equal("chat.db.seal-old");
        var (state, journal) = SealJournal.Read(dir);
        state.Should().Be(SealJournalState.Valid);
        journal!.Phase.Should().Be("swapping");
        File.Exists(SealJournal.UnreadablePathFor(dir)).Should().BeTrue("kept until no *.seal-* file is left");

        // The next start has a journal it can read and finishes the wipe; then the evidence goes too.
        SealSwapResolver.Resolve(dir).WipePendingNames.Should().BeEmpty();
        NoSealLeftovers(dir);
        NoCanaryAnywhere(dir);
    }

    [Fact]
    public void A_leftover_with_a_second_name_is_unlinked_never_overwritten_and_is_named()
    {
        var dir = SwappedWithLeftovers();
        var elsewhere = NewDir();
        var other = Path.Combine(elsewhere, "my-backup-of-the-database.db");
        TestLinks.CreateHardLink(Path.Combine(dir, "beememorybank.db.seal-old"), other);
        var before = Sha(other);

        var result = SealSwapResolver.Resolve(dir);

        result.LinkedPlainNames.Should().Equal("beememorybank.db.seal-old");
        File.Exists(other).Should().BeTrue();
        Sha(other).Should().Be(before, "the other name keeps every byte: the wipe did not write through it");
        NoSealLeftovers(dir);
    }

    // ---- refusals: nothing is changed ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("plain live database", true)]
    [InlineData("no live database at all", false)]
    public void Leftovers_beside_a_pair_that_is_not_provably_sealed_end_in_a_named_refusal_and_nothing_changes(string label, bool plainLive)
    {
        var dir = NewDir();
        if (plainLive) PlainFile(Path.Combine(dir, "beememorybank.db"), Pool.PlainMain);
        PlainFile(Path.Combine(dir, "beememorybank.db.seal-old"), Pool.PlainMain);
        File.WriteAllText(Path.Combine(dir, "beememorybank.db.seal-old-wal"), _canary);
        Directory.CreateDirectory(Path.Combine(dir, "pre-encryption"));
        File.WriteAllText(Path.Combine(dir, "pre-encryption", "snap-1.tar.gz"), "x");
        File.WriteAllText(SealJournal.PathFor(dir), "garbage");
        var before = Hashes(dir);

        var result = SealSwapResolver.Resolve(dir);

        result.Action.Should().Be(SealResolveAction.Refused, label);
        result.Refusal!.Code.Should().Be("seal_leftovers");
        result.Refusal.Files.Should().Contain("beememorybank.db.seal-old").And.Contain("beememorybank.db.seal-old-wal");
        result.Refusal.PreEncryptionSnapshot.Should().Be("snap-1.tar.gz");
        result.Refusal.Message.Should().Contain("beememorybank.db.seal-old").And.Contain("pre-encryption/snap-1.tar.gz");
        Hashes(dir).Should().Equal(before, "no file is deleted, moved, renamed or changed, the bad journal included");
    }

    [Fact]
    public void A_swapping_journal_whose_live_database_is_gone_and_which_has_no_target_never_wipes_the_only_copy()
    {
        var dir = NewDir();
        PlainFile(Path.Combine(dir, "beememorybank.db.seal-old"), Pool.PlainMain);
        SealJournal.Write(dir, SealJournal.New(SealJournal.Swapping, ["beememorybank.db"], string.Empty));
        var before = Hashes(dir);

        var result = SealSwapResolver.Resolve(dir);

        result.Action.Should().Be(SealResolveAction.Refused);
        File.Exists(Path.Combine(dir, "beememorybank.db.seal-old")).Should().BeTrue();
        Hashes(dir)[("beememorybank.db.seal-old")].Should().Be(before["beememorybank.db.seal-old"]);
    }

    [Fact]
    public void A_target_beside_a_live_file_that_is_already_encrypted_is_not_renamed_over_it()
    {
        var dir = NewDir();
        File.Copy(Pool.SealedMain, Path.Combine(dir, "beememorybank.db"));
        File.Copy(Pool.SealedMain, Path.Combine(dir, "beememorybank.db.seal-new"));
        SealedCorpus.MakeBoot(Path.Combine(dir, "beememorybank.boot.db"));
        SealJournal.Write(dir, SealJournal.New(SealJournal.Verified, ["beememorybank.db"], string.Empty));
        var before = Hashes(dir);

        var result = SealSwapResolver.Resolve(dir);

        result.Action.Should().Be(SealResolveAction.Refused);
        Hashes(dir).Should().Equal(before);
    }

    // ---- the start gate --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void VaultStartup_runs_the_resolver_under_the_exclusive_lease_and_reports_what_it_did()
    {
        var dir = Migrating(SealJournal.Verified);

        var (resolution, lease) = VaultStartup.Enter(dir);
        using (lease)
        {
            resolution.Seal.Should().NotBeNull();
            resolution.Seal!.Action.Should().Be(SealResolveAction.SwappedForward);
        }

        VaultStoreProbe.IsSealedPair(dir).Should().BeTrue();
        NoSealLeftovers(dir);
        var (second, lease2) = VaultStartup.Enter(dir);
        using (lease2) second.Seal.Should().BeNull("a start with nothing to resolve reports nothing");
    }

    // ---- review round 1, H1: another try at the wipe while the node runs ------------------------------------------------------------------

    [Fact]
    public void RetryWipe_wipes_the_leftovers_beside_a_sealed_pair_and_takes_the_swapping_journal_with_the_last_of_them()
    {
        var dir = SwappedWithLeftovers(journal: true);
        var before = Sha(Path.Combine(dir, "beememorybank.db"));

        var stuck = SealSwapResolver.RetryWipe(dir, wipe: path => Path.GetFileName(path) != "chat.db.seal-old" && SecureFileWipe.TryWipe(path));

        stuck.WipePendingNames.Should().Equal("chat.db.seal-old");
        SealSwapResolver.Leftovers(dir).Should().Equal("chat.db.seal-old");
        SealJournal.Read(dir).Journal!.Files.Should().Equal("chat.db.seal-old");   // the journal lists what is still to be wiped

        var done = SealSwapResolver.RetryWipe(dir);

        done.WipePendingNames.Should().BeEmpty();
        NoSealLeftovers(dir);
        NoCanaryAnywhere(dir);
        Sha(Path.Combine(dir, "beememorybank.db")).Should().Be(before, "the live files are never touched");
    }

    [Fact]
    public void RetryWipe_does_nothing_when_no_leftover_is_there_and_never_rolls_a_migration_back()
    {
        var dir = Migrating(SealJournal.Building);   // a migration under way: targets and a "building" journal, no leftover
        var before = Hashes(dir);

        var result = SealSwapResolver.RetryWipe(dir);

        result.Action.Should().Be(SealResolveAction.None);
        Hashes(dir).Should().Equal(before, "only a leftover is its business: the targets and the journal of a running migration stay");
    }

    [Theory]
    [InlineData("plain live database", true)]
    [InlineData("no live database at all", false)]
    public void RetryWipe_never_wipes_a_leftover_that_may_be_the_only_copy(string label, bool plainLive)
    {
        var dir = NewDir();
        if (plainLive) PlainFile(Path.Combine(dir, "beememorybank.db"), Pool.PlainMain);
        PlainFile(Path.Combine(dir, "beememorybank.db.seal-old"), Pool.PlainMain);
        var before = Hashes(dir);

        var result = SealSwapResolver.RetryWipe(dir);

        result.Action.Should().Be(SealResolveAction.Refused, label);
        Hashes(dir).Should().Equal(before);
    }
}
