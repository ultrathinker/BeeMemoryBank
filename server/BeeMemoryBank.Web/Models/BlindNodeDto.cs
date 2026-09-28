namespace BeeMemoryBank.Web.Models;

/// <summary>A blind node as GET /api/blind-nodes reports it (see BlindNodeManager.ListAsync).</summary>
/// <param name="Alarms">"old_protocol", "silent" (plan 5.6).</param>
public record BlindNodeDto(
    Guid NodeId, string DisplayName, string? Address, int? Protocol, DateTime? LastContact, List<string> Alarms);
