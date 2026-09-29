namespace BeeMemoryBank.Web.Models;

/// <summary>A blind node as GET /api/blind-nodes reports it (see BlindNodeManager.ListAsync).</summary>
/// <param name="Alarms">"old_protocol", "silent" (plan 5.6).</param>
/// <param name="AdoptedCheckpoint">The checkpoint this node took from the blind node as its pull position, or null.</param>
public record BlindNodeDto(
    Guid NodeId, string DisplayName, string? Address, int? Protocol, DateTime? LastContact, List<string> Alarms,
    long? AdoptedCheckpoint = null);
