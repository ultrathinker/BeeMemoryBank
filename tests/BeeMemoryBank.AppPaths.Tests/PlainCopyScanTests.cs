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

    private static IDisposable RegisterHeldLock(string path, FileStream held) =>
        HeldLockProofs.Register(HeldLockProofs.FromHeldHandle(path, held.SafeFileHandle))!;

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

    [Fact]
    public void The_empty_lock_held_by_this_process_with_an_identity_proof_is_not_a_scanner_finding()
    {
        var dir = NewDir();
        var lockPath = Path.Combine(dir, "node.lock");
        using var running = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 1, FileOptions.DeleteOnClose);
        using var proof = RegisterHeldLock(lockPath, running);

        PlainVaultCopyScanner.Scan(dir, PlainScanMode.Preflight).Should().BeEmpty();
        PlainVaultCopyScanner.Scan(dir, PlainScanMode.Sealed).Should().BeEmpty();
    }

    [Fact]
    public void The_same_named_empty_lock_held_by_another_owner_without_a_proof_is_a_finding()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "node.lock");
        using var held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);

        PlainVaultCopyScanner.Scan(dir, PlainScanMode.Preflight).Select(f => (f.RelativePath, f.Kind))
            .Should().Equal([("node.lock", PlainCopyKinds.Unreadable)]);
    }

    [Theory]
    [InlineData("node.lock")]
    [InlineData(InstanceGuard.FileName)]
    public void A_nonempty_held_lock_name_is_a_finding_even_when_its_identity_is_registered(string name)
    {
        var dir = NewDir();
        var path = Path.Combine(dir, name);
        PlantPlainDatabase(path);
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var proof = RegisterHeldLock(path, held);

        PlainCopyScan.Run(dir, PlainScanMode.Preflight).Select(f => (f.RelativePath, f.Kind))
            .Should().Equal([(name, PlainCopyKinds.Unreadable)]);
    }

    [Fact]
    public void A_hard_link_to_a_proven_lock_is_not_exempted()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "node.lock");
        File.WriteAllText(path, "");
        var alias = Path.Combine(dir, "another-name.bin");
        BeeMemoryBank.TestSupport.TestLinks.CreateHardLink(path, alias);
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var proof = RegisterHeldLock(path, held);

        PlainVaultCopyScanner.Scan(dir, PlainScanMode.Preflight).Select(f => (f.RelativePath, f.Kind))
            .Should().Contain(("node.lock", PlainCopyKinds.Unreadable));
    }

    [Theory]
    [InlineData("Node.lock")]
    [InlineData("tmp/node.lock")]
    public void A_case_variant_or_subfolder_lock_name_has_no_special_status(string relative)
    {
        var dir = NewDir();
        var path = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);

        PlainVaultCopyScanner.Scan(dir, PlainScanMode.Preflight).Select(f => (f.RelativePath, f.Kind))
            .Should().Contain((relative, PlainCopyKinds.Unreadable));
    }

    [Fact]
    public void A_directory_named_like_a_lock_is_walked_and_its_plain_content_blocks()
    {
        var dir = NewDir();
        PlantPlainDatabase(Path.Combine(dir, "node.lock", "copy.db"));

        PlainVaultCopyScanner.Scan(dir, PlainScanMode.Preflight).Select(f => (f.RelativePath, f.Kind))
            .Should().Contain(("node.lock/copy.db", PlainCopyKinds.PlainSqlite));
    }

    [Fact]
    public void A_link_named_like_a_lock_is_never_exempted()
    {
        var dir = NewDir();
        var target = Path.Combine(dir, "target.bin");
        File.WriteAllText(target, "");
        var path = Path.Combine(dir, "node.lock");
        try { File.CreateSymbolicLink(path, target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        PlainVaultCopyScanner.Scan(dir, PlainScanMode.Preflight).Select(f => (f.RelativePath, f.Kind))
            .Should().Contain(("node.lock", PlainCopyKinds.Link));
    }

    [Fact]
    public void An_unheld_node_lock_that_contains_a_plain_database_is_still_a_finding()
    {
        var dir = NewDir();
        PlantPlainDatabase(Path.Combine(dir, "node.lock"));

        PlainVaultCopyScanner.Scan(dir, PlainScanMode.Preflight).Select(f => (f.RelativePath, f.Kind))
            .Should().Equal([("node.lock", PlainCopyKinds.PlainSqlite)]);
    }

    [Fact]
    public void An_unreadable_non_lock_file_still_fails_closed()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "unreadable.bin");
        using var held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 1, FileOptions.DeleteOnClose);

        PlainVaultCopyScanner.Scan(dir, PlainScanMode.Preflight).Select(f => (f.RelativePath, f.Kind))
            .Should().Equal([("unreadable.bin", PlainCopyKinds.Unreadable)]);
    }

    [Fact]
    public void An_unreadable_instance_lock_name_with_plain_sqlite_content_fails_closed()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, InstanceGuard.FileName);
        PlantPlainDatabase(path);
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        PlainCopyScan.Run(dir, PlainScanMode.Preflight).Select(f => (f.RelativePath, f.Kind))
            .Should().Equal([(InstanceGuard.FileName, PlainCopyKinds.Unreadable)]);
    }
}
