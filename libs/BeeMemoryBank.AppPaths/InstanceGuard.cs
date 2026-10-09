namespace BeeMemoryBank.AppPaths;

/// <summary>Another node process already runs on this data folder.</summary>
public sealed class InstanceInUseException(string message) : InvalidOperationException(message);

/// <summary>A process's exclusive hold on <c>&lt;D&gt;/.instance.lock</c>: at most one node process works on a data folder.</summary>
public sealed class InstanceLock : IDisposable
{
    private readonly FileStream _file;

    internal InstanceLock(FileStream file) => _file = file;

    public void Dispose() => _file.Dispose();
}

/// <summary>
/// One node process per data folder. <see cref="VaultStartup"/> holds the vault lease <b>shared</b> by design (a node, its
/// child Api and a CLI command all open the same folder), so by itself it does not stop a second node: two containers on one
/// volume both started healthy, both unlocked, and wrote different events under the same node id and the same Lamport times,
/// which a peer's conflict resolver takes for one write and so keeps only the first (week review, F4).
///
/// <para>The Api and the blind node take this lock, exclusively, right after the vault gate and keep it until their host has stopped;
/// the CLI and the re-key do not (a command next to a running node stays what it was). It lives <b>inside</b> the data
/// folder, unlike the lease: a second container shares the volume, not the first container's <c>/app</c>. A start that finds
/// it held waits a little (a restart whose predecessor is still shutting down) and is then refused.</para>
///
/// <para>The file is held open with a sharing mode that refuses a second holder (a sharing violation on Windows, an exclusive
/// <c>flock</c> on Linux and macOS), and it goes with the process, so a crash never leaves the folder locked. On Windows other
/// programs may still read the file (a copy of the live folder works). A file system that does not carry locks between hosts (NFS used by two
/// machines) cannot be guarded this way; <c>docs/deployment.md</c> says so.</para>
/// </summary>
public static class InstanceGuard
{
    public const string FileName = ".instance.lock";

    public static string LockPathFor(string dataDir) => Path.Combine(dataDir, FileName);

    /// <summary>Takes the lock; the caller keeps it (a variable the host stays reachable from) and disposes it. Waits up to <paramref name="wait"/> (default 15 s) for a holder that is going away.</summary>
    public static InstanceLock Acquire(string dataDir, TimeSpan? wait = null)
    {
        var path = LockPathFor(dataDir);
        var until = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(15));
        while (true)
        {
            if (TryOpen(path) is { } held) return held;
            if (DateTime.UtcNow >= until)
                throw new InstanceInUseException(
                    $"Another Bee Memory Bank process is already using the data folder {dataDir} ({path} is held). " +
                    "Two nodes on one folder write different events under one node id; stop the other one first " +
                    "(a second container, an old container left running, a restore script).");
            Thread.Sleep(100);
        }
    }

    /// <summary>For a program's entry point: when another process holds the folder, says so on stderr and ends the process with exit code 1 (no stack trace, no core dump).</summary>
    public static InstanceLock AcquireOrExit(string dataDir)
    {
        try
        {
            return Acquire(dataDir);
        }
        catch (InstanceInUseException ex)
        {
            Console.Error.WriteLine($"[Error] {ex.Message}");
            Environment.Exit(1);
            throw;
        }
    }

    private static InstanceLock? TryOpen(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            // Windows: write access with FileShare.Read refuses a second holder (it wants write access) and still lets a copy, an
            // Explorer drag or a backup tool read the file: FileShare.None would make "copy the data folder" fail on this one file.
            // Linux and macOS: .NET turns FileShare.None into an exclusive flock, and anything less into a shared one, which would
            // not exclude; flock is advisory, so cp, tar and restic are not held off by it.
            var share = OperatingSystem.IsWindows() ? FileShare.Read : FileShare.None;
            return new InstanceLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, share, bufferSize: 1));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
