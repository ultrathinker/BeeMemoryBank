using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Vault.Metadata;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.FullIos.Services;

/// <summary>Why this phone's database could not be encrypted; <see cref="Exception.Message"/> is the reason, for the person.</summary>
public sealed class PhoneSealingException(string code, string message) : Exception(message)
{
    /// <summary>The network does not encrypt its metadata yet (and this phone may not switch it on for everybody).</summary>
    public const string OldNetwork = "old_network";

    public string Code { get; } = code;
}

/// <summary>
/// Every iPhone database is encrypted, with no button (the desktops do the same since BMB-306 through FreshNodeSealing): a vault made
/// on this phone right after it is made, a joined one right after the join. A plain copy left by an older build is not converted: the phone is
/// set up again. It runs the same "Encrypt this node" migration the button ran (<see cref="SealMigrationService"/>, its
/// verification, swap and wipe included); only the phone's "no other copy" question is answered here, because there is nothing to lose: a new
/// vault holds nothing yet, a joined one is also on the computer it came from. The vault stays open afterwards.
/// </summary>
public sealed class PhoneSealing(IServiceProvider services)
{
    /// <summary>True while this phone's database is still a plain file and the vault is open, so it can be encrypted now.</summary>
    public bool IsNeeded =>
        services.GetService<PhoneVaultLifecycle>() is { IsSealed: false }
        && services.GetService<SealMigrationService>() is not null
        && services.GetRequiredService<SessionService>().IsUnlocked;

    /// <summary>The share of the work done (0..1) and the step, while it runs.</summary>
    public (double Done, string Step)? Progress =>
        services.GetService<SealMigrationService>()?.Progress is { } p ? (p.Percent / 100.0, p.Step ?? "") : null;

    /// <summary>
    /// Encrypts the open plain database. <paramref name="startsNetwork"/>: a vault made on this phone, which starts its own network, switches
    /// the metadata encryption on first (the desktops' setup does the same); a phone in an existing network never switches it on for everybody.
    /// </summary>
    /// <exception cref="PhoneSealingException">It did not happen; the database is as it was, or encrypted and locked when the message says so.</exception>
    public async Task SealAsync(bool startsNetwork, CancellationToken ct = default)
    {
        if (!IsNeeded) return;
        var policy = services.GetRequiredService<ISyncProtocolPolicy>();
        policy.RecheckNow();
        if (startsNetwork && !policy.SealsMetadata)
        {
            using var scope = services.CreateScope();
            var outcome = await scope.ServiceProvider.GetRequiredService<MetadataActivationService>().ActivateAsync(FreshNodeSealing.SetupActor, ct);
            if (!outcome.Activated)
                throw new PhoneSealingException("activation", "the new sync format could not be switched on ("
                    + string.Join(", ", outcome.Blockers.Select(b => b.Code)) + ")");
        }
        if (!policy.SealsMetadata)
            throw new PhoneSealingException(PhoneSealingException.OldNetwork, "the network still uses the old sync format");

        var seal = services.GetRequiredService<SealMigrationService>();
        var done = await seal.RunAsync(new SealOptions(AcceptNoSnapshot: true), ct);
        if (done.Phase != SealProgress.Done)
            throw new PhoneSealingException(done.ErrorCode ?? "failed", done.Error ?? "the encryption did not finish");
    }
}
