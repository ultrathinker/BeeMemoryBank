using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.TestSupport;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.AppPaths.Tests;

/// <summary>
/// <see cref="VaultStoreProbe"/> (AppPaths) and <see cref="VaultStoreFiles"/> (Storage, contract A.2) are two copies of the same start-time
/// checks, because AppPaths cannot reference Storage (docs/design/step-b-spec.md 7.6, 15.1.15). This project references both and feeds real
/// SQLite3 Multiple Ciphers files to both, so the copies cannot drift; the scanner's WAL and journal rule is held to the same answers.
/// </summary>
public sealed class VaultStoreProbeAgreementTests : IClassFixture<VaultStoreProbeAgreementTests.Corpus>
{
    public sealed class Corpus : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "bmb_probe_" + Guid.NewGuid().ToString("N"));
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

    public VaultStoreProbeAgreementTests(Corpus corpus) => _corpus = corpus;

    private SealedCorpus.Pool Pool => _corpus.Pool;

    private string NewDir()
    {
        var dir = Path.Combine(_corpus.Root, "d-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string SealedPair()
    {
        var dir = NewDir();
        File.Copy(Pool.SealedMain, Path.Combine(dir, "beememorybank.db"));
        SealedCorpus.MakeBoot(Path.Combine(dir, "beememorybank.boot.db"));
        return dir;
    }

    /// <summary>Every directory shape of red test 15.1.15, each made of real files.</summary>
    private IEnumerable<(string Label, string Dir)> Cases()
    {
        yield return ("empty directory", NewDir());
        yield return ("missing directory", Path.Combine(_corpus.Root, "does-not-exist"));

        var plainDb = NewDir(); File.Copy(Pool.PlainMain, Path.Combine(plainDb, "beememorybank.db"));
        yield return ("plain database only", plainDb);

        yield return ("sealed pair", SealedPair());

        var noBoot = NewDir(); File.Copy(Pool.SealedMain, Path.Combine(noBoot, "beememorybank.db"));
        yield return ("encrypted database without boot store", noBoot);

        var bootBesidePlain = NewDir(); File.Copy(Pool.PlainMain, Path.Combine(bootBesidePlain, "beememorybank.db"));
        SealedCorpus.MakeBoot(Path.Combine(bootBesidePlain, "beememorybank.boot.db"));
        yield return ("boot store beside a plain database", bootBesidePlain);

        var foreign = SealedPair(); File.Delete(Path.Combine(foreign, "beememorybank.boot.db")); File.Copy(Pool.PlainChat, Path.Combine(foreign, "beememorybank.boot.db"));
        yield return ("boot store that is not ours", foreign);

        var noDb = NewDir(); SealedCorpus.MakeBoot(Path.Combine(noDb, "beememorybank.boot.db"));
        yield return ("boot store without database", noDb);

        var emptyDb = NewDir(); SealedCorpus.MakeBoot(Path.Combine(emptyDb, "beememorybank.boot.db")); File.WriteAllBytes(Path.Combine(emptyDb, "beememorybank.db"), []);
        yield return ("boot store beside an empty database", emptyDb);

        var cut = SealedPair(); var cutDb = Path.Combine(cut, "beememorybank.db"); File.WriteAllBytes(cutDb, File.ReadAllBytes(cutDb)[..^10]);
        yield return ("database cut inside a page", cut);

        foreach (var (label, companion, source) in new[]
                 {
                     ("plain chat database", "chat.db", Pool.PlainChat), ("sealed chat database", "chat.db", Pool.SealedChat),
                     ("plain chat wal", "chat.db-wal", Pool.PlainWal), ("plain chat wal without page 1", "chat.db-wal", Pool.PlainWalNoPageOne),
                     ("plain vault wal", "beememorybank.db-wal", Pool.PlainWal), ("sealed vault wal", "beememorybank.db-wal", Pool.SealedWal),
                     ("sealed chat wal", "chat.db-wal", Pool.SealedWal),
                     ("plain vault journal", "beememorybank.db-journal", Pool.PlainJournal), ("sealed vault journal", "beememorybank.db-journal", Pool.SealedJournal),
                     ("plain chat journal", "chat.db-journal", Pool.PlainJournal), ("sealed chat journal", "chat.db-journal", Pool.SealedJournal),
                 })
        {
            var dir = SealedPair();
            File.Copy(source, Path.Combine(dir, companion));
            yield return (label, dir);
        }

        var two = SealedPair(); File.Copy(Pool.PlainChat, Path.Combine(two, "chat.db")); File.Copy(Pool.PlainWal, Path.Combine(two, "beememorybank.db-wal"));
        yield return ("two plain companions", two);

        var empties = SealedPair();
        foreach (var name in VaultStoreProbe.Companions) File.WriteAllBytes(Path.Combine(empties, name), []);
        yield return ("zero-length companions", empties);

        var random = SealedPair();
        var frames = new byte[32 + 4000 * (24 + 4096)];
        new Random(8).NextBytes(frames);
        new byte[] { 0x37, 0x7F, 0x06, 0x82, 0x00, 0x2D, 0xE2, 0x18, 0x00, 0x00, 0x10, 0x00 }.CopyTo(frames, 0);
        File.WriteAllBytes(Path.Combine(random, "chat.db-wal"), frames);
        yield return ("wal of 4000 random frames", random);
    }

    [Fact]
    public void IsSealedPair_and_PlainCompanions_agree_with_Diagnose_on_every_case_of_the_corpus()
    {
        var checkedCases = 0;
        foreach (var (label, dir) in Cases())
        {
            var diagnosis = VaultStoreFiles.Diagnose(dir);

            VaultStoreProbe.IsSealedPair(dir).Should().Be(diagnosis.Mode == VaultStoreMode.Sealed, label);
            if (diagnosis.Reason == "plain_companion")
                VaultStoreProbe.PlainCompanions(dir).Should().BeEquivalentTo(diagnosis.Files, label);
            else if (diagnosis.Mode == VaultStoreMode.Sealed)
                VaultStoreProbe.PlainCompanions(dir).Should().BeEmpty(label);
            checkedCases++;
        }

        checkedCases.Should().Be(24, "the corpus is the list of Cases(); a new case changes this number on purpose");
    }

    [Fact]
    public void The_seal_old_leftover_rule_is_the_one_place_the_two_differ_on_purpose()
    {
        var dir = SealedPair();
        File.WriteAllBytes(Path.Combine(dir, "beememorybank.db.seal-old"), [1]);
        File.WriteAllBytes(Path.Combine(dir, "chat.db.seal-old-wal"), [1]);

        VaultStoreProbe.HasSealOldLeftovers(dir).Should().Be(VaultStoreFiles.HasSealOldLeftovers(dir)).And.Be(true);
        VaultStoreProbe.SealOldLeftovers(dir).Should().BeEquivalentTo(VaultStoreFiles.SealOldLeftovers(dir));
        VaultStoreFiles.Diagnose(dir).Reason.Should().Be("seal_leftovers_no_journal");
        VaultStoreProbe.IsSealedPair(dir).Should().BeTrue("the resolver of 10.6 asks about the live pair while leftovers lie beside it");
    }

    [Fact]
    public void InspectCompanion_gives_the_same_answer_on_every_real_file_of_the_pool()
    {
        var files = new[]
        {
            Pool.SealedMain, Pool.SealedChat, Pool.PlainMain, Pool.PlainChat, Pool.SealedWal, Pool.PlainWal, Pool.PlainWalNoPageOne, Pool.SealedJournal, Pool.PlainJournal,
            Path.Combine(_corpus.Root, "missing-file"),
        };

        foreach (var file in files)
        {
            var storage = VaultStoreFiles.InspectCompanion(file);
            var probe = VaultStoreProbe.InspectCompanion(file);
            probe.ToString().Should().Be(storage.ToString(), Path.GetFileName(file));
        }
        VaultStoreProbe.InspectCompanion(Pool.PlainWal).Should().Be(ProbeFileState.Plain);
        VaultStoreProbe.InspectCompanion(Pool.SealedWal).Should().Be(ProbeFileState.Fine);
        VaultStoreProbe.InspectCompanion(Pool.SealedJournal).Should().Be(ProbeFileState.Fine);
    }

    [Fact]
    public void The_plain_page_test_agrees_on_real_pages_and_random_pages()
    {
        foreach (var path in new[] { Pool.PlainMain, Pool.SealedMain, Pool.PlainChat })
        {
            var bytes = File.ReadAllBytes(path);
            for (var i = 0; i < bytes.Length / 4096; i++)
            {
                var page = bytes.AsSpan(i * 4096, 4096);
                VaultStoreProbe.LooksLikePlainPage(page, i == 0).Should().Be(VaultStoreFiles.LooksLikePlainPage(page, i == 0));
            }
        }

        var random = new Random(77);
        var buffer = new byte[4096];
        for (var i = 0; i < 200_000; i++)
        {
            random.NextBytes(buffer);
            VaultStoreProbe.LooksLikePlainPage(buffer, i % 2 == 0).Should().Be(VaultStoreFiles.LooksLikePlainPage(buffer, i % 2 == 0));
        }
    }

    [Fact]
    public void The_magic_and_application_id_checks_agree()
    {
        SealedCorpus.MakeBoot(Path.Combine(_corpus.Root, "boot-for-agreement.db"));
        foreach (var path in new[] { Pool.PlainMain, Pool.SealedMain, Path.Combine(_corpus.Root, "boot-for-agreement.db"), Path.Combine(_corpus.Root, "missing-file") })
        {
            VaultStoreProbe.HasSqliteMagic(path).Should().Be(VaultStoreFiles.HasSqliteMagic(path), path);
            VaultStoreProbe.ReadApplicationId(path).Should().Be(VaultStoreFiles.ReadApplicationId(path), path);
        }
        VaultStoreProbe.BootApplicationId.Should().Be(VaultStoreFiles.BootApplicationId);
        VaultStoreProbe.Companions.Should().Equal(VaultStoreFiles.Companions);
        VaultStoreProbe.IsBootStore(Path.Combine(_corpus.Root, "boot-for-agreement.db")).Should().BeTrue();
        VaultStoreProbe.IsBootStore(Pool.PlainMain).Should().BeFalse();
    }

    [Fact]
    public void The_scanner_and_the_probe_give_the_same_plain_or_not_answer_for_every_wal_and_journal_of_the_pool()
    {
        foreach (var (file, expectedPlain) in new[]
                 {
                     (Pool.SealedWal, false), (Pool.SealedJournal, false), (Pool.PlainWal, true), (Pool.PlainWalNoPageOne, true), (Pool.PlainJournal, true),
                 })
        {
            var dir = NewDir();
            File.Copy(file, Path.Combine(dir, "stray.bin"));

            var scanner = PlainVaultCopyScanner.Scan(dir, PlainScanMode.Preflight).Any(f => f.Kind == PlainCopyKinds.PlainJournal);
            var probe = VaultStoreProbe.InspectCompanion(Path.Combine(dir, "stray.bin")) == ProbeFileState.Plain;

            scanner.Should().Be(probe, Path.GetFileName(file));
            probe.Should().Be(expectedPlain, Path.GetFileName(file));
        }
    }
}
