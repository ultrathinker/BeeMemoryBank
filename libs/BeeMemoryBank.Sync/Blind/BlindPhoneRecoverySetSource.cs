using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Recovery;

namespace BeeMemoryBank.Sync.Blind;

/// <summary>
/// The recovery set of the phone's own database — the open header of every backup file. It needs no key:
/// <see cref="RecoverySetBuilder"/> only reads the replicated boxes, links, anchors and sealed secrets, all
/// of them sealed already. The format is the one the Windows restore parses; it is not rebuilt here.
/// </summary>
public sealed class BlindPhoneRecoverySetSource(IDbConnectionFactory connections) : IRecoverySetJsonSource
{
    public Task<string> BuildJsonAsync(CancellationToken ct) => new RecoverySetBuilder(connections).BuildJsonAsync();
}
