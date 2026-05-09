namespace BeeMemoryBank.Core.Interfaces;

public interface IAuditLogRepository
{
    Task LogAsync(string entityType, string entityId, string action, string actorType, string details);
    Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default);
}
