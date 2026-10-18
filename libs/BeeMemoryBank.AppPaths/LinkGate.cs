namespace BeeMemoryBank.AppPaths;

/// <summary>
/// The first thing every entry point of a data folder does (BMB-306 fix round 5, J1): ONE inventory of what must not be a link and ONE check of it, made from the directory
/// entries themselves (<see cref="LinkEntry"/>) before any question that looks through a link ("does it exist", "is a marker pending", "is it a sealed pair"), any read, any
/// probe, any swap, move or wipe. A link, to something or to nothing, is refused by name and nothing is opened, read, written or removed through it.
/// <para>
/// The entry points that call it first: <see cref="VaultStartup.Enter"/> (both of its branches), <see cref="RekeySwapResolver.Resolve"/>, <see cref="SealSwapResolver.Resolve"/>,
/// <see cref="SealSwapResolver.GoForward"/>, <see cref="SealSwapResolver.RetryWipe"/>, <see cref="SealIntent.Begin"/>, <see cref="SealIntent.Resolve"/>,
/// <see cref="SealIntent.KindOf"/>, and <see cref="VaultStoreProbe"/> on its own (its probes treat a linked input as "not a sealed pair", "unreadable", "no magic"). The names are
/// listed here once; a test ties the wipe inventory and the marker files of <see cref="SealIntent"/> to this list so that it cannot drift.
/// </para>
/// <para>
/// Scope: the folder's own last entry and the names directly inside it. A parent of the data folder that is a link is not judged (the folder is then reached by its real
/// name), and what is below the working folder <c>tmp</c> is inventoried by <see cref="SealIntent"/> before it wipes.
/// </para>
/// </summary>
internal static class LinkGate
{
    private static readonly string[] Sidecars = ["", "-wal", "-shm", "-journal"];

    /// <summary>Every name directly inside a data folder that a start, a swap, a probe or the seal intent reads, writes, moves or wipes.</summary>
    public static readonly IReadOnlyList<string> ReservedNames = BuildReservedNames();

    private static IReadOnlyList<string> BuildReservedNames()
    {
        var names = new List<string>();
        var stores = new[] { VaultStoreProbe.Database, VaultStoreProbe.Chat, VaultStoreProbe.Boot };
        foreach (var store in stores)
            foreach (var suffix in Sidecars)
                names.Add(store + suffix);
        foreach (var store in stores)
            foreach (var suffix in Sidecars)
                names.Add(store + VaultStoreProbe.SealNewSuffix + suffix);
        names.AddRange(SealIntent.MarkerFiles());
        names.Add(SealJournal.FileName);
        names.Add(SealJournal.FileName + ".tmp");
        names.Add(SealJournal.FileName + SealJournal.UnreadableSuffix);
        names.Add(SealIntent.WorkingFolder);
        return names;
    }

    /// <summary>
    /// The entries that are links, as names relative to the data folder: the folder itself (by its own name, and then nothing inside it is looked at) or every reserved name inside
    /// it that is one, a link to nothing too. Empty when none is. Reads the attributes of the entries only; nothing is opened, followed or enumerated.
    /// </summary>
    public static List<string> Linked(string dataDir)
    {
        var d = RekeySwapJournal.Normalize(dataDir);
        if (LinkEntry.IsLink(d))
        {
            var own = Path.GetFileName(d);
            return [own.Length == 0 ? "the data folder" : own];
        }
        var linked = new List<string>();
        foreach (var name in ReservedNames)
            if (LinkEntry.IsLink(Path.Combine(d, name)))
                linked.Add(name);
        return linked;
    }

    /// <summary>The refusal for a data folder that holds a link (see <see cref="Linked"/>), or null when it holds none.</summary>
    public static SealStartRefusal? Refusal(string dataDir) => Refusal(Linked(dataDir), dataDir);

    private static SealStartRefusal? Refusal(List<string> linked, string dataDir)
    {
        if (linked.Count == 0) return null;
        var folderItself = LinkEntry.IsLink(RekeySwapJournal.Normalize(dataDir));
        var message = folderItself
            ? "The data folder of this node is a link (a symbolic link or a junction). What is behind a link cannot be proven to be inside the folder, so nothing was opened, " +
              "read, moved or deleted. Point the data path at the real folder and start again."
            : string.Join(", ", linked) + (linked.Count == 1 ? " is a link" : " are links") +
              " (a symbolic link or a junction) inside the data folder of this node: a link is never opened, followed or removed here, because what is behind it cannot be proven " +
              "to be inside the data folder. Nothing was opened, read, moved or deleted. Remove the link yourself (not what it points to), and start again.";
        return new SealStartRefusal(SealIntent.UnsafeCode, linked, message);
    }

    /// <summary>
    /// The resolution of a start this gate refuses, or null when the data folder holds no link. The refusal is in <see cref="RekeySwapResolution.Seal"/>, where every host reads why
    /// a start is refused; the resolvers that would act through the folder are not called.
    /// </summary>
    public static RekeySwapResolution? RefusedStart(string dataDir) =>
        Refusal(dataDir) is { } refusal
            ? new RekeySwapResolution(RekeySwapJournal.Normalize(dataDir), false, new SealSwapResolution(SealResolveAction.Refused, Refusal: refusal))
            : null;
}
