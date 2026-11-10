namespace BeeMemoryBank.Api.Dream;

/// <summary>One <c>candidate</c> event of a run's journal, opened: what the run made of one candidate.</summary>
/// <param name="Seq">The journal's own order: the order the run looked at its candidates in.</param>
public sealed record DreamCandidateRecord(Guid RunId, long Seq, DreamCandidateEventData Data);

/// <summary>
/// The table of <c>GET /api/dream/compare</c> (plan 3.8, package C1): the candidate events of two or three trial runs laid side
/// by side, one row per candidate. Pure: it takes what the reader found and says nothing about where it came from, so the rules
/// are tested without a node.
///
/// <list type="bullet">
/// <item>A candidate is the same candidate in every run when its <c>candidateKey</c> is: a hash of the task and the sorted
/// ids, so it does not depend on the model, the order or the run.</item>
/// <item>A run that looked at a candidate more than once (a tier offered it again) is represented by its last look.</item>
/// <item><c>Common</c> counts the candidates every run looked at. <c>Agreement</c> is the share of the common ones on which
/// every run gave the same verdict; a candidate a run failed on has no verdict from that run and is left out of both the
/// agreeing and the counted (<c>AgreementBasis</c>), so a flaky provider does not read as disagreement between models.</item>
/// <item>Rows: the common ones first, then the others; inside each group in the order the runs looked at them (the least
/// position of the candidate in any run's own sequence), ties by key. With one seed the runs share their order, so the
/// shared start of the table is what a quick model and a slow one both reached.</item>
/// </list>
/// </summary>
public static class DreamCompareBuilder
{
    /// <param name="runs">The runs in the order the table's columns follow.</param>
    /// <param name="objectsOf">The articles and folders of a candidate, as the table names them.</param>
    /// <param name="runsWithoutLog">Runs the reader found no journal for although they examined candidates.</param>
    public static DreamCompareResponse Build(
        IReadOnlyList<DreamRunSummary> runs,
        IReadOnlyList<DreamCandidateRecord> candidates,
        Func<DreamCandidateEventData, IReadOnlyList<DreamAdviceObject>> objectsOf,
        IReadOnlyList<Guid>? runsWithoutLog = null)
    {
        var runIds = runs.Select(r => r.Id).ToList();

        // Per run: candidate key -> (position in the run's own order, the last look at it).
        var perRun = new Dictionary<Guid, Dictionary<string, (int Rank, DreamCandidateRecord Record)>>();
        foreach (var id in runIds) perRun[id] = new(StringComparer.Ordinal);
        foreach (var group in candidates.Where(c => perRun.ContainsKey(c.RunId)).GroupBy(c => c.RunId))
        {
            var map = perRun[group.Key];
            var rank = 0;
            foreach (var record in group.OrderBy(c => c.Seq))
            {
                var key = record.Data.CandidateKey;
                map[key] = (map.TryGetValue(key, out var seen) ? seen.Rank : rank++, record);
            }
        }

        var rows = new List<(bool All, int Rank, DreamCompareRow Row)>();
        foreach (var key in perRun.Values.SelectMany(m => m.Keys).Distinct(StringComparer.Ordinal))
        {
            var results = new Dictionary<string, DreamCompareResult?>(StringComparer.Ordinal);
            DreamCandidateRecord? first = null;
            var rank = int.MaxValue;
            var all = true;
            foreach (var id in runIds)
            {
                if (perRun[id].TryGetValue(key, out var hit))
                {
                    first ??= hit.Record;
                    rank = Math.Min(rank, hit.Rank);
                    results[id.ToString("D")] = ResultOf(hit.Record.Data);
                }
                else
                {
                    results[id.ToString("D")] = null;
                    all = false;
                }
            }
            if (first == null) continue;
            rows.Add((all, rank, new DreamCompareRow(key, first.Data.Task, objectsOf(first.Data), results)));
        }

        var ordered = rows
            .OrderByDescending(r => r.All)
            .ThenBy(r => r.Rank)
            .ThenBy(r => r.Row.CandidateKey, StringComparer.Ordinal)
            .ToList();

        var common = ordered.Where(r => r.All).Select(r => r.Row).ToList();
        var counted = common.Where(HasVerdictFromEveryRun).ToList();
        var agreeing = counted.Count(SameVerdict);

        return new DreamCompareResponse(runs, ordered.Select(r => r.Row).ToList(), common.Count, counted.Count == 0 ? 0 : (double)agreeing / counted.Count)
        {
            AgreementBasis = counted.Count,
            RunsWithoutLog = runsWithoutLog ?? [],
        };
    }

    private static DreamCompareResult ResultOf(DreamCandidateEventData data) =>
        new(data.Verdict, data.Kind, data.Score, data.Summary) { Outcome = data.Outcome };

    private static bool HasVerdictFromEveryRun(DreamCompareRow row) =>
        row.Results.Values.All(r => r != null && r.Outcome != DreamExaminedVerdict.Error);

    private static bool SameVerdict(DreamCompareRow row) =>
        row.Results.Values.Select(r => r!.Verdict ?? "").Distinct(StringComparer.Ordinal).Count() == 1;
}
