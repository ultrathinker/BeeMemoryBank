using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// Finding out whether the app's root already holds a 2.6 blind copy, so that the first start of 2.7 can list it as account 1 without moving, copying or re-keying
/// anything. Reading only: it opens the legacy state store (and looks at the database file) and writes nothing; what it finds is written by the account host in the
/// one atomic write of <c>accounts.json</c>, so the adoption is idempotent and a crash at any point of it leaves either no registry (it runs again) or the whole one.
/// </summary>
internal static class BlindLegacyAdoption
{
    private const string DefaultName = "My copy";

    /// <summary>
    /// The legacy account's entry when the root holds a copy (an identity or call code in its state, or a database file with content), otherwise null.
    /// Throws whatever the state store throws: an unreadable state is not "no install", and the host must not write an empty registry over it.
    /// </summary>
    public static BlindAccountEntry? Detect(string legacyDatabasePath, IBlindStateStoreFactory states, TimeProvider time)
    {
        var probe = new BlindAccountEntry(BlindAccountEntry.LegacyId, BlindAccountLayouts.Root, DefaultName);
        var state = new BlindPhoneState(states.For(probe));
        var callCode = state.CallCode;
        var present = state.NodeId is not null || state.PublicKey is not null || callCode is not null || HasContent(legacyDatabasePath);
        if (!present) return null;
        return probe with { Name = NameFrom(callCode?.Address), AddedAt = null, Established = true };
    }

    /// <summary>
    /// True when the database file is there and holds something, false when it is NOT there. Anything else is uncertainty and is thrown, never answered "absent":
    /// <see cref="FileInfo.Exists"/> says false for a file whose metadata cannot be read, and the adoption would then write an empty list over a live database. The
    /// file is opened for reading (sharing everything) so that "access denied", "locked by someone" and "I/O error" all stop the adoption, which can be run again.
    /// </summary>
    private static bool HasContent(string databasePath)
    {
        try
        {
            using var stream = new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            return stream.Length > 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Not there. A dangling link is a link that cannot be followed, not an absence.
            if (BlindPathSafety.IsLink(databasePath)) throw new BlindFileSentenceException("The database path is a link that cannot be followed.", ex);
            return false;
        }
    }

    /// <summary>The host of the address the copy calls, as the account's name; "My copy" when there is none or it cannot be a name.</summary>
    internal static string NameFrom(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return DefaultName;
        var host = uri.Host;
        return BlindAccountEntry.CheckName(host) is null && host.Length > 0 ? host : DefaultName;
    }
}
