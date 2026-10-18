namespace BeeMemoryBank.AppPaths;

/// <summary>What <see cref="SealSwapResolver"/> did at a start (docs/design/step-b-spec.md 10.6).</summary>
public enum SealResolveAction
{
    /// <summary>Nothing of a migration was on disk.</summary>
    None,
    /// <summary>A migration that never reached the swap was undone: its targets were deleted, the live files were never touched.</summary>
    RolledBack,
    /// <summary>The swap went forward (or was already done) and the replaced plain files were wiped, or are still to be wiped.</summary>
    SwappedForward,
    /// <summary>Plain leftovers beside a sealed pair were wiped (no journal, a garbage journal, or a journal that only said "building").</summary>
    LeftoversWiped,
    /// <summary>The start is refused (<see cref="SealStartRefusal"/>): nothing was deleted, moved, renamed or changed.</summary>
    Refused,
}

/// <summary>A start refused by name (spec 10.6 (b)): the files, the way back, nothing touched.</summary>
public sealed record SealStartRefusal(string Code, IReadOnlyList<string> Files, string Message)
{
    public const string SealLeftovers = "seal_leftovers";
}

/// <param name="Action">What was done.</param>
/// <param name="Refusal">Set when <paramref name="Action"/> is <see cref="SealResolveAction.Refused"/>.</param>
/// <param name="WipePending">Relative names of replaced plain files whose wipe failed; the journal stays at <c>swapping</c> and the next start retries.</param>
/// <param name="LinkedPlain">Relative names of leftovers that had another name: unlinked, never overwritten (the bytes live on under the other name).</param>
public sealed record SealSwapResolution(
    SealResolveAction Action,
    SealStartRefusal? Refusal = null,
    IReadOnlyList<string>? WipePending = null,
    IReadOnlyList<string>? LinkedPlain = null)
{
    public static readonly SealSwapResolution Nothing = new(SealResolveAction.None);

    public IReadOnlyList<string> WipePendingNames => WipePending ?? [];
    public IReadOnlyList<string> LinkedPlainNames => LinkedPlain ?? [];
}

/// <summary>
/// Finishes, undoes or refuses an interrupted "Encrypt this node" at a start, under the exclusive vault lease, right after the re-key's resolver
/// (docs/design/step-b-spec.md 10.6). It decides from what is on disk first and the journal second. A <b>leftover</b> is any file named
/// <c>beememorybank.db.seal-old*</c> or <c>chat.db.seal-old*</c> (the replaced plain files and their sidecars): it is a repair state whether or not a
/// journal exists, and no start ever serves while one is left (the wipe, or a named refusal that changes nothing).
/// </summary>
public static class SealSwapResolver
{
    private const string Db = VaultStoreProbe.Database;
    private const string Chat = VaultStoreProbe.Chat;
    private const string Boot = VaultStoreProbe.Boot;
    private static readonly string[] SwapOrder = [Db, Chat, Boot];
    private static readonly (string Live, string Old)[] SidecarNames = [("-wal", ".seal-old-wal"), ("-shm", ".seal-old-shm"), ("-journal", ".seal-old-journal")];

    /// <summary>
    /// Called by <see cref="VaultStartup.Enter"/> with the exclusive lease held. <paramref name="afterStep"/> is a test seam: it is called with the
    /// name of each step after it finished (a test that throws from it is a process killed at that step).
    /// </summary>
    public static SealSwapResolution Resolve(string dataDir, Action<string>? afterStep = null, Func<string, bool>? wipe = null)
    {
        var d = Path.GetFullPath(dataDir);
        // The link gate FIRST, before "does the folder exist": nothing is enumerated, read, renamed or wiped through a link, live or dangling.
        if (LinkGate.Refusal(d) is { } gate) return new SealSwapResolution(SealResolveAction.Refused, Refusal: gate);
        if (!Directory.Exists(d)) return SealSwapResolution.Nothing;

        var (state, journal) = SealJournal.Read(d);
        var leftovers = Leftovers(d);
        var targets = Targets(d);

        if (state == SealJournalState.Valid && journal!.Phase is SealJournal.Verified or SealJournal.Swapping)
            return GoForward(d, journal!, afterStep, wipe);

        // From here: no journal, an unreadable one, or one that says "building".
        if (leftovers.Count == 0)
        {
            if (targets.Count == 0 && state == SealJournalState.Missing) return SealSwapResolution.Nothing;
            // The migration never reached the swap: its targets (encrypted, key-less) and the journal go; the node stays plain with its old files.
            foreach (var t in targets) DeleteBestEffort(Path.Combine(d, t));
            if (state == SealJournalState.Valid) SealJournal.Delete(d);
            else if (state == SealJournalState.Unreadable) DeleteUnreadableJournal(d);
            FlushQuietly(d);
            return new SealSwapResolution(SealResolveAction.RolledBack);
        }

        // A leftover and no usable journal: the live pair decides.
        if (!VaultStoreProbe.IsSealedPair(d))
            return new SealSwapResolution(SealResolveAction.Refused, Refusal: LeftoverRefusal(d, leftovers));

        // Provably sealed: the swap had passed its renames (it only starts after the verification), so every leftover is a plain file that was
        // replaced. The unreadable journal is kept aside until the wipe is done.
        if (state == SealJournalState.Unreadable) KeepUnreadableJournal(d);
        foreach (var t in targets) DeleteBestEffort(Path.Combine(d, t));
        var result = WipeLeftovers(d, afterStep, wipe);
        afterStep?.Invoke("wiped");
        if (result.Pending.Count == 0)
        {
            if (state == SealJournalState.Valid) SealJournal.Delete(d);
            DeleteUnreadableJournal(d);
            FlushQuietly(d);
        }
        else
        {
            // The wipe could not finish: the retry rides on a journal the next start can read. The unreadable one stays as evidence
            // until no *.seal-* file is left.
            SealJournal.Write(d, SealJournal.New(SealJournal.Swapping, result.Pending, journal?.Snapshot ?? string.Empty));
        }
        return new SealSwapResolution(SealResolveAction.LeftoversWiped, WipePending: result.Pending, LinkedPlain: result.Linked);
    }

    /// <summary>
    /// The forward path of the swap (spec 10.6): one file at a time in the order database, chat, boot store; idempotent after a crash at any
    /// point; when all renames are done, every leftover is wiped and the journal goes. Also called by the migration itself (10.4 step 9) after it
    /// wrote the journal as <c>swapping</c>.
    /// </summary>
    public static SealSwapResolution GoForward(string dataDir, SealJournal journal, Action<string>? afterStep = null, Func<string, bool>? wipe = null)
    {
        var d = Path.GetFullPath(dataDir);
        if (LinkGate.Refusal(d) is { } gate) return new SealSwapResolution(SealResolveAction.Refused, Refusal: gate);
        var linked = new List<string>();

        // Nothing is renamed over a file that is not what the migration replaces: a live file that is already encrypted next to a target means
        // the directory is not in a state this migration produces (a restored set); the start is refused and nothing is changed.
        var foreign = new List<string>();
        foreach (var f in SwapOrder)
        {
            var target = Path.Combine(d, f + VaultStoreProbe.SealNewSuffix);
            var live = Path.Combine(d, f);
            if (!File.Exists(target) || !File.Exists(live) || new FileInfo(live).Length == 0) continue;
            if (!VaultStoreProbe.HasSqliteMagic(live) || f == Boot) foreign.Add(f);
        }
        if (foreign.Count > 0)
            return new SealSwapResolution(SealResolveAction.Refused, Refusal: LeftoverRefusal(d, foreign.Select(f => f + VaultStoreProbe.SealNewSuffix).ToList()));

        if (journal.Phase != SealJournal.Swapping)
            SealJournal.Write(d, journal with { Phase = SealJournal.Swapping });
        afterStep?.Invoke("journal-swapping");

        var snapshotName = journal.Snapshot;
        foreach (var f in SwapOrder)
        {
            var live = Path.Combine(d, f);
            var target = live + VaultStoreProbe.SealNewSuffix;
            if (!File.Exists(target))
            {
                afterStep?.Invoke("skip-" + f);
                continue;
            }

            // (a) a leftover of an earlier attempt next to a live file: wiped first (the live file is about to become the leftover).
            if (File.Exists(live))
            {
                foreach (var name in OldNamesOf(f))
                    if (File.Exists(Path.Combine(d, name))) WipeOne(d, name, wipe, linked);
                afterStep?.Invoke("wiped-old-" + f);

                // (b) the live plain file and its sidecars step aside. A stale -wal/-shm must never sit beside the new file.
                MoveAside(d, f);
                afterStep?.Invoke("old-aside-" + f);
            }
            else if (f != Boot)
            {
                // A crash between (b) and (c): the live name is free, the old file is already aside.
                afterStep?.Invoke("old-already-aside-" + f);
            }

            // The sidecars of the target (a boot store target may carry a WAL) follow it to the live name; a database target has none.
            MoveWithRetry(target, live);
            foreach (var (suffix, _) in SidecarNames.Take(2))
                if (File.Exists(target + suffix)) MoveWithRetry(target + suffix, live + suffix);
            DurableFs.FlushDirectory(d);
            afterStep?.Invoke("renamed-" + f);
        }

        // All renames are done. The replaced plain files are wiped only when the live pair is provably sealed: a leftover that is the only copy
        // of the database (the live name is gone and no target was there to put in its place) is never destroyed by a guess.
        var left = Leftovers(d);
        if (left.Count > 0 && !VaultStoreProbe.IsSealedPair(d))
            return new SealSwapResolution(SealResolveAction.Refused, Refusal: LeftoverRefusal(d, left));

        var result = WipeLeftovers(d, afterStep, wipe, linked);
        afterStep?.Invoke("wiped");
        if (result.Pending.Count == 0)
        {
            SealJournal.Delete(d);
            DeleteUnreadableJournal(d);
            FlushQuietly(d);
            afterStep?.Invoke("journal-deleted");
        }
        else
        {
            // The journal stays at "swapping" and lists what is still to be wiped; the node starts sealed and closed as usual.
            SealJournal.Write(d, new SealJournal(SealJournal.Swapping, journal.StartedAt, result.Pending, snapshotName));
        }
        return new SealSwapResolution(SealResolveAction.SwappedForward, WipePending: result.Pending, LinkedPlain: result.Linked);
    }

    /// <summary>
    /// Another try at the wipe of the replaced plain files, while the node runs (the migration after a swap whose wipe failed; the gate before an
    /// open). Nothing happens when no leftover is on the disk. With a leftover this is <see cref="Resolve"/>: the files go only when the live pair is
    /// provably sealed (a start is refused by name otherwise), and the journal goes with the last of them. It never rolls back and never touches a
    /// target, so it is safe next to a running node. What is still there is <see cref="Leftovers"/> afterwards.
    /// </summary>
    public static SealSwapResolution RetryWipe(string dataDir, Action<string>? afterStep = null, Func<string, bool>? wipe = null)
    {
        var d = Path.GetFullPath(dataDir);
        if (LinkGate.Refusal(d) is { } gate) return new SealSwapResolution(SealResolveAction.Refused, Refusal: gate);
        return Directory.Exists(d) && Leftovers(d).Count > 0 ? Resolve(d, afterStep, wipe) : SealSwapResolution.Nothing;
    }

    // ---- the pieces ---------------------------------------------------------------------------------------------------------------

    /// <summary>Every <c>*.seal-old*</c> file of the two databases, relative names.</summary>
    public static IReadOnlyList<string> Leftovers(string dataDir) => VaultStoreProbe.SealOldLeftovers(dataDir).Order(StringComparer.Ordinal).ToList();

    /// <summary>The <c>.seal-new</c> targets (and a WAL or SHM of the boot target) that exist, relative names.</summary>
    private static List<string> Targets(string d)
    {
        var found = new List<string>();
        foreach (var f in SwapOrder)
        {
            var name = f + VaultStoreProbe.SealNewSuffix;
            foreach (var n in new[] { name, name + "-wal", name + "-shm", name + "-journal" })
                if (File.Exists(Path.Combine(d, n))) found.Add(n);
        }
        return found;
    }

    private static IEnumerable<string> OldNamesOf(string f)
    {
        yield return f + VaultStoreProbe.SealOldSuffix;
        foreach (var (_, old) in SidecarNames) yield return f + old;
    }

    /// <summary>Renames the live file and its sidecars to the <c>.seal-old</c> names (a destination that exists was wiped before).</summary>
    private static void MoveAside(string d, string f)
    {
        var live = Path.Combine(d, f);
        MoveWithRetry(live, live + VaultStoreProbe.SealOldSuffix);
        foreach (var (suffix, old) in SidecarNames)
            if (File.Exists(live + suffix)) MoveWithRetry(live + suffix, live + old);
        DurableFs.FlushDirectory(d);
    }

    /// <summary>The retrying durable move of the re-key swap: a handle still closing on Windows fails a rename for a moment.</summary>
    private static void MoveWithRetry(string from, string to, int attempts = 10)
    {
        for (var i = 1; ; i++)
        {
            try
            {
                DurableFs.MoveFile(from, to);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && i < attempts)
            {
                Thread.Sleep(200 * i);
            }
        }
    }

    private sealed record WipeResult(List<string> Pending, List<string> Linked);

    /// <summary>Wipes every <c>*.seal-old*</c> file. A wipe that fails is listed as pending; a file with another name is unlinked, never overwritten.</summary>
    private static WipeResult WipeLeftovers(string d, Action<string>? afterStep, Func<string, bool>? wipe, List<string>? linked = null)
    {
        linked ??= [];
        var pending = new List<string>();
        foreach (var name in Leftovers(d))
        {
            if (!WipeOne(d, name, wipe, linked)) pending.Add(name);
            afterStep?.Invoke("wiped-" + name);
        }
        FlushQuietly(d);
        return new WipeResult(pending, linked);
    }

    private static bool WipeOne(string d, string name, Func<string, bool>? wipe, List<string> linked)
    {
        var path = Path.Combine(d, name);
        if (wipe is not null) return wipe(path);
        return SecureFileWipe.TryWipe(path, sharedWithAnotherName: p =>
        {
            // Another name for the same bytes (a hard link the owner made): only this name goes. An identity that cannot be read is not a link.
            var shared = FileIdentity.TryGet(p) is { LinkCount: > 1 };
            if (shared) linked.Add(name);
            return shared;
        });
    }

    private static SealStartRefusal LeftoverRefusal(string d, IReadOnlyList<string> files)
    {
        var message = "This node's data folder holds files of an interrupted \"Encrypt this node\" that cannot be placed safely: "
                      + string.Join(", ", files) + ". Nothing was opened, moved or deleted. "
                      + "Restore the data folder from one backup set (docs/sealed-database.md, \"Restore a backup set\")"
                      + "; do not delete any *.seal-old* file before you have checked which of them is your database.";
        return new SealStartRefusal(SealStartRefusal.SealLeftovers, files, message);
    }

    private static void KeepUnreadableJournal(string d)
    {
        var path = SealJournal.PathFor(d);
        if (!LinkEntry.Exists(path)) return;
        // Kept as evidence until the repair has ended; an older one of the same name is replaced.
        DurableFs.MoveFile(path, SealJournal.UnreadablePathFor(d));
    }

    private static void DeleteUnreadableJournal(string d)
    {
        // An unreadable journal still in place is renamed first, then both names go: nothing seal-related is left to repair.
        try
        {
            if (LinkEntry.Exists(SealJournal.PathFor(d)) && SealJournal.Read(d).State == SealJournalState.Unreadable) KeepUnreadableJournal(d);
            File.Delete(SealJournal.UnreadablePathFor(d));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void DeleteBestEffort(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void FlushQuietly(string d)
    {
        try { DurableFs.FlushDirectory(d); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
