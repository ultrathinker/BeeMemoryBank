using BeeMemoryBank.Api.Dream;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Api.Endpoints;

// GET /api/dream/compare: trial runs side by side, from their candidate events (package C1).
public static partial class DreamEndpoints
{
    /// <summary>Two or three runs: more is a wall of columns, fewer is nothing to compare.</summary>
    private const int MinCompareRuns = 2;
    private const int MaxCompareRuns = 3;

    /// <summary>Ids per query when the notes of the table are looked up (SQLite's limit on variables is far above this).</summary>
    private const int LookupChunk = 400;

    /// <summary>
    /// The table of what each trial run made of each candidate (plan 3.8): <c>{ runs, rows, common, agreement }</c>, built from the
    /// <c>candidate</c> events of the runs' journals (<see cref="DreamCompareBuilder"/>). Trial runs only: a normal run skips what the
    /// registry already holds, so its candidates are not the same set. The journal is kept seven days; a run whose journal is gone
    /// is listed in <c>runsWithoutLog</c> and has no cells. Locked vault: 409 <c>locked</c> (the events are sealed under the chat key).
    /// </summary>
    /// <param name="runs">Run ids separated by commas (two or three trial runs).</param>
    private static async Task<IResult> CompareRunsAsync(HttpContext ctx, string? runs, CancellationToken ct)
    {
        var ids = new List<Guid>();
        foreach (var part in (runs ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(part, out var id)) return BadRequest("runs must be run ids separated by commas.");
            if (ids.Contains(id)) return BadRequest("Each run can be compared once.");
            ids.Add(id);
        }
        if (ids.Count < MinCompareRuns || ids.Count > MaxCompareRuns)
            return BadRequest($"Pick {MinCompareRuns} or {MaxCompareRuns} trial runs to compare.");

        var sp = ctx.RequestServices;
        try
        {
            var reader = ActivatorUtilities.CreateInstance<DreamCompareReader>(sp);
            var found = await reader.GetRunsAsync(ids, ct);
            foreach (var id in ids)
            {
                if (!found.TryGetValue(id, out var hit)) return NotFound("Run");
                if (hit.Run.Mode != DreamRunMode.Trial) return BadRequest("Only trial runs can be compared.");
            }

            var users = sp.GetRequiredService<IUserRepository>();
            var summaries = new List<DreamRunSummary>(ids.Count);
            var records = new List<DreamCandidateRecord>();
            var withoutLog = new List<Guid>();
            foreach (var id in ids)
            {
                var (run, startedBy) = found[id];
                if (startedBy is { } userId && await users.GetByIdAsync(userId) is { } user)
                    run = run with { StartedBy = new DreamUserRef(user.Id, string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName) };
                summaries.Add(run);

                var mine = await reader.GetCandidatesAsync(id, ct);
                if (mine.Count == 0 && run.Counters.CandidatesExamined > 0) withoutLog.Add(id);
                records.AddRange(mine);
            }

            var objectsOf = await CompareObjectsAsync(sp, records);
            return Results.Ok(DreamCompareBuilder.Build(summaries, records, objectsOf, withoutLog));
        }
        catch (Exception e) when (e is SessionLockedException or VaultSealedException)
        {
            return Locked();
        }
    }

    /// <summary>
    /// Names the articles and folders of the candidates for the table: title and path, as the notes have them now. A note that is gone
    /// has its id only. A note under the second password has its id only as well: Dream never reads it, and its title is not named
    /// on a page either. Superadmin only, so no access filter narrows this.
    /// </summary>
    private static async Task<Func<DreamCandidateEventData, IReadOnlyList<DreamAdviceObject>>> CompareObjectsAsync(
        IServiceProvider sp, IReadOnlyList<DreamCandidateRecord> records)
    {
        var articleIds = records.SelectMany(r => r.Data.ArticleIds).Distinct().ToList();
        var folderIds = records.SelectMany(r => r.Data.FolderIds).Distinct().ToList();

        var articles = new Dictionary<Guid, DreamAdviceObject>();
        var articleRepo = sp.GetRequiredService<IArticleRepository>();
        foreach (var chunk in articleIds.Chunk(LookupChunk))
            foreach (var a in await articleRepo.GetByIdsAsync([.. chunk]))
                articles[a.Id] = a.Protected
                    ? new DreamAdviceObject { Type = DreamObjectType.Article, Id = a.Id }
                    : new DreamAdviceObject
                    {
                        Type = DreamObjectType.Article, Id = a.Id, Title = a.Title, TreePath = a.TreePath,
                        LamportTs = a.LamportTs, UpdatedAt = a.UpdatedAt,
                    };

        var folders = new Dictionary<Guid, DreamAdviceObject>();
        var folderRepo = sp.GetRequiredService<IFolderRepository>();
        foreach (var id in folderIds)
            if (await folderRepo.GetByIdAsync(id) is { } folder)
                folders[id] = DreamAdviceObject.ForFolder(id, folder.Path);

        return data =>
        {
            var objects = new List<DreamAdviceObject>(data.ArticleIds.Count + data.FolderIds.Count);
            foreach (var id in data.ArticleIds)
                objects.Add(articles.TryGetValue(id, out var a) ? a : new DreamAdviceObject { Type = DreamObjectType.Article, Id = id });
            foreach (var id in data.FolderIds)
                objects.Add(folders.TryGetValue(id, out var f) ? f : new DreamAdviceObject { Type = DreamObjectType.Folder, Id = id });
            return objects;
        };
    }
}
