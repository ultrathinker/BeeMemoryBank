using System.Data;

namespace BeeMemoryBank.Core.Interfaces;

public interface IDbConnectionFactory
{
    IDbConnection CreateConnection();

    /// <summary>
    /// Stable identifier for the database this factory opens. Process-wide caches keyed by a
    /// row id (see <c>FolderAccessService</c>'s folder-ACL cache) must include it: user ids
    /// restart at 1 in every database, so two vaults open in one process would otherwise share
    /// — and silently answer for — each other's cache entries. It never contains key material: on a sealed node it is the key-free data
    /// source of the file (docs/design/step-b-spec.md 7.2), so it is safe as a cache key, in a log line and in a status answer.
    /// </summary>
    string DatabaseId { get; }
}
