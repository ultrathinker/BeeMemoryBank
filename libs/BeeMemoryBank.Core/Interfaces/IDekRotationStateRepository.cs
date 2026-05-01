namespace BeeMemoryBank.Core.Interfaces;

public enum DekRotationState
{
    Proposed,
    Committing,
    Applied,
    Cancelled,
    Failed,
    Rejected
}

public record DekRotationStateRow(
    string EventId,
    DekRotationState State,
    string? ProposedEventId,
    string RotationTs,
    string? AppliedAt,
    string? ErrorMessage,
    long? LastProcessedIdArticle,
    long? LastProcessedIdArticleVersion,
    long? LastProcessedIdMedia,
    long? LastProcessedIdConflictVersion,
    long? LastProcessedIdComment,
    string CreatedAt,
    string UpdatedAt
);

public interface IDekRotationStateRepository
{
    Task<DekRotationStateRow?> GetAsync(string eventId);
    Task UpsertAsync(DekRotationStateRow row);
    Task UpdateStateAsync(string eventId, DekRotationState newState, string? errorMessage = null);
    Task UpdateLastProcessedAsync(string eventId, string tableSuffix, long lastId);
    Task<List<DekRotationStateRow>> GetByStateAsync(DekRotationState state);
}
