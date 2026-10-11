using System.Security.Cryptography;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.TestSupport;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Storage.Tests.Sealed;

/// <summary>
/// <see cref="VaultStoreFiles.Diagnose"/> and <see cref="VaultStoreFiles.DetectMode"/> (docs/design/step-b-spec.md 2.5, Appendix A.2, red test
/// 15.1.15), with real SQLite3 Multiple Ciphers files: the mode of a data directory, the reason and the file names of every refusal, the
/// plain-companion rule (a plain chat.db, or a WAL or journal that holds a plain page, beside a sealed pair) and the sealed pair's own WAL
/// and journal, which have a plain header and encrypted pages and must never be taken for plain.
/// </summary>
public sealed class DetectModeTests : IClassFixture<DetectModeTests.Corpus>, IDisposable
{
    public sealed class Corpus : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "bmb_detect_" + Guid.NewGuid().ToString("N"));
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

    private readonly Corpus _corpus;
    private SealedCorpus.Pool Pool => _corpus.Pool;

    public DetectModeTests(Corpus corpus) => _corpus = corpus;

    public void Dispose() { }

    private string NewDir()
    {
        var dir = Path.Combine(_corpus.Root, "d-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A valid sealed pair: a real sealed beememorybank.db and a real boot store.</summary>
    private string SealedPair()
    {
        var dir = NewDir();
        File.Copy(Pool.SealedMain, Path.Combine(dir, "beememorybank.db"));
        SealedCorpus.MakeBoot(Path.Combine(dir, "beememorybank.boot.db"));
        return dir;
    }

    private static void Copy(string from, string dir, string name) => File.Copy(from, Path.Combine(dir, name), overwrite: true);

    private static void Check(VaultStoreDiagnosis got, VaultStoreMode mode, string? reason = null, params string[] files)
    {
        got.Mode.Should().Be(mode, $"{got.Reason} [{string.Join(",", got.Files)}]");
        got.Reason.Should().Be(reason);
        got.Files.OrderBy(x => x, StringComparer.Ordinal).Should().Equal(files.OrderBy(x => x, StringComparer.Ordinal));
    }

    // ---------------------------------------------------------------- the modes

    [Fact]
    public void A_directory_with_no_boot_store_is_plain_whether_it_has_a_plain_database_or_none()
    {
        var empty = NewDir();
        Check(VaultStoreFiles.Diagnose(empty), VaultStoreMode.Plain);

        var plain = NewDir();
        Copy(Pool.PlainMain, plain, "beememorybank.db");
        Check(VaultStoreFiles.Diagnose(plain), VaultStoreMode.Plain);
        VaultStoreFiles.DetectMode(plain).Should().Be(VaultStoreMode.Plain);

        Check(VaultStoreFiles.Diagnose(Path.Combine(_corpus.Root, "does-not-exist")), VaultStoreMode.Plain);
    }

    [Fact]
    public void A_boot_store_of_ours_beside_an_encrypted_page_aligned_database_is_sealed()
    {
        var dir = SealedPair();

        Check(VaultStoreFiles.Diagnose(dir), VaultStoreMode.Sealed);
        VaultStoreFiles.DetectMode(dir).Should().Be(VaultStoreMode.Sealed);
    }

    [Fact]
    public void Every_broken_shape_has_a_stable_reason_and_names_its_files()
    {
        // an encrypted database without its boot store
        var noBoot = NewDir();
        Copy(Pool.SealedMain, noBoot, "beememorybank.db");
        Check(VaultStoreFiles.Diagnose(noBoot), VaultStoreMode.Broken, "db_without_boot", "beememorybank.db");

        // a boot store beside a plain database
        var bootBesidePlain = NewDir();
        Copy(Pool.PlainMain, bootBesidePlain, "beememorybank.db");
        SealedCorpus.MakeBoot(Path.Combine(bootBesidePlain, "beememorybank.boot.db"));
        Check(VaultStoreFiles.Diagnose(bootBesidePlain), VaultStoreMode.Broken, "boot_beside_plain_db", "beememorybank.boot.db", "beememorybank.db");

        // a boot store that is not ours: a plain SQLite file without our application id, and bytes that are not SQLite at all
        var foreign = SealedPair();
        File.Delete(Path.Combine(foreign, "beememorybank.boot.db"));
        Copy(Pool.PlainChat, foreign, "beememorybank.boot.db");
        Check(VaultStoreFiles.Diagnose(foreign), VaultStoreMode.Broken, "boot_not_ours", "beememorybank.boot.db");
        File.WriteAllBytes(Path.Combine(foreign, "beememorybank.boot.db"), RandomNumberGenerator.GetBytes(5000));
        Check(VaultStoreFiles.Diagnose(foreign), VaultStoreMode.Broken, "boot_not_ours", "beememorybank.boot.db");

        // a boot store without its database, and beside an empty one
        var noDb = NewDir();
        SealedCorpus.MakeBoot(Path.Combine(noDb, "beememorybank.boot.db"));
        Check(VaultStoreFiles.Diagnose(noDb), VaultStoreMode.Broken, "db_missing_or_empty", "beememorybank.db");
        File.WriteAllBytes(Path.Combine(noDb, "beememorybank.db"), []);
        Check(VaultStoreFiles.Diagnose(noDb), VaultStoreMode.Broken, "db_missing_or_empty", "beememorybank.db");

        // a copy cut inside a page
        var cut = SealedPair();
        var dbPath = Path.Combine(cut, "beememorybank.db");
        File.WriteAllBytes(dbPath, File.ReadAllBytes(dbPath)[..^10]);
        Check(VaultStoreFiles.Diagnose(cut), VaultStoreMode.Broken, "db_not_page_aligned", "beememorybank.db");
    }

    [Fact]
    public void A_seal_old_file_beside_the_live_files_with_no_journal_is_broken_and_with_a_journal_the_resolver_governs()
    {
        var dir = SealedPair();
        File.WriteAllBytes(Path.Combine(dir, "beememorybank.db.seal-old"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(dir, "chat.db.seal-old-wal"), [1, 2, 3]);

        var got = VaultStoreFiles.Diagnose(dir);

        Check(got, VaultStoreMode.Broken, "seal_leftovers_no_journal", "beememorybank.db.seal-old", "chat.db.seal-old-wal");

        File.WriteAllText(Path.Combine(dir, "seal-journal.json"), "{}");
        Check(VaultStoreFiles.Diagnose(dir), VaultStoreMode.Sealed);
    }

    // ---------------------------------------------------------------- plain companions

    [Fact]
    public void A_plain_chat_database_beside_a_sealed_pair_is_a_plain_companion()
    {
        var dir = SealedPair();
        Copy(Pool.PlainChat, dir, "chat.db"); // a real plain chat database

        Check(VaultStoreFiles.Diagnose(dir), VaultStoreMode.Broken, "plain_companion", "chat.db");
    }

    [Fact]
    public void A_plain_sqlite_file_with_no_schema_named_chat_db_is_a_plain_companion_too()
    {
        var dir = SealedPair();
        File.WriteAllBytes(Path.Combine(dir, "chat.db"), "SQLite format 3\0"u8.ToArray().Concat(new byte[4080]).ToArray());

        Check(VaultStoreFiles.Diagnose(dir), VaultStoreMode.Broken, "plain_companion", "chat.db");
    }

    [Fact]
    public void A_mixed_set_an_encrypted_database_and_a_plain_chat_database_is_refused_at_the_start()
    {
        var dir = SealedPair();
        Copy(Pool.PlainChat, dir, "chat.db");

        var got = VaultStoreFiles.Diagnose(dir);

        got.Mode.Should().Be(VaultStoreMode.Broken);
        got.Reason.Should().Be("plain_companion");
    }

    [Theory]
    [InlineData("plain wal of chat", "chat.db-wal", true)]
    [InlineData("plain wal of chat without a page-1 frame", "chat.db-wal", false)]
    [InlineData("plain wal of the vault", "beememorybank.db-wal", true)]
    [InlineData("plain wal of the vault without a page-1 frame", "beememorybank.db-wal", false)]
    public void A_wal_made_by_a_real_plain_connection_is_a_plain_companion(string label, string name, bool withPageOne)
    {
        var dir = SealedPair();
        Copy(withPageOne ? Pool.PlainWal : Pool.PlainWalNoPageOne, dir, name);

        Check(VaultStoreFiles.Diagnose(dir), VaultStoreMode.Broken, "plain_companion", name);
        label.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("chat.db-journal")]
    [InlineData("beememorybank.db-journal")]
    public void A_rollback_journal_made_by_a_real_plain_connection_is_a_plain_companion(string name)
    {
        var dir = SealedPair();
        Copy(Pool.PlainJournal, dir, name);

        Check(VaultStoreFiles.Diagnose(dir), VaultStoreMode.Broken, "plain_companion", name);
    }

    [Fact]
    public void Two_companions_at_once_are_both_named()
    {
        var dir = SealedPair();
        Copy(Pool.PlainChat, dir, "chat.db");
        Copy(Pool.PlainWal, dir, "beememorybank.db-wal");
        Copy(Pool.PlainJournal, dir, "chat.db-journal");

        Check(VaultStoreFiles.Diagnose(dir), VaultStoreMode.Broken, "plain_companion", "chat.db", "beememorybank.db-wal", "chat.db-journal");
    }

    [Fact]
    public void Plain_pages_do_not_hide_behind_a_file_name_a_wal_whose_header_says_wal_is_judged_by_its_pages()
    {
        // The plain WAL copied under the name of a renamed plain database copy is still a plain WAL beside the pair.
        var dir = SealedPair();
        Copy(Pool.PlainWal, dir, "chat.db-wal");
        File.Copy(Path.Combine(dir, "chat.db-wal"), Path.Combine(dir, "renamed-copy.bin"));

        Check(VaultStoreFiles.Diagnose(dir), VaultStoreMode.Broken, "plain_companion", "chat.db-wal");
    }

    // ---------------------------------------------------------------- sealed, not refused

    [Fact]
    public void A_sealed_pair_with_no_chat_database_or_a_sealed_one_or_empty_files_is_sealed()
    {
        var absent = SealedPair();
        Check(VaultStoreFiles.Diagnose(absent), VaultStoreMode.Sealed);

        var sealedChat = SealedPair();
        Copy(Pool.SealedChat, sealedChat, "chat.db");
        Check(VaultStoreFiles.Diagnose(sealedChat), VaultStoreMode.Sealed);

        var empty = SealedPair();
        foreach (var name in new[] { "chat.db", "chat.db-wal", "chat.db-journal", "beememorybank.db-wal", "beememorybank.db-journal" })
            File.WriteAllBytes(Path.Combine(empty, name), []);
        Check(VaultStoreFiles.Diagnose(empty), VaultStoreMode.Sealed);
    }

    [Fact]
    public void The_sealed_pairs_own_wal_and_journal_have_a_plain_header_and_encrypted_pages_and_are_not_refused()
    {
        // The control that makes the test mean something: a sealed WAL and journal DO begin like plain ones.
        var walHead = File.ReadAllBytes(Pool.SealedWal)[..8];
        walHead[..4].Should().Equal(0x37, 0x7F, 0x06, 0x82);
        File.ReadAllBytes(Pool.SealedJournal)[..8].Should().Equal(0xD9, 0xD5, 0x05, 0xF9, 0x20, 0xA1, 0x63, 0xD7);
        new FileInfo(Pool.SealedWal).Length.Should().BeGreaterThan(32 + 24 + 4096);
        new FileInfo(Pool.SealedJournal).Length.Should().BeGreaterThan(512 + 4096);

        var dir = SealedPair();
        Copy(Pool.SealedWal, dir, "beememorybank.db-wal");
        Copy(Pool.SealedJournal, dir, "beememorybank.db-journal");
        Copy(Pool.SealedChat, dir, "chat.db");
        Copy(Pool.SealedWal, dir, "chat.db-wal");
        Copy(Pool.SealedJournal, dir, "chat.db-journal");

        Check(VaultStoreFiles.Diagnose(dir), VaultStoreMode.Sealed);
    }

    [Fact]
    public void A_wal_with_four_thousand_random_frames_and_a_wal_with_an_invalid_header_are_not_refused()
    {
        var random = new byte[32 + 4000 * (24 + 4096)];
        RandomNumberGenerator.Fill(random);
        // a valid WAL header: magic, version 3007000, page size 4096
        new byte[] { 0x37, 0x7F, 0x06, 0x82, 0x00, 0x2D, 0xE2, 0x18, 0x00, 0x00, 0x10, 0x00 }.CopyTo(random, 0);
        var dir = SealedPair();
        File.WriteAllBytes(Path.Combine(dir, "chat.db-wal"), random);
        Check(VaultStoreFiles.Diagnose(dir), VaultStoreMode.Sealed);

        // an invalid header: SQLite ignores such a file
        var garbage = (byte[])random.Clone();
        garbage[10] = 0x03; // a page size that is not a power of two
        File.WriteAllBytes(Path.Combine(dir, "chat.db-wal"), garbage);
        Check(VaultStoreFiles.Diagnose(dir), VaultStoreMode.Sealed);
    }

    [WindowsOnlyFact]
    public void A_companion_that_cannot_be_read_is_companion_unreadable_and_closes_the_start()
    {
        var dir = SealedPair();
        Copy(Pool.SealedChat, dir, "chat.db");
        using var hold = new FileStream(Path.Combine(dir, "chat.db"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Check(VaultStoreFiles.Diagnose(dir), VaultStoreMode.Broken, "companion_unreadable", "chat.db");
    }

    // ---------------------------------------------------------------- the plain page test

    [Fact]
    public void Every_page_of_a_real_plain_database_passes_the_plain_page_test_and_none_of_a_real_sealed_one()
    {
        var plain = File.ReadAllBytes(Pool.PlainMain);
        var plainPages = plain.Length / 4096;
        plainPages.Should().BeGreaterThan(8);
        var b = 0;
        for (var i = 0; i < plainPages; i++)
        {
            var page = plain.AsSpan(i * 4096, 4096);
            var isFreelistOrOverflow = page[0] is not (2 or 5 or 10 or 13) && i != 0;
            if (isFreelistOrOverflow) continue; // only b-tree pages are claimed (spec 19.17)
            VaultStoreFiles.LooksLikePlainPage(page, i == 0).Should().BeTrue($"page {i + 1} of a plain database");
            b++;
        }
        b.Should().BeGreaterThan(5);

        var sealedBytes = File.ReadAllBytes(Pool.SealedMain);
        for (var i = 0; i < sealedBytes.Length / 4096; i++)
            VaultStoreFiles.LooksLikePlainPage(sealedBytes.AsSpan(i * 4096, 4096), i == 0).Should().BeFalse($"page {i + 1} of a sealed database");
    }

    [Fact]
    public void Four_million_random_pages_pass_none()
    {
        var page = new byte[4096];
        var random = new Random(20261010); // a fixed seed: the result must not change from run to run
        var passed = 0;
        for (var i = 0; i < 4_000_000; i++)
        {
            random.NextBytes(page);
            if (VaultStoreFiles.LooksLikePlainPage(page, i % 2 == 0)) passed++;
        }

        passed.Should().Be(0);
    }

    // ---------------------------------------------------------------- a restore that is interrupted, and nothing is written

    [Fact]
    public void A_sealed_set_with_one_file_removed_is_refused_with_a_named_reason_never_an_empty_vault()
    {
        var withoutBoot = SealedPair();
        File.Delete(Path.Combine(withoutBoot, "beememorybank.boot.db"));
        Check(VaultStoreFiles.Diagnose(withoutBoot), VaultStoreMode.Broken, "db_without_boot", "beememorybank.db");

        var withoutDb = SealedPair();
        File.Delete(Path.Combine(withoutDb, "beememorybank.db"));
        Check(VaultStoreFiles.Diagnose(withoutDb), VaultStoreMode.Broken, "db_missing_or_empty", "beememorybank.db");
    }

    [Fact]
    public void A_plain_backup_chat_database_copied_over_a_sealed_set_is_refused_at_the_start_not_later()
    {
        var dir = SealedPair();
        Copy(Pool.PlainChat, dir, "chat.db");

        VaultStoreFiles.DetectMode(dir).Should().Be(VaultStoreMode.Broken);
    }

    [Fact]
    public void Diagnose_creates_moves_and_deletes_nothing_the_hashes_of_every_file_are_the_same_before_and_after()
    {
        var dirs = new List<string>();
        var a = SealedPair(); Copy(Pool.PlainChat, a, "chat.db"); Copy(Pool.PlainWal, a, "beememorybank.db-wal"); dirs.Add(a);
        var b = SealedPair(); Copy(Pool.SealedWal, b, "beememorybank.db-wal"); dirs.Add(b);
        var c = NewDir(); Copy(Pool.SealedMain, c, "beememorybank.db"); dirs.Add(c);
        var d = SealedPair(); File.WriteAllBytes(Path.Combine(d, "beememorybank.db.seal-old"), [9]); dirs.Add(d);

        foreach (var dir in dirs)
        {
            var before = Snapshot(dir);
            VaultStoreFiles.Diagnose(dir);
            Snapshot(dir).Should().Equal(before);
        }
    }

    private static Dictionary<string, string> Snapshot(string dir) =>
        Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(dir, p), p => File.Exists(p) ? Convert.ToHexString(SHA256.HashData(SealedCorpus.ReadShared(p))) : "<dir>");
}
