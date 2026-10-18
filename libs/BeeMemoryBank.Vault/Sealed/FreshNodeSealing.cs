using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Vault.Metadata;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Vault.Sealed;

/// <summary>
/// A node that is set up is encrypted from its first day (BMB-306, the owner's rule): a fresh installation - the Web Setup, <c>bmb init</c>, a Docker
/// start that is set up through either - ends with an encrypted database under the master password it was given, and (for a node that starts a network)
/// with metadata encryption already on. This is the one routine all of them call after the vault is written: it opens the session with that password,
/// switches the metadata encryption on when asked (<see cref="MetadataActivationService"/>) and runs the ordinary "Encrypt this node" migration
/// (<see cref="SealMigrationService"/>, its verification, swap and wipe included) over what is a few kilobytes of identity, key slots and schema.
/// There is no copy of the unencrypted database afterwards, and the session is locked again: the node opens at its first sign-in, as after any restart.
/// </summary>
public class FreshNodeSealing(
    SessionService session,
    ISyncProtocolPolicy policy,
    MetadataActivationService activation,
    SealMigrationService seal,
    IBaselineScheduler? baselines = null,
    IDekRotationStateRepository? rotations = null,
    ILogger<FreshNodeSealing>? logger = null,
    ISealIntents? intents = null)
{
    /// <summary>The actor named in the audit entry of the activation a setup makes.</summary>
    public const string SetupActor = "setup";

    /// <param name="masterPassword">The master password of the setup, which the vault was just written under.</param>
    /// <param name="activateMetadata">True for a node that starts a network (it declares protocol 4 from its first start); false for a node that joined
    /// one (it holds the activation of the node it joined, which came with the snapshot).</param>
    /// <exception cref="FreshNodeSealingException">Anything went wrong: what failed, in a sentence for the person who ran the setup. The node is not
    /// encrypted unless <see cref="FreshNodeSealingException.NodeIsEncrypted"/> says so.</exception>
    /// <param name="afterRestore">True for the end of a restore: the restore replayed the rotations of the network's past, and a rotation that is still
    /// proposed or waiting to be accepted on a node that was made a moment ago is such a leftover - its effect is in the keys the restore recovered, and
    /// it would block the encryption ("a key rotation is not finished") - so those are marked cancelled first.</param>
    public virtual async Task SealAsync(string masterPassword, bool activateMetadata, CancellationToken ct = default, bool afterRestore = false)
    {
        // The activation asks for an early baseline, which works from a plain staging copy of the database: that copy in the data folder is a blocker
        // of the migration ("plain_copies"). The baseline waits until the node is encrypted and opened (it is made after the first unlock).
        using var noBaseline = baselines is null ? BaselineHold.None : await baselines.PauseAsync(ct);
        logger?.LogInformation("Encrypting the new node: the baseline work is paused, the vault is opened.");

        if (!session.IsUnlocked && !await session.UnlockAsync(masterPassword))
            throw new FreshNodeSealingException("The master password of the setup does not open the new vault.", nodeIsEncrypted: false);

        if (afterRestore && rotations is not null)
            foreach (var state in new[] { DekRotationState.Proposed, DekRotationState.Committing })
                foreach (var row in await rotations.GetByStateAsync(state))
                    await rotations.UpdateStateAsync(row.EventId, DekRotationState.Cancelled, "Replayed by the restore of this node: its effect is in the keys the restore recovered.");

        // A restore or a join imports the activation with the database: the answer the policy gave before that is not the answer now.
        policy.RecheckNow();
        if (activateMetadata && !policy.SealsMetadata)
        {
            var outcome = await activation.ActivateAsync(SetupActor, ct);
            if (!outcome.Activated)
                throw new FreshNodeSealingException(
                    "Metadata encryption could not be switched on for the new node (" + string.Join(", ", outcome.Blockers.Select(b => b.Code)) + ").",
                    nodeIsEncrypted: false);
        }

        logger?.LogInformation("Encrypting the new node: the migration starts (metadata encryption {State}).", policy.SealsMetadata ? "on" : "off");
        var progress = await seal.RunAsync(new SealOptions(), ct);
        if (progress.Phase != SealProgress.Done)
        {
            logger?.LogError("Encrypting the new node failed ({Code}) at step {Step}.", progress.ErrorCode, progress.Step);
            // Encrypted but kept locked: nothing is in the open any more, so the intent ends. A swap that stopped leaves its journal and the intent:
            // the next start finishes the swap (the pair is encrypted, the marker goes) or refuses by name.
            if (progress.Step == SealMigrationService.LockedStep) intents?.Complete();
            throw new FreshNodeSealingException(
                "The new node's database could not be encrypted" + (string.IsNullOrEmpty(progress.Error) ? "" : ": " + progress.Error),
                nodeIsEncrypted: progress.Step == SealMigrationService.LockedStep,
                restartFinishes: seal.SwapUnfinished);
        }

        // The setup does not leave the key in memory: the first sign-in opens the database, the way every restart does.
        session.Lock();

        // The database is encrypted and nothing of this setup is in the open: the intent ends (a start that finds it still there, because the
        // process died right here, sees an encrypted pair and only removes it).
        intents?.Complete();
    }
}

/// <summary>The setup of a node could not make it encrypted (BMB-306). <see cref="Exception.Message"/> is for the person who ran the setup.</summary>
public sealed class FreshNodeSealingException(string message, bool nodeIsEncrypted, bool restartFinishes = false) : Exception(message)
{
    /// <summary>True when the failure came after the swap: the database is encrypted but kept locked (the migration says so), which is not a node to reset.</summary>
    public bool NodeIsEncrypted { get; } = nodeIsEncrypted;

    /// <summary>True when the swap itself stopped (a file that would not move): the journal is on disk and the next start finishes it. Not a node to reset either.</summary>
    public bool RestartFinishes { get; } = restartFinishes;

    /// <summary>True when the node must be left exactly as it is: it is encrypted, or a restart makes it so.</summary>
    public bool LeaveAsItIs => NodeIsEncrypted || RestartFinishes;
}
