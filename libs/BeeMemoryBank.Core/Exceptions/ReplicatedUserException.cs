namespace BeeMemoryBank.Core.Exceptions;

/// <summary>
/// Raised when an operation tries to mutate a replicated superadmin mirror row on a shared node,
/// or create/promote a local superadmin on a shared node. Carries the accounts node identity so
/// API and CLI callers name where people are managed.
/// </summary>
public sealed class ReplicatedUserException(
    string message,
    Guid? accountsHomeNodeId = null,
    string? accountsHomeName = null) : InvalidOperationException(message)
{
    public Guid? AccountsHomeNodeId { get; } = accountsHomeNodeId;
    public string? AccountsHomeName { get; } = accountsHomeName;

    public static string FormatHomeLabel(string? homeName, Guid? homeNodeId)
    {
        if (!string.IsNullOrWhiteSpace(homeName) && homeNodeId is { } id)
            return $"accounts node \"{homeName}\" ({id:D})";
        if (!string.IsNullOrWhiteSpace(homeName))
            return $"accounts node \"{homeName}\"";
        if (homeNodeId is { } onlyId)
            return $"accounts node {onlyId:D}";
        return "the accounts node";
    }

    public static ReplicatedUserException ForReplicatedUser(string? homeName, Guid? homeNodeId) =>
        new($"This account is managed on {FormatHomeLabel(homeName, homeNodeId)} and cannot be changed on a shared node.",
            homeNodeId, homeName);

    public static ReplicatedUserException ForSharedSuperadminWrite(string? homeName, Guid? homeNodeId) =>
        new($"This is a shared node; create or promote superadmins on {FormatHomeLabel(homeName, homeNodeId)}.",
            homeNodeId, homeName);
}
