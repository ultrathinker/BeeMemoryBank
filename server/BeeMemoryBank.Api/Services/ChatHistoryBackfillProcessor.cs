using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Moves legacy chat.db data onto the node chat key (<see cref="ChatDataProtector"/>): message
/// columns and attachment blobs still holding plaintext from before the H3/H3b encryption fixes
/// (the original H3a backfill), and message columns, attachment blobs and LLM provider keys sealed
/// directly under the master DEK from before the chat key existed — which a DEK rotation would
/// otherwise orphan. The per-row work lives in the repositories' <c>MigrateLegacyBatchAsync</c>
/// methods — this class is only the scheduling loop around them. <see cref="ChatDekRotationHook"/>
/// runs the same batch to completion right before every DEK rotation.
///
/// <para><b>Why a polling BackgroundService, not a SessionService unlock hook.</b> The backfill
/// needs the master DEK, which only exists once the vault is unlocked
/// (<see cref="SessionService.IsUnlocked"/>) — it cannot run as a plain SQL migration at startup.
/// The obvious alternative is hooking directly into the moment of unlock (SessionService's private
/// <c>TriggerPostUnlockCatchUp</c>, which already fires other one-time lazy migrations — e.g. the
/// node-identity v0→v1 private-key upgrade — the instant a password or DEK unlock succeeds). This
/// class deliberately does NOT do that: it follows the SAME pattern <c>PendingEmbeddingProcessor</c>
/// / <c>PendingIndexProcessor</c> already use instead — a periodic tick that checks
/// <see cref="SessionService.IsUnlocked"/> itself and silently no-ops while locked. The end result
/// is equivalent (both converge on "runs shortly after the vault unlocks, never while locked, and
/// resumes on its own after an interruption"), but polling needs no change to SessionService or to
/// how hosted services are wired at startup, and it composes for free with everything
/// PendingEmbeddingProcessor's design already got right: self-healing after a mid-batch crash (the
/// next tick just resumes where the last one left off), and a single-flight guard so the periodic
/// tick and a manual trigger can never double-process the same batch.</para>
///
/// <para><b>Fresh-node cost.</b> A node that has never written a plaintext row (every node created
/// after both encryption fixes shipped) pays only a cheap per-tick SELECT that the partial indexes
/// in <c>ChatDbInitializer</c> keep empty regardless of table size — see those indexes' comments.
/// Zero rows back means no DEK is touched and no UPDATE is issued; the tick is a no-op.</para>
/// </summary>
public sealed class ChatHistoryBackfillProcessor(
    IServiceScopeFactory scopeFactory,
    ILogger<ChatHistoryBackfillProcessor> logger,
    TimeSpan? interval = null,
    int? batchSize = null) : BackgroundService
{
    private readonly TimeSpan _interval = interval ?? TimeSpan.FromMinutes(5);
    private readonly int _batchSize = batchSize ?? 200;

    // Same non-reentrancy guard as PendingEmbeddingProcessor/PendingIndexProcessor: without it,
    // the periodic tick and a manual drain (or two overlapping ticks under a slow batch) could
    // both pull and encrypt the same legacy rows at once. The repositories' own COALESCE/`AND iv
    // IS NULL` guards make that harmless rather than corrupting, but it would still waste AES
    // work and double-count the log line below.
    private readonly SemaphoreSlim _runLock = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give startup (chat.db schema init, node init/join, first unlock) a moment to settle
        // before the first tick — matches PendingEmbeddingProcessor's own startup delay.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error backfilling legacy plaintext chat history");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    /// <summary>
    /// Runs one batch across both tables. Returns the number of rows migrated (messages +
    /// attachments), 0 if there was nothing to do (locked session counts as "nothing to do" too —
    /// there is no DEK to encrypt with), or -1 if another run (the periodic tick, or a concurrent
    /// call to this method) is already in flight.
    /// </summary>
    public async Task<int> ProcessPendingAsync(CancellationToken ct)
    {
        if (!await _runLock.WaitAsync(0, ct)) return -1;
        try
        {
            return await ProcessPendingCoreAsync(ct);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<int> ProcessPendingCoreAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var total = await MigrateOneBatchAsync(scope.ServiceProvider, _batchSize, logger, ct);
        return total;
    }

    /// <summary>
    /// One batch across all three tables; returns the number of rows looked at (0 = nothing left,
    /// or the vault is locked). Shared by the periodic tick and <see cref="ChatDekRotationHook"/> —
    /// the repositories' <c>*_key_v IS NULL</c> guards make the two harmless to overlap.
    /// </summary>
    internal static async Task<int> MigrateOneBatchAsync(
        IServiceProvider services, int batchSize, ILogger logger, CancellationToken ct)
    {
        var session = services.GetRequiredService<SessionService>();

        // No DEK, nothing to encrypt with — this is the normal state for most of a locked node's
        // uptime, not an error.
        if (!session.IsUnlocked) return 0;

        var msgRepo = services.GetRequiredService<ChatMessageRepository>();
        var attachRepo = services.GetRequiredService<ChatAttachmentRepository>();
        var settingsRepo = services.GetRequiredService<ChatSettingsRepository>();

        int messages, attachments, apiKeys;
        try
        {
            messages = await msgRepo.MigrateLegacyBatchAsync(batchSize, session, ct);
            attachments = await attachRepo.MigrateLegacyBatchAsync(batchSize, session, ct);
            apiKeys = await settingsRepo.MigrateLegacyBatchAsync(batchSize, session, ct);
        }
        catch (Core.Exceptions.SessionLockedException)
        {
            // Locked mid-batch. Every column already moved stays moved; the rest waits for the next
            // unlock, exactly like the locked case above.
            return 0;
        }

        var total = messages + attachments + apiKeys;
        if (total > 0)
            logger.LogInformation(
                "Moved legacy chat data onto the node chat key: {Messages} message row(s), {Attachments} attachment(s), {ApiKeys} provider key(s)",
                messages, attachments, apiKeys);
        return total;
    }

    /// <summary>
    /// One-shot catch-up: repeatedly runs <see cref="ProcessPendingAsync"/> back-to-back (no
    /// inter-batch delay) until a batch makes no progress — either because nothing is left
    /// pending, or because the session locked mid-drain (in which case the periodic
    /// <see cref="ExecuteAsync"/> loop will pick the rest up on its normal schedule once unlocked
    /// again). Bounded by <paramref name="maxBatches"/> as a hard safety cap, not a target — mirrors
    /// PendingEmbeddingProcessor.DrainAllPendingAsync exactly, including the -1 back-off.
    /// </summary>
    public async Task<int> DrainAllPendingAsync(CancellationToken ct, int maxBatches = 500)
    {
        var total = 0;
        for (var i = 0; i < maxBatches && !ct.IsCancellationRequested; i++)
        {
            var processed = await ProcessPendingAsync(ct);
            if (processed == -1)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                continue;
            }
            if (processed == 0) break;
            total += processed;
        }
        return total;
    }
}
