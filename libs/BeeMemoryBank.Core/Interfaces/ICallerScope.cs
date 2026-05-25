using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface ICallerScope
{
    bool IsSuperadmin { get; }

    bool IsAccessDenied(string? treePath);

    // True when path is accessible for read but read-only for write — i.e. the
    // caller has an allow-ACL with is_read_only=1 covering this path. Repo-level
    // write methods use this on top of IsAccessDenied to refuse mutations.
    bool IsWriteDenied(string? treePath);

    // True when the caller has read access but the path matches a read-only ACL
    // entry. Repos use this to throw a distinct ReadOnlyAccessException with a
    // clearer message than the generic UnauthorizedAccessException.
    bool IsReadOnly(string? treePath);

    // Navigable = readable OR ancestor-of-allowed path.
    // Ancestors (e.g. "/Work" for AllowList "/Work/Project2") are shown as
    // empty navigation stubs so the user can walk the tree down to their allowed subtree.
    bool IsNavigable(string? treePath);

    List<Article> FilterArticles(List<Article> articles);

    // Returns readable folders PLUS ancestor stubs so the tree can be rendered.
    List<Folder> FilterFolders(List<Folder> folders);
}
