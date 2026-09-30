using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services.BlindBackup;

namespace BeeMemoryBank.Api.Endpoints;

/// <summary>
/// <c>/api/blind/backup/*</c> (CONTRACTS §2) — the console's and the CLI's backup surface. Every
/// route requires the internal key: these are local-admin operations, never published to peers
/// (PublicSurface does not list the group, so a keyless caller gets the 404, not a 403).
/// </summary>
public static class BlindBackupEndpoints
{
    public static void MapBlindBackupEndpoints(this WebApplication app)
    {
        // Auth on the blind node: the internal key (only the console process and `docker exec … bmb`
        // on the box hold it) PLUS the superadmin role, which both of them assert as the node's
        // local administrator — the console after its own password login. A regular user never
        // gets here: the Web layer forwards "user" for them, and on a full node these routes are
        // not mapped at all (BlindNodeEndpoints).
        var group = app.MapGroup("/api/blind/backup").RequireInternalKey().RequireSuperadmin().WithTags("Blind");

        group.MapGet("/settings", (BlindBackupSettingsStore store) =>
        {
            var s = store.Load();
            return Results.Ok(WithConfigured(store.MaskedForDisplay(s), s.Validate(store.DataPath).Ok));
        });

        // Partial update: a field that is null or the "••••" mask keeps its stored value (the
        // console sends back exactly what the GET showed it); an empty string clears it. Secrets
        // therefore never have to travel back to the client to be preserved.
        group.MapPut("/settings", async (
            BlindBackupSettingsDto dto,
            BlindBackupSettingsStore store,
            BlindJobManager jobs) =>
        {
            if (jobs.IsWiping)
                return Results.Conflict(new ErrorResponse("the node is being wiped"));
            var s = store.Load();
            Apply(dto, s);
            // The RESULT is checked, not the request: with the schedule on, a partial update (a
            // typo in the memory limit, an emptied password) would otherwise be saved, and the
            // scheduler would then skip every backup in silence. A draft may be incomplete, but
            // nothing in it may be wrong.
            //
            // "On" here is ScheduleGuarded, not the raw flag: since release A a node with a
            // repository backs itself up without the operator ever touching the toggle, so a
            // configured node is guarded even while the stored value is still undecided — and an
            // explicit "on" over a configuration that cannot run it is refused rather than stored
            // as a promise the node will not keep.
            var guarded = s.ScheduleGuarded(store.DataPath);
            var (ok, problem) = guarded ? s.Validate(store.DataPath) : s.ValidateValues(store.DataPath);
            if (!ok)
                return Results.BadRequest(new ErrorResponse(guarded
                    ? $"the schedule is on and would stop working: {problem}"
                    : problem!));
            try
            {
                store.Save(s);
            }
            catch (SettingsClosedException ex)
            {
                return Results.Conflict(new ErrorResponse(ex.Message));
            }
            return Results.Ok(WithConfigured(store.MaskedForDisplay(s), s.Validate(store.DataPath).Ok));
        });

        group.MapPost("/now", (BlindBackupService backups) =>
        {
            var job = backups.TryStartBackup();
            return job is null
                ? Results.Conflict(new ErrorResponse("another job is already running"))
                : Results.Accepted((string?)null, new { id = job.Id, state = "running" });
        });

        group.MapGet("/list", async (BlindBackupService backups, CancellationToken ct) =>
        {
            try
            {
                return Results.Content(await backups.ListSnapshotsJsonAsync(ct), "application/json");
            }
            catch (ResticRepositoryBusyException ex)
            {
                return Results.Conflict(new ErrorResponse(ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
        });

        group.MapPost("/verify", (BlindBackupService backups, VerifyRequest req) =>
        {
            var job = backups.TryStartVerify(req.Full);
            return job is null
                ? Results.Conflict(new ErrorResponse("another job is already running"))
                : Results.Accepted((string?)null, new { id = job.Id, state = "running", full = req.Full });
        });

        // "Save a copy to…": a one-off copy of the repository into another folder.
        group.MapPost("/copy", (CopyRequest req, BlindBackupService backups) =>
        {
            if (backups.CheckCopyDestination(req.Destination) is { } problem)
                return Results.BadRequest(new ErrorResponse(problem));
            var job = backups.TryStartCopy(req.Destination!);
            return job is null
                ? Results.Conflict(new ErrorResponse("another job is already running"))
                : Results.Accepted((string?)null, new { id = job.Id, state = "running" });
        });

        group.MapPost("/mode", (ModeRequest req, BlindJobManager jobs) =>
        {
            var mode = BlindCpuModeExtensions.FromName(req.Mode);
            if (mode is null)
                return Results.BadRequest(new ErrorResponse(
                    "mode must be one of: economy, fast, pause"));
            if (!jobs.SetMode(mode.Value))
                return Results.Conflict(new ErrorResponse("the node is being wiped"));
            return Results.Ok(new { mode = req.Mode });
        });
    }

    /// <summary>
    /// Applies the wire DTO onto the stored settings. Masked ("••••") and null fields mean "keep";
    /// an explicit empty string clears. Numeric fields apply only when present; the PUT handler
    /// validates the result before saving it.
    /// </summary>
    internal static void Apply(BlindBackupSettingsDto dto, BlindBackupSettings s)
    {
        if (dto.RepoType is { } t)
            s.RepoType = t == "s3" ? BlindRepoType.S3 : BlindRepoType.Folder;
        if (dto.RepoFolder.HasNewValue(out var v)) s.RepoFolder = v;
        if (dto.S3Endpoint.HasNewValue(out v)) s.S3Endpoint = v;
        if (dto.S3Bucket.HasNewValue(out v)) s.S3Bucket = v;
        if (dto.S3Prefix.HasNewValue(out v)) s.S3Prefix = v;
        if (dto.S3Region.HasNewValue(out v)) s.S3Region = v;
        if (dto.S3AccessKey.HasNewValue(out v)) s.S3AccessKey = v;
        if (dto.S3SecretKey.HasNewValue(out v)) s.S3SecretKey = v;
        if (dto.ResticPassword.HasNewValue(out v))
        {
            // Entered by the operator: it wins over the console-password default from now on. An
            // empty string clears it, and with it the decision.
            s.ResticPassword = v;
            s.ResticPasswordSource = string.IsNullOrEmpty(v) ? null : ResticPasswordSources.Explicit;
        }
        if (dto.KeepDaily is { } kd) s.KeepDaily = kd;
        if (dto.KeepWeekly is { } kw) s.KeepWeekly = kw;
        if (dto.KeepMonthly is { } km) s.KeepMonthly = km;
        if (dto.KeepYearly is { } ky) s.KeepYearly = ky;
        if (dto.ScheduleTime is { } st) s.ScheduleTime = st;
        if (dto.ScheduleEnabled is { } se) s.ScheduleEnabled = se;
        if (dto.CheckSubsetPercent is { } cs) s.CheckSubsetPercent = cs;
        if (dto.ResticGoMemLimit is { } gm) s.ResticGoMemLimit = gm;
    }

    private static object WithConfigured(BlindBackupSettings masked, bool configured) => new
    {
        settings = masked,
        configured,
    };

    public sealed class VerifyRequest
    {
        public bool Full { get; set; }
    }

    public sealed class CopyRequest
    {
        public string? Destination { get; set; }
    }

    public sealed class ModeRequest
    {
        public string? Mode { get; set; }
    }
}

/// <summary>
/// Wire form of the backup settings. Nullable on purpose: null = "not sent, keep the stored
/// value" (see Apply); the secrets additionally accept the "••••" mask with the same meaning so
/// a console that round-trips what it read cannot accidentally wipe a password it never saw.
/// </summary>
public sealed class BlindBackupSettingsDto
{
    public string? RepoType { get; set; }
    public string? RepoFolder { get; set; }
    public string? S3Endpoint { get; set; }
    public string? S3Bucket { get; set; }
    public string? S3Prefix { get; set; }
    public string? S3Region { get; set; }
    public string? S3AccessKey { get; set; }
    public string? S3SecretKey { get; set; }
    public string? ResticPassword { get; set; }
    public int? KeepDaily { get; set; }
    public int? KeepWeekly { get; set; }
    public int? KeepMonthly { get; set; }
    public int? KeepYearly { get; set; }
    public string? ScheduleTime { get; set; }
    public bool? ScheduleEnabled { get; set; }
    public int? CheckSubsetPercent { get; set; }
    public string? ResticGoMemLimit { get; set; }
    // No ResticBinary here on purpose: which executable the node runs is not something a console
    // session may change — a settable path is arbitrary code execution for whoever holds the
    // console password. The override stays a local settings-file edit.
}

file static class WireValue
{
    /// <summary>True with the value when the field carries a change (mask excluded); false to keep.</summary>
    public static bool HasNewValue(this string? sent, out string? value)
    {
        value = sent;
        return sent is not null && sent != "••••";
    }
}
