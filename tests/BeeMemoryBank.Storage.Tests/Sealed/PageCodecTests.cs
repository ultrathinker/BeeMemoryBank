using System.Buffers.Binary;
using System.Security.Cryptography;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Tests.Engine;
using BeeMemoryBank.TestSupport;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Storage.Tests.Sealed;

/// <summary>
/// The page codec (docs/design/step-b-spec.md 8.2, Appendix C, red test 15.1.5): a SQLite3MC-written sealed file becomes a plain image
/// that stock SQLite opens, a flipped byte anywhere is refused, and the writer's output opens in SQLite3MC again.
/// </summary>
public sealed class PageCodecTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_codec_" + Guid.NewGuid().ToString("N"));

    public PageCodecTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static byte[] ReadImage(string sealedPath, byte[] key)
    {
        using var reader = PlainImageReader.Open(sealedPath, key);
        using var memory = new MemoryStream();
        reader.CopyTo(memory);
        return memory.ToArray();
    }

    [Fact]
    public async Task The_plain_image_of_a_sealed_file_opens_in_stock_sqlite_with_the_same_rows_and_the_same_full_text_hits()
    {
        var db = await SealedFixtures.CreateSealedAsync(_dir, "vault.db", rows: 300);
        string expectedRows, expectedFts;
        using (var factory = SealedFixtures.OpenFactory(db.Path, db.Key))
        using (var c = factory.CreateConnection())
        {
            expectedRows = SealedFixtures.Dump(c);
            expectedFts = SealedFixtures.FtsIds(c, "golf");
        }
        SqliteConnection.ClearAllPools();

        var imagePath = Path.Combine(_dir, "image.db");
        await File.WriteAllBytesAsync(imagePath, ReadImage(db.Path, db.Key));

        using var stock = StockSqlite.Open(imagePath);
        stock.Scalar("PRAGMA integrity_check").Should().Be("ok");
        stock.Scalar("SELECT count(*) FROM tbl_article").Should().Be("300");
        stock.Scalar("SELECT group_concat(rowid) FROM (SELECT rowid FROM fts_article WHERE fts_article MATCH 'golf' ORDER BY rowid)")
            .Should().Be(expectedFts);
        stock.Scalar("SELECT group_concat(id || '|' || title, char(10)) FROM (SELECT id, title FROM tbl_article ORDER BY id)")
            .Should().Be(string.Join("\n", expectedRows.Split('\n').Select(r => string.Join("|", r.Split('|').Take(2)))));
    }

    [Fact]
    public async Task The_image_is_a_rollback_journal_image_with_80_reserved_bytes_that_are_zero_and_the_length_of_the_file()
    {
        var db = await SealedFixtures.CreateSealedAsync(_dir, "vault.db", rows: 120);

        var image = ReadImage(db.Path, db.Key);

        ((long)image.Length).Should().Be(new FileInfo(db.Path).Length);
        image.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8).Should().BeTrue();
        BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(16, 2)).Should().Be(4096);
        image[18].Should().Be(1);
        image[19].Should().Be(1, "an image cannot be in WAL mode");
        image[20].Should().Be(80);
        for (var page = 0; page < image.Length / 4096; page++)
            image.AsSpan(page * 4096 + 4016, 80).ContainsAnyExcept((byte)0).Should().BeFalse($"page {page + 1} has its reserved bytes zeroed");
    }

    [Fact]
    public async Task The_image_stream_is_seekable_and_a_read_from_the_middle_equals_that_part_of_the_whole()
    {
        var db = await SealedFixtures.CreateSealedAsync(_dir, "vault.db", rows: 200);
        var whole = ReadImage(db.Path, db.Key);

        using var reader = PlainImageReader.Open(db.Path, db.Key);
        reader.Length.Should().Be(whole.Length);
        foreach (var (offset, count) in new[] { (5000L, 3000), (4090L, 20), (0L, 100), (whole.Length - 17L, 17) })
        {
            reader.Position = offset;
            var part = new byte[count];
            reader.ReadExactly(part);
            part.Should().Equal(whole.AsSpan((int)offset, count).ToArray());
        }
        reader.Seek(0, SeekOrigin.End).Should().Be(whole.Length);
        reader.Read(new byte[10], 0, 10).Should().Be(0);
    }

    [Theory]
    [InlineData("salt in page 1", 3)]
    [InlineData("page 1 content", 100)]
    [InlineData("page 1 iv", 4016 + 3)]
    [InlineData("page 1 hmac", 4032 + 5)]
    [InlineData("page 2 first byte", 4096)]
    [InlineData("page 2 content", 4096 + 2000)]
    [InlineData("page 3 iv", 8192 + 4016 + 9)]
    [InlineData("page 3 hmac", 8192 + 4096 - 1)]
    public async Task A_flipped_byte_anywhere_in_a_page_is_an_InvalidDataException(string where, int offset)
    {
        var db = await SealedFixtures.CreateSealedAsync(_dir, "vault.db", rows: 200);
        var bytes = await File.ReadAllBytesAsync(db.Path);
        bytes.Length.Should().BeGreaterThan(3 * 4096);
        bytes[offset] ^= 0x01;
        var damaged = Path.Combine(_dir, "damaged.db");
        await File.WriteAllBytesAsync(damaged, bytes);

        var act = () =>
        {
            using var reader = PlainImageReader.Open(damaged, db.Key);
            reader.CopyTo(Stream.Null);
        };

        act.Should().Throw<InvalidDataException>(where);
    }

    [Fact]
    public async Task A_byte_flipped_in_the_last_page_is_found_too_and_a_wrong_key_is_refused()
    {
        var db = await SealedFixtures.CreateSealedAsync(_dir, "vault.db", rows: 200);
        var bytes = await File.ReadAllBytesAsync(db.Path);
        bytes[^100] ^= 0x80;
        var damaged = Path.Combine(_dir, "damaged.db");
        await File.WriteAllBytesAsync(damaged, bytes);

        var flipped = () => { using var r = PlainImageReader.Open(damaged, db.Key); r.CopyTo(Stream.Null); };
        var wrong = () => { using var r = PlainImageReader.Open(db.Path, RandomNumberGenerator.GetBytes(32)); r.CopyTo(Stream.Null); };

        flipped.Should().Throw<InvalidDataException>();
        wrong.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task An_all_zero_encrypted_page_is_refused_without_an_authenticated_HMAC()
    {
        var db = await SealedFixtures.CreateSealedAsync(_dir, "vault.db", rows: 200);
        var bytes = await File.ReadAllBytesAsync(db.Path);
        bytes.AsSpan(4096, 4096).Clear();
        var damaged = Path.Combine(_dir, "zero-page.db");
        await File.WriteAllBytesAsync(damaged, bytes);

        var act = () =>
        {
            using var reader = PlainImageReader.Open(damaged, db.Key);
            reader.CopyTo(Stream.Null);
        };

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task A_file_that_is_not_whole_pages_or_is_empty_is_refused_at_open()
    {
        var db = await SealedFixtures.CreateSealedAsync(_dir, "vault.db", rows: 50);
        var cut = Path.Combine(_dir, "cut.db");
        await File.WriteAllBytesAsync(cut, (await File.ReadAllBytesAsync(db.Path))[..(4096 * 2 + 10)]);
        var empty = Path.Combine(_dir, "empty.db");
        await File.WriteAllBytesAsync(empty, []);

        ((Action)(() => PlainImageReader.Open(cut, db.Key))).Should().Throw<InvalidDataException>();
        ((Action)(() => PlainImageReader.Open(empty, db.Key))).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task The_writer_output_opens_in_SQLite3MC_under_the_key_and_reads_back_to_the_same_image()
    {
        var db = await SealedFixtures.CreateSealedAsync(_dir, "vault.db", rows: 250);
        var image = ReadImage(db.Path, db.Key);
        string expectedRows, expectedFts;
        using (var factory = SealedFixtures.OpenFactory(db.Path, db.Key))
        using (var c = factory.CreateConnection())
        {
            expectedRows = SealedFixtures.Dump(c);
            expectedFts = SealedFixtures.FtsIds(c, "hotel");
        }
        SqliteConnection.ClearAllPools();

        var written = Path.Combine(_dir, "written.db");
        await SealedFileWriter.WriteAsync(new MemoryStream(image), image.Length, written, db.Key);

        File.ReadAllBytes(written).AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8).Should().BeFalse("a sealed file has no SQLite header");
        new FileInfo(written).Length.Should().Be(image.Length);
        using (var factory = SealedFixtures.OpenFactory(written, db.Key))
        using (var c = factory.CreateConnection())
        {
            c.QuerySingle<string>("PRAGMA integrity_check").Should().Be("ok");
            SealedFixtures.Dump(c).Should().Be(expectedRows);
            SealedFixtures.FtsIds(c, "hotel").Should().Be(expectedFts);
            await c.ExecuteAsync("INSERT INTO tbl_article (title, body) VALUES ('written after', x'00')");
        }
        SqliteConnection.ClearAllPools();

        // Two writes of the same image differ (random salt and IVs) but both read back to the same image.
        var again = Path.Combine(_dir, "again.db");
        SealedFileWriter.Write(new MemoryStream(image), image.Length, again, db.Key);
        File.ReadAllBytes(again).Should().NotEqual(File.ReadAllBytes(Path.Combine(_dir, "written.db")));
        ReadImage(again, db.Key).Should().Equal(image);
    }

    [Fact]
    public async Task The_writer_refuses_an_image_it_cannot_seal_and_leaves_no_file()
    {
        var plain = SealedFixtures.CreatePlain(_dir, "plain.db", rows: 50); // reserve 0: the writer needs 80 or more
        var bytes = await File.ReadAllBytesAsync(plain.Path);
        var small = SealedFixtures.CreatePlain(_dir, "small.db", rows: 50, pageSize: 1024);
        var garbage = RandomNumberGenerator.GetBytes(8192);
        var key = RandomNumberGenerator.GetBytes(32);

        foreach (var (name, image) in new[] { ("reserve 0", bytes), ("page size 1024", await File.ReadAllBytesAsync(small.Path)), ("garbage", garbage), ("cut", bytes[..5000]) })
        {
            var target = Path.Combine(_dir, $"{name.Replace(' ', '_')}.sealed");
            var act = async () => await SealedFileWriter.WriteAsync(new MemoryStream(image), image.Length, target, key);

            await act.Should().ThrowAsync<InvalidDataException>(name);
            File.Exists(target).Should().BeFalse($"{name}: nothing is left behind");
        }
    }

    [Fact]
    public async Task A_short_image_stream_is_an_InvalidDataException_and_leaves_no_file()
    {
        var db = await SealedFixtures.CreateSealedAsync(_dir, "vault.db", rows: 100);
        var image = ReadImage(db.Path, db.Key);
        var target = Path.Combine(_dir, "short.sealed");

        var act = async () => await SealedFileWriter.WriteAsync(new MemoryStream(image[..(image.Length - 4096)]), image.Length, target, db.Key);

        await act.Should().ThrowAsync<InvalidDataException>();
        File.Exists(target).Should().BeFalse();
    }

    [Fact]
    public async Task A_64_MiB_round_trip_streams_with_bounded_memory_and_reads_back_identical()
    {
        // 2,048 rows of 32 KiB of random bytes: about 64 MiB of pages.
        var db = await SealedFixtures.CreateSealedAsync(_dir, "big.db", rows: 2048, blobBytes: 32 * 1024);
        new FileInfo(db.Path).Length.Should().BeGreaterThan(60L << 20);
        var written = Path.Combine(_dir, "big-written.db");

        var before = GC.GetAllocatedBytesForCurrentThread();
        using (var reader = PlainImageReader.Open(db.Path, db.Key))
            SealedFileWriter.Write(reader, reader.Length, written, db.Key);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().BeLessThan(8L << 20, "the codec holds a few pages, not the file: it allocated " + allocated + " bytes for ~64 MiB");
        new FileInfo(written).Length.Should().Be(new FileInfo(db.Path).Length);
        Sha256OfImage(written, db.Key).Should().Be(Sha256OfImage(db.Path, db.Key), "the sealed copy reads back to the identical plain image");
        using (var factory = SealedFixtures.OpenFactory(written, db.Key))
        using (var c = factory.CreateConnection())
            c.QuerySingle<long>("SELECT count(*) FROM tbl_article").Should().Be(2048);
    }

    private static string Sha256OfImage(string path, byte[] key)
    {
        using var reader = PlainImageReader.Open(path, key);
        return Convert.ToHexString(SHA256.HashData(reader));
    }

    [Fact]
    public async Task The_key_is_not_in_the_reader_after_it_is_disposed_and_the_caller_keeps_its_own_copy()
    {
        var db = await SealedFixtures.CreateSealedAsync(_dir, "vault.db", rows: 30);
        var key = (byte[])db.Key.Clone();
        PlainImageReader reader;
        using (reader = PlainImageReader.Open(db.Path, key)) reader.CopyTo(Stream.Null);

        key.Should().Equal(db.Key, "the reader copied the key; it does not clear the caller's");
        KeyGraphWalker.Find([("reader", reader)], db.Key).Should().BeEmpty("a disposed reader holds neither the page key nor the HMAC key");
    }
}
