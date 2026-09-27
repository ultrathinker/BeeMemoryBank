namespace BeeMemoryBank.Api.Services.BlindStatus;

/// <summary>
/// A provider that fills its own part of <c>GET /api/blind/status</c> (CONTRACTS §5). The endpoint
/// owns nothing but assembly: the base fields come from <see cref="CoreBlindStatusContributor"/>,
/// and each blind-node subsystem (recovery boxes and anchor, pairing, jobs, backups) registers its
/// own contributor and writes its section through <see cref="BlindStatusBuilder.Set"/>. That keeps
/// the status surface extensible without everyone editing one handler.
/// </summary>
public interface IBlindStatusContributor
{
    Task ContributeAsync(BlindStatusBuilder b, CancellationToken ct);
}
