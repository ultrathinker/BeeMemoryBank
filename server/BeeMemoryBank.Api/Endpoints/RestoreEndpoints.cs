using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Recovery;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

/// <param name="Code">The restore code from the blind node's console (<see cref="BlindRestoreCode"/>).</param>
/// <param name="Address">Only when the code's own address is not the one to use from here.</param>
/// <param name="Pin">Optional: when given, it must be the pin the code carries.</param>
public sealed record RestoreFromBlindRequest(string? Address, string Code, string? Pin, string Password, string AdminUsername, string DisplayName);
public sealed record RestoreFromBackupRequest(string Path, string Password, string AdminUsername, string DisplayName);
/// <summary>Confirms devices a restore kept inactive, from the wizard: the master password authenticates it.</summary>
public sealed record RestoreConfirmPeersRequest(string Password, List<Guid> NodeIds);

/// <summary>The user's answer to "N recovery boxes were not tried": "all" or "skip".</summary>
public sealed record RestoreContinueRequest(string Boxes);

/// <summary>Where the one restore a fresh node can run stands.</summary>
public sealed class RestoreProgress
{
    private readonly object _lock = new();
    private CancellationTokenSource _cts = new();
    private Func<RestoreBoxPolicy, CancellationToken, Task<RestoreResult>>? _pending;
    private Timer? _pendingExpiry;
    public string State { get; private set; } = "idle"; // idle | running | done | failed | cancelled | boxes_remaining
    public string? Error { get; private set; }
    public RestoreResult? Result { get; private set; }

    /// <summary>In "boxes_remaining": how many recovery boxes the budget left untried.</summary>
    public int? RemainingBoxes { get; private set; }

    /// <summary>How long a restore waits for the user's answer to "boxes remaining" before it is dropped.</summary>
    public static readonly TimeSpan ChoiceTimeout = TimeSpan.FromMinutes(15);

    /// <summary>Cancels the running restore: the wizard, the restore gate and the node are free at once.</summary>
    public CancellationToken Token { get { lock (_lock) return _cts.Token; } }

    public bool TryStart()
    {
        lock (_lock)
        {
            if (State == "running") return false;
            (State, Error, Result, RemainingBoxes) = ("running", null, null, null);
            DropPending();
            _cts.Dispose();
            _cts = new CancellationTokenSource();
            return true;
        }
    }

    /// <summary>
    /// Nothing written yet; the restore waits for the user to try the remaining boxes or go on without them.
    /// <paramref name="resume"/> holds the request (the password with it) until then, at most <see cref="ChoiceTimeout"/>.
    /// </summary>
    public void AwaitBoxChoice(int remaining, Func<RestoreBoxPolicy, CancellationToken, Task<RestoreResult>> resume)
    {
        lock (_lock)
        {
            (State, RemainingBoxes) = ("boxes_remaining", remaining);
            _pending = resume;
            _pendingExpiry = new Timer(_ => { lock (_lock) if (State == "boxes_remaining") { DropPending(); State = "cancelled"; } },
                null, ChoiceTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Takes the waiting restore up again, with a fresh cancellation token.</summary>
    public bool TryResume(out Func<RestoreBoxPolicy, CancellationToken, Task<RestoreResult>> resume)
    {
        lock (_lock)
        {
            resume = _pending!;
            if (State != "boxes_remaining" || _pending == null) return false;
            DropPending();
            (State, Error, Result, RemainingBoxes) = ("running", null, null, null);
            _cts.Dispose();
            _cts = new CancellationTokenSource();
            return true;
        }
    }

    public void Cancel()
    {
        lock (_lock)
        {
            if (State == "running") _cts.Cancel();
            else if (State == "boxes_remaining") { DropPending(); State = "cancelled"; }
        }
    }

    private void DropPending()
    {
        _pending = null;
        _pendingExpiry?.Dispose();
        _pendingExpiry = null;
    }
    public void Finish(RestoreResult result) { lock (_lock) (State, Result) = ("done", result); }
    public void Fail(string error) { lock (_lock) (State, Error) = ("failed", error); }
    public void Cancelled() { lock (_lock) (State, Error) = ("cancelled", null); }
}

/// <summary>
/// Restore wizard backend on the NEW device (plan 6.7, 6.8): "Restore from a blind node" and "Open an
/// existing profile" from a backup. Only a node that is not initialized yet takes these; the work runs
/// in the background (heavy derivations, a whole vault to import) and the wizard polls progress.
/// </summary>
public static class RestoreEndpoints
{
    /// <summary>Entries listed by identity in the progress reply; the counts beside the lists are complete.</summary>
    public const int MaxListedEntries = 500;

    public static void MapRestoreEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/restore").WithTags("Restore").RequireInternalKey();

        group.MapPost("/blind", async (RestoreFromBlindRequest req, InitializationService init, RestoreProgress progress,
            BlindRestoreClient client, ILogger<RestoreProgress> logger) =>
        {
            if (await init.IsInitializedAsync()) return Results.Conflict(new ErrorResponse("Node is already initialized."));
            if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrEmpty(req.Password))
                return Results.BadRequest(new ErrorResponse("The restore code and the master password are required."));
            BlindRestoreCode code;
            try { code = BlindRestoreCode.Parse(req.Code); }
            catch (FormatException ex) { return Results.BadRequest(new ErrorResponse(ex.Message)); }
            var address = string.IsNullOrWhiteSpace(req.Address) ? code.Address : req.Address.Trim();
            if (address == null)
                return Results.BadRequest(new ErrorResponse("The restore code carries no address: enter the blind node's address."));
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return Results.BadRequest(new ErrorResponse("A blind node is reached over HTTPS only."));
            if (!string.IsNullOrWhiteSpace(req.Pin) && !(RestoreTlsPin.IsWellFormed(req.Pin) && Spki.Equal(req.Pin.Trim(), code.TlsSpki)))
                return Results.BadRequest(new ErrorResponse("The certificate pin does not match the restore code."));
            if (!progress.TryStart()) return Results.Conflict(new ErrorResponse("A restore is already running."));

            var who = new RestoreIdentity(req.AdminUsername, req.DisplayName, req.Password);
            Start(progress, logger, (boxes, token) => client.RestoreFromBlindNodeAsync(req.Address, req.Code, req.Pin, who, token, boxes), RestoreBoxPolicy.Default);
            return Results.Accepted("/api/restore/progress");
        });

        group.MapPost("/backup", async (RestoreFromBackupRequest req, InitializationService init, RestoreProgress progress,
            BlindRestoreClient client, ILogger<RestoreProgress> logger) =>
        {
            if (await init.IsInitializedAsync()) return Results.Conflict(new ErrorResponse("Node is already initialized."));
            if (string.IsNullOrWhiteSpace(req.Path) || string.IsNullOrEmpty(req.Password))
                return Results.BadRequest(new ErrorResponse("Backup location and master password are required."));
            if (!progress.TryStart()) return Results.Conflict(new ErrorResponse("A restore is already running."));

            var who = new RestoreIdentity(req.AdminUsername, req.DisplayName, req.Password);
            Start(progress, logger, (boxes, token) => client.RestoreFromBackupAsync(req.Path, who, token, boxes), RestoreBoxPolicy.Default);
            return Results.Accepted("/api/restore/progress");
        });

        // POST /api/restore/confirm-peers — the wizard's way out of "no anchor vouched for anyone": right after a
        // restore nobody is signed in yet, so the master password in the body authenticates, as it did for the
        // restore itself. Only rows the restore kept unconfirmed can be confirmed; they become active peers,
        // never superadmins.
        group.MapPost("/confirm-peers", async (RestoreConfirmPeersRequest req, InitializationService init, SessionService session,
            IDbConnectionFactory db, BeeMemoryBank.Sync.Blind.SpkiPinRegistry pins) =>
        {
            if (!await init.IsInitializedAsync()) return Results.Conflict(new ErrorResponse("Nothing has been restored yet."));
            if (string.IsNullOrEmpty(req.Password) || !await session.VerifyMasterPasswordAsync(req.Password))
                return Results.Json(new ErrorResponse("The master password is not correct."), statusCode: StatusCodes.Status401Unauthorized);
            var confirmed = 0;
            using (var conn = db.CreateConnection())
                foreach (var id in (req.NodeIds ?? []).Distinct())
                    confirmed += await Dapper.SqlMapper.ExecuteAsync(conn,
                        "UPDATE tbl_whitelist SET status = 'A', updated_at = @Now WHERE node_id = @Id COLLATE NOCASE AND status = @U",
                        new { Id = id.ToString(), U = RestoredPeerStatus.Unconfirmed, Now = DateTime.UtcNow.ToString("O") });
            pins.Invalidate();
            return Results.Ok(new { confirmed });
        });

        // POST /api/restore/continue — the user's answer when the restore stopped at "boxes_remaining"
        // (nothing written): "all" tries every remaining box (cancellable), "skip" goes on without them,
        // and the result then cannot be confirmed.
        group.MapPost("/continue", (RestoreContinueRequest req, RestoreProgress progress, ILogger<RestoreProgress> logger) =>
        {
            var boxes = req.Boxes switch
            {
                "all" => RestoreBoxPolicy.TryAll,
                "skip" => RestoreBoxPolicy.SkipRemaining,
                _ => (RestoreBoxPolicy?)null,
            };
            if (boxes == null) return Results.BadRequest(new ErrorResponse("Answer \"all\" or \"skip\"."));
            if (!progress.TryResume(out var resume))
                return Results.Conflict(new ErrorResponse("No restore is waiting for that answer."));
            Start(progress, logger, resume, boxes.Value);
            return Results.Accepted("/api/restore/progress");
        });

        // POST /api/restore/cancel — stop the running restore. The wizard and the restore gate are free at
        // once; nothing has been written if the keys were not resolved yet, and a derivation already
        // running finishes in the background (HeavyDerivationQueue.RunAsync).
        group.MapPost("/cancel", (RestoreProgress progress) =>
        {
            progress.Cancel();
            return Results.Accepted("/api/restore/progress");
        });

        group.MapGet("/progress", (RestoreProgress progress) => Results.Ok(new
        {
            state = progress.State,
            error = progress.Error,
            remainingBoxes = progress.RemainingBoxes ?? progress.Result?.RemainingBoxes,
            anchor = progress.Result is { } r
                ? new
                {
                    state = r.Anchor.State,
                    asOf = r.Anchor.AsOf,
                    headProven = r.Anchor.HeadProven,
                    found = r.Anchor.Found,
                    matchesAnchor = r.Anchor.MatchesAnchor,
                    confirmed = r.Anchor.Confirmed,
                    createdAt = r.Anchor.CreatedAt,
                    newerRows = r.Anchor.NewerRows,
                    newerRowsSigned = r.Anchor.NewerRowsSigned,
                    uncheckedRows = r.Anchor.UncheckedRows,
                    // Covered entries next to a newer change: not compared, so named (capped; the count is complete).
                    @unchecked = (r.Anchor.Unchecked ?? []).Take(MaxListedEntries).Select(u => new { type = u.Type, id = u.Id }).ToList(),
                    unverified = r.Anchor.Unverified.Take(MaxListedEntries).Select(u => new { type = u.Type, id = u.Id }).ToList(),
                    unconfirmedAnchorDates = r.Anchor.UnconfirmedAnchorDates,
                    history = r.Anchor.HistoryState,
                    historyDifferingGroups = r.Anchor.History?.DifferingBuckets ?? 0,
                    historyUnverified = r.Anchor.History?.Unverified.Count ?? 0,
                    historyNotVouched = (r.Anchor.History?.Unverified ?? []).Take(MaxListedEntries).Select(u => new { type = u.Type, id = u.Id }).ToList(),
                }
                : null,
            retiredKeys = progress.Result?.RetiredKeys,
            claim = progress.Result?.Claim,
            unconfirmedPeers = progress.Result?.UnconfirmedPeers?.Select(p => new { id = p.NodeId, name = p.DisplayName, wasSuperadmin = p.WasSuperadmin }).ToList(),
        }));
    }

    private static void Start(RestoreProgress progress, ILogger logger,
        Func<RestoreBoxPolicy, CancellationToken, Task<RestoreResult>> work, RestoreBoxPolicy boxes)
    {
        var token = progress.Token;
        _ = Task.Run(() => RunAsync(progress, logger, work, boxes, token));
    }

    private static async Task RunAsync(RestoreProgress progress, ILogger logger,
        Func<RestoreBoxPolicy, CancellationToken, Task<RestoreResult>> work, RestoreBoxPolicy boxes, CancellationToken token)
    {
        try
        {
            progress.Finish(await work(boxes, token));
        }
        catch (RecoveryBoxesRemainingException ex)
        {
            progress.AwaitBoxChoice(ex.Remaining, work);
        }
        catch (OperationCanceledException)
        {
            progress.Cancelled();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Restore failed");
            // A wrong password or code is the user's to fix; say which, not a stack trace.
            progress.Fail(ex is UnauthorizedAccessException or FileNotFoundException or InvalidDataException or ArgumentException
                ? ex.Message
                : "Restore failed: " + ex.Message);
        }
    }
}
