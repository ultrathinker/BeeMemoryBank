using System.Data;

namespace BeeMemoryBank.Core.Interfaces;

public interface IDbConnectionFactory
{
    IDbConnection CreateConnection();

    /// <summary>
    /// Stable identifier for the database this factory opens. Process-wide caches keyed by a
    /// row id (see <c>FolderAccessService</c>'s folder-ACL cache) must include it: user ids
    /// restart at 1 in every database, so two vaults open in one process would otherwise share
    /// — and silently answer for — each other's cache entries.
    /// </summary>
    string DatabaseId { get; }
}
