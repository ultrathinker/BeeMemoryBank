using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Storage.Sealed;

namespace BeeMemoryBank.Vault.Sealed;

/// <summary>
/// Why a data directory cannot start and what to do about it (docs/design/step-b-spec.md 2.5, 10.6): one text for the start message, the log,
/// <c>bmb status</c>, <c>bmb vault verify</c> and the Web start page. It names the reason code and the relative file names, never a path outside
/// the data folder and never any content, and it always says that nothing was opened, created, moved or deleted.
/// </summary>
public sealed record BrokenStart(string Reason, IReadOnlyList<string> Files, string Message)
{
    /// <summary>The refusal for a diagnosis that is Broken. Null for Plain and Sealed.</summary>
    public static BrokenStart? From(VaultStoreDiagnosis diagnosis) =>
        diagnosis.Mode == VaultStoreMode.Broken ? Describe(diagnosis.Reason ?? "unknown", diagnosis.Files) : null;

    /// <summary>The refusal the seal resolver reported (a leftover it could not place safely).</summary>
    public static BrokenStart From(SealStartRefusal refusal) => new(refusal.Code, refusal.Files, refusal.Message);

    /// <summary>
    /// What a start does with the resolver and the diagnosis, in order (spec 5.2 steps 1-2): a refusal of the resolver first, then the
    /// diagnosis of the directory. Null when the node may start.
    /// </summary>
    public static BrokenStart? For(string dataPath, SealSwapResolution? resolved)
    {
        if (resolved?.Refusal is { } refusal) return From(refusal);
        return From(VaultStoreFiles.Diagnose(dataPath));
    }

    public static BrokenStart Describe(string reason, IReadOnlyList<string> files)
    {
        var names = files.Count == 0 ? "the files of the database" : string.Join(", ", files);
        var untouched = " Nothing was opened, created, changed or deleted.";
        var message = reason switch
        {
            "plain_companion" =>
                "This node's database is encrypted, but " + names + " hold unencrypted data of this node, and a stolen disk would show it. "
                + "Move the file out of the data folder (keep it elsewhere if you need its content), or restore the data directory from one backup set "
                + "(docs/sealed-database.md, \"Restore a backup set\"), then start the node again." + untouched,
            "companion_unreadable" =>
                "This node's database is encrypted, but " + names + " cannot be read, so it cannot be checked for unencrypted data. "
                + "Close the program that holds the file and start the node again." + untouched,
            "db_without_boot" =>
                "The database " + names + " is encrypted and its boot store (beememorybank.boot.db) is missing, so nothing can open it. "
                + "Restore the boot store from the same backup as the database, or restore the vault from a snapshot, a peer or a blind copy." + untouched,
            "boot_beside_plain_db" =>
                "A boot store (beememorybank.boot.db) lies beside a database that is not encrypted (" + names + "): the two do not belong together. "
                + "Restore the data directory from one backup set." + untouched,
            "boot_not_ours" =>
                names + " is not a boot store of this program. Restore the boot store from the same backup as the database." + untouched,
            "db_missing_or_empty" =>
                "The boot store is there, but " + names + " is missing or empty. Restore the data directory from one backup set." + untouched,
            "db_not_page_aligned" =>
                names + " is cut off inside a page, as a copy interrupted halfway is. Restore the data directory from one backup set." + untouched,
            "seal_leftovers_no_journal" =>
                "Files of an interrupted \"Encrypt this node\" lie in the data folder without their journal: " + names + ". "
                + "Restore the data directory from one backup set or from the pre-encryption snapshot (docs/sealed-database.md); "
                + "do not delete any *.seal-old* file before you have checked which of them is your database." + untouched,
            _ => "This node's data folder cannot start (" + reason + "): " + names + "." + untouched,
        };
        return new BrokenStart(reason, files, message);
    }
}
