using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// The invitations this full node made (<c>tbl_blind_invite</c>). Every state change is ONE conditional statement, so that two callers racing for the
/// same row have exactly one winner and the loser is told so: the redemption that burns the secret, the owner's decision, the expiry.
/// </summary>
public interface IBlindInviteRepository
{
    /// <summary>
    /// Inserts the row unless <paramref name="maxOpen"/> invitations are already open at <paramref name="now"/> (issued and not past their time, or redeemed or
    /// confirming and not past the owner's time). The count and the insert are ONE statement, so concurrent owners cannot all slip in under the cap.
    /// </summary>
    Task<bool> TryInsertAsync(BlindInviteRecord row, DateTime now, int maxOpen);

    Task<BlindInviteRecord?> GetAsync(string inviteId);

    /// <summary>The newest rows first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<BlindInviteRecord>> ListAsync(int limit = 200);

    /// <summary>How many invitations are still open (issued, redeemed or confirming, not yet past their time).</summary>
    Task<int> CountOpenAsync(DateTime now);

    /// <summary>
    /// <c>issued -> redeemed</c> if the row is still issued, has not expired and carries <paramref name="secretHash"/>; true for exactly one caller.
    /// </summary>
    Task<bool> TryRedeemAsync(string inviteId, string secretHash, DateTime now, BlindInviteRedemption redemption);

    /// <summary>
    /// Replaces the stored pair code of a server blind node's redemption while the row is still <c>redeemed</c> and the same node redeemed it: the node's retry after a lost answer
    /// came with a fresh code. False when the row is not a redeemed server row of that node.
    /// </summary>
    Task<bool> TryReplacePairCodeAsync(string inviteId, Guid nodeId, byte[] pairCodeEnc);

    /// <summary>
    /// True if a SERVER blind node's redemption of this node id is waiting for the owner (<c>redeemed</c> or <c>confirming</c>): the old pairing of the same node must not be run
    /// meanwhile.
    /// </summary>
    Task<bool> HasOpenServerRedemptionAsync(Guid nodeId);

    /// <summary>
    /// <c>redeemed -> confirming</c> if it is still redeemed and the owner's time has not run out at <paramref name="now"/>: written BEFORE the device is enrolled.
    /// <paramref name="enrollEventId"/> is the id the enrolment's <c>whitelist_add</c> will be logged under, recorded now so that a retry and an undo can recognise it.
    /// </summary>
    Task<bool> TryBeginConfirmAsync(string inviteId, DateTime now, string enrollEventId);

    /// <summary><c>confirming -> confirmed</c>, written AFTER the enrolment, if the owner's time has not run out at <paramref name="now"/>.</summary>
    Task<bool> TryConfirmAsync(string inviteId, DateTime now, string decidedBy);

    /// <summary><c>confirming -> refused | expired</c>: the enrolment was undone (or never completed) and the row ends. <paramref name="finalState"/> is one of those two.</summary>
    Task<bool> TryAbortConfirmingAsync(string inviteId, string finalState, DateTime now, string decidedBy);

    /// <summary>The rows left in <c>confirming</c>, oldest first: what a reconciler finishes or undoes.</summary>
    Task<IReadOnlyList<BlindInviteRecord>> ListConfirmingAsync();

    /// <summary><c>redeemed -> refused</c>.</summary>
    Task<bool> TryRefuseAsync(string inviteId, DateTime now, string decidedBy);

    /// <summary><c>issued -> expired</c>: the owner withdrew an invitation nobody redeemed.</summary>
    Task<bool> TryWithdrawAsync(string inviteId, DateTime now, string decidedBy);

    /// <summary>The ids of the rows whose time has run out (issued past <c>expires_at</c>, redeemed past <c>confirm_deadline</c>).</summary>
    Task<IReadOnlyList<string>> ListDueForExpiryAsync(DateTime now);

    /// <summary>Marks one such row expired, if it is still due.</summary>
    Task<bool> TryExpireAsync(string inviteId, DateTime now);

    /// <summary>Deletes the rows in a final state that were decided (or expired) before <paramref name="cutoff"/>.</summary>
    Task<int> DeleteFinalBeforeAsync(DateTime cutoff);
}

/// <summary>
/// What a redemption writes on the row. An app copy's carries <paramref name="BackupKeyEnc"/>; a server blind node's (<paramref name="Kind"/> <see cref="BlindInviteKinds.Server"/>)
/// carries <paramref name="PairCodeEnc"/> instead.
/// </summary>
public sealed record BlindInviteRedemption(
    Guid NodeId, string PublicKey, string DeviceName, string Platform, string AppVersion, string SeenFrom, byte[]? BackupKeyEnc, DateTime ConfirmDeadline,
    string Kind = BlindInviteKinds.Device, byte[]? PairCodeEnc = null);
