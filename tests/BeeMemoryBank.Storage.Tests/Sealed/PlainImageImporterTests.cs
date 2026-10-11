using System.Security.Cryptography;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.TestSupport;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Storage.Tests.Sealed;

/// <summary>
/// The importer (docs/design/step-b-spec.md 8.2, red test 15.1.6): a plain image becomes a sealed file without a plain file ever
/// appearing - streamed when the image has a page reserve, in memory under a cap when it has none, refused above the cap.
/// </summary>
public sealed class PlainImageImporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_importer_" + Guid.NewGuid().ToString("N"));

    public PlainImageImporterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>Records the name of every file that appears in a folder while the action runs (a watcher and a poll on a plain thread).</summary>
    private static async Task<HashSet<string>> WatchAsync(string folder, Func<Task> action)
    {
        var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        using var watcher = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.FileName, EnableRaisingEvents = true };
        watcher.Created += (_, e) => seen.TryAdd(e.Name ?? "?", 0);
        watcher.Renamed += (_, e) => seen.TryAdd(e.Name ?? "?", 0);
        var stop = false;
        using var started = new ManualResetEventSlim();
        var poller = new Thread(() =>
        {
            started.Set();
            while (!Volatile.Read(ref stop))
            {
                foreach (var f in Directory.GetFiles(folder)) seen.TryAdd(Path.GetFileName(f), 0);
                Thread.Sleep(1);
            }
        }) { IsBackground = true };
        poller.Start();
        started.Wait();
        try { await action(); }
        finally
        {
            Thread.Sleep(100);
            Volatile.Write(ref stop, true);
            poller.Join();
        }
        return seen.Keys.ToHashSet();
    }

    private static async Task<byte[]> ImageOfSealedAsync(string path, byte[] key)
    {
        await using var reader = PlainImageReader.Open(path, key);
        using var memory = new MemoryStream();
        await reader.CopyToAsync(memory);
        return memory.ToArray();
    }

    private static void AssertSameContent(string sealedPath, byte[] key, string expectedRows, string expectedFts, string word)
    {
        SqliteConnection.ClearAllPools();
        using var factory = SealedFixtures.OpenFactory(sealedPath, key);
        using var c = factory.CreateConnection();
        c.QuerySingle<string>("PRAGMA integrity_check").Should().Be("ok");
        SealedFixtures.Dump(c).Should().Be(expectedRows);
        SealedFixtures.FtsIds(c, word).Should().Be(expectedFts);
    }

    [Fact]
    public async Task An_image_with_a_page_reserve_is_streamed_and_only_the_keyed_target_appears_in_the_folder()
    {
        var db = await SealedFixtures.CreateSealedAsync(_dir, "source.db", rows: 200);
        var image = await ImageOfSealedAsync(db.Path, db.Key);
        string rows, fts;
        using (var factory = SealedFixtures.OpenFactory(db.Path, db.Key))
        using (var c = factory.CreateConnection()) { rows = SealedFixtures.Dump(c); fts = SealedFixtures.FtsIds(c, "delta"); }
        SqliteConnection.ClearAllPools();

        var staging = Path.Combine(_dir, "staging");
        Directory.CreateDirectory(staging);
        var target = Path.Combine(staging, "vault.seal-new");
        var newKey = RandomNumberGenerator.GetBytes(32);
        SealedImportMethod method = default;

        var seen = await WatchAsync(staging, async () =>
            method = await PlainImageImporter.ToSealedFileAsync(new MemoryStream(image), image.Length, target, newKey, memoryCap: 1 << 20));

        method.Should().Be(SealedImportMethod.Streamed, "the image has 4096-byte pages with 80 reserved bytes, so no memory cap applies");
        seen.Should().BeEquivalentTo(["vault.seal-new"], "no other file, plain or temporary, appears while the image is imported");
        AssertSameContent(target, newKey, rows, fts, "delta");
    }

    [Fact]
    public async Task An_image_with_no_page_reserve_is_read_into_memory_deserialized_and_vacuumed_into_the_keyed_target()
    {
        var canary = MetadataCanaries.Ascii("note");
        var plain = SealedFixtures.CreatePlain(_dir, "plain.db", rows: 200, canary: canary);
        var image = await File.ReadAllBytesAsync(plain.Path);
        image[20].Should().Be(0, "a 2.5 database has no page reserve");
        string rows, fts;
        using (var c = new SqliteConnection($"Data Source={plain.Path};Pooling=False;Mode=ReadOnly"))
        {
            c.Open();
            rows = SealedFixtures.Dump(c);
            fts = SealedFixtures.FtsIds(c, "echo");
        }

        var staging = Path.Combine(_dir, "staging");
        Directory.CreateDirectory(staging);
        var target = Path.Combine(staging, "vault.seal-new");
        var key = RandomNumberGenerator.GetBytes(32);
        SealedImportMethod method = default;

        var seen = await WatchAsync(staging, async () =>
            method = await PlainImageImporter.ToSealedFileAsync(new MemoryStream(image), image.Length, target, key, memoryCap: 64L << 20));

        method.Should().Be(SealedImportMethod.Deserialized);
        // VACUUM INTO keeps a rollback journal of its own target for a moment: a sidecar of the keyed target (its pages are encrypted
        // under the same key, Appendix C), never a second database and never a plain file.
        seen.Should().Contain("vault.seal-new").And.BeSubsetOf(["vault.seal-new", "vault.seal-new-journal", "vault.seal-new-wal", "vault.seal-new-shm"]);
        new FileInfo(target).Length.Should().BeGreaterThan(0);
        (new FileInfo(target).Length % 4096).Should().Be(0);
        MetadataCanaries.ScanFile(target, [canary]).Should().BeEmpty("the target is encrypted");
        MetadataCanaries.ScanFile(plain.Path, [canary]).Should().NotBeEmpty("the source image does hold the canary: the scan can see it");
        AssertSameContent(target, key, rows, fts, "echo");
    }

    [Fact]
    public async Task An_image_over_the_memory_cap_is_refused_and_nothing_is_written()
    {
        var plain = SealedFixtures.CreatePlain(_dir, "plain.db", rows: 300);
        var image = await File.ReadAllBytesAsync(plain.Path);
        var target = Path.Combine(_dir, "vault.seal-new");

        var act = async () => await PlainImageImporter.ToSealedFileAsync(
            new MemoryStream(image), image.Length, target, RandomNumberGenerator.GetBytes(32), memoryCap: image.Length - 1);

        var thrown = await act.Should().ThrowAsync<SealedImportTooLargeException>();
        thrown.Which.Message.Should().Contain("made by an older version").And.Contain("too large to import into an encrypted database");
        File.Exists(target).Should().BeFalse();
        // The same image is accepted at exactly the cap.
        await PlainImageImporter.ToSealedFileAsync(new MemoryStream(image), image.Length, target, RandomNumberGenerator.GetBytes(32), memoryCap: image.Length);
        File.Exists(target).Should().BeTrue();
    }

    [Fact]
    public async Task A_database_flagged_as_wal_is_imported_through_memory_too()
    {
        var plain = SealedFixtures.CreatePlain(_dir, "wal.db", rows: 120, walHeader: true);
        var image = await File.ReadAllBytesAsync(plain.Path);
        image[18].Should().Be(2, "the control: this image really is flagged as WAL");
        string rows;
        using (var c = new SqliteConnection($"Data Source={plain.Path};Pooling=False;Mode=ReadOnly")) { c.Open(); rows = SealedFixtures.Dump(c); }
        var target = Path.Combine(_dir, "wal.sealed");
        var key = RandomNumberGenerator.GetBytes(32);

        var method = await PlainImageImporter.ToSealedFileAsync(new MemoryStream(image), image.Length, target, key, memoryCap: 64L << 20);

        method.Should().Be(SealedImportMethod.Deserialized);
        (new FileInfo(target).Length % 4096).Should().Be(0);
        SqliteConnection.ClearAllPools();
        using var factory = SealedFixtures.OpenFactory(target, key);
        using var conn = factory.CreateConnection();
        conn.QuerySingle<string>("PRAGMA integrity_check").Should().Be("ok");
        SealedFixtures.Dump(conn).Should().Be(rows);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(8192)]
    public async Task An_image_with_a_page_size_other_than_4096_is_refused_because_a_sealed_file_is_whole_4096_byte_pages(int pageSize)
    {
        var plain = SealedFixtures.CreatePlain(_dir, $"p{pageSize}.db", rows: 120, pageSize: pageSize);
        var image = await File.ReadAllBytesAsync(plain.Path);
        var target = Path.Combine(_dir, $"p{pageSize}.sealed");

        var act = async () => await PlainImageImporter.ToSealedFileAsync(new MemoryStream(image), image.Length, target, RandomNumberGenerator.GetBytes(32), memoryCap: 64L << 20);

        (await act.Should().ThrowAsync<InvalidDataException>()).Which.Message.Should().Contain("4096");
        File.Exists(target).Should().BeFalse();
    }

    [Fact]
    public async Task Bytes_that_are_not_a_database_are_refused_with_InvalidDataException_and_no_target()
    {
        var target = Path.Combine(_dir, "vault.seal-new");
        var key = RandomNumberGenerator.GetBytes(32);
        var plain = SealedFixtures.CreatePlain(_dir, "plain.db", rows: 40);
        var good = await File.ReadAllBytesAsync(plain.Path);
        var damaged = (byte[])good.Clone();
        for (var i = 100; i < damaged.Length; i += 7) damaged[i] = 0xFF; // a valid header, broken pages

        foreach (var (name, bytes) in new[]
                 {
                     ("random", RandomNumberGenerator.GetBytes(10_000)),
                     ("short", good[..40]),
                     ("header only", good[..100]),
                     ("damaged pages", damaged),
                 })
        {
            var act = async () => await PlainImageImporter.ToSealedFileAsync(new MemoryStream(bytes), bytes.Length, target, key, memoryCap: 64L << 20);

            if (name == "damaged pages")
            {
                // The header is valid, so SQLite takes the image; what it makes of the pages is VACUUM INTO's to say. Whatever it says, it is
                // not success with a half-written target.
                try { await act(); }
                catch (Exception ex) when (ex is InvalidDataException or SqliteException) { }
                if (File.Exists(target)) File.Delete(target);
            }
            else
            {
                await act.Should().ThrowAsync<InvalidDataException>(name);
            }
            File.Exists(target).Should().BeFalse(name);
        }
    }

    [Fact]
    public async Task A_stream_shorter_than_its_stated_length_is_refused()
    {
        var plain = SealedFixtures.CreatePlain(_dir, "plain.db", rows: 100);
        var image = await File.ReadAllBytesAsync(plain.Path);
        var target = Path.Combine(_dir, "vault.seal-new");

        var act = async () => await PlainImageImporter.ToSealedFileAsync(
            new MemoryStream(image[..(image.Length / 2)]), image.Length, target, RandomNumberGenerator.GetBytes(32), memoryCap: 64L << 20);

        await act.Should().ThrowAsync<InvalidDataException>();
        File.Exists(target).Should().BeFalse();
    }

    [Fact]
    public void The_default_cap_is_one_gigabyte_on_a_desktop_and_BMB_SEALED_IMPORT_MEMORY_MB_overrides_it()
    {
        var before = Environment.GetEnvironmentVariable("BMB_SEALED_IMPORT_MEMORY_MB");
        try
        {
            Environment.SetEnvironmentVariable("BMB_SEALED_IMPORT_MEMORY_MB", null);
            PlainImageImporter.DefaultMemoryCap().Should().Be(OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() ? 256L << 20 : 1L << 30);
            Environment.SetEnvironmentVariable("BMB_SEALED_IMPORT_MEMORY_MB", "7");
            PlainImageImporter.DefaultMemoryCap().Should().Be(7L << 20);
            Environment.SetEnvironmentVariable("BMB_SEALED_IMPORT_MEMORY_MB", "nonsense");
            PlainImageImporter.DefaultMemoryCap().Should().BeGreaterThan(0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BMB_SEALED_IMPORT_MEMORY_MB", before);
        }
    }
}
