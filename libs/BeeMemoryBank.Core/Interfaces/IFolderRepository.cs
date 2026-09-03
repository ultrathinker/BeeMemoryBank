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

    /// <summary>
    /// Throws if any ACTIVE folder strictly under <paramref name="pathPrefix"/> is denied or
    /// read-only for the ambient caller scope. <see cref="SoftDeleteByPathPrefixAsync"/> calls this
    /// itself, but it is exposed separately so a caller that destroys other things BEFORE reaching
    /// the folder delete (the REST delete endpoint removes the folder's articles first) can
    /// validate up front instead of discovering the denial half-way through.
    /// </summary>
    Task ThrowIfAnyDescendantWriteDeniedAsync(string pathPrefix);
    /// <summary>Returns soft-deleted folders under pathPrefix that share the same cascade op id.
    /// Used by Restore to recreate the subtree structure.</summary>
    Task<List<Folder>> ListSoftDeletedByCascadeOpIdAsync(Guid cascadeOpId, string pathPrefix);
    /// <summary>
    /// Atomically renames: updates tbl_folder path for folder + all sub-folders.
    /// Articles are not touched — they reference folder_id which doesn't change on rename.
    /// </summary>
    Task<int> RenamePathAsync(string oldPath, string newPath, Guid folderId,
        long lamportTs, Guid? sourceNodeId, DateTime updatedAt);
    /// <summary>
    /// Creates <paramref name="path"/> and any missing ancestors. The LEAF is checked against the
    /// caller's folder ACL first; ancestors deliberately are not, so an allow-list caller can still
    /// create /A/B/C when /A and /A/B lie outside their scope.
    ///
    /// <para>
    /// Does NOT canonicalize <paramref name="path"/> via <c>TreePathCanonicalizer</c> — it trusts
    /// the caller already did. Untrusted input (import manifests, anything not already produced by
    /// a canonicalizing write path) must be canonicalized by the caller BEFORE it reaches here, or
    /// "../"/control-char segments get persisted verbatim.
    /// </para>
    /// </summary>
    Task EnsureExistsAsync(string path, Guid? sourceNodeId);

    /// <summary>
    /// Same vivification with NO ACL check at all — for callers whose leaf is a DIFFERENT path
    /// they authorize themselves, so what they pass here is only ever an ancestor.
    ///
    /// <para>
    /// <see cref="FolderService"/> is the case: it creates folder X and passes X's PARENT here,
    /// which under <see cref="EnsureExistsAsync"/>'s leaf check would refuse an allow-list user
    /// permission to create the very folder their allow entry names — /Work/Project would be
    /// rejected because /Work is outside their scope. Callers of this method MUST check the real
    /// leaf themselves (see <see cref="ThrowIfWriteDenied"/>) BEFORE calling it; skipping that is
    /// what let a denied caller litter restricted subtrees with folders in the first place.
    /// </para>
    ///
    /// <para>
    /// Also does NOT canonicalize <paramref name="path"/> — same caveat as
    /// <see cref="EnsureExistsAsync"/> above; canonicalize untrusted input before calling this.
    /// </para>
    /// </summary>
    Task EnsureAncestorsExistAsync(string path, Guid? sourceNodeId);

    /// <summary>
    /// Throws if the current caller may not write at <paramref name="path"/> — the same two checks
    /// the folder write methods apply, exposed so a service can enforce them BEFORE taking any
    /// action that persists something.
    /// </summary>
    void ThrowIfWriteDenied(string? path);
    Task<List<Folder>> SearchAsync(string query);
    /// <summary>
    /// Pre-WP-07 exact-substring search (per-row <c>unicode_contains</c> scan over name and path,
    /// no morphology). Preserved for a possible future "exact substring" search mode; not used by
    /// <c>SearchService</c>, which routes through FTS-backed <see cref="SearchAsync"/>.
    /// </summary>
    Task<List<Folder>> SearchByExactSubstringAsync(string query);
    Task<List<Guid>> ListIdsByPathPrefixAsync(string pathPrefix);
}
