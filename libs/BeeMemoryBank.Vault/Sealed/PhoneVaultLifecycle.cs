using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Vault.Sealed;

/// <summary>What the phone's operating system says about the app right now (docs/design/step-b-spec.md 10.2 <c>mobile_foreground</c>, 10.8).</summary>
public interface IPhoneEnvironment
{
    bool IsForeground { get; }

    /// <summary>Battery level in percent, or null when the platform does not say.</summary>
    int? BatteryPercent { get; }

    bool IsCharging { get; }

    /// <summary>Free bytes of the file system that holds <paramref name="path"/> (the app's data folder), or null when the platform does not say.</summary>
    long? FreeBytes(string path) => null;
}

/// <summary>One piece of the app's start work and the number it runs at (spec 5.3 table 2: 100 migrations, 200 folder bootstrap, ... 800 the pragmas and the rotation auto-arm).</summary>
public sealed record PhoneOpenWork(int Order, Func<IServiceProvider, bool, Task> RunAsync);

/// <summary>What the app's start found (spec 5.2 steps 1-2): the mode of the data folder, why it cannot start, what the seal resolver did, and the lease to keep.</summary>
public sealed record PhoneStart(VaultStoreMode Mode, BrokenStart? Broken, SealSwapResolution? Seal, VaultLease Lease)
{
    public bool CanStart => Broken is null;
}

/// <summary>
/// The start and open sequence of both phone apps (docs/design/step-b-spec.md 5.8, 12.1, 12.2, 15.3.11), in one platform-free class, so that it is tested
/// without a device. The app calls <see cref="Begin"/> first - the swap of an interrupted migration or re-key is finished or undone, the folder is
/// diagnosed, the lease is held - then registers what it used to do at start as open steps (<see cref="OpenSteps"/>: they run each time the database
/// opens, and the once-per-process ones only the first time), and locks on every move to the background (<see cref="OnBackgroundAsync"/>), which
/// closes the sealed database. It never gives a key store a database key or a page key: it holds none, the gate does, and only while the database is open.
/// </summary>
public sealed class PhoneVaultLifecycle(IVaultGate? gate, SessionService session)
{
    /// <summary>
    /// "Encrypt this phone's database" was hidden until the native release gate of spec 12.4 is green; the integration round sets this to
    /// true after it. This build offers it, by the owner's decision, to be run on his own device (the gate's device run: encrypt, background,
    /// unlock, kill mid-encryption, restart). The lifecycle and the rest of this file work with it either way.
    /// </summary>
    public static bool PhoneEncryptionAvailable => true;

    /// <summary>
    /// True when the app has to start through <see cref="Begin"/>: the phone may be encrypted (<see cref="PhoneEncryptionAvailable"/>), or the folder
    /// already holds a boot store, a seal journal or a seal leftover that only the start of the vault can resolve. While encryption is hidden and the
    /// folder is plain this is false, and the app starts exactly as it did before Step B (no lease, no gate).
    /// </summary>
    public static bool NeedsGate(string dataPath)
    {
        if (PhoneEncryptionAvailable) return true;
        if (!Directory.Exists(dataPath)) return false;
        return File.Exists(Path.Combine(dataPath, VaultStoreFiles.Boot))
               || File.Exists(Path.Combine(dataPath, VaultStoreFiles.SealJournal))
               || Directory.EnumerateFileSystemEntries(dataPath, "*" + VaultStoreFiles.SealOldSuffix + "*").Any();
    }

    /// <summary>
    /// Passes the gate of the vault (the re-key's and the seal's resolvers, under the exclusive lease), then looks at the folder. The caller keeps
    /// <see cref="PhoneStart.Lease"/> for the life of the app. A phone that cannot start shows the reason and nothing else.
    /// </summary>
    public static PhoneStart Begin(string dataPath)
    {
        var (resolution, lease) = VaultStartup.Enter(dataPath);
        var broken = BrokenStart.For(resolution.DataDir, resolution.Seal);
        var mode = broken is not null ? VaultStoreMode.Broken : VaultStoreFiles.DetectMode(resolution.DataDir);
        return new PhoneStart(mode, broken, resolution.Seal, lease);
    }

    /// <summary>
    /// What an encrypted phone does before any door has opened its database (the phone's form of spec 5.2 step 4): Step A's protocol policy would read
    /// <c>tbl_metadata_activation</c> on its first question, which a closed database cannot answer, and an encrypted phone is always activated, so the
    /// policy is marked from the boot store first. Nothing here reads the vault.
    /// </summary>
    public static async Task PrepareSealedStartAsync(IServiceProvider services)
    {
        if (services.GetService<IBootStore>() is not { } boot) return;
        await boot.EnsureCreatedAsync();
        if (await boot.GetMetaAsync(BootMetaKeys.Activated) == "1")
            services.GetService<ISyncProtocolPolicy>()?.MarkActivated();
    }

    /// <summary>
    /// One piece of the app's start work as an open step the gate runs (spec 5.8): the migrations, the folder bootstrap, the pragmas and the rotation
    /// auto-arm were the body of <c>OnStart</c>; on a sealed phone they cannot run before a door has opened the database, so they run at each open
    /// (<c>firstOpenInProcess</c> tells the once-per-process ones). Registered on the app's container; a plain phone runs the same work at start as before.
    /// </summary>
    public static IServiceCollection AddOpenStep(IServiceCollection services, PhoneOpenWork work) =>
        services.AddSingleton<IVaultOpenStep>(sp => new PhoneOpenStep(work, sp));

    /// <summary>True when this phone's database is encrypted (in any state): the unlock paths then open it, instead of reading it first.</summary>
    public bool IsSealed => gate?.IsSealed == true;

    /// <summary>The app moves to the background: the session locks and a sealed database closes (spec 5.4, 12.1).</summary>
    public async Task OnBackgroundAsync()
    {
        session.Lock();
        if (gate is not null) await gate.CloseAsync("lock");
    }

    /// <summary>The unlock page: the typed password, or the one the Keystore released behind the fingerprint (spec 12.1).</summary>
    public Task<bool> UnlockWithPasswordAsync(string password) => session.UnlockAsync(password);

    /// <summary>
    /// The iPhone's quick unlock (spec 12.2): the Keychain key behind Face ID unwraps the master DEK, which opens a sealed database through the same
    /// path as every no-password door and is checked against the sentinel after the open. A DEK that no longer opens the vault returns false, and the
    /// caller turns quick unlock off. The caller clears <paramref name="masterDek"/>.
    /// </summary>
    public Task<bool> UnlockWithQuickKeyAsync(byte[] masterDek, CancellationToken ct = default) => session.TryUnlockWithDekAsync(masterDek, door: null, ct);

    private sealed class PhoneOpenStep(PhoneOpenWork work, IServiceProvider services) : IVaultOpenStep
    {
        public int Order => work.Order;

        public Task RunAsync(bool firstOpenInProcess, CancellationToken ct) => work.RunAsync(services, firstOpenInProcess);
    }
}

/// <summary>True when some full peer has reported a position at or above this phone's log head (spec 10.8): the phone holds nothing a peer has not got.</summary>
public delegate Task<bool> PeerHoldsHead(CancellationToken ct);

/// <summary>
/// What a phone adds to "Encrypt this node" (spec 10.8): the blocker <c>phone_no_copy</c> (no copy of the plain database is kept anywhere; the way back
/// is to join again from a peer) and the blockers of the foreground and the battery.
/// </summary>
/// <param name="environment">The app's foreground and battery.</param>
/// <param name="peerHoldsHead">Whether a peer holds the phone's head.</param>
public sealed class PhoneSealMigrationHost(IPhoneEnvironment environment, PeerHoldsHead peerHoldsHead) : ISealMigrationHost
{
    public const int MinBatteryPercent = 30;

    /// <summary>
    /// The default answer of <see cref="PeerHoldsHead"/>: some active peer has acknowledged a position at or above the head of this phone's own event
    /// log (<c>POST /api/sync/report-position</c>, the position the peer says it has read), so the phone holds nothing a peer has not got.
    /// </summary>
    public static PeerHoldsHead PeerHoldsHeadFrom(IServiceScopeFactory scopes) => async ct =>
    {
        using var scope = scopes.CreateScope();
        var head = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();
        var peers = await scope.ServiceProvider.GetRequiredService<ISyncPushPositionRepository>().GetAllActivePeersWithPushPositionsAsync();
        return peers.Any(p => p.ReportedSeq is { } reported && reported >= head);
    };

    public long? FreeBytes(string path) => environment.FreeBytes(path);

    public async Task<IReadOnlyList<SealBlocker>> HostBlockersAsync(SealOptions options, CancellationToken ct)
    {
        var blockers = new List<SealBlocker>();
        if (!environment.IsForeground)
            blockers.Add(new SealBlocker(SealBlockers.MobileForeground, "Keep the app in the foreground while the database is encrypted."));
        else if (!environment.IsCharging && environment.BatteryPercent is < MinBatteryPercent)
            blockers.Add(new SealBlocker(SealBlockers.MobileForeground, $"Charge the phone first: encrypting needs at least {MinBatteryPercent} % battery or a charger."));

        // The tick is the owner's word that nothing here is missing elsewhere (spec 10.8).
        if (!options.AcceptNoSnapshot && !await peerHoldsHead(ct))
            blockers.Add(new SealBlocker(SealBlockers.PhoneNoCopy,
                "No other device has this phone's latest changes yet. Let it sync first, or tick \"This phone holds the only copy of these notes; encrypting cannot be undone\"."));
        return blockers;
    }

    public Task<IReadOnlyList<SealWarning>> HostWarningsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SealWarning>>([]);
}

/// <summary>The sealed half of a phone app's container (spec 5.8): nothing on a plain phone that cannot be encrypted, the gate over the vault file otherwise.</summary>
public static class PhoneVaultServices
{
    /// <summary>
    /// Registers <see cref="PhoneVaultLifecycle"/> and, for a sealed folder or a phone that may be encrypted, the boot store, the unlock material, the
    /// gate over the vault file (a phone has no chat database and no relay) and "Encrypt this node" with the phone's host. Called once by the app's
    /// composition root, after <c>AddStorage</c> and <c>AddCore</c>; the app registers <see cref="IPhoneEnvironment"/> and <see cref="PeerHoldsHead"/>.
    /// </summary>
    public static IServiceCollection AddPhoneVault(this IServiceCollection services, string dataPath, PhoneStart start,
        bool encryptionOffered = false)
    {
        services.AddSingleton(start);
        var sealedFolder = start.Mode == VaultStoreMode.Sealed;
        if (sealedFolder || encryptionOffered)
        {
            services.AddBootStore(dataPath);
            services.AddSealedUnlockMaterial();
            services.AddSingleton(sp =>
            {
                var gate = new VaultGate(
                    sp.GetRequiredService<BootStore>(),
                    sp.GetRequiredService<IDatabaseKeyService>(),
                    () => PhoneFiles(sp, dataPath),
                    () => sp.GetServices<IVaultOpenStep>().ToList(),
                    () => null,
                    () => sp.GetService<IUnlockMaterialMirror>(),
                    sp.GetService<ILogger<VaultGate>>(),
                    sealedFolder ? VaultStoreMode.Sealed : VaultStoreMode.Plain);
                gate.PlainLeftovers = () => SealSwapResolver.Leftovers(dataPath).Count == 0 ? [] : sp.GetRequiredService<SealMigrationService>().ClearLeftovers();   // review round 1, H1
                return gate;
            });
            services.AddSingleton<IVaultGate>(sp => sp.GetRequiredService<VaultGate>());
            services.AddSingleton<IVaultKeySource>(sp => sp.GetRequiredService<VaultGate>());
            services.AddSingleton<ISealedVaultKeys>(sp => sp.GetRequiredService<VaultGate>());
            services.AddSingleton<ISealedUnlockDiagnostics>(sp => sp.GetRequiredService<VaultGate>());
            services.TryAddSingleton<PeerHoldsHead>(sp => PhoneSealMigrationHost.PeerHoldsHeadFrom(sp.GetRequiredService<IServiceScopeFactory>()));
            services.AddSingleton<ISealMigrationHost>(sp => new PhoneSealMigrationHost(
                sp.GetRequiredService<IPhoneEnvironment>(), sp.GetRequiredService<PeerHoldsHead>()));
            services.AddSingleton(sp => new SealMigrationService(dataPath, sp.GetRequiredService<VaultGate>(), sp.GetRequiredService<SessionService>(),
                sp.GetRequiredService<IDatabaseKeyService>(), sp.GetRequiredService<BootStore>(), sp.GetRequiredService<IUnlockMaterialMirror>(),
                sp.GetRequiredService<ISealMigrationHost>(), () => PhoneFiles(sp, dataPath), sp.GetRequiredService<MaintenanceModeService>(),
                sp.GetService<SyncScheduler>(), sp.GetRequiredService<IServiceScopeFactory>(), sp.GetService<ILogger<SealMigrationService>>()));
        }
        services.AddSingleton(sp => new PhoneVaultLifecycle(sp.GetService<IVaultGate>(), sp.GetRequiredService<SessionService>()));
        return services;
    }

    private static List<SealedFile> PhoneFiles(IServiceProvider sp, string dataPath)
    {
        var vault = sp.GetRequiredService<DbConnectionFactory>();
        return [new SealedFile(DbPurposes.Vault, Path.Combine(dataPath, VaultStoreFiles.Database), vault, vault.CreateConnection)];
    }
}
