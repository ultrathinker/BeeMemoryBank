namespace BeeMemoryBank.Web.Models;

public record SnapshotUploadDto(
    Guid FileId,
    string FileName,
    long FileSizeBytes,
    string OriginatorNodeId,
    string SnapshotHash,
    string CreatedAt,
    bool NetworkRestoreAllowed,
    string? DekMismatchReason
);
