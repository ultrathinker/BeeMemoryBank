namespace BeeMemoryBank.AppPaths;

/// <summary>A process's hold on <c>&lt;D&gt;.vault.lease</c>: shared by everything that opens the vault, exclusive for the re-key.</summary>
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
/// <item><c>&lt;D&gt;.vault.lease</c>, next to D like the re-key's other files, is held <b>shared</b> for the life of the
///   process: any number of these processes at once
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
    /// <summary>
    /// Next to D, not in it: the verb creating it leaves the old vault byte-identical, and an open lease handle never
    /// sits inside a directory the swap renames (which Windows refuses).
    /// </summary>
    public static string LeasePathFor(string dataDir) => RekeySwapJournal.Normalize(dataDir) + ".vault.lease";

    /// <summary>
    /// Passes the gate. Throws <see cref="VaultInUseException"/> while a re-key runs. The caller keeps the lease for its
    /// whole life (a field or a DI singleton: a local the JIT sees as dead may be finalized, which releases it).
    /// <para>Nothing is resolved without the lease (review release-b-fix #1). The swap resolver renames directories, so
    /// it runs only with the lease held <b>exclusively</b>. Then no re-key (which needs it exclusively too) and no
    /// other start can be in the middle of the same journal, and the re-key lock is checked again under it. Afterwards
    /// the lease is taken shared for the life of the process. A start that finds others holding it shared joins them:
    /// they passed this gate, so the swap is already resolved. One that finds it held exclusively waits up to
    /// <paramref name="wait"/> (another start resolving) and is then refused (a re-key holding the vault).</para>
    /// </summary>
    public static (RekeySwapResolution Resolution, VaultLease Lease) Enter(string dataDir, TimeSpan? wait = null)
    {
        var d = RekeySwapJournal.Normalize(dataDir);
        var until = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(15));
        while (true)
        {
            if (Refusal(d) is { } running) throw new VaultInUseException(running);

            RekeySwapResolution? resolved = null;
            using (var exclusive = TryAcquireExclusive(d))
            {
                if (exclusive != null)
                {
                    if (Refusal(d) is { } late) throw new VaultInUseException(late);
                    // The link gate FIRST (BMB-306 fix round 5): a data folder that is a link, or a reserved name in it that is one (live or dangling), refuses the start by
                    // name before any resolver below asks whether anything exists, reads, moves or wipes through it.
                    resolved = LinkGate.RefusedStart(d) ?? RekeySwapResolver.Resolve(d);
                    if (resolved.Seal is null)
                    {
                        // An interrupted "Encrypt this node" (docs/design/step-b-spec.md 10.6) is finished, undone or refused by name here too,
                        // under the same exclusive lease and before anything opens or creates a file of the data folder.
                        if (SealSwapResolver.Resolve(resolved.DataDir) is { Action: not SealResolveAction.None } seal)
                            resolved = resolved with { Seal = seal };
                        // A setup, join or restore that was cut off before the node was encrypted (SealIntent): the encrypted pair loses only its marker,
                        // anything else it had written in the open is wiped, here, before the plain start can serve it. A file that will not go refuses
                        // the start by name, like a leftover of an interrupted encryption.
                        if (SealIntent.Resolve(resolved.DataDir) is { Action: not SealIntentAction.None } intent)
                        {
                            resolved = resolved with { Intent = intent };
                            if (intent.Refusal is { } refusal && resolved.Seal?.Refusal is null)
                                resolved = resolved with { Seal = new SealSwapResolution(SealResolveAction.Refused, Refusal: refusal) };
                        }
                    }
                }
            }

            // Shared from here. Between releasing the exclusive hold and taking this one another start may resolve
            // (it finds nothing left to do) or a re-key may take the lease (this one then fails, and its lock refuses).
            var shared = TryAcquireShared(d);
            if (shared != null)
            {
                if (Refusal(d) is { } late)
                {
                    shared.Dispose();
                    throw new VaultInUseException(late);
                }
                return (resolved ?? Joined(d), shared);
            }

            if (DateTime.UtcNow >= until)
                throw new VaultInUseException(
                    $"A content re-key holds the vault {d} ({LeasePathFor(d)} is held exclusively). Start again once it has finished.");
            Thread.Sleep(100);
        }
    }

    /// <summary>A start that joined others already past the gate: the swap is resolved; only say whether it is the
    /// first start on a swapped-in vault.</summary>
    private static RekeySwapResolution Joined(string d)
    {
        // The link gate FIRST on this path too: a start that joined others does not read a journal or ask about a marker through a link either.
        if (LinkGate.RefusedStart(d) is { } gated) return gated;
        RekeySwapJournal? journal = null;
        try { journal = RekeySwapJournal.Read(d); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or NotSupportedException) { }
        var joined = new RekeySwapResolution(d, journal?.Phase == RekeySwapJournal.Swapped && Directory.Exists(d));

        // The others passed the gate, so the swap is resolved - but a setup, join or restore may be running in one of them, or have been cut off while another
        // still runs. This start does not resolve a seal intent (that needs the lease exclusively, and it may be a live operation's), and it must not serve
        // what the operation writes in the open either: it is refused by name, and nothing is wiped (BMB-306 fix round 2).
        if (SealIntent.BusyRefusal(d) is { } busy)
            joined = joined with
            {
                Intent = new SealIntentResolution(SealIntentAction.Refused, SealIntent.KindOf(d), null, busy),
                Seal = new SealSwapResolution(SealResolveAction.Refused, Refusal: busy),
            };
        return joined;
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
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (access == FileAccess.Read && !File.Exists(path))
                using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }
            return new VaultLease(new FileStream(path, FileMode.OpenOrCreate, access, share, bufferSize: 1));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
