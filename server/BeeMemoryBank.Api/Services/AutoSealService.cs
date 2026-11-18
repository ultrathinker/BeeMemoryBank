using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// an old plain database on a computer encrypts ITSELF, with no button. Right after the vault is unlocked
/// (password unlock in the web or desktop app, or start-unlocked on a server), in the background, at most one attempt per
/// unlock. If the preflight says it cannot be done yet (the network still on the old sync format, for one), it does not
/// try: the reason is logged once, and the Database encryption page says it as one calm line. A failure is logged to the
/// node's normal log and said the same calm way; the node keeps working as before. The run is the same
/// <see cref="SealMigrationService"/> the button used, with its journal and its start recovery: a node closed mid-run is
/// finished or undone at the next start, never left half-done. While it runs the node is in maintenance mode, exactly as
/// the button's run was, and the admin pages follow the same progress.
/// </summary>
public sealed class AutoSealService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AutoSealService> _logger;
    private int _attempted;
    private string? _failure;

    /// <summary>
    /// False only on a test node that drives the seal itself (the explicit API, the kill points, the canary in the log). The gate is the
    /// Testing-only setting <c>BMB_AUTO_SEAL_TEST_OFF</c>, the same shape as the kill-point hook <c>BMB_SEAL_TEST_KILL_AFTER</c>: it is read
    /// only in the Testing environment, so a product always encrypts itself and there is no way to turn this off in a product.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public AutoSealService(IServiceProvider services, ILogger<AutoSealService> logger)
    {
        _services = services;
        _logger = logger;
        // A new unlock may decide again; until then the decision of this unlock stands (one attempt per unlock).
        services.GetRequiredService<SessionService>().Locked += () => Volatile.Write(ref _attempted, 0);
        if (services.GetService<IWebHostEnvironment>() is { EnvironmentName: "Testing" }
            && services.GetService<IConfiguration>()?["BMB_AUTO_SEAL_TEST_OFF"] == "1")
            Enabled = false;
    }

    /// <summary>The last failure's reason, for one calm line on the Database encryption page; null when there is none.</summary>
    public string? FailureNote => Volatile.Read(ref _failure);

    /// <summary>
    /// The background run the most recent unlock started, or null when that unlock started none (locked, already sealed, or a blocker).
    /// It completes when the attempt is over - done, blocked or failed - so a caller (a test) can wait for the end instead of polling.
    /// </summary>
    public Task? LastRun { get; private set; }

    /// <summary>
    /// Called after every successful unlock. The first call of an unlock decides; the rest do nothing. A call while locked is a no-op.
    /// The "already sealed" and "network still on the old sync format" questions are answered HERE, synchronously, at the unlock:
    /// a node that is activated only after this unlock is not sealed mid-use by a delayed background read — it is decided again at
    /// the next unlock. Only a node that is plain and already activated at the unlock goes on to the background preflight and run.
    /// </summary>
    public void Trigger()
    {
        if (!Enabled) return;
        var session = _services.GetRequiredService<SessionService>();
        if (!session.IsUnlocked) return;
        if (Interlocked.CompareExchange(ref _attempted, 1, 0) != 0) return;
        var gate = _services.GetService<IVaultGate>();
        var seal = _services.GetService<SealMigrationService>();
        var policy = _services.GetService<BeeMemoryBank.Sync.Metadata.ISyncProtocolPolicy>();
        if (gate is null || seal is null || gate.IsSealed) return;
        if (policy is null || !policy.SealsMetadata)
        {
            // The network is still on the old sync format at this unlock: no attempt. The page names the same blocker as one calm line.
            _logger.LogInformation("The database was not encrypted by itself at this unlock: the network still uses the old sync format.");
            return;
        }
        LastRun = Task.Run(RunOnceAsync);
    }

    private async Task RunOnceAsync()
    {
        var seal = _services.GetRequiredService<SealMigrationService>();
        try
        {
            var preflight = await seal.PreflightAsync(new SealOptions(CallerIsSuperadmin: true));
            if (!preflight.CanStart)
            {
                // Not an error: the page names the same blocker as one calm line, and the next unlock decides again.
                _logger.LogInformation("The database was not encrypted by itself at this unlock: {Reason}", preflight.Blockers[0].Message);
                return;
            }
            var done = await seal.RunAsync(new SealOptions(CallerIsSuperadmin: true));
            if (done.Phase == SealProgress.Done)
            {
                Volatile.Write(ref _failure, null);
                _logger.LogInformation("The database of this node encrypted itself after the unlock.");
            }
            else
            {
                Volatile.Write(ref _failure, done.Error ?? "the encryption did not finish");
                _logger.LogWarning("The database could not encrypt itself: {Code} {Error}", done.ErrorCode, done.Error);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // One fixed calm sentence for the page, naming the kind of failure, never the exception's own text; the whole exception goes to the log.
            Volatile.Write(ref _failure, "The encryption failed (" + ex.GetType().Name + "); the node keeps working as before.");
            _logger.LogWarning(ex, "The database could not encrypt itself after the unlock; the node keeps working as before.");
        }
    }
}
