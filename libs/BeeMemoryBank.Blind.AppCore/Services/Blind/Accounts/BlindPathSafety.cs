namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// Is a path of the account tree what it should be: a real directory or file, not a symbolic link, a junction or any other reparse point. An id that is valid as a
/// folder NAME says nothing about WHERE the folder really is: <c>accounts/B</c> that is a link to <c>accounts/A</c> (or to a folder outside the app) would let account B
/// open account A's database, state and log, and a link on <c>accounts</c> does the same for all of them. So before anything is created, opened or composed below
/// the app's root, the root, <c>accounts</c>, the account's folder and the registry file are looked at WITHOUT following them, and an unsafe path is treated like a
/// damaged registry or a damaged account: nothing is started, nothing is created through it, and a person is told what to repair.
///
/// <para>Only the root itself and what is below it are judged: the folders ABOVE the root (a symbolic <c>/var</c> on macOS, a moved profile) are the host's own choice
/// of where the app lives and are not walked. A root that is itself a link is refused: point the host at the real folder.</para>
///
/// <para>Looking and then using is not atomic; a process that swaps a folder for a link in between is a process that can already write the app's folder, which is
/// outside what this check can stop. It stops the mistakes (a restore, a copy, a tool, a person), and the registry lock keeps the app's own processes apart.</para>
/// </summary>
internal static class BlindPathSafety
{
    /// <summary>True when <paramref name="path"/> itself is a link or a reparse point, existing or dangling. A path that cannot be examined counts as one (fail closed).</summary>
    public static bool IsLink(string path)
    {
        FileSystemInfo info = new FileInfo(path);
        try
        {
            if (info.LinkTarget is not null) return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
        try
        {
            var attributes = info.Attributes;
            return attributes != (FileAttributes)(-1) && attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>True when something is at <paramref name="path"/>, a dangling link included (which <see cref="Directory.Exists(string)"/> and <see cref="File.Exists(string)"/> do not say).</summary>
    public static bool Occupied(string path) => Directory.Exists(path) || File.Exists(path) || IsLink(path);

    /// <summary>Null when the app's root is absent or a real folder; otherwise the sentence that says what is wrong.</summary>
    public static string? RootProblem(string root)
    {
        if (!Occupied(root)) return null;
        if (IsLink(root))
            return "The folder this app keeps its data in is a link (a symbolic link or a junction), which could send an account's data somewhere else. Use the real folder.";
        if (!Directory.Exists(root)) return "The name of the folder this app keeps its data in is taken by a file.";
        return null;
    }

    /// <summary>Null when <c>accounts</c> is absent or a real folder below a safe root.</summary>
    public static string? AccountsFolderProblem(string root)
    {
        if (RootProblem(root) is { } rootProblem) return rootProblem;
        var accounts = Path.Combine(root, BlindAccountRegistry.AccountsFolder);
        if (!Occupied(accounts)) return null;
        if (IsLink(accounts))
            return "The folder 'accounts' is a link (a symbolic link or a junction), which could send an account's data somewhere else. Remove the link by hand.";
        if (!Directory.Exists(accounts)) return "The name of the folder 'accounts' is taken by a file.";
        return null;
    }

    /// <summary>
    /// Null when the registry file is absent or an ordinary file in a safe root. A link is judged BEFORE the existence check: a dangling one would otherwise read as "no
    /// registry" and be replaced by an empty one.
    /// </summary>
    public static string? RegistryFileProblem(string root, string filePath)
    {
        if (RootProblem(root) is { } rootProblem) return rootProblem;
        if (!Occupied(filePath)) return null;
        if (IsLink(filePath)) return "accounts.json is a link, which could make this app read or write another file. Nothing was started or changed.";
        if (!File.Exists(filePath)) return "The name accounts.json is taken by a folder. Nothing was started or changed.";
        return null;
    }

    /// <summary>
    /// Null when <paramref name="baseDirectory"/> and every EXISTING component from it down to <paramref name="path"/> are real folders or files (no link, no reparse point, a dangling
    /// one included); otherwise the sentence that says which is not. Looks only. A path that is not below the base is judged by itself. This is what a measurement of the data an account
    /// owns asks before it walks: a root that is a link would be counted for what lies behind it.
    /// </summary>
    public static string? ComponentProblem(string baseDirectory, string path)
    {
        var basePath = Path.GetFullPath(baseDirectory);
        var full = Path.GetFullPath(path);
        if (RootProblem(basePath) is { } baseProblem) return baseProblem;

        var relative = Path.GetRelativePath(basePath, full);
        if (relative == "." ) return null;
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return IsLink(full) ? "A folder the app keeps data in is a link (a symbolic link or a junction). Nothing was measured through it." : null;

        var current = basePath;
        foreach (var part in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!Occupied(current)) return null; // nothing below an absent component exists
            if (IsLink(current)) return "A folder the app keeps data in is a link (a symbolic link or a junction). Nothing was measured through it.";
        }
        return null;
    }

    /// <summary>
    /// The shallow look INSIDE an account's folder: the immediate children the app opens by their name (the database and the files SQLite keeps beside it, the state file, the log, the usage counts,
    /// and the roots of the media, the replica work folder, the backups and the baselines) are looked at, without following them, before the account is composed. One of them that is a
    /// link (or any reparse point) is that account's repair state: it would have the account open, write and measure somewhere else, another account's files included. Null when none is.
    /// The sentence names the kind of thing, never a path. A child that is not there is no problem (a new account has none yet).
    ///
    /// <para>This is as deep as the check goes, on purpose: what is below those roots, and a swap between this look and the use, are what a process that can already write the account's folder
    /// can do, which the sandbox the folder lives in is there to prevent (see the remarks of the type).</para>
    /// </summary>
    public static string? OwnedChildProblem(string dataDirectory, string databasePath)
    {
        foreach (var (path, what) in OwnedChildren(dataDirectory, databasePath))
        {
            if (IsLink(path))
                return $"{what} inside this account's folder is a link (a symbolic link or a junction). The account was not opened. Remove the link by hand.";
        }
        return null;
    }

    private static IEnumerable<(string Path, string What)> OwnedChildren(string dataDirectory, string databasePath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) yield return (databasePath + suffix, "The database");
        yield return (Path.Combine(dataDirectory, BlindAccountNames.StateFile), "The state file");
        yield return (Path.Combine(dataDirectory, "blind-state.json"), "The state file");
        yield return (BlindPaths.Log(dataDirectory), "The log");
        yield return (BlindPaths.Usage(dataDirectory), "The usage counts file");
        yield return (Path.Combine(dataDirectory, "media"), "The media folder");
        yield return (BlindPaths.Replica(dataDirectory), "The replica work folder");
        yield return (BlindPaths.Backups(dataDirectory), "The backups folder");
        yield return (BlindPaths.Baselines(dataDirectory), "The baselines folder");
    }

    /// <summary>
    /// Null when the account's folder is absent, or a real folder directly below a real <c>accounts</c> below a real root; otherwise the sentence that says what is
    /// wrong. Creates nothing and opens nothing. For the legacy account (whose folder is the root) only the root is judged.
    /// </summary>
    public static string? AccountFolderProblem(string root, BlindAccountEntry entry)
    {
        if (entry.IsLegacy) return RootProblem(root);
        if (AccountsFolderProblem(root) is { } accountsProblem) return accountsProblem;

        var folder = BlindAccountNames.DataDirectory(root, entry); // refuses an id that is not 32 hex characters
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(folder));
        var expected = Path.Combine(BlindAccountRegistry.AccountsFolder, entry.Id);
        if (!string.Equals(relative, expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return "The folder of this account is not where the app keeps it.";

        if (!Occupied(folder)) return null;
        if (IsLink(folder))
            return "The folder of this account is a link (a symbolic link or a junction): it was not opened. Remove the link by hand.";
        if (!Directory.Exists(folder)) return "The name of this account's folder is taken by a file.";
        return null;
    }
}
