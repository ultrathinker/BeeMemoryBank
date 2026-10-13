using System.Text;
using BeeMemoryBank.TestSupport;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The plain-file watch behind the "no plain file exists" scans: <see cref="PlainFileWatch.Forget"/> drops what a deliberately planted control file
/// made the watch record, and leaves nothing of it behind even when a look at that file is still half done, while a real plain file that appears
/// afterwards is still recorded.
/// </summary>
public sealed class PlainFileWatchTests
{
    private static byte[] PlainFile(string canary) => [.. "SQLite format 3\0"u8.ToArray(), .. Encoding.UTF8.GetBytes(canary)];

    private static string NewFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmb-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Forget_waits_for_a_look_that_is_half_done_so_a_deleted_control_file_leaves_nothing_behind()
    {
        var dir = NewFolder();
        try
        {
            var canary = MetadataCanaries.Ascii("watch");
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var watch = new PlainFileWatch([dir], [canary], midLook: _ =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            });
            var probe = Path.Combine(dir, "probe-plain.tmp");
            await File.WriteAllBytesAsync(probe, PlainFile(canary));
            try
            {
                entered.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("the watch is looking at the probe: its plain header is recorded, its canary hits are not");
                File.Delete(probe);

                var forget = Task.Run(watch.Forget);
                var first = await Task.WhenAny(forget, Task.Delay(300));
                (first == forget).Should().BeFalse("Forget waits for the look that is half done");
                release.Set();
                await forget.WaitAsync(TimeSpan.FromSeconds(30));
                await Task.Delay(100);

                watch.Plain.Should().BeEmpty();
                watch.CanaryHits.Should().BeEmpty("the hits of the half-done look were recorded before Forget cleared, not after");
            }
            finally
            {
                release.Set();
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task A_plain_file_that_appears_after_Forget_is_still_recorded()
    {
        var dir = NewFolder();
        try
        {
            var canary = MetadataCanaries.Ascii("watch");
            using var watch = new PlainFileWatch([dir], [canary]);
            var probe = Path.Combine(dir, "probe-plain.tmp");
            await File.WriteAllBytesAsync(probe, PlainFile(canary));
            SpinWait.SpinUntil(() => watch.Plain.Contains("probe-plain.tmp") && !watch.CanaryHits.IsEmpty, TimeSpan.FromSeconds(30)).Should().BeTrue();
            File.Delete(probe);
            watch.Forget();

            await File.WriteAllBytesAsync(Path.Combine(dir, "leak-plain.tmp"), PlainFile(canary));
            SpinWait.SpinUntil(() => watch.Plain.Contains("leak-plain.tmp") && !watch.CanaryHits.IsEmpty, TimeSpan.FromSeconds(30))
                .Should().BeTrue("a file written after Forget is a finding like any other");

            watch.Plain.Should().OnlyContain(n => n == "leak-plain.tmp");
            watch.CanaryHits.Should().OnlyContain(h => h.StartsWith("leak-plain.tmp:"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
