using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface ICallerScope
{
    bool IsSuperadmin { get; }

    bool IsAccessDenied(string? treePath);

    // Navigable = readable OR ancestor-of-allowed path.
    // Ancestors (e.g. "/Work" for AllowList "/Work/Project2") are shown as
    // empty navigation stubs so the user can walk the tree down to their allowed subtree.
    bool IsNavigable(string? treePath);

    List<Article> FilterArticles(List<Article> articles);

    // Returns readable folders PLUS ancestor stubs so the tree can be rendered.
    List<Folder> FilterFolders(List<Folder> folders);
}
