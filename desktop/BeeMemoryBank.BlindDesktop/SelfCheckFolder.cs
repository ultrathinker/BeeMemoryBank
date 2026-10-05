namespace BeeMemoryBank.BlindDesktop;

/// <summary>
/// Which folders <c>--self-check --data-dir &lt;folder&gt;</c> may use. The check writes into its folder (a state key, set and erased
/// again, a lock file, a socket) and the wipe of the state store removes files, so it must never be pointed at the data of a blind app:
/// the rule is that the folder must not exist yet or must be EMPTY, apart from the lock and socket files that an earlier check of the
/// one-copy rule leaves behind; the platform's default data folder (and anything inside it, or above it) is refused by name as well.
/// Judged BEFORE the platform is created, so nothing has been written when the answer is "no".
/// </summary>
internal static class SelfCheckFolder
{
    /// <summary>What an earlier check leaves in its folder: the one-copy lock and the activation socket, both empty of data.</summary>
    private static readonly string[] Leftovers = [".instance.lock", ".instance.sock"];

    /// <summary>Null if <paramref name="folder"/> may be used for a check; otherwise the reason it may not.</summary>
    /// <param name="defaultDataFolder">The platform's default data folder (the real app's), or null if it cannot be determined.</param>
    public static string? Refusal(string folder, string? defaultDataFolder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "no folder was given";
        if (!Path.IsPathRooted(folder)) return "the folder is not an absolute path";

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "the folder is not a usable path (" + ex.GetType().Name + ")";
        }

        if (full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(s => s.StartsWith("BeeMemoryBankData", StringComparison.OrdinalIgnoreCase)))
            return "it is inside the full app's data";

        if (!string.IsNullOrWhiteSpace(defaultDataFolder))
        {
            var real = Path.TrimEndingDirectorySeparator(Path.GetFullPath(defaultDataFolder));
            if (Same(full, real)) return "it is the data folder of the blind app itself";
            if (Inside(full, real)) return "it is inside the data folder of the blind app";
            if (Inside(real, full)) return "it contains the data folder of the blind app";
        }

        if (File.Exists(full)) return "it is a file, not a folder";

        if (Directory.Exists(full))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(full))
            {
                var name = Path.GetFileName(entry);
                if (!Leftovers.Contains(name, StringComparer.Ordinal)) return $"it is not empty (it holds '{name}')";
            }
        }
        return null;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool Inside(string path, string parent) =>
        path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
