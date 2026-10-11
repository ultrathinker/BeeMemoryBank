using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Hosting.AspNetCore;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Vault.Sealed;

namespace BeeMemoryBank.Api.Startup;

/// <summary>
/// One-shot work between <c>builder.Build()</c> and the first request: migrations, bootstrappers,
/// crash-recovery sweeps, clock restore, auto-unlock.
///
/// <para>Deliberately NOT converted into <c>IHostedService</c>s. Their order is load-bearing —
/// migrations before anything that reads a table, the restore sweep before the flow can be
/// re-entered — and hosted services are ordered by registration in a way that is easy to disturb
/// from the other end of the file. A numbered list of awaits says what it does.</para>
///
/// <para>A sealed node (docs/design/step-b-spec.md 5.2) cannot read its database before a door has opened it, so its start is split: the
/// work that needs no key runs here (the boot store, the protocol policy, the sweep of staging copies, the auto-unlock attempts), and the work
/// that reads the database is the <see cref="IVaultOpenStep"/>s of <see cref="ApiStartWork"/>, which the gate runs each time the database opens.
/// A plain node runs exactly the statements it always ran, in the same order, through the same methods.</para>
/// </summary>
public static class ApiStartupTasks
{
    public static async Task RunBeeApiStartupTasksAsync(this WebApplication app, string dataPath)
    {
        // NOTE: UseLoopbackForwardedHeaders() is deliberately NOT called here. It belongs to the pipeline,
        // not to the startup tasks, and Program.cs calls it immediately before this method -- exactly
        // where the pre-split file had it. The split briefly had it in both places, registering two
        // ForwardedHeadersMiddleware instances: with ForwardLimit = 1 each, two passes consume two hops
        // whenever the first one is itself a trusted proxy, which is the shape a spoofed X-Forwarded-For
        // needs to survive.

        // BMB_READY_FILE: signals startup completion to a parent orchestrator (bmbd) by writing
        // {pid, urls, applicationName, version, startupTimeUtc} once Kestrel has bound its actual
        // port — ApplicationStarted fires after that. Off by default: standalone/Docker/tests don't
        // set this env var and see no behavior change.
        var readyFilePath = Environment.GetEnvironmentVariable("BMB_READY_FILE");
        if (!string.IsNullOrEmpty(readyFilePath))
        {
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                var readyInfo = new BeeMemoryBank.Hosting.ReadyFileInfo(
                    Pid: Environment.ProcessId,
                    Urls: app.Urls.ToList(),
                    ApplicationName: "BeeMemoryBank.Api",
                    Version: BeeMemoryBank.Api.Helpers.AppVersion.Current,
                    StartupTimeUtc: DateTime.UtcNow
                );
                BeeMemoryBank.Hosting.ReadyFileManager.Write(readyFilePath, readyInfo);
            });
        }

        // BMB_STDIN_LIFELINE: when the parent orchestrator closes this process's stdin (graceful
        // stop signal) or dies without closing it (still an EOF from this end), trigger a normal
        // graceful shutdown via StopApplication() instead of relying solely on a hard kill.
        if (Environment.GetEnvironmentVariable("BMB_STDIN_LIFELINE") == "1")
        {
            BeeMemoryBank.Hosting.StdinLifeline.Start(() => app.Lifetime.StopApplication());
        }

        var services = app.Services;
        var isBlind = services.GetRequiredService<INodeRole>().IsBlind;
        var gate = services.GetService<IVaultGate>();

        // A blind seed's cutover interrupted by a crash is finished or rolled back before anything opens the database.
        if (isBlind)
            BlindSeedCutover.Recover(dataPath, services.GetRequiredService<ILogger<Program>>());

        if (gate is { IsSealed: true })
        {
            await RunSealedPreOpenAsync(app, dataPath, gate);
        }
        else
        {
            await RunPlainStartAsync(app, dataPath, isBlind);
        }

        // Session cleanup: securely wipe master DEK from memory on shutdown. A sealed database closes with the lock (the session does
        // it), and the boot store is folded into its main file so the three files of a stopped node are each self-contained (spec 5.4).
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            var session = services.GetRequiredService<SessionService>();
            session.ClearPendingDek();
            session.Lock();
            if (services.GetService<IVaultGate>() is { IsSealed: true } sealedGate)
                sealedGate.CloseAsync(VaultGate.ShutdownReason).GetAwaiter().GetResult();
            services.GetRequiredService<ILogger<Program>>()
                .LogInformation("Session locked on application shutdown — master DEK wiped from memory");
        });
    }

    /// <summary>A plain node, or a blind host: the start of 2.5, statement for statement.</summary>
    private static async Task RunPlainStartAsync(WebApplication app, string dataPath, bool isBlind)
    {
        var services = app.Services;

        // Run migrations on startup
        await ApiStartWork.RunMigrationsAsync(services);

        // Blind role (plan 3.4, 3.5): identity with the key in a file, leftover temp files. Right after
        // migrations — which a blind node runs like any other, no DEK involved.
        if (isBlind)
            await BlindRoleStartup.RunAsync(services, dataPath, services.GetRequiredService<ILogger<Program>>());

        // Bootstrap tbl_folder from existing article tree_path values (one-time, idempotent)
        await ApiStartWork.BootstrapFoldersAsync(services);

        // Initialize the AI chat DB schema. A blind node has no AI chat.
        if (!isBlind)
            await ApiStartWork.InitializeChatAsync(services);

        // Restore Lamport clock from DB
        await ApiStartWork.RestoreClockAsync(services);

        // Crash-recovery sweeps (restore, standalone restore staging, DEK rotation, media staging) and the snapshot working copies.
        await ApiStartWork.RunCrashSweepsAsync(services, dataPath, sweepSnapshotStaging: true);

        // ── OS auto-unlock (opt-in; where the OS has a secret store: Windows DPAPI, macOS Keychain) ──
        // Attempt to auto-unlock the session using the OS-protected secret, if the feature was
        // previously enabled by the admin. This runs in-process (Api's SessionService is the
        // authoritative singleton), after all migrations so the tbl_key_slot table is fully ready.
        // No new HTTP endpoint is needed: SessionService lives in-process and UnlockWithDek/
        // TryAutoUnlockAsync are direct method calls. If auto-unlock fails for any reason (file absent,
        // secret-store error, sentinel mismatch) we log a warning and continue — the admin can still
        // unlock manually via /Login.
        if (!isBlind)
            await TryAutoUnlockAsync(services);

        // "Start unlocked after a restart" (docs/design/step-b-spec.md 11.2): a server's own opt-in. Off on every node after every upgrade;
        // with it off this reads the slot table and one file name and does nothing else.
        if (!isBlind)
            await TryServerAutoUnlockAsync(services);

        // Backfill legacy media ciphertext into the content-addressed blob store, in background (16b).
        ApiStartWork.StartMediaBackfill(services);

        // Backfill concept tag embeddings in background. Not on a blind node: it has no model.
        if (!isBlind)
            ApiStartWork.StartConceptTagBackfill(services);
    }

    /// <summary>
    /// A sealed node (spec 5.2): nothing here reads the database. Order: the boot store, the protocol policy, the sweep of encrypted staging
    /// copies, then the auto-unlock attempts, each of which opens the database through the gate (and so runs the open steps) only when a door
    /// accepts. With no auto-unlock the node waits, closed, for its owner.
    /// </summary>
    private static async Task RunSealedPreOpenAsync(WebApplication app, string dataPath, IVaultGate gate)
    {
        var services = app.Services;
        var logger = services.GetRequiredService<ILogger<Program>>();
        var boot = services.GetRequiredService<IBootStore>();

        // 3. The boot store's own migrations (its ledger is not the vault's).
        await boot.EnsureCreatedAsync();

        // 4. Step A's policy would read tbl_metadata_activation on its first question, which a closed database cannot answer: a sealed node
        // is always activated, so it is marked first and never reads.
        if (await boot.GetMetaAsync(BootMetaKeys.Activated) == "1")
            services.GetRequiredService<BeeMemoryBank.Sync.Metadata.ISyncProtocolPolicy>().MarkActivated();

        // 5. Staging copies of snapshots are encrypted; sweeping them is still right, and needs no key.
        ApiStartWork.SweepSnapshotStaging(dataPath, logger);

        // 6. Update hand-off, then OS auto-unlock (desktop) or server auto-unlock (server).
        await TryAutoUnlockAsync(services);
        await TryServerAutoUnlockAsync(services);

        logger.LogInformation("The encrypted database is {State}.", gate.State == VaultState.Closed ? "closed: unlock it with the master password" : gate.State.ToString());
    }

    private static async Task TryAutoUnlockAsync(IServiceProvider services)
    {
        if (services.GetService<BeeMemoryBank.Infrastructure.OsAutoUnlock.OsAutoUnlockService>()?.IsSupported != true) return;

        using var autoUnlockScope = services.CreateScope();
        var autoUnlockLogger = autoUnlockScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        try
        {
            // A desktop app update hands the open vault over to the restarted process (one-shot,
            // minutes-long, removed here whether used or not). Tried first: it is the more specific
            // of the two and must not be left on disk just because auto-unlock also succeeded.
            var handoff = services.GetService<BeeMemoryBank.Infrastructure.OsAutoUnlock.UpdateUnlockHandoff>();
            if (handoff != null)
            {
                var handoffRepo = autoUnlockScope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
                if (await handoff.TryConsumeAsync(handoffRepo))
                    autoUnlockLogger.LogInformation("Session unlocked from the desktop update handoff.");
            }

            var autoUnlockSvc = services.GetService<BeeMemoryBank.Infrastructure.OsAutoUnlock.OsAutoUnlockService>();
            // Already open from the handoff: TryAutoUnlockAsync would report success without doing
            // anything, and the log would claim an auto-unlock that never happened.
            if (autoUnlockSvc != null && !services.GetRequiredService<SessionService>().IsUnlocked)
            {
                var nodeRepo = autoUnlockScope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
                var unlocked = await autoUnlockSvc.TryAutoUnlockAsync(nodeRepo);
                if (unlocked)
                    autoUnlockLogger.LogInformation("Session auto-unlocked via OS auto-unlock slot (OS secret store).");
                else
                    autoUnlockLogger.LogDebug("OS auto-unlock: slot or secret file not present, or unlock failed — manual unlock required.");
            }
        }
        catch (Exception ex)
        {
            services.GetRequiredService<ILogger<Program>>()
                .LogWarning(ex, "OS auto-unlock attempt failed at startup (non-fatal); manual unlock required.");
        }
    }

    private static async Task TryServerAutoUnlockAsync(IServiceProvider services)
    {
        var server = services.GetService<BeeMemoryBank.Infrastructure.AutoUnlock.ServerAutoUnlockService>();
        if (server is null || services.GetRequiredService<SessionService>().IsUnlocked) return;
        try
        {
            using var scope = services.CreateScope();
            var outcome = await server.RunAtStartAsync(scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>());
            if (outcome == BeeMemoryBank.Infrastructure.AutoUnlock.ServerAutoUnlockStartOutcome.Unlocked)
                services.GetRequiredService<ILogger<Program>>().LogInformation("Session unlocked by 'start unlocked after a restart'.");
        }
        catch (Exception ex)
        {
            services.GetRequiredService<ILogger<Program>>()
                .LogWarning(ex, "'Start unlocked after a restart' failed at startup (non-fatal); manual unlock required.");
        }
    }
}
