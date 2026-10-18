namespace BeeMemoryBank.Web.Models;

/// <summary>
/// One row of GET /api/whitelist/sync-status: when this node last made contact with a peer (either way, whether or not events moved), whose call it
/// was, and whether this node's calls to it fail (BMB-313).
/// </summary>
/// <param name="UpdatedAt">The delivery time (the later of the last pull from it and the last push of events to it), as before BMB-313; <c>0001-01-01</c> when neither exists.</param>
/// <param name="LastContactAt">The last successful contact, either way, whether or not events moved; null when none is known.</param>
/// <param name="Direction"><c>outbound</c> (we -&gt; it) or <c>inbound</c> (it -&gt; us), or null.</param>
/// <param name="PushedAt">When events last went out to it.</param>
/// <param name="State"><c>answering</c>, <c>not_answering</c>, <c>calls_us</c> or <c>unknown</c>.</param>
public record NodeContactDto(
    Guid NodeId, DateTime UpdatedAt, DateTime? LastContactAt = null, string? Direction = null, DateTime? PushedAt = null, string State = "unknown",
    DateTime? NotAnsweringSince = null, string? NotAnsweringReason = null);
