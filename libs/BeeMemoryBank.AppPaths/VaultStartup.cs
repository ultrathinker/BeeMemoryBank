namespace BeeMemoryBank.AppPaths;

/// <summary>A process's hold on <c>D/vault.lease</c>: shared by everything that opens the vault, exclusive for the re-key.</summary>
public sealed class VaultLease : IDisposable
{
    private readonly FileStream _file;

    internal VaultLease(FileStream file) => _file = file;

    public void Dispose() => _file.Dispose();
}

/// <summary>The re-key refuses, or a start is refused, because the other side holds the vault.</summary>
public sealed class VaultInUseException(string message) : InvalidOperationException(message);

/// <summary>
/// The one gate every process that opens a data directory passes before it creates or opens anything in it (review
/// release-b R1-1..R1-3): the Node in both its modes, the Api (standalone, Docker, or the Node's child) and every CLI
/// command. Only the <c>bmb rekey</c> verb stays out: it takes the lease exclusively instead.
/// <list type="number">
/// <item>A re-key that is running (its <c>&lt;D&gt;.rekey.lock</c> held) refuses the start before anything is resolved:
///   the verb may be in the middle of its own renames.</item>
/// <item>Otherwise an interrupted swap is finished or rolled back (<see cref="RekeySwapResolver.Resolve"/>).</item>
/// <item><c>D/vault.lease</c> is held <b>shared</b> for the life of the process: any number of these processes at once
///   (a node and its child Api, an Api and a CLI command), while the verb, which needs it <b>exclusive</b>, is refused
///   for as long as one of them runs, and none of them can start while the verb holds it.</item>
/// <item>The re-key lock is checked once more with the lease held, closing the window between the first check and the
///   lease.</item>
/// </list>
/// FileShare.None is an exclusive OS lock on Windows and an exclusive <c>flock</c> on Linux and macOS; any other share
/// mode takes a shared one. Both go with the process, so a crash never leaves the vault locked.
/// </summary>
public static class VaultStartup
{
    public const string LeaseFile = "vault.lease";

    public static string LeasePathFor(string dataDir) => Path.Combine(RekeySwapJournal.Normalize(dataDir), LeaseFile);

    /// <summary>Passes the gate. Throws <see cref="VaultInUseException"/> while a re-key runs. The caller keeps the lease
    /// for its whole life (a field or a DI singleton: a local the JIT sees as dead may be finalized, which releases it).</summary>
    public static (RekeySwapResolution Resolution, VaultLease Lease) Enter(string dataDir)
    {
        var refusal = Refusal(dataDir);
        if (refusal != null) throw new VaultInUseException(refusal);
        var resolution = RekeySwapResolver.Resolve(dataDir);
        Directory.CreateDirectory(resolution.DataDir);
        var lease = TryAcquireShared(resolution.DataDir)
            ?? throw new VaultInUseException(
                $"A content re-key holds the vault {resolution.DataDir} ({LeaseFile} is held exclusively). Start again once it has finished.");
        if (Refusal(resolution.DataDir) is { } late)
        {
            lease.Dispose();
            throw new VaultInUseException(late);
        }
        return (resolution, lease);
    }

    private static string? Refusal(string dataDir) =>
        RekeyLock.IsHeld(dataDir)
            ? $"A content re-key is running on {RekeySwapJournal.Normalize(dataDir)} ({RekeySwapJournal.LockPathFor(dataDir)} is held). "
              + "Start again once it has finished."
            : null;

    /// <summary>A shared hold, as every process that opens the vault takes it; null while the verb holds it exclusively.</summary>
    public static VaultLease? TryAcquireShared(string dataDir) =>
        TryOpen(LeasePathFor(dataDir), FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>The verb's exclusive hold; null while any process holds it shared.</summary>
    public static VaultLease? TryAcquireExclusive(string dataDir) =>
        TryOpen(LeasePathFor(dataDir), FileAccess.ReadWrite, FileShare.None);

    private static VaultLease? TryOpen(string path, FileAccess access, FileShare share)
    {
        try
        {
            if (access == FileAccess.Read && !File.Exists(path))
                using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }
            return new VaultLease(new FileStream(path, FileMode.OpenOrCreate, access, share, bufferSize: 1));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
