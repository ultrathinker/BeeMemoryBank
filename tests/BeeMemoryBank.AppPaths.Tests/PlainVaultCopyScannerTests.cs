using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using BeeMemoryBank.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace BeeMemoryBank.AppPaths.Tests;

/// <summary>
/// <see cref="PlainVaultCopyScanner"/> (docs/design/step-b-spec.md 10.2, red test 15.1.14): content-based, skips nothing, every member of
/// every archive. Every planted case carries NO canary and a table set that matches nothing the product has, so no schema, name or
/// canary heuristic can pass it; the archive cases are red on a scanner that looks at the first entry or at names only.
/// </summary>
public sealed class PlainVaultCopyScannerTests : IClassFixture<PlainVaultCopyScannerTests.Corpus>, IDisposable
{
    public sealed class Corpus : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "bmb_scan_" + Guid.NewGuid().ToString("N"));
        public SealedCorpus.Pool Pool { get; }
        public byte[] PlainImage { get; }

        public Corpus()
        {
            Directory.CreateDirectory(Root);
            Pool = SealedCorpus.Pool.Create(Root);
            PlainImage = File.ReadAllBytes(Pool.PlainMain);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, true); } catch { }
        }
    }

    private readonly Corpus _corpus;
    private readonly ITestOutputHelper _output;

    public PlainVaultCopyScannerTests(Corpus corpus, ITestOutputHelper output)
    {
        _corpus = corpus;
        _output = output;
    }

    public void Dispose() { }

    private SealedCorpus.Pool Pool => _corpus.Pool;
    private byte[] Image => _corpus.PlainImage;

    private string NewDir()
    {
        var dir = Path.Combine(_corpus.Root, "d-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Plant(string dir, string relative, byte[] bytes)
    {
        var path = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static IReadOnlyList<PlainCopyFinding> Preflight(string dir, PlainScanLimits? limits = null) =>
        PlainVaultCopyScanner.Scan(dir, PlainScanMode.Preflight, limits);

    private static void ExpectOne(IReadOnlyList<PlainCopyFinding> findings, string path, string kind)
    {
        findings.Should().ContainSingle($"exactly the planted file is found, got [{string.Join("; ", findings.Select(f => f.RelativePath + " " + f.Kind))}]");
        findings[0].RelativePath.Should().Be(path);
        findings[0].Kind.Should().Be(kind);
    }

    private static byte[] Bmbdb2() => ArchiveBuilder.WithMagic("BMBDB2\0\0"u8.ToArray(), 6000);

    // ---------------------------------------------------------------- plain files, wherever they are and whatever they are called

    [Theory]
    [InlineData("updates/pre-update-x/beememorybank.db")]
    [InlineData("tmp/snapshot-staging-1.db")]
    [InlineData("restore.standalone-staging")]
    [InlineData("tmp/chat-only-copy.bin")]
    [InlineData("tmp/x.jpg")]
    [InlineData("media/ab/cd/blob.enc")]
    [InlineData("search-index-segments/seg-0001.idx")]
    [InlineData("snapshots/old/backup.dat")]
    [InlineData("baselines/old.bmb")]
    [InlineData("baselines/current/baseline.bin")]
    [InlineData("pre-encryption/copy.dat")]
    [InlineData("deep/er/and/deeper/still/readme.txt")]
    public void A_plain_sqlite_image_is_found_wherever_it_lies_and_whatever_it_is_called(string relative)
    {
        var dir = NewDir();
        Plant(dir, relative, Image);

        ExpectOne(Preflight(dir), relative, PlainCopyKinds.PlainSqlite);
    }

    [Fact]
    public void A_chat_only_database_is_found_too_there_is_no_table_name_test()
    {
        var dir = NewDir();
        File.Copy(Pool.PlainChat, Path.Combine(dir, "tmp-chat.bin"));

        ExpectOne(Preflight(dir), "tmp-chat.bin", PlainCopyKinds.PlainSqlite);
    }

    [Fact]
    public void A_stray_wal_and_journal_made_by_a_real_plain_connection_are_found_and_so_is_a_wal_with_no_page_one_frame()
    {
        var dir = NewDir();
        File.Copy(Pool.PlainWal, Path.Combine(dir, "stray.db-wal"));
        File.Copy(Pool.PlainJournal, Path.Combine(dir, "stray.db-journal"));
        File.Copy(Pool.PlainWalNoPageOne, Path.Combine(dir, "tmp-nop1.wal"));

        var findings = Preflight(dir);

        findings.Select(f => (f.RelativePath, f.Kind)).Should().BeEquivalentTo(new[]
        {
            ("stray.db-journal", PlainCopyKinds.PlainJournal),
            ("stray.db-wal", PlainCopyKinds.PlainJournal),
            ("tmp-nop1.wal", PlainCopyKinds.PlainJournal),
        });
    }

    [Fact]
    public void A_gzip_of_a_bare_database_is_found()
    {
        var dir = NewDir();
        Plant(dir, "tmp/copy.db.gz", ArchiveBuilder.Gzip(Image));

        ExpectOne(Preflight(dir), "tmp/copy.db.gz", PlainCopyKinds.PlainArchive);
    }

    // ---------------------------------------------------------------- archives: every member, whatever it is called

    [Fact]
    public void A_tar_gz_whose_first_entry_is_the_database_is_found()
    {
        var dir = NewDir();
        Plant(dir, "snapshots/old.tar.gz", ArchiveBuilder.Gzip(ArchiveBuilder.Tar(TarEntryFormat.Pax,
            ("beememorybank.db", Image), ("manifest.json", "{}"u8.ToArray()))));

        ExpectOne(Preflight(dir), "snapshots/old.tar.gz", PlainCopyKinds.PlainArchive);
    }

    [Theory]
    [InlineData("payload.bin", 1)]
    [InlineData("data", 1)]
    [InlineData("x.jpg", 1)]
    [InlineData("beememorybank.db", 1)]
    [InlineData("payload.bin", 3)]
    [InlineData("data", 3)]
    [InlineData("x.jpg", 4)]
    [InlineData("beememorybank.db", 4)]
    public void A_database_as_a_later_member_of_a_tar_gz_is_found_under_any_name_after_a_manifest_a_text_file_and_a_real_BMBDB2_entry(string name, int index)
    {
        // manifest, a text file, a real BMBDB2 entry, a filler: the database goes in at the given index (1 = second, 3 = fourth, 4 = last)
        var members = new List<(string, byte[])>
        {
            ("manifest.json", "{\"v\":1}"u8.ToArray()),
            ("notes.txt", "hello"u8.ToArray()),
            ("db.bmbdb2", Bmbdb2()),
            ("filler-a.bin", ArchiveBuilder.RandomBytes(3000, 1)),
        };
        members.Insert(index, (name, Image));
        var dir = NewDir();
        Plant(dir, "snapshots/snap.tar.gz", ArchiveBuilder.Gzip(ArchiveBuilder.Tar(TarEntryFormat.Pax, members.ToArray())));

        var findings = Preflight(dir);

        ExpectOne(findings, "snapshots/snap.tar.gz", PlainCopyKinds.PlainArchive);
        findings[0].Detail.Should().Contain(name, "the finding names the member");
    }

    [Theory]
    [InlineData(TarEntryFormat.Pax)]
    [InlineData(TarEntryFormat.Gnu)]
    [InlineData(TarEntryFormat.Ustar)]
    [InlineData(TarEntryFormat.V7)]
    public void An_uncompressed_tar_with_the_database_as_a_later_entry_is_found_in_every_tar_format(TarEntryFormat format)
    {
        var dir = NewDir();
        Plant(dir, "updates/pack.tar", ArchiveBuilder.Tar(format,
            ("manifest.json", "{}"u8.ToArray()), ("readme", "x"u8.ToArray()), ("blob", Image)));

        ExpectOne(Preflight(dir), "updates/pack.tar", PlainCopyKinds.PlainArchive);
    }

    [Theory]
    [InlineData("payload", CompressionLevel.NoCompression)]
    [InlineData("payload", CompressionLevel.Optimal)]
    [InlineData("a/b/c", CompressionLevel.NoCompression)]
    [InlineData("a/b/c", CompressionLevel.Optimal)]
    public void A_database_as_an_extensionless_or_nested_zip_entry_is_found_stored_or_deflated(string entry, CompressionLevel level)
    {
        var dir = NewDir();
        Plant(dir, "export.zip", ArchiveBuilder.Zip(level, ("notes/one.md", "# one"u8.ToArray()), ("img/a.png", ArchiveBuilder.RandomBytes(2000)), (entry, Image)));

        ExpectOne(Preflight(dir), "export.zip", PlainCopyKinds.PlainArchive);
    }

    [Fact]
    public void A_database_as_the_first_zip_entry_is_found_as_well()
    {
        var dir = NewDir();
        Plant(dir, "x.zip", ArchiveBuilder.Zip(CompressionLevel.Optimal, ("db", Image), ("later.txt", "x"u8.ToArray())));

        ExpectOne(Preflight(dir), "x.zip", PlainCopyKinds.PlainArchive);
    }

    // ---------------------------------------------------------------- not examined: fail closed

    [Fact]
    public void A_nested_archive_is_unverified_whichever_way_it_is_nested()
    {
        var inner = ArchiveBuilder.Gzip("inner text"u8.ToArray());
        var tar = ArchiveBuilder.Tar(TarEntryFormat.Ustar, ("a.txt", "a"u8.ToArray()));
        var zip = ArchiveBuilder.Zip(CompressionLevel.Optimal, ("z.txt", "z"u8.ToArray()));
        var dir = NewDir();
        Plant(dir, "tar-gz-with-gzip.tar.gz", ArchiveBuilder.Gzip(ArchiveBuilder.Tar(TarEntryFormat.Pax, ("manifest", "{}"u8.ToArray()), ("inner.gz", inner))));
        Plant(dir, "tar-with-tar.tar", ArchiveBuilder.Tar(TarEntryFormat.Pax, ("manifest", "{}"u8.ToArray()), ("inner.tar", tar)));
        Plant(dir, "zip-with-zip.zip", ArchiveBuilder.Zip(CompressionLevel.Optimal, ("inner.zip", zip)));
        Plant(dir, "zip-with-gzip.zip", ArchiveBuilder.Zip(CompressionLevel.Optimal, ("inner.gz", inner)));

        var findings = Preflight(dir);

        findings.Select(f => (f.RelativePath, f.Kind)).Should().BeEquivalentTo(new[]
        {
            ("tar-gz-with-gzip.tar.gz", PlainCopyKinds.UnverifiedArchive),
            ("tar-with-tar.tar", PlainCopyKinds.UnverifiedArchive),
            ("zip-with-zip.zip", PlainCopyKinds.UnverifiedArchive),
            ("zip-with-gzip.zip", PlainCopyKinds.UnverifiedArchive),
        });
        findings.Should().OnlyContain(f => f.Detail!.Contains("inside an archive"));
    }

    [Fact]
    public void An_archive_with_a_bzip2_xz_or_zstd_member_is_unverified()
    {
        var dir = NewDir();
        Plant(dir, "bz.tar", ArchiveBuilder.Tar(TarEntryFormat.Ustar, ("m", "x"u8.ToArray()), ("b", ArchiveBuilder.WithMagic(ArchiveBuilder.Bzip2))));
        Plant(dir, "xz.tar", ArchiveBuilder.Tar(TarEntryFormat.Ustar, ("m", "x"u8.ToArray()), ("b", ArchiveBuilder.WithMagic(ArchiveBuilder.Xz))));
        Plant(dir, "zst.zip", ArchiveBuilder.Zip(CompressionLevel.NoCompression, ("m", "x"u8.ToArray()), ("b", ArchiveBuilder.WithMagic(ArchiveBuilder.Zstd))));

        Preflight(dir).Select(f => f.Kind).Should().OnlyContain(k => k == PlainCopyKinds.UnverifiedArchive).And.HaveCount(3);
    }

    [Theory]
    [InlineData("bzip2")]
    [InlineData("xz")]
    [InlineData("zstd")]
    [InlineData("lz4")]
    [InlineData("7z")]
    [InlineData("rar")]
    public void A_top_level_file_with_the_magic_of_a_format_this_version_cannot_open_is_unverified(string format)
    {
        var magic = format switch
        {
            "bzip2" => ArchiveBuilder.Bzip2, "xz" => ArchiveBuilder.Xz, "zstd" => ArchiveBuilder.Zstd,
            "lz4" => ArchiveBuilder.Lz4, "7z" => ArchiveBuilder.SevenZip, _ => ArchiveBuilder.Rar,
        };
        var dir = NewDir();
        Plant(dir, "logs/old." + format, ArchiveBuilder.WithMagic(magic));

        ExpectOne(Preflight(dir), "logs/old." + format, PlainCopyKinds.UnverifiedArchive);
    }

    [Fact]
    public void A_tar_gz_cut_inside_its_second_entry_is_unverified()
    {
        var tar = ArchiveBuilder.Tar(TarEntryFormat.Pax, ("one", ArchiveBuilder.RandomBytes(20_000, 1)), ("two", ArchiveBuilder.RandomBytes(20_000, 2)),
            ("three", ArchiveBuilder.RandomBytes(20_000, 3)));
        var gz = ArchiveBuilder.Gzip(tar);
        var dir = NewDir();
        Plant(dir, "cut.tar.gz", gz[..(gz.Length * 45 / 100)]);

        ExpectOne(Preflight(dir), "cut.tar.gz", PlainCopyKinds.UnverifiedArchive);
    }

    [Fact]
    public void An_uncompressed_tar_cut_inside_an_entry_is_unverified_too()
    {
        var tar = ArchiveBuilder.Tar(TarEntryFormat.Ustar, ("one", ArchiveBuilder.RandomBytes(20_000, 1)), ("two", ArchiveBuilder.RandomBytes(20_000, 2)));
        var dir = NewDir();
        Plant(dir, "cut.tar", tar[..(tar.Length * 70 / 100)]);

        ExpectOne(Preflight(dir), "cut.tar", PlainCopyKinds.UnverifiedArchive);
    }

    [Fact]
    public void A_gzip_with_a_valid_header_and_a_corrupt_stream_is_unverified()
    {
        var dir = NewDir();
        var corrupt = new byte[2000];
        new byte[] { 0x1F, 0x8B, 0x08, 0, 0, 0, 0, 0, 0, 0xFF }.CopyTo(corrupt, 0);
        new Random(3).NextBytes(corrupt.AsSpan(10));
        corrupt[10] = 0x07; // BFINAL = 1, BTYPE = 11: a reserved block type no deflate stream may start with
        Plant(dir, "corrupt.gz", corrupt);

        ExpectOne(Preflight(dir), "corrupt.gz", PlainCopyKinds.UnverifiedArchive);
    }

    [Fact]
    public void A_non_tar_gzip_with_a_corrupt_tail_is_unverified_even_when_its_prefix_is_ordinary_data()
    {
        var dir = NewDir();
        var corrupt = ArchiveBuilder.Gzip(ArchiveBuilder.RandomBytes(20_000, 23));
        corrupt[^8] ^= 0xFF; // damage the gzip CRC after the first 512 inflated bytes
        Plant(dir, "corrupt-tail.gz", corrupt);

        ExpectOne(Preflight(dir), "corrupt-tail.gz", PlainCopyKinds.UnverifiedArchive);
    }

    [Fact]
    public void A_zip_member_with_corrupt_data_after_the_prefix_probe_is_unverified()
    {
        var dir = NewDir();
        var corrupt = ArchiveBuilder.ZipWithInvalidDeflateTail("ordinary.bin", ArchiveBuilder.RandomBytes(1_000, 17));
        PlainVaultCopyScanner.IsZipHeader(corrupt.AsSpan(0, 4)).Should().BeTrue();
        using (var checkFile = new MemoryStream(corrupt))
        using (var checkZip = new ZipArchive(checkFile, ZipArchiveMode.Read))
        using (var checkEntry = checkZip.Entries[0].Open())
        {
            var prefix = new byte[512];
            checkEntry.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: true);
            var readTail = () => checkEntry.CopyTo(Stream.Null);
            readTail.Should().Throw<InvalidDataException>();
        }
        Plant(dir, "corrupt-tail.zip", corrupt);

        var findings = Preflight(dir);
        ExpectOne(findings, "corrupt-tail.zip", PlainCopyKinds.UnverifiedArchive);
    }

    [Fact]
    public void A_tar_whose_second_header_has_a_bad_checksum_is_unverified()
    {
        var tar = ArchiveBuilder.Tar(TarEntryFormat.Ustar, ("one", ArchiveBuilder.RandomBytes(300, 1)), ("two", ArchiveBuilder.RandomBytes(300, 2)),
            ("three", ArchiveBuilder.RandomBytes(300, 3)));
        var dir = NewDir();
        Plant(dir, "badsum.tar", ArchiveBuilder.TarWithBadChecksum(tar, entryIndex: 1, memberDataSize: 300));

        ExpectOne(Preflight(dir), "badsum.tar", PlainCopyKinds.UnverifiedArchive);
    }

    [Fact]
    public void A_zip_entry_marked_encrypted_one_with_an_unsupported_method_and_an_unreadable_central_directory_are_unverified()
    {
        var zip = ArchiveBuilder.Zip(CompressionLevel.Optimal, ("a.txt", "hello hello hello"u8.ToArray()), ("b.txt", "world"u8.ToArray()));
        var dir = NewDir();
        Plant(dir, "encrypted.zip", ArchiveBuilder.ZipWithFlags(zip, 0x1));
        Plant(dir, "method99.zip", ArchiveBuilder.ZipWithMethod(zip, 99));
        Plant(dir, "bzip2-method.zip", ArchiveBuilder.ZipWithMethod(zip, 12));
        Plant(dir, "no-directory.zip", zip[..^30]);

        var findings = Preflight(dir);

        findings.Select(f => (f.RelativePath, f.Kind)).Should().BeEquivalentTo(new[]
        {
            ("encrypted.zip", PlainCopyKinds.UnverifiedArchive),
            ("method99.zip", PlainCopyKinds.UnverifiedArchive),
            ("bzip2-method.zip", PlainCopyKinds.UnverifiedArchive),
            ("no-directory.zip", PlainCopyKinds.UnverifiedArchive),
        });
    }

    [Fact]
    public void An_archive_over_the_entry_limit_and_one_over_the_inflated_bytes_limit_are_unverified()
    {
        var many = Enumerable.Range(0, 12).Select(i => ($"e{i}", ArchiveBuilder.RandomBytes(100, i))).ToArray();
        var big = ArchiveBuilder.Tar(TarEntryFormat.Ustar, ("one", ArchiveBuilder.RandomBytes(400_000, 1)), ("two", ArchiveBuilder.RandomBytes(400_000, 2)),
            ("three", ArchiveBuilder.RandomBytes(400_000, 3)));
        var dir = NewDir();
        Plant(dir, "many.tar", ArchiveBuilder.Tar(TarEntryFormat.Ustar, many));
        Plant(dir, "many.zip", ArchiveBuilder.Zip(CompressionLevel.NoCompression, many));
        Plant(dir, "big.tar.gz", ArchiveBuilder.Gzip(big));

        var limits = new PlainScanLimits(MaxArchiveEntries: 5, MaxInflatedBytes: 600_000);
        var findings = Preflight(dir, limits);

        findings.Select(f => (f.RelativePath, f.Kind)).Should().BeEquivalentTo(new[]
        {
            ("many.tar", PlainCopyKinds.UnverifiedArchive),
            ("many.zip", PlainCopyKinds.UnverifiedArchive),
            ("big.tar.gz", PlainCopyKinds.UnverifiedArchive),
        });
        Preflight(dir).Should().BeEmpty("the same archives are fine under the default limits");
    }

    [Fact]
    public void A_zip_whose_member_passes_the_prefix_and_then_exceeds_the_inflated_limit_is_unverified()
    {
        // The first 512 bytes of the member are ordinary data; the rest of the member takes the archive past the limit.
        // That is a finding of the scan (spec 10.2, a resource limit), not an exception that escapes the scanner.
        var zip = ArchiveBuilder.Zip(CompressionLevel.Optimal, ("ordinary.txt", Enumerable.Repeat((byte)'a', 1_000).ToArray()));
        var dir = NewDir();
        Plant(dir, "over-limit.zip", zip);

        var findings = Preflight(dir, new PlainScanLimits(MaxInflatedBytes: 600));

        ExpectOne(findings, "over-limit.zip", PlainCopyKinds.UnverifiedArchive);
        findings[0].Detail.Should().Be("over the size limit of the scan");
        Preflight(dir).Should().BeEmpty("the same archive is fine under the default limits");
    }

    // ---------------------------------------------------------------- unreadable, links, other names

    [WindowsOnlyFact]
    public void A_file_it_cannot_open_is_unreadable_and_the_scan_goes_on()
    {
        var dir = NewDir();
        Plant(dir, "locked.bin", Image);
        Plant(dir, "other.bin", Image);
        using var hold = new FileStream(Path.Combine(dir, "locked.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var findings = Preflight(dir);

        findings.Single(f => f.RelativePath == "locked.bin").Kind.Should().Be(PlainCopyKinds.Unreadable);
        findings.Single(f => f.RelativePath == "other.bin").Kind.Should().Be(PlainCopyKinds.PlainSqlite);
    }

    [Fact]
    public void A_symbolic_link_or_a_directory_link_is_a_finding_of_kind_link_and_its_target_is_not_scanned()
    {
        var dir = NewDir();
        var outside = NewDir();
        Plant(outside, "target.bin", Image);
        Plant(outside, "folder/inner.bin", Image);
        var fileLink = Path.Combine(dir, "link.bin");
        var folderLink = Path.Combine(dir, "folder-link");
        var made = 0;
        try { File.CreateSymbolicLink(fileLink, Path.Combine(outside, "target.bin")); made++; } catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        try { Directory.CreateSymbolicLink(folderLink, Path.Combine(outside, "folder")); made++; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            if (OperatingSystem.IsWindows() && MakeJunction(folderLink, Path.Combine(outside, "folder"))) made++;
        }
        if (made == 0) return; // this account may create no links at all

        var findings = Preflight(dir);

        findings.Should().OnlyContain(f => f.Kind == PlainCopyKinds.Link, "only the links are reported; their targets are never scanned");
        findings.Should().HaveCount(made);
    }

    private static bool MakeJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
        process!.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    [Fact]
    public void A_hard_link_to_the_live_database_is_shares_identity_and_the_live_file_is_live_linked()
    {
        var dir = NewDir();
        File.Copy(Pool.PlainMain, Path.Combine(dir, "beememorybank.db"));
        TestLinks.CreateHardLink(Path.Combine(dir, "beememorybank.db"), Path.Combine(dir, "tmp-hidden-name.bin"));

        var findings = Preflight(dir);

        findings.Select(f => (f.RelativePath, f.Kind)).Should().BeEquivalentTo(new[]
        {
            ("tmp-hidden-name.bin", PlainCopyKinds.SharesIdentity),
            ("beememorybank.db", PlainCopyKinds.LiveLinked),
        });
        findings.Single(f => f.Kind == PlainCopyKinds.SharesIdentity).Detail.Should().Contain("beememorybank.db");
        PlainCopyKinds.CanBeErased(PlainCopyKinds.SharesIdentity).Should().BeFalse();
        PlainCopyKinds.CanBeErased(PlainCopyKinds.LiveLinked).Should().BeFalse();
    }

    [Fact]
    public void A_live_file_with_a_second_name_outside_the_data_directory_is_live_linked()
    {
        var dir = NewDir();
        var elsewhere = NewDir();
        File.Copy(Pool.PlainMain, Path.Combine(dir, "beememorybank.db"));
        File.Copy(Pool.PlainChat, Path.Combine(dir, "chat.db"));
        TestLinks.CreateHardLink(Path.Combine(dir, "chat.db"), Path.Combine(elsewhere, "my-chat-backup.db"));

        ExpectOne(Preflight(dir), "chat.db", PlainCopyKinds.LiveLinked);
    }

    // ---------------------------------------------------------------- the two modes and the live files

    [Fact]
    public void In_Preflight_mode_the_plain_live_files_and_their_sidecars_are_allowed_only_at_the_top_level()
    {
        var dir = NewDir();
        File.Copy(Pool.PlainMain, Path.Combine(dir, "beememorybank.db"));
        File.Copy(Pool.PlainChat, Path.Combine(dir, "chat.db"));
        File.Copy(Pool.PlainWal, Path.Combine(dir, "beememorybank.db-wal"));
        File.WriteAllBytes(Path.Combine(dir, "beememorybank.db-shm"), new byte[32768]);
        File.Copy(Pool.PlainJournal, Path.Combine(dir, "chat.db-journal"));
        Preflight(dir).Should().BeEmpty("the files the migration is about to replace");

        Plant(dir, "tmp/beememorybank.db", Image);
        ExpectOne(Preflight(dir), "tmp/beememorybank.db", PlainCopyKinds.PlainSqlite);
    }

    [Fact]
    public void In_Sealed_mode_a_plain_database_is_a_finding_and_the_plain_boot_store_and_its_sidecars_are_allowed()
    {
        var dir = NewDir();
        File.Copy(Pool.SealedMain, Path.Combine(dir, "beememorybank.db"));
        SealedCorpus.MakeBoot(Path.Combine(dir, "beememorybank.boot.db"));
        File.WriteAllBytes(Path.Combine(dir, "beememorybank.boot.db-shm"), new byte[32768]);
        PlainVaultCopyScanner.Scan(dir, PlainScanMode.Sealed).Should().BeEmpty();

        File.Delete(Path.Combine(dir, "beememorybank.db"));
        File.Copy(Pool.PlainMain, Path.Combine(dir, "beememorybank.db"));
        File.Copy(Pool.PlainChat, Path.Combine(dir, "chat.db"));

        PlainVaultCopyScanner.Scan(dir, PlainScanMode.Sealed).Select(f => (f.RelativePath, f.Kind)).Should().BeEquivalentTo(new[]
        {
            ("beememorybank.db", PlainCopyKinds.PlainSqlite), ("chat.db", PlainCopyKinds.PlainSqlite),
        });
    }

    [Fact]
    public void A_copy_of_the_boot_store_anywhere_is_not_a_finding_in_either_mode_by_its_application_id()
    {
        var dir = NewDir();
        SealedCorpus.MakeBoot(Path.Combine(dir, "updates", "pre-update-x", "beememorybank.boot.db"));
        SealedCorpus.MakeBoot(Path.Combine(dir, "tmp", "renamed-boot.bin"));

        Preflight(dir).Should().BeEmpty();
        PlainVaultCopyScanner.Scan(dir, PlainScanMode.Sealed).Should().BeEmpty();

        // a vault database has application id 0: the boot-store rule does not apply to it
        Plant(dir, "updates/pre-update-x/beememorybank.db", Image);
        ExpectOne(Preflight(dir), "updates/pre-update-x/beememorybank.db", PlainCopyKinds.PlainSqlite);
    }

    [Fact]
    public void A_sealed_nodes_own_live_wal_and_journal_made_by_real_keyed_connections_are_no_finding()
    {
        var dir = NewDir();
        File.Copy(Pool.SealedMain, Path.Combine(dir, "beememorybank.db"));
        File.Copy(Pool.SealedChat, Path.Combine(dir, "chat.db"));
        SealedCorpus.MakeBoot(Path.Combine(dir, "beememorybank.boot.db"));
        File.Copy(Pool.SealedWal, Path.Combine(dir, "beememorybank.db-wal"));
        File.Copy(Pool.SealedJournal, Path.Combine(dir, "beememorybank.db-journal"));
        File.Copy(Pool.SealedWal, Path.Combine(dir, "chat.db-wal"));
        // the control: they do begin like plain ones
        File.ReadAllBytes(Path.Combine(dir, "beememorybank.db-wal"))[..4].Should().Equal(0x37, 0x7F, 0x06, 0x82);

        PlainVaultCopyScanner.Scan(dir, PlainScanMode.Sealed).Should().BeEmpty();

        // and a plain WAL beside the same pair is one
        File.Copy(Pool.PlainWal, Path.Combine(dir, "beememorybank.db-wal"), overwrite: true);
        ExpectOne(PlainVaultCopyScanner.Scan(dir, PlainScanMode.Sealed), "beememorybank.db-wal", PlainCopyKinds.PlainJournal);
    }

    // ---------------------------------------------------------------- finds nothing

    [Fact]
    public void A_clean_folder_of_real_looking_snapshots_media_baselines_logs_and_exports_has_no_finding_in_either_mode()
    {
        var dir = NewDir();
        // real BMBDB2-style snapshots: the encrypted database, 10,000 random media blobs, a manifest, a signature and a key envelope
        var blobs = Enumerable.Range(0, 10_000).Select(i => ($"media/{i:D5}.enc", ArchiveBuilder.RandomBytes(600, i))).ToList();
        var members = new List<(string, byte[])> { ("manifest.json", "{\"v\":1}"u8.ToArray()), ("beememorybank.db", Bmbdb2()), ("signature", ArchiveBuilder.RandomBytes(64)),
            ("key-envelope.json", "{\"v\":1}"u8.ToArray()) };
        members.AddRange(blobs);
        Plant(dir, "snapshots/2026-10-09.bmb", ArchiveBuilder.Gzip(ArchiveBuilder.Tar(TarEntryFormat.Ustar, members.ToArray())));
        // a tar.gz whose beememorybank.db entry is a SQLCipher file
        Plant(dir, "snapshots/sealed-entry.tar.gz", ArchiveBuilder.Gzip(ArchiveBuilder.Tar(TarEntryFormat.Pax,
            ("manifest.json", "{}"u8.ToArray()), ("beememorybank.db", File.ReadAllBytes(Pool.SealedMain)))));
        for (var i = 0; i < 200; i++) Plant(dir, $"media/{i % 16:x}/{i:D4}.enc", ArchiveBuilder.RandomBytes(2000, 1000 + i));
        for (var i = 0; i < 20; i++) Plant(dir, $"baselines/current/{i}.bmbch1", ArchiveBuilder.WithMagic("BMBCH1\0\0"u8.ToArray(), 5000));
        Plant(dir, "logs/app.log.1.gz", ArchiveBuilder.Gzip(System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("a rotated log line\n", 200)))));
        Plant(dir, "logs/archive.tar.gz", ArchiveBuilder.Gzip(ArchiveBuilder.Tar(TarEntryFormat.Pax,
            ("a.log", System.Text.Encoding.UTF8.GetBytes(new string('x', 3000))), ("b.log", System.Text.Encoding.UTF8.GetBytes(new string('y', 3000))))));
        Plant(dir, "export/notes.zip", ArchiveBuilder.Zip(CompressionLevel.Optimal,
            ("notes/one.md", "# one\nsome markdown"u8.ToArray()), ("img/a.png", [.. new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, .. ArchiveBuilder.RandomBytes(3000)]),
            ("img/b.jpg", [.. new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, .. ArchiveBuilder.RandomBytes(3000, 2)])));
        SealedCorpus.MakeBoot(Path.Combine(dir, "updates", "pre-update-x", "beememorybank.boot.db"));
        // a WAL that holds only its header
        var header = new byte[32];
        new byte[] { 0x37, 0x7F, 0x06, 0x82, 0x00, 0x2D, 0xE2, 0x18, 0x00, 0x00, 0x10, 0x00 }.CopyTo(header, 0);
        Plant(dir, "tmp/header-only.wal", header);

        Preflight(dir).Should().BeEmpty();
        PlainVaultCopyScanner.Scan(dir, PlainScanMode.Sealed).Should().BeEmpty();
    }

    [Fact]
    public void A_missing_directory_has_no_finding_and_the_scan_does_not_stop_at_the_first_finding()
    {
        PlainVaultCopyScanner.Scan(Path.Combine(_corpus.Root, "nowhere"), PlainScanMode.Preflight).Should().BeEmpty();

        var dir = NewDir();
        for (var i = 0; i < 5; i++) Plant(dir, $"copy{i}.bin", Image);
        Preflight(dir).Should().HaveCount(5);
    }

    [Fact]
    public void The_scan_of_a_folder_with_fifty_thousand_files_is_timed()
    {
        var dir = NewDir();
        var media = Path.Combine(dir, "media");
        for (var d = 0; d < 100; d++)
        {
            var sub = Path.Combine(media, d.ToString("D3"));
            Directory.CreateDirectory(sub);
            for (var f = 0; f < 500; f++) File.WriteAllBytes(Path.Combine(sub, $"{f:D4}.enc"), ArchiveBuilder.RandomBytes(64, d * 500 + f));
        }
        File.Copy(Pool.PlainMain, Path.Combine(dir, "beememorybank.db"));

        var watch = Stopwatch.StartNew();
        var findings = Preflight(dir);
        watch.Stop();

        _output.WriteLine($"scan of 50,001 files: {watch.Elapsed.TotalSeconds:F1} s");
        findings.Should().BeEmpty();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromMinutes(2));
    }
}
