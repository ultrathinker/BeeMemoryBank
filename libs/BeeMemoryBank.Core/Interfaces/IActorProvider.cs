namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// Provides information about who initiated the current operation.
/// Implementations: HttpActorProvider (API), CliActorProvider (CLI).
/// </summary>
public interface IActorProvider
{
    string ActorType { get; }   // "agent", "web", "cli"
    string? ActorName { get; }  // agent name or null
}
