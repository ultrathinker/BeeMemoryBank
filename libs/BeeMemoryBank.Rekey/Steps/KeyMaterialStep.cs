namespace BeeMemoryBank.Rekey.Steps;

/// <summary>Placeholder for the floor tests' red run; the step lands in the next commit.</summary>
public sealed class KeyMaterialStep : IRekeyStep, IRekeyOwnerCredentialConsumer
{
    public string Name => "KeyMaterial";
    public void SetOwnerCredential(int ownerSlotId, string ownerPassword) { }
    public Task<RekeyStepResult> RunAsync(RekeyContext ctx) => Task.FromResult(new RekeyStepResult(Name, new Dictionary<string, long>(), []));
    public Task<IReadOnlyList<RekeyProblem>> VerifyAsync(RekeyContext ctx) => Task.FromResult<IReadOnlyList<RekeyProblem>>([]);
}
