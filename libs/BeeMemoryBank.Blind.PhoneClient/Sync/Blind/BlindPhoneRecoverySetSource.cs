using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Recovery;

namespace BeeMemoryBank.Sync.Blind;

/// <summary>
/// The recovery set of the phone's own database — the open header of every backup file. It needs no key:
/// <see cref="RecoverySetBuilder"/> only reads the replicated boxes, links, anchors and sealed secrets, all
/// of them sealed already. The format is the one the Windows restore parses; it is not rebuilt here.
///
/// <para>A header with no active recovery box is refused (the backup waits): the master password opens a box and
/// the box's chain gives the DEKs, so a file whose header holds none can never be opened — the Windows restore
/// ends in "The master password opens none of the recovery boxes." The network builds a box when a superadmin signs
/// in on a computer, and it reaches the phone by sync.</para>
/// </summary>
public sealed class BlindPhoneRecoverySetSource(IDbConnectionFactory connections) : IRecoverySetJsonSource
{
    public async Task<string> BuildJsonAsync(CancellationToken ct)
    {
        var set = await new RecoverySetBuilder(connections).BuildAsync();
        if (set.Boxes.Count == 0)
            throw new BlindFeaturePendingException(
                "a recovery box of the network (the computer publishes one when its owner signs in on it; it reaches this phone by sync)");
        return set.ToJson();
    }
}
