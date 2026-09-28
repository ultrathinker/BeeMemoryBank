using BeeMemoryBank.Api.Services.BlindStatus;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// The "pairing" section of /api/blind/status (CONTRACTS §5): whether a pair code someone could still
/// use exists — the console shows it, and a consumed or expired code reads as none.
/// </summary>
public sealed class PairingBlindStatusContributor(BlindPairing pairing) : IBlindStatusContributor
{
    public async Task ContributeAsync(BlindStatusBuilder b, CancellationToken ct) =>
        b.Set("pairing", new Dictionary<string, object?> { ["code_active"] = await pairing.IsCodeActiveAsync() });
}
