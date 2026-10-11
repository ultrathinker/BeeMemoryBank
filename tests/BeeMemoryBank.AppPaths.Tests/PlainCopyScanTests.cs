using BeeMemoryBank.AppPaths;

namespace BeeMemoryBank.AppPaths.Tests;

/// <summary>
/// The scan as the node's own code runs it (docs/design/step-b-spec.md 10.2): the scanner's findings without the node's own instance lock. A running node
/// holds <c>.instance.lock</c> exclusively, so on Linux and macOS (and inside the holding process on Windows) the scanner cannot open it and reports it
/// as unreadable; the preflight of a running node must not be blocked by its own lock.
/// </summary>
public class PlainCopyScanTests
{
    private static string NewDir() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "bmb-plain-copy-scan-" + Guid.NewGuid().ToString("N"))).FullName;

    private static void PlantPlainDatabase(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var image = new byte[4096];
        "SQLite format 3\0"u8.CopyTo(image);
        File.WriteAllBytes(path, image);
    }

    [Fact]
    public void The_instance_lock_of_a_running_node_is_not_a_finding()
    {
        var dir = NewDir();
        using var running = InstanceGuard.Acquire(dir);

        PlainCopyScan.Run(dir, PlainScanMode.Preflight).Should().BeEmpty();
        PlainCopyScan.Run(dir, PlainScanMode.Sealed).Should().BeEmpty();
    }

    [Fact]
    public void A_plain_database_that_merely_carries_the_name_of_the_lock_elsewhere_is_judged_by_its_content()
    {
        var dir = NewDir();
        PlantPlainDatabase(Path.Combine(dir, "tmp", InstanceGuard.FileName));
        using var running = InstanceGuard.Acquire(dir);

        PlainCopyScan.Run(dir, PlainScanMode.Preflight).Select(f => (f.RelativePath, f.Kind))
            .Should().Equal([("tmp/" + InstanceGuard.FileName, PlainCopyKinds.PlainSqlite)]);
    }

    [Fact]
    public void A_plain_database_in_the_place_of_the_lock_is_still_found_when_it_can_be_read()
    {
        var dir = NewDir();
        PlantPlainDatabase(Path.Combine(dir, InstanceGuard.FileName));

        PlainCopyScan.Run(dir, PlainScanMode.Preflight).Select(f => (f.RelativePath, f.Kind))
            .Should().Equal([(InstanceGuard.FileName, PlainCopyKinds.PlainSqlite)], "only an unreadable lock is dropped: nothing hides behind the name");
    }
}
