namespace BeeMemoryBank.Web.Models;

/// <summary>A blind node as GET /api/blind-nodes reports it (see BlindNodeManager.ListAsync).</summary>
/// <param name="Alarms">The banners to show: "old_protocol", "pc_too_old", "silent" (plan 5.6; judged by the Api's BlindAlarmService).</param>
/// <param name="AdoptedCheckpoint">The checkpoint this node took from the blind node as its pull position, or null.</param>
public record BlindNodeDto(
    Guid NodeId, string DisplayName, string? Address, int? Protocol, DateTime? LastContact, List<string> Alarms,
    long? AdoptedCheckpoint = null, BlindContactDto? Contact = null);

/// <summary>
/// The contact with a blind node, as the Api tells it for every node (the same fields as the Trusted Nodes and the header): the last contact with its
/// direction, the last time events went out, and since when this PC's calls fail and why (BMB-313).
/// </summary>
/// <param name="LastContactDirection"><c>outbound</c> (this PC called), <c>inbound</c> (it called this PC), or null.</param>
/// <param name="State"><c>answering</c>, <c>not_answering</c>, <c>calls_us</c> or <c>unknown</c> (not checked since this node started).</param>
public record BlindContactDto(
    DateTime? LastContactAt, string? LastContactDirection = null, DateTime? LastPushAt = null, DateTime? LastOutboundAt = null, DateTime? LastInboundAt = null,
    string State = "unknown", bool Online = false, DateTime? NotAnsweringSince = null, string? NotAnsweringReason = null);
