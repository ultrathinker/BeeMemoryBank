using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Api.Services.Offboarding;

/// <summary>What a node does for a removal another node drives: serve its request for a new identity key (step S1).</summary>
public sealed partial class OffboardingService
{
    /// <summary>
    /// Serves the <c>node_rekey_request</c> of the driver of <paramref name="processId"/> when it names this node (release 2.12.0, step S1): this node
    /// gives itself a new identity key with the reason <c>offboarding:&lt;process id&gt;</c>, once per process (<see cref="IdentityRekeyStep.RekeySelfForAsync"/>).
    /// Only a request from the node that drives the process counts; the applier already refused any other. Called by the background pass for every
    /// open process this node does not drive; needs this node open (a locked node serves it once it is opened).
    /// </summary>
    public async Task ServeRekeyRequestsAsync(Guid processId, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var record = await new OffboardingStore(connFactory).GetAsync(processId);
            if (record is null || !record.IsOpen) return;
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var self = (await sp.GetRequiredService<INodeIdentityRepository>().GetAsync())?.NodeId;
            if (self is null || BlindNodeId.IsBlind(self.Value)) return;
            if (record.DriverNodeId == self.Value && record.Origin == "local") return;   // the driver re-keys itself in S1
            var asked = (await IdentityRekeyStep.RequestsAsync(connFactory, record.DriverNodeId, processId)).Any(nodes => nodes.Contains(self.Value));
            if (!asked) return;
            if (sp.GetRequiredService<IIdentityRekeyStep>() is IdentityRekeyStep step)
                await step.RekeySelfForAsync(processId, ct);
        }
        finally
        {
            Gate.Release();
        }
    }
}
