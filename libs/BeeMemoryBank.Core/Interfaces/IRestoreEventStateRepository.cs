namespace BeeMemoryBank.Core.Interfaces;

public enum RestoreEventState
{
    Pending,
    Downloading,
    Applying,
    Applied,
    Rejected,
    Superseded,
    Cancelled,
    Failed
}

public record RestoreEventStateRow(
    string EventId,
    RestoreEventState State,
    string? SupersededBy,
    bool RejectedLocally,
    string? AppliedAt,
    string? ErrorMessage,
    string CreatedAt,
    string UpdatedAt
);

public interface IRestoreEventStateRepository
{
    Task<RestoreEventStateRow?> GetAsync(string eventId);
    Task UpsertAsync(RestoreEventStateRow row);
    Task UpdateStateAsync(string eventId, RestoreEventState newState, string? errorMessage = null);
    Task MarkSupersededAsync(string oldEventId, string newEventId);
    Task<List<RestoreEventStateRow>> GetByStateAsync(RestoreEventState state);
}
