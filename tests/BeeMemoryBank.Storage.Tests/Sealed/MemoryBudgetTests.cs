using System.Security.Cryptography;
using BeeMemoryBank.Storage.Sealed;

namespace BeeMemoryBank.Storage.Tests.Sealed;

/// <summary>
/// BMB-304: the container of a snapshot holds a whole database as one buffer, and a node that cannot hold it says so - how much it needs, how much it
/// has, the limit that would do - before it reads or allocates anything, instead of ending in an OutOfMemoryException (a 645 MB database in a container
/// limited to 3 GB did). The ceiling is the test's: <c>freeMemoryBytes</c> stands in for what the runtime would report.
/// </summary>
public sealed class MemoryBudgetTests
{
    private const long Mb = 1024 * 1024;
    private static readonly byte[] Dek = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void A_job_that_fits_passes_and_one_that_does_not_is_refused_with_the_numbers()
    {
        MemoryBudget.RequireFree(100 * Mb, "Packing the database", freeBytesOverride: 100 * Mb);

        var tooMuch = () => MemoryBudget.RequireFree(645 * Mb + MemoryBudget.Slack, "Making the snapshot of a 645 MB database", freeBytesOverride: 300 * Mb);

        var ex = tooMuch.Should().Throw<NotEnoughMemoryException>().Which;
        ex.NeededBytes.Should().Be(645 * Mb + MemoryBudget.Slack);
        ex.FreeBytes.Should().Be(300 * Mb);
        ex.RecommendedLimitBytes.Should().BeGreaterThan(ex.NeededBytes).And.Be(ex.RecommendedLimitBytes / (256 * Mb) * (256 * Mb), "a round number of 256 MB steps");
        ex.Message.Should().StartWith("Making the snapshot of a 645 MB database needs about 709 MB of free memory")
            .And.Contain("300 MB free")
            .And.Contain($"to at least {ex.RecommendedLimitBytes / Mb} MB")
            .And.Contain("memory limit").And.Contain("Nothing was changed");
        ex.Should().BeAssignableTo<InvalidOperationException>("a refusal with words, not an exception type nobody can act on");
    }

    [Fact]
    public void What_the_runtime_reports_is_what_is_measured_when_no_ceiling_is_given()
    {
        MemoryBudget.FreeBytes().Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        MemoryBudget.RequireFree(1, "A byte");
    }

    [Fact]
    public void A_buffer_the_runtime_cannot_give_is_a_refusal_in_words_not_an_OutOfMemoryException()
    {
        var take = () => MemoryBudget.Allocate(Array.MaxLength + 1L, "Packing the database into a snapshot");

        var ex = take.Should().Throw<NotEnoughMemoryException>().Which;
        ex.Message.Should().StartWith("Packing the database into a snapshot needs about").And.Contain("memory limit").And.Contain("Nothing was changed");
        ex.NeededBytes.Should().Be(Array.MaxLength + 1L + MemoryBudget.Slack);

        MemoryBudget.Allocate(1024, "A kilobyte").Length.Should().Be(1024);
    }

    [Fact]
    public void A_process_is_not_refused_for_garbage_it_could_collect()
    {
        // The refusal this must never make: memory the node holds only as garbage. A node that ran for a day has hundreds of megabytes of it.
        var garbage = new List<byte[]>();
        for (var i = 0; i < 8; i++) garbage.Add(new byte[16 * 1024 * 1024]);
        var before = MemoryBudget.FreeBytes();
        garbage.Clear();

        var after = MemoryBudget.FreeBytes();

        after.Should().BeGreaterThanOrEqualTo(before, "the estimate counts what is alive, and the garbage is collected before it counts");
    }

    [Fact]
    public async Task A_database_larger_than_the_ceiling_is_refused_before_anything_is_read_or_allocated()
    {
        var image = RandomNumberGenerator.GetBytes(8 * (int)Mb);
        var source = new CountingStream(image);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        var pack = async () => await Bmbdb2Container.EncryptAsync(source, image.Length, Dek, freeMemoryBytes: 4 * Mb);

        var ex = (await pack.Should().ThrowAsync<NotEnoughMemoryException>()).Which;
        ex.Message.Should().Contain("Packing the database into a snapshot needs about").And.Contain("memory limit");
        ex.NeededBytes.Should().Be(image.Length + Bmbdb2Container.OverheadV2 + MemoryBudget.Slack);
        source.BytesRead.Should().Be(0, "nothing was read");
        (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore).Should().BeLessThan(1 * Mb, "no buffer of the image's size was allocated");
    }

    [Fact]
    public async Task A_container_larger_than_the_ceiling_is_refused_before_it_is_read_and_one_that_fits_opens()
    {
        var image = RandomNumberGenerator.GetBytes(4 * (int)Mb);
        var container = await Bmbdb2Container.EncryptAsync(new MemoryStream(image), image.Length, Dek);

        var open = async () => await Bmbdb2Container.OpenAsync(new MemoryStream(container), container.Length, Dek, freeMemoryBytes: 8 * Mb);
        var refused = (await open.Should().ThrowAsync<NotEnoughMemoryException>()).Which;
        refused.Message.Should().Contain("Opening the database of a snapshot needs about").And.Contain("memory limit");

        using var opened = await Bmbdb2Container.OpenAsync(new MemoryStream(container), container.Length, Dek, freeMemoryBytes: container.Length + MemoryBudget.Slack);
        opened.Span.SequenceEqual(image).Should().BeTrue();
    }

    [Fact]
    public async Task Packing_a_database_holds_one_copy_of_it_not_two()
    {
        var image = RandomNumberGenerator.GetBytes(16 * (int)Mb);
        var source = new MemoryStream(image);   // completes synchronously: the whole job runs on this thread, so its allocations are this thread's

        var before = GC.GetAllocatedBytesForCurrentThread();
        var container = await Bmbdb2Container.EncryptAsync(source, image.Length, Dek);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        container.Length.Should().Be(image.Length + Bmbdb2Container.OverheadV2);
        allocated.Should().BeLessThan(image.Length + 1 * Mb, "the image was read into the buffer its ciphertext takes and encrypted where it lies");

        // The control of the measurement: the two-buffer way (read the image, encrypt into another) allocates both.
        var naive = GC.GetAllocatedBytesForCurrentThread();
        var copy = new byte[image.Length];
        image.CopyTo(copy, 0);
        var second = new byte[image.Length + Bmbdb2Container.OverheadV2];
        (GC.GetAllocatedBytesForCurrentThread() - naive).Should().BeGreaterThan(image.Length * 2L, "the control sees two copies");
        GC.KeepAlive(second);

        // And it is the same container as ever: opened by the reader of every version.
        (await Bmbdb2Container.DecryptAsync(new MemoryStream(container), container.Length, Dek)).Should().Equal(image);
    }

    [Fact]
    public async Task Opening_a_container_holds_one_copy_of_it_not_two()
    {
        var image = RandomNumberGenerator.GetBytes(16 * (int)Mb);
        var container = await Bmbdb2Container.EncryptAsync(new MemoryStream(image), image.Length, Dek);

        var before = GC.GetAllocatedBytesForCurrentThread();
        using var opened = await Bmbdb2Container.OpenAsync(new MemoryStream(container), container.Length, Dek);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        opened.Length.Should().Be(image.Length);
        opened.Span.SequenceEqual(image).Should().BeTrue();
        allocated.Should().BeLessThan(container.Length + 1 * Mb, "the container was decrypted where it was read");
        using (var stream = opened.OpenRead()) stream.Length.Should().Be(image.Length);
    }

    [Fact]
    public async Task A_container_that_is_opened_and_disposed_leaves_no_plain_image_behind_and_a_wrong_key_clears_what_it_read()
    {
        var image = RandomNumberGenerator.GetBytes(64 * 1024);
        var container = await Bmbdb2Container.EncryptAsync(new MemoryStream(image), image.Length, Dek);

        var wrong = async () => await Bmbdb2Container.OpenAsync(new MemoryStream(container), container.Length, RandomNumberGenerator.GetBytes(32));
        await wrong.Should().ThrowAsync<CryptographicException>();

        var opened = await Bmbdb2Container.OpenAsync(new MemoryStream(container), container.Length, Dek);
        opened.Dispose();
        // The window still points at the buffer; disposing zeroed all of it.
        opened.Span.ToArray().Should().OnlyContain(b => b == 0, "disposing the image clears it");
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = base.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }

        public override int Read(Span<byte> buffer)
        {
            var n = base.Read(buffer);
            BytesRead += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = base.Read(buffer.Span);
            BytesRead += n;
            return ValueTask.FromResult(n);
        }
    }
}
