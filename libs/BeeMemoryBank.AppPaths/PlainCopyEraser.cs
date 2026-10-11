namespace BeeMemoryBank.AppPaths;

/// <summary>What <see cref="PlainCopyEraser.Erase"/> did, by relative path.</summary>
/// <param name="Erased">Overwritten with random bytes and removed.</param>
/// <param name="UnlinkedOnly">Another name for the same bytes appeared after the scan: this name was removed, the bytes were not overwritten.</param>
/// <param name="NotErased">Still there: the wipe failed (locked, access denied) or the file's identity could not be read.</param>
/// <param name="Refused">Asked for, but not erased: the kind the rescan gave it when it is a finding of a kind that is never erased (a link, an archive that could not be read...), <see cref="PlainEraseRefusal.NotFound"/> when the rescan does not list it.</param>
/// <param name="Remaining">The findings of the rescan after the erase, for the page to show.</param>
public sealed record PlainEraseResult(
    IReadOnlyList<string> Erased,
    IReadOnlyList<string> UnlinkedOnly,
    IReadOnlyList<string> NotErased,
    IReadOnlyList<PlainEraseRefusal> Refused,
    IReadOnlyList<PlainCopyFinding> Remaining);

/// <summary>A path the erase refused and why (spec 10.2: "for each refused path, its kind").</summary>
public sealed record PlainEraseRefusal(string Path, string Kind)
{
    public const string NotFound = "not_found";
}

/// <summary>
/// The "erase these plain copies" of the preflight (docs/design/step-b-spec.md 10.2): the owner is shown the findings of <see cref="PlainVaultCopyScanner"/>
/// and confirms the ones to erase. The scan runs again here, and exactly the intersection of what was asked and what the rescan finds as a plain SQLite
/// file, a plain journal or a plain archive is erased - never a file of another kind (a link, a file sharing a live file's identity, an archive that
/// could not be read), never a path outside the data folder, never a live database. Each file goes through <see cref="SecureFileWipe"/> with the guard
/// against a second name: the bytes of a file that has one are not overwritten.
/// </summary>
/// <summary>
/// The content scan as this node's own code runs it (the preflight, the final scan of the migration, the erase and its re-scan): the scanner's findings
/// without the one that is not a copy. A running node holds <c>.instance.lock</c> exclusively (<see cref="InstanceGuard"/>); the file holds no data, but on
/// Linux and macOS (advisory flock) and inside the holding process on Windows no other open succeeds, so the scanner reports it as <c>unreadable</c> and
/// every preflight of a running node would be blocked by its own lock. Only that finding of that file at the top of the data folder is dropped: a file of
/// that name anywhere else, or one that can be read and holds a plain database, is judged like any other.
/// </summary>
public static class PlainCopyScan
{
    public static IReadOnlyList<PlainCopyFinding> Run(string dataPath, PlainScanMode mode, CancellationToken ct = default) =>
        PlainVaultCopyScanner.Scan(dataPath, mode, ct: ct)
            .Where(f => !(f.Kind == PlainCopyKinds.Unreadable && string.Equals(f.RelativePath, InstanceGuard.FileName, StringComparison.Ordinal)))
            .Where(f => !VanishedWhileScanned(dataPath, f))
            .ToList();

    /// <summary>
    /// SQLite deletes a database's WAL and SHM when its last connection closes. A file that is listed and gone before the scanner reads it has
    /// attributes of -1 (taken for a reparse point and reported as a <c>link</c>) or cannot have its identity read (<c>identity_unavailable</c>,
    /// <c>unreadable</c>). A finding for a path that is not there any more, and is not a dangling link either, is that: it is dropped.
    /// (The scanner itself belongs to the shared layer; this is the caller's guard.)
    /// </summary>
    private static bool VanishedWhileScanned(string dataPath, PlainCopyFinding finding)
    {
        if (finding.Kind is not (PlainCopyKinds.Link or PlainCopyKinds.IdentityUnavailable or PlainCopyKinds.Unreadable)) return false;
        try
        {
            var info = new FileInfo(Path.Combine(dataPath, finding.RelativePath));
            return !info.Exists && info.LinkTarget is null && !Directory.Exists(info.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}

public static class PlainCopyEraser
{
    public static PlainEraseResult Erase(string dataPath, PlainScanMode mode, IReadOnlyCollection<string> relativePaths, Func<string, bool>? wipeOverride = null)
    {
        var root = Path.GetFullPath(dataPath);
        var asked = new HashSet<string>(relativePaths.Select(Normalize), StringComparer.Ordinal);
        var all = PlainCopyScan.Run(root, mode);
        var found = all.Where(f => PlainCopyKinds.CanBeErased(f.Kind)).ToDictionary(f => Normalize(f.RelativePath), f => f, StringComparer.Ordinal);
        var otherKinds = all.Where(f => !PlainCopyKinds.CanBeErased(f.Kind)).GroupBy(f => Normalize(f.RelativePath), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Kind, StringComparer.Ordinal);

        var erased = new List<string>();
        var unlinked = new List<string>();
        var notErased = new List<string>();
        var refused = new List<PlainEraseRefusal>();

        foreach (var name in asked.OrderBy(n => n, StringComparer.Ordinal))
        {
            if (!found.TryGetValue(name, out var finding))
            {
                refused.Add(new PlainEraseRefusal(name, otherKinds.GetValueOrDefault(name, PlainEraseRefusal.NotFound)));
                continue;
            }
            var path = Path.GetFullPath(Path.Combine(root, finding.RelativePath));
            // The relative path came from the scanner, but the check costs nothing: a file outside the folder is never touched.
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                refused.Add(new PlainEraseRefusal(name, PlainEraseRefusal.NotFound));
                continue;
            }

            var sharedAfterScan = false;
            bool gone;
            if (wipeOverride is not null) gone = wipeOverride(path);
            else
            {
                gone = SecureFileWipe.TryWipe(path, sharedWithAnotherName: p =>
                {
                    // An identity that cannot be read is not a licence to overwrite: the IOException makes the wipe report "not erased".
                    var identity = FileIdentity.TryGet(p) ?? throw new IOException("The identity of the file could not be read.");
                    sharedAfterScan = identity.LinkCount > 1 || SharesIdentityWithLive(root, identity);
                    return sharedAfterScan;
                });
            }

            if (!gone) notErased.Add(name);
            else if (sharedAfterScan) unlinked.Add(name);
            else erased.Add(name);
        }

        return new PlainEraseResult(erased, unlinked, notErased, refused, PlainCopyScan.Run(root, mode));
    }

    private static bool SharesIdentityWithLive(string root, FileIdentityInfo identity)
    {
        foreach (var live in new[] { VaultStoreProbe.Database, VaultStoreProbe.Chat, VaultStoreProbe.Boot })
        {
            var path = Path.Combine(root, live);
            if (File.Exists(path) && FileIdentity.TryGet(path) is { } other && other.IsSameFileAs(identity)) return true;
        }
        return false;
    }

    private static string Normalize(string relative) => relative.Replace('\\', '/').Trim();
}
