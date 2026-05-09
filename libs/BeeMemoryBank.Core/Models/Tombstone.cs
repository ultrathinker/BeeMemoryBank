namespace BeeMemoryBank.Core.Models;

public class Tombstone
{
    public Guid ArticleId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Lamport timestamp of the delete event that produced this tombstone.
    /// Used for LWW resolution against late-arriving Create events. (Wave 2 audit.)
    /// </summary>
    public long LamportTs { get; set; }

    /// <summary>
    /// Node that issued the delete. Used as tiebreaker when two concurrent deletes
    /// carry the same lamport timestamp. Higher SourceNodeId wins. (Wave 2 RERUN.)
    /// </summary>
    public Guid? SourceNodeId { get; set; }
}
