using BeeMemoryBank.AppPaths;

namespace BeeMemoryBank.Rekey;

/// <summary>
/// The swap of an offline re-key (rekey-offline.md §2 step 6, §5), run by the verb once the new vault in
/// <c>D.rekey-new</c> is verified and scrubbed and every connection to it is closed:
/// <list type="number">
/// <item><see cref="CarryOver"/>: the §5 "carried over" files and directories are copied from D into the new vault.
///   They are copied, never moved: D is not written.</item>
/// <item>The journal <c>&lt;D&gt;.rekey-journal.json</c> is written durably ({new, old, phase: prepared}).</item>
/// <item>Rename 1, D → <c>D.pre-rekey-&lt;ts&gt;</c>; the journal says <c>old-moved</c>.</item>
/// <item>Rename 2, <c>D.rekey-new</c> → D; the journal says <c>swapped</c>.</item>
/// </list>
/// A crash anywhere leaves D startable: before the journal, D is the old vault; after it, the next start's
/// <see cref="RekeySwapResolver"/> finishes the swap from what is on disk. The first successful start removes the
/// journal and the re-key lock (<see cref="RekeySwapResolver.CompleteFirstStart"/>). The old vault's content is never
/// changed; it stays at its new name until the owner deletes it.
/// </summary>
public static class RekeySwap
{
    /// <summary>
    /// §5 "carried over": TLS and network config and the plaintext audit logs. The databases are not
    /// in the list: the new vault's are the re-keyed copies. Everything else stays behind in the old directory.
    /// </summary>
    public static readonly IReadOnlyList<string> CarriedOver =
    [
        // Not .internal-key: it is the Web-to-Api trust credential, and an old copy of it must not open the new
        // vault; KeyMaterialStep writes a fresh one into the new vault (L-2).
        "certs", "tls", "internet-access", "ddns-state.json", "wipe-audit.log", "reset-audit.log",
    ];

    /// <summary>The fault points of the swap, for the brick tests.</summary>
    public const string FaultCarryOver = "carry-over", FaultBeforeRename1 = "before-rename-1",
        FaultBetweenRenames = "between-renames", FaultAfterRename2 = "after-rename-2";

    /// <summary>Copies every <see cref="CarriedOver"/> entry of <paramref name="dataDir"/> that exists into <paramref name="newDir"/>.</summary>
    public static IReadOnlyList<string> CarryOver(string dataDir, string newDir, Action<string>? fault = null) =>
        CarryOver(dataDir, newDir, skippedLinks: null, fault);

    /// <summary>
    /// The carry-over, never following a link (review release-b R1-7): a junction or symbolic link anywhere under a
    /// carried-over entry, the entry itself included, is not copied and its path relative to D goes into
    /// <paramref name="skippedLinks"/>. Only regular files and directories that really are inside D are copied.
    /// </summary>
    public static IReadOnlyList<string> CarryOver(string dataDir, string newDir, List<string>? skippedLinks, Action<string>? fault = null)
    {
        var copied = new List<string>();
        foreach (var name in CarriedOver)
        {
            var from = Path.Combine(dataDir, name);
            var to = Path.Combine(newDir, name);
            if (!Directory.Exists(from) && !File.Exists(from)) continue;
            if (NoFollow.IsLink(from))
            {
                skippedLinks?.Add(name);
                continue;
            }
            if (Directory.Exists(from)) CopyDirectory(dataDir, from, to, skippedLinks, fault);
            else
            {
                fault?.Invoke(FaultCarryOver);
                File.Copy(from, to, overwrite: true);
            }
            copied.Add(name);
        }
        return copied;
    }

    private static void CopyDirectory(string dataDir, string from, string to, List<string>? skippedLinks, Action<string>? fault)
    {
        Directory.CreateDirectory(to);
        foreach (var entry in Directory.EnumerateFileSystemEntries(from))
        {
            if (NoFollow.IsLink(entry))
            {
                skippedLinks?.Add(Path.GetRelativePath(dataDir, entry).Replace('\\', '/'));
                continue;
            }
            var target = Path.Combine(to, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CopyDirectory(dataDir, entry, target, skippedLinks, fault);
            else
            {
                fault?.Invoke(FaultCarryOver);
                File.Copy(entry, target, overwrite: true);
            }
        }
    }

    /// <summary>
    /// Carries over, writes the journal, and swaps. Returns where the old vault went. The new vault must be at
    /// <see cref="RekeySwapJournal.NewDirFor"/>(<paramref name="dataDir"/>).
    /// <paramref name="fault"/> is the tests' fault injection, called at each named point.
    /// </summary>
    public static string Swap(string dataDir, DateTimeOffset now, Action<string>? fault = null)
    {
        var d = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir));
        var newDir = RekeySwapJournal.NewDirFor(d);
        var oldDir = RekeySwapJournal.OldDirFor(d, now);
        if (!Directory.Exists(d)) throw new InvalidOperationException($"The data directory {d} does not exist.");
        if (!Directory.Exists(newDir)) throw new InvalidOperationException($"The new vault {newDir} does not exist.");
        if (Directory.Exists(oldDir)) throw new InvalidOperationException($"{oldDir} already exists.");
        if (RekeySwapJournal.Read(d) != null)
            throw new InvalidOperationException($"A swap is already in progress ({RekeySwapJournal.PathFor(d)}); start the node to finish it.");

        CarryOver(d, newDir, fault);

        var journal = new RekeySwapJournal(newDir, oldDir, RekeySwapJournal.Prepared);
        RekeySwapJournal.Write(d, journal);
        fault?.Invoke(FaultBeforeRename1);
        RekeySwapResolver.RenameWithRetry(d, oldDir);
        RekeySwapJournal.Write(d, journal with { Phase = RekeySwapJournal.OldMoved });
        fault?.Invoke(FaultBetweenRenames);
        RekeySwapResolver.RenameWithRetry(newDir, d);
        RekeySwapJournal.Write(d, journal with { Phase = RekeySwapJournal.Swapped });
        fault?.Invoke(FaultAfterRename2);
        return oldDir;
    }
}
