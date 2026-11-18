using System.Globalization;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Contributes the bridge from an old master key to <c>node_state</c> (release 2.12, full nodes only; 2.12 cross review, finding 2): how many v1 doors
/// this node has (<see cref="NodeStateExtraKeys.V1Doors"/>) and whether it keeps the chain material of a rotation (<see cref="NodeStateExtraKeys.DekBridge"/>).
/// A removal reads both from every full node's report when it closes (plan E6): a v1 door reaches the new master key through that chain, so the bridge
/// is what the administrator still has to remove on each node.
/// </summary>
public sealed class DekBridgeNodeStateContributor(IDekBridgeStatus bridge) : INodeStateContributor
{
    public int Order => 20;

    public async Task ContributeAsync(NodeStateDraft draft, CancellationToken ct = default)
    {
        int doors;
        bool present;
        try
        {
            doors = await bridge.GetV1DoorCountAsync();
            present = await bridge.IsBridgePresentAsync();
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or VaultSealedException or SessionLockedException))
        {
            // The rest of the report still goes out, and says the bridge is not known: a reader must never take a failure for "no bridge".
            draft.Extra[NodeStateExtraKeys.DekBridge] = NodeStateExtraKeys.Unknown;
            return;
        }
        draft.Extra[NodeStateExtraKeys.V1Doors] = doors.ToString(CultureInfo.InvariantCulture);
        draft.Extra[NodeStateExtraKeys.DekBridge] = present ? "1" : "0";
    }
}
