using System.Security.Cryptography;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.TestSupport;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Storage.Tests.Sealed;

/// <summary>
/// BMB-304 fix round, findings 1, 2 and 7: the memory budget is ONE model of the limit that really governs a whole-image allocation (the heap limit the
/// runtime enforces and the limit of the process: a container's cgroup, a Windows job, the machine), the recommended limit comes from the same model, and
/// every whole-image allocation goes through it - the container codec's buffers, its copy, and the importer's native copy of a legacy image.
/// </summary>
public sealed class MemoryModelTests : IDisposable
{
    private const long Mb = 1024 * 1024;
    private static readonly byte[] Dek = RandomNumberGenerator.GetBytes(32);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_memmodel_" + Guid.NewGuid().ToString("N"));

    public MemoryModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        MemoryBudget.ReadingForTests.Value = null;
        MemoryBudget.Allocations.Value = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>A container of <paramref name="limitMb"/> MB, as the .NET runtime treats one: the heap may take 75% of it.</summary>
    private static MemoryReading Container(long limitMb, long managedMb, long nativeMb, double heapFraction = 0.75) =>
        new(limitMb * Mb, (long)(limitMb * Mb * heapFraction), managedMb * Mb, nativeMb * Mb, LimitIsOperatorSet: true, HeapLimitIsFixed: false);

    // ---------------------------------------------------------------- the model

    [Fact]
    public void A_container_of_one_gigabyte_gives_the_heap_three_quarters_of_it_and_the_job_is_measured_against_that()
    {
        var reading = Container(limitMb: 1024, managedMb: 100, nativeMb: 80);

        MemoryBudget.Free(reading).Should().Be(668 * Mb, "768 MB of heap minus the 100 MB that is alive, not the 844 MB the container still has");
        MemoryBudget.Free(reading, native: true).Should().Be(844 * Mb, "native memory is bounded by the process, not by the heap");
    }

    [Fact]
    public void The_recommended_limit_is_computed_from_the_same_model_and_one_gigabyte_is_not_promised_for_a_645_MB_database()
    {
        var reading = Container(limitMb: 1024, managedMb: 100, nativeMb: 80);
        var needed = 645 * Mb + Bmbdb2Container.OverheadV2 + MemoryBudget.Slack;   // what the codec asks for

        var recommended = MemoryBudget.RecommendedLimit(reading, needed);

        recommended.Should().BeGreaterThan(1024 * Mb, "a 1024 MB container gives the heap 768 MB, which is less than the 100 MB alive plus the 709 MB job");
        recommended.Should().Be(1280 * Mb, "(100 + 709) / 0.75 = 1079 MB, in steps of 256 MB");
        // The promise is kept: a container of exactly that size does hold the job under the same model.
        MemoryBudget.Free(Container(limitMb: recommended / Mb, managedMb: 100, nativeMb: 80)).Should().BeGreaterThanOrEqualTo(needed);
    }

    [Theory]
    [InlineData(512, 50, 60, 200)]
    [InlineData(1024, 100, 80, 645)]
    [InlineData(2048, 300, 150, 1200)]
    [InlineData(3072, 600, 400, 645)]
    public void The_recommended_limit_always_fits_the_job_and_one_step_less_does_not(long limitMb, long managedMb, long nativeMb, long imageMb)
    {
        var reading = Container(limitMb, managedMb, nativeMb);
        var needed = imageMb * Mb + Bmbdb2Container.OverheadV2 + MemoryBudget.Slack;

        var recommended = MemoryBudget.RecommendedLimit(reading, needed) / Mb;

        MemoryBudget.Free(Container(recommended, managedMb, nativeMb)).Should().BeGreaterThanOrEqualTo(needed, "the answer holds under the model that asked");
        MemoryBudget.Free(Container(recommended - 256, managedMb, nativeMb)).Should().BeLessThan(needed, "and it is the smallest step that does");
    }

    [Fact]
    public void A_machine_with_no_limit_is_bounded_by_its_memory_and_the_heap_is_not_a_fraction_of_it()
    {
        var host = new MemoryReading(16 * 1024 * Mb, 16 * 1024 * Mb, 200 * Mb, 300 * Mb, LimitIsOperatorSet: false, HeapLimitIsFixed: false);

        MemoryBudget.Free(host).Should().Be(16 * 1024 * Mb - 500 * Mb);
        var needed = 645 * Mb + MemoryBudget.Slack;
        MemoryBudget.RecommendedLimit(host, needed).Should().Be(1280 * Mb, "200 + 300 + 709 = 1209 MB: no 75% on a machine without a limit");
    }

    [Fact]
    public void A_native_job_is_measured_against_the_process_and_needs_no_room_in_the_heap()
    {
        var reading = Container(limitMb: 1024, managedMb: 700, nativeMb: 100);   // the heap is nearly full, the process is not

        MemoryBudget.Free(reading).Should().Be(68 * Mb);
        MemoryBudget.Free(reading, native: true).Should().Be(224 * Mb);
    }

    [Fact]
    public void A_heap_limit_that_is_configured_is_named_in_the_refusal_and_the_container_limit_alone_is_not_promised_to_help()
    {
        var reading = new MemoryReading(4096 * Mb, 600 * Mb, 100 * Mb, 100 * Mb, LimitIsOperatorSet: true, HeapLimitIsFixed: true);
        MemoryBudget.ReadingForTests.Value = reading;

        var tooMuch = () => MemoryBudget.RequireFree(645 * Mb + MemoryBudget.Slack, "Making the snapshot of a 645 MB database");

        var ex = tooMuch.Should().Throw<NotEnoughMemoryException>().Which;
        ex.Message.Should().Contain("DOTNET_GCHeapHardLimit").And.Contain("raise it to at least 809 MB");
        ex.FreeBytes.Should().Be(500 * Mb);
    }

    [Fact]
    public void The_refusal_of_a_container_names_the_limit_from_the_model_not_a_raw_size()
    {
        MemoryBudget.ReadingForTests.Value = Container(limitMb: 1024, managedMb: 100, nativeMb: 80);

        var tooMuch = () => MemoryBudget.RequireFree(645 * Mb + Bmbdb2Container.OverheadV2 + MemoryBudget.Slack, "Making the snapshot of a 645 MB database");

        var ex = tooMuch.Should().Throw<NotEnoughMemoryException>().Which;
        ex.Message.Should().Contain("668 MB free").And.Contain("to at least 1280 MB").And.NotContain("to at least 1024 MB");
        ex.RecommendedLimitBytes.Should().Be(1280 * Mb);
    }

    // ---------------------------------------------------------------- what the platform says

    private static Func<string, string?> Files(params (string Path, string Text)[] files)
    {
        var map = files.ToDictionary(f => f.Path, f => f.Text);
        return path => map.GetValueOrDefault(path);
    }

    [Fact]
    public void Cgroup_v2_at_the_root_of_a_container_gives_its_memory_max()
    {
        var read = Files(("/proc/self/cgroup", "0::/\n"), ("/sys/fs/cgroup/memory.max", "1073741824\n"));

        MemoryLimits.CgroupLimit(read).Should().Be(1024 * Mb);
    }

    [Fact]
    public void Cgroup_v2_nested_takes_the_smallest_limit_of_the_cgroup_and_its_parents()
    {
        var read = Files(
            ("/proc/self/cgroup", "0::/system.slice/bmb.service\n"),
            ("/sys/fs/cgroup/system.slice/bmb.service/memory.max", "max\n"),
            ("/sys/fs/cgroup/system.slice/memory.max", "2147483648\n"),
            ("/sys/fs/cgroup/memory.max", "max\n"));

        MemoryLimits.CgroupLimit(read).Should().Be(2048 * Mb, "the service has no limit of its own; its slice does");
    }

    [Fact]
    public void Cgroup_v1_reads_the_memory_controller_and_ignores_the_unlimited_value()
    {
        var limited = Files(("/proc/self/cgroup", "4:cpu:/\n3:memory:/docker/abc\n"), ("/sys/fs/cgroup/memory/docker/abc/memory.limit_in_bytes", "536870912\n"));
        var unlimited = Files(("/proc/self/cgroup", "3:memory:/\n"), ("/sys/fs/cgroup/memory/memory.limit_in_bytes", "9223372036854771712\n"));

        MemoryLimits.CgroupLimit(limited).Should().Be(512 * Mb);
        MemoryLimits.CgroupLimit(unlimited).Should().BeNull("a number that large is how v1 says there is no limit");
    }

    [Theory]
    [InlineData("max")]
    [InlineData("")]
    [InlineData("not a number")]
    [InlineData("0")]
    [InlineData("-5")]
    public void A_limit_file_that_says_nothing_useful_is_no_limit(string text)
    {
        MemoryLimits.ParseLimit(text).Should().BeNull();
        MemoryLimits.CgroupLimit(Files(("/proc/self/cgroup", "0::/\n"), ("/sys/fs/cgroup/memory.max", text))).Should().BeNull();
    }

    [Fact]
    public void No_cgroup_files_at_all_is_no_limit()
    {
        MemoryLimits.CgroupLimit(_ => null).Should().BeNull();
    }

    // ---------------------------------------------------------------- what the limit is charged for: the cgroup's usage (BMB-304 fix2)

    [Fact]
    public void Cgroup_v2_usage_is_memory_current_less_the_inactive_file_cache_of_the_cgroup_that_has_the_limit()
    {
        var read = Files(
            ("/proc/self/cgroup", "0::/\n"),
            ("/sys/fs/cgroup/memory.max", "536870912\n"),
            ("/sys/fs/cgroup/memory.current", "450000000\n"),
            ("/sys/fs/cgroup/memory.stat", "anon 100\nfile 300000000\ninactive_file 120000000\nactive_file 180000000\n"));

        MemoryLimits.CgroupUsage(read).Should().Be(330000000, "the cache the kernel gives back first is not what stops an allocation");
    }

    [Fact]
    public void Cgroup_v2_usage_is_read_where_the_limit_is_even_when_the_process_sits_in_a_child_with_none()
    {
        var read = Files(
            ("/proc/self/cgroup", "0::/system.slice/bmb.service\n"),
            ("/sys/fs/cgroup/system.slice/bmb.service/memory.max", "max\n"),
            ("/sys/fs/cgroup/system.slice/bmb.service/memory.current", "100\n"),
            ("/sys/fs/cgroup/system.slice/memory.max", "2147483648\n"),
            ("/sys/fs/cgroup/system.slice/memory.current", "1500000000\n"),
            ("/sys/fs/cgroup/memory.max", "max\n"));

        MemoryLimits.CgroupUsage(read).Should().Be(1500000000, "the slice is the one that is limited, and it holds the siblings of this service too");
    }

    [Fact]
    public void Cgroup_v1_usage_is_usage_in_bytes_less_total_inactive_file()
    {
        var read = Files(
            ("/proc/self/cgroup", "3:memory:/docker/abc\n"),
            ("/sys/fs/cgroup/memory/docker/abc/memory.limit_in_bytes", "536870912\n"),
            ("/sys/fs/cgroup/memory/docker/abc/memory.usage_in_bytes", "400000000\n"),
            ("/sys/fs/cgroup/memory/docker/abc/memory.stat", "cache 90\ntotal_inactive_file 50000000\n"));

        MemoryLimits.CgroupUsage(read).Should().Be(350000000);
    }

    [Fact]
    public void Usage_without_a_stat_file_is_the_usage_itself_and_without_a_limit_or_a_usage_file_it_is_unknown()
    {
        MemoryLimits.CgroupUsage(Files(("/proc/self/cgroup", "0::/\n"), ("/sys/fs/cgroup/memory.max", "1000000000\n"), ("/sys/fs/cgroup/memory.current", "123\n")))
            .Should().Be(123);
        MemoryLimits.CgroupUsage(Files(("/proc/self/cgroup", "0::/\n"), ("/sys/fs/cgroup/memory.max", "max\n"), ("/sys/fs/cgroup/memory.current", "123\n")))
            .Should().BeNull("nothing limits the process: its own resident set is the measure");
        MemoryLimits.CgroupUsage(Files(("/proc/self/cgroup", "0::/\n"), ("/sys/fs/cgroup/memory.max", "1000000000\n"))).Should().BeNull("a limit whose usage cannot be read");
        MemoryLimits.CgroupUsage(_ => null).Should().BeNull();
    }

    [Fact]
    public void The_native_memory_of_the_model_is_what_the_cgroup_charges_when_that_is_more_than_the_resident_set_of_the_process()
    {
        var charged = 50L * 1024 * Mb;   // far more than any resident set of this test run
        MemoryLimits.UsageForTests.Value = charged;
        try
        {
            var reading = MemoryBudget.Read();

            reading.NativeBytes.Should().BeGreaterThan(40L * 1024 * Mb, "the siblings in the container and the tmpfs are part of what the limit is charged for");
        }
        finally { MemoryLimits.UsageForTests.Value = null; }
    }

    [Fact]
    public void A_windows_job_object_limit_is_the_smaller_of_the_process_and_the_job_limit_when_their_flags_are_set()
    {
        const uint processFlag = 0x100, jobFlag = 0x200;

        MemoryLimits.JobLimit(processFlag, 3000 * (ulong)Mb, 1000 * (ulong)Mb).Should().Be(3000 * Mb, "only the flagged limit counts");
        MemoryLimits.JobLimit(jobFlag, 3000 * (ulong)Mb, 1000 * (ulong)Mb).Should().Be(1000 * Mb);
        MemoryLimits.JobLimit(processFlag | jobFlag, 3000 * (ulong)Mb, 1000 * (ulong)Mb).Should().Be(1000 * Mb);
        MemoryLimits.JobLimit(0, 3000 * (ulong)Mb, 1000 * (ulong)Mb).Should().BeNull("a job that limits no memory");
        MemoryLimits.JobLimit(processFlag, 0, 0).Should().BeNull();
    }

    [Fact]
    public void Asking_this_platform_never_throws_and_gives_nothing_or_a_positive_limit()
    {
        var limit = MemoryLimits.ProcessLimit();

        (limit is null || limit > 0).Should().BeTrue();
        var reading = MemoryBudget.Read();
        reading.ProcessLimitBytes.Should().BeGreaterThan(0);
        reading.HeapLimitBytes.Should().BeInRange(1, reading.ProcessLimitBytes);
        reading.ManagedBytes.Should().BeGreaterThan(0);
    }

    // ---------------------------------------------------------------- every whole-image allocation goes through the budget

    [Fact]
    public async Task Decrypting_to_an_array_takes_both_whole_images_through_the_budget()
    {
        var image = RandomNumberGenerator.GetBytes(2 * (int)Mb);
        var container = await Bmbdb2Container.EncryptAsync(new MemoryStream(image), image.Length, Dek);
        var log = new List<(string What, long Bytes, bool Native)>();
        MemoryBudget.Allocations.Value = log;

        var copy = await Bmbdb2Container.DecryptAsync(new MemoryStream(container), container.Length, Dek);

        copy.Should().Equal(image);
        log.Should().HaveCount(2, "the container's buffer and the copy of the image: both asked of the budget");
        log[0].Bytes.Should().Be(container.Length);
        log[1].Should().Be(("Copying the database of a snapshot", (long)image.Length, false));
    }

    [Fact]
    public void A_native_allocation_asks_the_budget_first_and_is_refused_in_the_same_words()
    {
        var refused = () => MemoryBudget.AllocateNative(512 * Mb, "Loading an older database image", freeBytesOverride: 256 * Mb);
        refused.Should().Throw<NotEnoughMemoryException>().Which.Message.Should().Contain("Loading an older database image needs about").And.Contain("Nothing was changed");

        var log = new List<(string What, long Bytes, bool Native)>();
        MemoryBudget.Allocations.Value = log;
        var memory = MemoryBudget.AllocateNative(1024, "A native kilobyte", freeBytesOverride: 256 * Mb);
        System.Runtime.InteropServices.Marshal.FreeHGlobal(memory);
        log.Should().Equal([("A native kilobyte", 1024L, true)]);
    }

    // ---------------------------------------------------------------- a legacy image (no page reserve): ONE whole image

    private static async Task<DecryptedImage> OpenedAsync(byte[] image)
    {
        var container = await Bmbdb2Container.EncryptAsync(new MemoryStream(image), image.Length, Dek);
        return await Bmbdb2Container.OpenAsync(new MemoryStream(container), container.Length, Dek);
    }

    [Fact]
    public async Task A_legacy_image_is_imported_where_it_lies_and_holds_one_whole_image_not_a_managed_one_and_a_native_copy()
    {
        var canary = MetadataCanaries.Ascii("legacy");
        var plain = SealedFixtures.CreatePlain(_dir, "legacy.db", rows: 300, canary: canary);
        var bytes = await File.ReadAllBytesAsync(plain.Path);
        bytes[20].Should().Be(0, "the control: a 2.5 database has no page reserve, so it takes the deserialized path");
        string rows;
        using (var c = new SqliteConnection($"Data Source={plain.Path};Pooling=False;Mode=ReadOnly")) { c.Open(); rows = SealedFixtures.Dump(c); }
        using var image = await OpenedAsync(bytes);
        var target = Path.Combine(_dir, "legacy.sealed");
        var key = RandomNumberGenerator.GetBytes(32);
        var log = new List<(string What, long Bytes, bool Native)>();
        MemoryBudget.Allocations.Value = log;

        var method = await PlainImageImporter.ToSealedFileAsync(image, target, key, memoryCap: 64L << 20);

        method.Should().Be(SealedImportMethod.Deserialized);
        log.Should().BeEmpty("no second whole image of any kind was taken: the buffer the container was opened in is what SQLite reads");
        MetadataCanaries.ScanFile(target, [canary]).Should().BeEmpty("the target is encrypted");
        SqliteConnection.ClearAllPools();
        using var factory = SealedFixtures.OpenFactory(target, key);
        using var conn = factory.CreateConnection();
        SealedFixtures.Dump(conn).Should().Be(rows, "the rows survived the in-place import");
    }

    [Fact]
    public async Task The_same_legacy_image_through_a_stream_takes_its_one_native_copy_through_the_budget()
    {
        var plain = SealedFixtures.CreatePlain(_dir, "legacy2.db", rows: 200);
        var bytes = await File.ReadAllBytesAsync(plain.Path);
        var target = Path.Combine(_dir, "legacy2.sealed");
        var log = new List<(string What, long Bytes, bool Native)>();
        MemoryBudget.Allocations.Value = log;

        await PlainImageImporter.ToSealedFileAsync(new MemoryStream(bytes), bytes.Length, target, RandomNumberGenerator.GetBytes(32), memoryCap: 64L << 20);

        log.Should().ContainSingle().Which.Should().Be(("Loading an older database image into the encrypted database", (long)bytes.Length, true));
    }

    [Fact]
    public async Task A_legacy_image_the_process_cannot_hold_natively_is_refused_in_words_before_anything_is_written()
    {
        var plain = SealedFixtures.CreatePlain(_dir, "legacy3.db", rows: 200);
        var bytes = await File.ReadAllBytesAsync(plain.Path);
        var target = Path.Combine(_dir, "legacy3.sealed");
        MemoryBudget.ReadingForTests.Value = new MemoryReading(bytes.Length + 8 * Mb, bytes.Length + 8 * Mb, 0, 0, true, false);   // less than the image and the slack

        var act = async () => await PlainImageImporter.ToSealedFileAsync(new MemoryStream(bytes), bytes.Length, target, RandomNumberGenerator.GetBytes(32), memoryCap: 64L << 20);

        (await act.Should().ThrowAsync<NotEnoughMemoryException>()).Which.Message.Should().Contain("Loading an older database image").And.Contain("memory limit");
        File.Exists(target).Should().BeFalse();
    }

    [Fact]
    public async Task A_stream_image_that_streams_needs_no_native_copy_and_no_budget()
    {
        var db = await SealedFixtures.CreateSealedAsync(_dir, "source.db", rows: 100);
        await using var reader = PlainImageReader.Open(db.Path, db.Key);
        using var memory = new MemoryStream();
        await reader.CopyToAsync(memory);
        SqliteConnection.ClearAllPools();
        var log = new List<(string What, long Bytes, bool Native)>();
        MemoryBudget.Allocations.Value = log;

        var method = await PlainImageImporter.ToSealedFileAsync(new MemoryStream(memory.ToArray()), memory.Length, Path.Combine(_dir, "streamed.sealed"), RandomNumberGenerator.GetBytes(32), memoryCap: 1 << 20);

        method.Should().Be(SealedImportMethod.Streamed);
        log.Should().BeEmpty();
    }
}
