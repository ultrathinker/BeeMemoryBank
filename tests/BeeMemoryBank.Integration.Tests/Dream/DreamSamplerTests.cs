using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BeeMemoryBank.Api.Dream;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The sample of a trial run (plan 3.8, package C1): the same seed gives every run the same candidates in the same order, whatever order
/// a task enumerates them in, and a cap cuts that order short across the tiers of a run. Hand-made corpus and fake model clients: no node,
/// no network. The last tests play three runs the way the engine will (wrap the task, walk the tiers, examine, write a candidate event) and
/// lay them side by side with <see cref="DreamCompareBuilder"/>.
/// </summary>
public class DreamSamplerTests
{
    // ── Test doubles ────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class SampleCorpus : IDreamCorpus
    {
        private readonly Dictionary<Guid, (DreamArticle Article, string Body)> _notes = [];

        public IReadOnlyList<DreamArticle> Articles => _notes.Values.Select(n => n.Article).ToList();
        public IReadOnlyList<DreamFolder> Folders => [];
        public IReadOnlyList<DreamTag> Tags => [];
        public IReadOnlyList<string> TagsOf(Guid articleId) => [];
        public IReadOnlyList<DreamNeighbour> NeighboursOf(Guid articleId, int topK) => [];
        public Task<string?> ReadBodyAsync(Guid articleId, CancellationToken ct) => Task.FromResult<string?>(_notes[articleId].Body);
        public Task<int> CountReferencesAsync(Guid articleId, CancellationToken ct) => Task.FromResult(0);

        public DreamArticle Add(string title, string body)
        {
            var a = new DreamArticle(Guid.NewGuid(), title, "/Notes", Guid.NewGuid(), 100,
                new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc), new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc), true);
            _notes[a.Id] = (a, body);
            return a;
        }
    }

    private sealed class NullLog : IDreamRunLog
    {
        public Task WriteAsync(DreamEventKind kind, string text, IReadOnlyList<Guid>? articleIds = null, JsonObject? data = null,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A task that offers what it is told to, once per tier, and counts nothing else.</summary>
    private sealed class StubTask(string name, Func<int, IReadOnlyList<DreamModelCandidate>> byTier) : IDreamModelTask
    {
        public string Name => name;
        public string PromptVersion => "stub-1";

        public async IAsyncEnumerable<DreamModelCandidate> CandidatesAsync(IDreamCorpus corpus, DreamRunContext ctx, int tier,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            foreach (var c in byTier(tier)) yield return c;
        }

        public Task<DreamTaskResult> ExamineAsync(DreamModelCandidate c, IDreamCorpus corpus, IDreamModelClient model, DreamRunContext ctx,
            CancellationToken ct) => Task.FromResult(DreamTaskResult.Nothing("related"));
    }

    private static DreamRunContext Ctx(DreamSampleRequest? sample, DreamRunBudget? budget = null) => new()
    {
        RunId = Guid.NewGuid(),
        Trigger = DreamRunTrigger.Manual,
        Mode = DreamRunMode.Trial,
        Settings = DreamSettings.Default,
        UseModel = true,
        ModelId = "test/model",
        ModelLabel = "Test model",
        Sample = sample,
        Budget = budget ?? new DreamRunBudget(200, 1_500_000),
        Log = new NullLog(),
        StartedAtUtc = DateTime.UtcNow,
    };

    private static DreamModelCandidate Cand(string task, string key, int tier = 1) =>
        new(task, key, "marker", tier, [Guid.NewGuid()], []);

    private static async Task<List<T>> ToList<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source) list.Add(item);
        return list;
    }

    private static List<string> Keys(IEnumerable<DreamModelCandidate> candidates) => candidates.Select(c => c.CandidateKey).ToList();

    private static readonly string[] ThirtyKeys = Enumerable.Range(0, 30).Select(i => $"candidate-{i:00}").ToArray();

    // ── The order ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheSortKey_IsSha256OfSeedBarCandidateKey()
    {
        // Pinned on purpose: the order of a saved trial comparison must survive a release, so the hash cannot change quietly.
        DreamSampler.SortKey(42, "abc").Should().Equal(SHA256.HashData(Encoding.UTF8.GetBytes("42|abc")));
        DreamSampler.SortKey(-7, "abc").Should().Equal(SHA256.HashData(Encoding.UTF8.GetBytes("-7|abc")));
    }

    [Fact]
    public void TheOrder_DoesNotDependOnTheOrderTheItemsCameIn()
    {
        var forward = DreamSampler.Order(ThirtyKeys, 7, k => k);
        var backward = DreamSampler.Order(ThirtyKeys.Reverse(), 7, k => k);
        var shuffled = DreamSampler.Order(ThirtyKeys.OrderBy(k => SHA256.HashData(Encoding.UTF8.GetBytes("x" + k)), Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b))), 7, k => k);

        backward.Should().Equal(forward);
        shuffled.Should().Equal(forward);
        forward.Should().BeEquivalentTo(ThirtyKeys, "nothing is dropped or invented");
    }

    [Fact]
    public void ADifferentSeed_GivesADifferentOrder_AndTheSameSeedTheSameOneEveryTime()
    {
        var first = DreamSampler.Order(ThirtyKeys, 1, k => k);

        DreamSampler.Order(ThirtyKeys, 1, k => k).Should().Equal(first);
        DreamSampler.Order(ThirtyKeys, 2, k => k).Should().NotEqual(first);
        first.Should().NotEqual(ThirtyKeys, "a hash order is not the order they were written in");
    }

    [Fact]
    public void ARepeatedKey_KeepsItsFirstItemOnly()
    {
        var ordered = DreamSampler.Order(new[] { ("a", 1), ("b", 2), ("a", 3) }, 5, x => x.Item1);

        ordered.Should().HaveCount(2);
        ordered.Should().Contain(("a", 1)).And.NotContain(("a", 3));
    }

    // ── The request ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASampleRequest_IsFine_WithAnySeedAndCountsInRange_AndRefusedOtherwise()
    {
        const DreamRunMode trial = DreamRunMode.Trial;
        DreamSampler.Validate(null, trial).Should().BeNull();
        DreamSampler.Validate(null, DreamRunMode.Normal).Should().BeNull("no sample, nothing to refuse");
        DreamSampler.Validate(new DreamSampleRequest(0), trial).Should().BeNull();
        DreamSampler.Validate(new DreamSampleRequest(int.MinValue, 1, DreamSampler.MaxSampleCount), trial).Should().BeNull();
        DreamSampler.Validate(new DreamSampleRequest(5, 0), trial).Should().Contain("sample.pairs");
        DreamSampler.Validate(new DreamSampleRequest(5, -3), trial).Should().Contain("sample.pairs");
        DreamSampler.Validate(new DreamSampleRequest(5, DreamSampler.MaxSampleCount + 1), trial).Should().Contain("sample.pairs");
        DreamSampler.Validate(new DreamSampleRequest(5, 10, 0), trial).Should().Contain("sample.folders");
    }

    [Fact]
    public void ASample_IsForATrialRunOnly()
    {
        DreamSampler.Validate(new DreamSampleRequest(5, 10), DreamRunMode.Normal).Should().Contain("trial run");
    }

    [Fact]
    public void TheCap_IsPerTask_AndOnlyPairsAndFoldersHaveOne()
    {
        var sample = new DreamSampleRequest(1, Pairs: 12, Folders: 3);

        DreamSampler.CapOf(sample, DreamNames.PairVerdictTask).Should().Be(12);
        DreamSampler.CapOf(sample, DreamNames.FolderReviewTask).Should().Be(3);
        DreamSampler.CapOf(sample, DreamNames.TreeReviewTask).Should().BeNull("the tree is one candidate; a sample does not cut it");
        DreamSampler.CapOf(new DreamSampleRequest(1), DreamNames.PairVerdictTask).Should().BeNull();
    }

    // ── The wrapper ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WithoutASample_TheRunGetsTheTaskItself()
    {
        var task = new StubTask(DreamNames.PairVerdictTask, _ => []);

        DreamSampler.Wrap(task, Ctx(null)).Should().BeSameAs(task);
        var wrapped = DreamSampler.Wrap(task, Ctx(new DreamSampleRequest(1)));
        wrapped.Should().BeOfType<DreamSampledTask>();
        wrapped.Name.Should().Be(DreamNames.PairVerdictTask);
        wrapped.PromptVersion.Should().Be("stub-1");
        DreamSampler.Wrap(wrapped, Ctx(new DreamSampleRequest(1))).Should().BeSameAs(wrapped, "wrapping twice does not stack");
    }

    [Fact]
    public async Task ASampledTask_OffersTheCandidatesInTheSeedsOrder_WhateverOrderTheTaskUsed()
    {
        var all = ThirtyKeys.Select(k => Cand(DreamNames.PairVerdictTask, k)).ToList();
        var ctxA = Ctx(new DreamSampleRequest(9));
        var ctxB = Ctx(new DreamSampleRequest(9));
        var forward = DreamSampler.Wrap(new StubTask(DreamNames.PairVerdictTask, _ => all), ctxA);
        var backward = DreamSampler.Wrap(new StubTask(DreamNames.PairVerdictTask, _ => ((IEnumerable<DreamModelCandidate>)all).Reverse().ToList()), ctxB);

        var a = Keys(await ToList(forward.CandidatesAsync(new SampleCorpus(), ctxA, 1, CancellationToken.None)));
        var b = Keys(await ToList(backward.CandidatesAsync(new SampleCorpus(), ctxB, 1, CancellationToken.None)));

        a.Should().Equal(DreamSampler.Order(ThirtyKeys, 9, k => k));
        b.Should().Equal(a);
    }

    [Fact]
    public async Task TheCap_HoldsAcrossTheTiers_AndACandidateOfTwoTiersCountsOnce()
    {
        var tier1 = ThirtyKeys.Take(6).Select(k => Cand(DreamNames.PairVerdictTask, k, 1)).ToList();
        var tier2 = ThirtyKeys.Skip(3).Take(9).Select(k => Cand(DreamNames.PairVerdictTask, k, 2)).ToList();   // 3 of them were in tier 1 too
        var ctx = Ctx(new DreamSampleRequest(3, Pairs: 10));
        var task = DreamSampler.Wrap(new StubTask(DreamNames.PairVerdictTask, t => t == 1 ? tier1 : tier2), ctx);

        var first = await ToList(task.CandidatesAsync(new SampleCorpus(), ctx, 1, CancellationToken.None));
        var second = await ToList(task.CandidatesAsync(new SampleCorpus(), ctx, 2, CancellationToken.None));
        var third = await ToList(task.CandidatesAsync(new SampleCorpus(), ctx, 3, CancellationToken.None));

        first.Should().HaveCount(6);
        second.Should().HaveCount(4, "10 in all, and the three that tier 1 already offered do not count twice");
        third.Should().BeEmpty("the cap of 10 is used up");
        Keys(first.Concat(second)).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task TheFolderCap_IsItsOwn_AndTheTreeIsNeverCut()
    {
        var ctx = Ctx(new DreamSampleRequest(4, Pairs: 1, Folders: 2));
        var folders = DreamSampler.Wrap(new StubTask(DreamNames.FolderReviewTask, _ => ThirtyKeys.Take(8).Select(k => Cand(DreamNames.FolderReviewTask, k)).ToList()), ctx);
        var tree = DreamSampler.Wrap(new StubTask(DreamNames.TreeReviewTask, _ => [Cand(DreamNames.TreeReviewTask, "the-tree")]), ctx);

        (await ToList(folders.CandidatesAsync(new SampleCorpus(), ctx, 1, CancellationToken.None))).Should().HaveCount(2);
        (await ToList(tree.CandidatesAsync(new SampleCorpus(), ctx, 1, CancellationToken.None))).Should().ContainSingle();
    }

    [Fact]
    public async Task TwoRunsOfOneSeed_KeepTheirOwnCounts()
    {
        var all = ThirtyKeys.Select(k => Cand(DreamNames.PairVerdictTask, k)).ToList();
        var inner = new StubTask(DreamNames.PairVerdictTask, _ => all);
        var ctxA = Ctx(new DreamSampleRequest(1, Pairs: 5));
        var ctxB = Ctx(new DreamSampleRequest(1, Pairs: 5));

        var a = await ToList(DreamSampler.Wrap(inner, ctxA).CandidatesAsync(new SampleCorpus(), ctxA, 1, CancellationToken.None));
        var b = await ToList(DreamSampler.Wrap(inner, ctxB).CandidatesAsync(new SampleCorpus(), ctxB, 1, CancellationToken.None));

        Keys(a).Should().HaveCount(5);
        Keys(b).Should().Equal(Keys(a), "the second run is not starved by the first one's count");
    }

    // ── Three trial runs, the way the engine will play them ─────────────────────────────────────────────────────

    private static string Reply(string verdict, int score) =>
        $$"""{"verdict":"{{verdict}}","keep":{{(verdict is "duplicate" or "superseded" ? "\"A\"" : "null")}},"score":{{score}},"summary":"It says {{verdict}}.","quotes":[]}""";

    private static (SampleCorpus Corpus, List<DreamPairCandidate> Pairs) TwelvePairs()
    {
        var corpus = new SampleCorpus();
        var notes = Enumerable.Range(0, 8).Select(i => corpus.Add($"Note {i}", $"Body of note {i}. " + new string('x', 20 + i))).ToList();
        var pairs = new List<DreamPairCandidate>();
        for (var i = 0; i < notes.Count && pairs.Count < 12; i++)
            for (var j = i + 1; j < notes.Count && pairs.Count < 12; j++)
                pairs.Add(new DreamPairCandidate(notes[i].Id, notes[j].Id, 0.9 + pairs.Count / 1000.0, 3, 1 + pairs.Count % 3));
        return (corpus, pairs);
    }

    /// <summary>What the engine does for a trial run of one model task: wrap, walk the tiers, examine, write one candidate event each.</summary>
    private static async Task<List<DreamCandidateRecord>> PlayAsync(DreamRunContext ctx, IDreamModelTask task, IDreamCorpus corpus, IDreamModelClient model)
    {
        var events = new List<DreamCandidateRecord>();
        var sampled = DreamSampler.Wrap(task, ctx);
        try
        {
            for (var tier = 1; tier <= 3; tier++)
                foreach (var c in await ToList(sampled.CandidatesAsync(corpus, ctx, tier, CancellationToken.None)))
                {
                    var r = await sampled.ExamineAsync(c, corpus, model, ctx, CancellationToken.None);
                    events.Add(new DreamCandidateRecord(ctx.RunId, events.Count + 1, new DreamCandidateEventData(
                        c.CandidateKey, c.Task, c.Tier, c.ArticleIds, c.FolderIds, r.Outcome, r.Verdict, r.Drafts.FirstOrDefault()?.Kind, r.Score, r.Summary)));
                }
        }
        catch (DreamBudgetExceededException)
        {
            // the engine ends the run with budget_limit; what was examined stays in the journal
        }
        return events;
    }

    private static DreamRunSummary SummaryOf(DreamRunContext ctx, string label, int examined) => new()
    {
        Id = ctx.RunId, Mode = DreamRunMode.Trial, State = DreamRunState.Finished, UseModel = true, ModelLabel = label,
        Counters = new DreamRunCounters { CandidatesExamined = examined },
    };

    [Fact]
    public async Task ThreeTrialRuns_OfOneSeed_ExamineTheSameCandidatesInTheSameOrder_EvenWhenTheirSourcesListThemDifferently()
    {
        var (corpus, pairs) = TwelvePairs();
        var sample = new DreamSampleRequest(Seed: 20261110, Pairs: 12);
        var rng = new Random(5);
        var sequences = new List<List<DreamCandidateRecord>>();
        for (var run = 0; run < 3; run++)
        {
            var listed = pairs.OrderBy(_ => rng.Next()).ToList();   // each run's source lists the pairs in its own order
            var task = new PairVerdictTask((_, _, tier) => listed.Where(p => p.Tier == tier).ToList());
            var client = new FakeDreamModelClient { Responder = _ => Reply("overlap", 70) };
            sequences.Add(await PlayAsync(Ctx(sample), task, corpus, client));
        }

        var keys = sequences.Select(s => s.Select(e => e.Data.CandidateKey).ToList()).ToList();
        keys[0].Should().HaveCount(12);
        keys[1].Should().Equal(keys[0]);
        keys[2].Should().Equal(keys[0]);

        var otherSeed = Ctx(new DreamSampleRequest(Seed: 1, Pairs: 12));
        var again = await PlayAsync(otherSeed, new PairVerdictTask((_, _, tier) => pairs.Where(p => p.Tier == tier).ToList()), corpus,
            new FakeDreamModelClient { Responder = _ => Reply("overlap", 70) });
        again.Select(e => e.Data.CandidateKey).Should().NotEqual(keys[0], "a different seed is a different order");
    }

    [Fact]
    public async Task TheTable_OfThreeSampledRuns_ShowsTheCommonPartAndWhereTheyDisagree()
    {
        var (corpus, pairs) = TwelvePairs();
        var sample = new DreamSampleRequest(Seed: 77, Pairs: 12);
        PairVerdictTask NewTask() => new((_, _, tier) => pairs.Where(p => p.Tier == tier).ToList());

        // Run 1 calls everything an overlap. Run 2 agrees except on every third call, where it says unrelated. Run 3 is slow: its budget runs out after 5 calls.
        var ctx1 = Ctx(sample);
        var ctx2 = Ctx(sample);
        var ctx3 = Ctx(sample, new DreamRunBudget(maxModelCalls: 5, maxTokens: 1_500_000));
        var calls = 0;
        var e1 = await PlayAsync(ctx1, NewTask(), corpus, new FakeDreamModelClient { Responder = _ => Reply("overlap", 72) });
        var e2 = await PlayAsync(ctx2, NewTask(), corpus, new FakeDreamModelClient { Responder = _ => ++calls % 3 == 0 ? Reply("unrelated", 10) : Reply("overlap", 70) });
        var e3 = await PlayAsync(ctx3, NewTask(), corpus, new FakeDreamModelClient { Responder = _ => Reply("overlap", 75) });

        e1.Should().HaveCount(12);
        e2.Should().HaveCount(12);
        e3.Should().HaveCount(5, "the third run went as far as its budget allowed");
        e3.Select(e => e.Data.CandidateKey).Should().Equal(e1.Take(5).Select(e => e.Data.CandidateKey), "the slow run walked the same start of the same order");

        var table = DreamCompareBuilder.Build(
            [SummaryOf(ctx1, "Model one", 12), SummaryOf(ctx2, "Model two", 12), SummaryOf(ctx3, "Model three", 5)],
            [.. e1, .. e2, .. e3], _ => []);

        table.Rows.Should().HaveCount(12);
        table.Common.Should().Be(5, "every run looked at the first five only");
        table.Rows.Take(5).Should().OnlyContain(r => r.Results.Values.All(v => v != null), "the common part comes first");
        table.Rows.Skip(5).Should().OnlyContain(r => r.Results[ctx3.RunId.ToString("D")] == null);
        table.Rows.Take(5).Select(r => r.CandidateKey).Should().Equal(e1.Take(5).Select(e => e.Data.CandidateKey), "in the order they were examined");
        table.AgreementBasis.Should().Be(5);
        var disagreeing = table.Rows.Take(5).Count(r => r.Results.Values.Select(v => v!.Verdict).Distinct().Count() > 1);
        disagreeing.Should().BeGreaterThan(0, "run 2 said unrelated on some of them");
        table.Agreement.Should().BeApproximately((5 - disagreeing) / 5.0, 1e-9);
        table.Rows.SelectMany(r => r.Results.Values).Where(v => v != null).Should().OnlyContain(v => v!.Verdict != null && v.Score != null && v.Summary != null);
    }
}
