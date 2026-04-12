using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Services;

public class FolderService(
    IFolderRepository folderRepo,
    IArticleRepository articleRepo,
    INodeIdentityRepository nodeRepo,
    ILamportClock clock,
    IEventLogger eventLogger)
{
    public async Task<Folder> CreateAsync(string path)
    {
        path = NormalizePath(path);

        var existing = await folderRepo.GetByPathAsync(path);
        if (existing != null)
            throw new InvalidOperationException($"Folder already exists at path '{path}'.");

        var identity = await nodeRepo.GetAsync();
        var lamportTs = clock.Tick();
        var now = DateTime.UtcNow;
        var parentPath = GetParentPath(path);

        // Ensure parent exists first
        if (parentPath != null)
            await folderRepo.EnsureExistsAsync(parentPath, identity?.NodeId);

        var folder = new Folder
        {
            Id = Guid.NewGuid(),
            Path = path,
            Name = GetLastSegment(path),
            ParentPath = parentPath,
            Status = "A",
            LamportTs = lamportTs,
            SourceNodeId = identity?.NodeId,
            CreatedAt = now,
            UpdatedAt = now
        };

        await folderRepo.CreateAsync(folder);
        await eventLogger.LogFolderCreateAsync(folder);
        return folder;
    }

    public async Task RenameAsync(Guid folderId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("Folder name cannot be empty.");
        if (newName.Contains('/') || newName.Contains('\\') || newName == ".." || newName == ".")
            throw new ArgumentException("Folder name contains invalid characters.");
        if (newName.Length > 255)
            throw new ArgumentException("Folder name is too long (max 255 characters).");

        var folder = await folderRepo.GetByIdAsync(folderId)
            ?? throw new KeyNotFoundException($"Folder {folderId} not found");

        var oldPath = folder.Path;
        var newParentPath = folder.ParentPath;
        var newPath = (newParentPath != null ? newParentPath : "") + "/" + newName;
        newPath = "/" + newPath.Trim('/');

        var lamportTs = clock.Tick();
        var updatedAt = DateTime.UtcNow;
        var identity = await nodeRepo.GetAsync();

        // 1. Rename in DB (atomic: folder + sub-folders + articles)
        await folderRepo.RenamePathAsync(oldPath, newPath, folderId, lamportTs, identity?.NodeId, updatedAt);

        await eventLogger.LogFolderRenameAsync(folderId, oldPath, newPath, newName, newParentPath, lamportTs, updatedAt);
    }

    public async Task MoveAsync(Guid folderId, string newParentPath)
    {
        if (string.IsNullOrWhiteSpace(newParentPath) || !newParentPath.StartsWith('/'))
            throw new ArgumentException("Path must start with '/'.");

        var folder = await folderRepo.GetByIdAsync(folderId)
            ?? throw new KeyNotFoundException($"Folder {folderId} not found");

        var oldPath = folder.Path;
        var folderName = GetLastSegment(oldPath);
        var newPath = newParentPath.TrimEnd('/') + "/" + folderName;

        if (newPath == oldPath) return;
        if (newPath.StartsWith(oldPath + "/"))
            throw new ArgumentException("Cannot move a folder into itself.");

        var lamportTs = clock.Tick();
        var updatedAt = DateTime.UtcNow;
        var identity = await nodeRepo.GetAsync();

        await folderRepo.RenamePathAsync(oldPath, newPath, folderId, lamportTs, identity?.NodeId, updatedAt);
        await eventLogger.LogFolderRenameAsync(folderId, oldPath, newPath, folderName, newParentPath, lamportTs, updatedAt);
    }

    public async Task DeleteAsync(Guid folderId)
    {
        var folder = await folderRepo.GetByIdAsync(folderId)
            ?? throw new KeyNotFoundException($"Folder {folderId} not found");

        var deletedAt = DateTime.UtcNow;

        // Cascade: soft-delete all sub-folders first
        await folderRepo.SoftDeleteByPathPrefixAsync(folder.Path, deletedAt);

        await folderRepo.SoftDeleteAsync(folderId, deletedAt);
        await articleRepo.ClearFolderIdAsync(folderId);
        await eventLogger.LogFolderDeleteAsync(folderId, folder.Path, deletedAt);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return "/";
        return "/" + path.Trim('/');
    }

    private static string? GetParentPath(string path)
    {
        if (path == "/") return null;
        var trimmed = path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx <= 0 ? null : trimmed[..idx];
    }

    private static string GetLastSegment(string path)
    {
        var trimmed = path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx < 0 ? trimmed.TrimStart('/') : trimmed[(idx + 1)..];
    }
}
