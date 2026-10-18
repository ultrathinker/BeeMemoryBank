namespace BeeMemoryBank.Storage.Sqlite;

/// <summary>Why <see cref="MigrationRunner"/> would not open a database.</summary>
public enum MigrationRefusal
{
    /// <summary>The migration history stops short of the final 2.6 schema: a release before 2.6, or an update that was interrupted.</summary>
    OlderDatabase,

    /// <summary>The migration history holds rows this program does not know (a newer program wrote it, or somebody edited it).</summary>
    UnknownHistory,

    /// <summary>The history is the complete 2.6 one but the database does not have the structure the program reads (a column is missing).</summary>
    IncompatibleStructure,

    /// <summary>The adoption of a 2.6 database failed half way; the transaction was rolled back and the database is as it was.</summary>
    AdoptionFailed,
}

/// <summary>
/// The runner looked at the database and decided not to touch it. The message is meant for the person who runs the node: it says what is
/// wrong and what to do, and carries no path, no key and no row content. Nothing was written when this is thrown.
/// </summary>
public sealed class MigrationRefusedException : InvalidOperationException
{
    public MigrationRefusal Reason { get; }

    /// <summary>For <see cref="MigrationRefusal.IncompatibleStructure"/>: what is wrong, in words that name tables and columns and nothing else; empty otherwise.</summary>
    public IReadOnlyList<string> Problems { get; }

    public MigrationRefusedException(MigrationRefusal reason, string message, Exception? inner = null, IReadOnlyList<string>? problems = null) : base(message, inner)
    {
        Reason = reason;
        Problems = problems ?? [];
    }

    internal static MigrationRefusedException Older() => new(MigrationRefusal.OlderDatabase,
        "This database is older than 2.6, or an update of it was interrupted, so this version cannot open it. " +
        "Open it once with version 2.6.1 to bring it up to date, then update to this version. Nothing was changed.");

    internal static MigrationRefusedException Unknown() => new(MigrationRefusal.UnknownHistory,
        "This database has a migration history this version does not recognise: it was written by a newer version, or it was edited. " +
        "Open it with the version that wrote it, or restore a backup made by a version that matches this one. Nothing was changed.");

    internal static MigrationRefusedException Incompatible(IReadOnlyList<string> problems)
    {
        const int shown = 5;
        var list = string.Join("; ", problems.Take(shown)) + (problems.Count > shown ? $"; and {problems.Count - shown} more" : "");
        return new MigrationRefusedException(MigrationRefusal.IncompatibleStructure,
            "This database has the history of version 2.6 but not its structure: " + list + ". " +
            "Restore a backup made by version 2.6.1 or later, or ask for help with this message. Nothing was changed.", problems: problems);
    }

    internal static MigrationRefusedException Failed(Exception inner) => new(MigrationRefusal.AdoptionFailed,
        "Adopting this database into the current schema failed, and everything the attempt did was rolled back. " +
        "It is as it was before. Make sure there is free disk space and that no other program uses the files, then start again; " +
        "if it fails again, restore a backup made by version 2.6.1 or later.", inner);
}
