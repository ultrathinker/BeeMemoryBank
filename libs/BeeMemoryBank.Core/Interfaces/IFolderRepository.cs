using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IFolderRepository
{
    Task<Folder?> GetByIdAsync(Guid id, bool includeDeleted = false);
    Task<Folder?> GetByPathAsync(string path);
    Task<List<Folder>> GetChildrenAsync(string? parentPath);  // null = root-level folders
    Task<List<Folder>> GetAllActiveAsync();
    Task<int> CountAsync();
    Task CreateAsync(Folder folder);
    Task UpdateAsync(Folder folder);
    Task SoftDeleteAsync(Guid id, DateTime deletedAt, Guid? cascadeOpId = null);
    /// <summary>Soft-deletes all sub-folders whose path starts with the given prefix.</summary>
    Task<int> SoftDeleteByPathPrefixAsync(string pathPrefix, DateTime deletedAt, Guid? cascadeOpId = null);
    /// <summary>Returns soft-deleted folders under pathPrefix that share the same cascade op id.
    /// Used by Restore to recreate the subtree structure.</summary>
    Task<List<Folder>> ListSoftDeletedByCascadeOpIdAsync(Guid cascadeOpId, string pathPrefix);
    /// <summary>
    /// Atomically renames: updates tbl_folder path for folder + all sub-folders.
    /// Articles are not touched — they reference folder_id which doesn't change on rename.
    /// </summary>
    Task<int> RenamePathAsync(string oldPath, string newPath, Guid folderId,
        long lamportTs, Guid? sourceNodeId, DateTime updatedAt);
    Task EnsureExistsAsync(string path, Guid? sourceNodeId); // creates if missing (for EventApplier)
    Task<List<Folder>> SearchAsync(string query);
    /// <summary>
    /// Pre-WP-07 exact-substring search (per-row <c>unicode_contains</c> scan over name and path,
    /// no morphology). Preserved for a possible future "exact substring" search mode; not used by
    /// <c>SearchService</c>, which routes through FTS-backed <see cref="SearchAsync"/>.
    /// </summary>
    Task<List<Folder>> SearchByExactSubstringAsync(string query);
    Task<List<Guid>> ListIdsByPathPrefixAsync(string pathPrefix);
}
