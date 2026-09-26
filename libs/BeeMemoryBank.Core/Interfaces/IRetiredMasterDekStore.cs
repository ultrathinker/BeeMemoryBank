namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// Master DEKs that DEK rotations retired, kept in <c>tbl_node_data_key</c> sealed under the
/// CURRENT master DEK (every later rotation re-wraps them with the other node data keys).
/// <para>
/// A body or media item wrapped under an older key can still arrive after this node rotated: a
/// peer that was offline during the rotation, a peer that had not applied it yet (seconds on an
/// auto-accepting desktop, forever on a node that cannot apply it), or a relay that held the event.
/// The in-memory retired cache covered that only until the next restart; after it such rows no
/// longer opened on this node at all. Keeping the old keys costs no confidentiality: an old key
/// opens only what the current key already opens, since the rotation re-wrapped everything it had.
/// </para>
/// </summary>
public interface IRetiredMasterDekStore
{
    /// <summary>Name prefix of the rows in <c>tbl_node_data_key</c>; the rest is the rotation's commit event id.</summary>
    public const string KeyNamePrefix = "retired-master-dek:";

    /// <summary>Every stored retired key as (row name, wrapped key, iv). Synchronous: read inside unlock.</summary>
    IReadOnlyList<(string Name, byte[]? Wrapped, byte[]? Iv)> ListWrapped();
}
