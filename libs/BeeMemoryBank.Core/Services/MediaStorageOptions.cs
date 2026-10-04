namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Where a node keeps media files on disk. Used by every node that applies replicated media rows or cleans up blobs
/// (EventApplier, HardDeleteService, CleanupService, MediaBlobBackfillService), so it lives apart from the content service
/// (<c>MediaService</c>) that only a node holding the master key runs.
/// </summary>
public record MediaStorageOptions(string MediaDir);
