using BeeMemoryBank.Api.Dream;

namespace BeeMemoryBank.Integration.Tests.Dream;

/// <summary>
/// The compare table's rules (plan 3.8, package C1) on hand-made candidate events: which candidates are common, what agreement means, how
/// the rows are ordered, and what an error, a repeat look or a missing journal does to them. No node.
/// </summary>
public class DreamCompareBuilderTests
{
    private static DreamRunSummary Run(string label, int examined = 10) => new()
    {
        Id = Guid.NewGuid(), Mode = DreamRunMode.Trial, State = DreamRunState.Finished, UseModel = true, ModelLabel = label,
        Counters = new DreamRunCounters { CandidatesExamined = examined },
    };

    private static DreamCandidateRecord Look(DreamRunSummary run, long seq, string key, string? verdict = "overlap", int? score = 70,
        DreamAdviceKind? kind = DreamAdviceKind.Overlap, DreamExaminedVerdict outcome = DreamExaminedVerdict.Advice, string task = DreamNames.PairVerdictTask) =>
        new(run.Id, seq, new DreamCandidateEventData(key, task, 1, [Guid.NewGuid(), Guid.NewGuid()], [], outcome, verdict, kind, score, verdict == null ? null : $"{verdict} because"));

    private static DreamCandidateRecord Failed(DreamRunSummary run, long seq, string key) =>
        Look(run, seq, key, verdict: null, score: null, kind: null, outcome: DreamExaminedVerdict.Error);

    private static DreamCompareResponse Build(DreamRunSummary[] runs, params DreamCandidateRecord[] looks) =>
        DreamCompareBuilder.Build(runs, looks, _ => []);

    private static string Id(DreamRunSummary run) => run.Id.ToString("D");

    [Fact]
    public void TheSameCandidates_WithTheSameVerdicts_AreFullAgreement()
    {
        DreamRunSummary a = Run("A"), b = Run("B");

        var table = Build([a, b], Look(a, 1, "k1"), Look(a, 2, "k2"), Look(b, 1, "k1"), Look(b, 2, "k2"));

        table.Rows.Should().HaveCount(2);
        table.Common.Should().Be(2);
        table.AgreementBasis.Should().Be(2);
        table.Agreement.Should().Be(1.0);
        table.Runs.Select(r => r.Id).Should().Equal([a.Id, b.Id], "the runs keep the order they were asked for");
    }

    [Fact]
    public void AgreementIsTheShareOfCommonCandidates_OnWhichEveryRunSaidTheSame()
    {
        DreamRunSummary a = Run("A"), b = Run("B"), c = Run("C");

        var table = Build([a, b, c],
            Look(a, 1, "same"), Look(b, 1, "same"), Look(c, 1, "same"),
            Look(a, 2, "split", "duplicate"), Look(b, 2, "split", "duplicate"), Look(c, 2, "split", "overlap"),
            Look(a, 3, "alone", "overlap"), Look(b, 3, "alone", "unrelated", 5, null, DreamExaminedVerdict.Nothing), Look(c, 3, "alone", "overlap"),
            Look(a, 4, "same2", "unrelated", 5, null, DreamExaminedVerdict.Nothing), Look(b, 4, "same2", "unrelated", 8, null, DreamExaminedVerdict.Nothing),
            Look(c, 4, "same2", "unrelated", 2, null, DreamExaminedVerdict.Nothing));

        table.Common.Should().Be(4);
        table.AgreementBasis.Should().Be(4);
        table.Agreement.Should().BeApproximately(0.5, 1e-9, "two of four candidates got one verdict from all three runs (the scores differ and do not matter)");
    }

    [Fact]
    public void ACandidateOnlySomeRunsExamined_IsARowWithNullWhereTheOthersDidNotLook_AndIsNotCommon()
    {
        DreamRunSummary a = Run("A"), b = Run("B");

        var table = Build([a, b], Look(a, 1, "both"), Look(b, 1, "both"), Look(a, 2, "only-a", "duplicate", 90, DreamAdviceKind.Duplicate));

        var row = table.Rows.Single(r => r.CandidateKey == "only-a");
        row.Results[Id(a)].Should().NotBeNull();
        row.Results[Id(a)]!.Kind.Should().Be(DreamAdviceKind.Duplicate);
        row.Results[Id(a)]!.Score.Should().Be(90);
        row.Results[Id(b)].Should().BeNull("run B never looked at it: not the same as an error");
        table.Common.Should().Be(1);
        table.AgreementBasis.Should().Be(1);
    }

    [Fact]
    public void AnError_IsNotAVerdict_ItIsKeptInTheRow_AndLeftOutOfAgreement()
    {
        DreamRunSummary a = Run("A"), b = Run("B");

        var table = Build([a, b], Look(a, 1, "ok"), Look(b, 1, "ok"), Look(a, 2, "broken"), Failed(b, 2, "broken"));

        table.Common.Should().Be(2, "both runs looked at both candidates");
        table.AgreementBasis.Should().Be(1, "a failed look is no opinion");
        table.Agreement.Should().Be(1.0);
        var broken = table.Rows.Single(r => r.CandidateKey == "broken").Results[Id(b)]!;
        broken.Outcome.Should().Be(DreamExaminedVerdict.Error);
        broken.Verdict.Should().BeNull();
        broken.Kind.Should().BeNull();
    }

    [Fact]
    public void WithNothingToCompare_AgreementIsZero_NotANumberThatDividesByZero()
    {
        DreamRunSummary a = Run("A"), b = Run("B");

        var empty = Build([a, b]);
        var disjoint = Build([a, b], Look(a, 1, "x"), Look(b, 1, "y"));

        empty.Rows.Should().BeEmpty();
        empty.Common.Should().Be(0);
        empty.Agreement.Should().Be(0);
        empty.AgreementBasis.Should().Be(0);
        disjoint.Rows.Should().HaveCount(2);
        disjoint.Common.Should().Be(0);
        disjoint.Agreement.Should().Be(0);
    }

    [Fact]
    public void TheCommonRowsComeFirst_ThenTheRestInTheOrderTheRunsLookedAtThem()
    {
        DreamRunSummary a = Run("A"), b = Run("B");

        // Run A looked at k1, k2, k3, k4, k5; run B, the slow one, at k1, k2, k3.
        var table = Build([a, b],
            Look(b, 10, "k1"), Look(b, 11, "k2"), Look(b, 12, "k3"),
            Look(a, 1, "k1"), Look(a, 2, "k2"), Look(a, 3, "k3"), Look(a, 4, "k4"), Look(a, 5, "k5"));

        table.Rows.Select(r => r.CandidateKey).Should().Equal("k1", "k2", "k3", "k4", "k5");
        table.Common.Should().Be(3);
    }

    [Fact]
    public void WhenTheRunsOrdersDiffer_ARowFollowsTheEarliestPlaceItHadInAnyRun()
    {
        DreamRunSummary a = Run("A"), b = Run("B");

        var table = Build([a, b],
            Look(a, 1, "x"), Look(a, 2, "y"), Look(a, 3, "z"),
            Look(b, 1, "z"), Look(b, 2, "y"), Look(b, 3, "x"));

        // x: rank 0 in A, 2 in B -> 0. y: 1 and 1 -> 1. z: 2 and 0 -> 0. x and z tie on 0 and go by key.
        table.Rows.Select(r => r.CandidateKey).Should().Equal("x", "z", "y");
    }

    [Fact]
    public void ARunThatLooksAtACandidateTwice_IsRepresentedByItsLastLook_WithoutMovingTheRow()
    {
        DreamRunSummary a = Run("A"), b = Run("B");

        var table = Build([a, b],
            Failed(a, 1, "retried"), Look(a, 2, "next"), Look(a, 3, "retried", "duplicate", 88, DreamAdviceKind.Duplicate),
            Look(b, 1, "retried", "duplicate", 80, DreamAdviceKind.Duplicate), Look(b, 2, "next"));

        table.Rows.Select(r => r.CandidateKey).Should().Equal("retried", "next");
        var result = table.Rows[0].Results[Id(a)]!;
        result.Outcome.Should().Be(DreamExaminedVerdict.Advice);
        result.Score.Should().Be(88);
    }

    [Fact]
    public void TheEventsOfOtherRuns_AreLeftOut_AndAMissingJournalIsPassedOn()
    {
        DreamRunSummary a = Run("A"), b = Run("B"), stranger = Run("Z");

        var table = DreamCompareBuilder.Build([a, b], [Look(a, 1, "k"), Look(stranger, 1, "k"), Look(stranger, 2, "only-z")], _ => [], [b.Id]);

        table.Rows.Should().ContainSingle().Which.CandidateKey.Should().Be("k");
        table.Rows[0].Results.Keys.Should().BeEquivalentTo([Id(a), Id(b)]);
        table.Rows[0].Results[Id(b)].Should().BeNull();
        table.RunsWithoutLog.Should().Equal([b.Id]);
    }

    [Fact]
    public void ARow_NamesItsObjectsThroughTheCallerAndKeepsTheTask()
    {
        DreamRunSummary a = Run("A"), b = Run("B");
        var look = Look(a, 1, "k", task: DreamNames.FolderReviewTask);
        var article = look.Data.ArticleIds[0];

        var table = DreamCompareBuilder.Build([a, b], [look, Look(b, 1, "k", task: DreamNames.FolderReviewTask)],
            data => [new DreamAdviceObject { Type = DreamObjectType.Article, Id = data.ArticleIds[0], Title = "A title" }]);

        var row = table.Rows.Should().ContainSingle().Subject;
        row.Task.Should().Be(DreamNames.FolderReviewTask);
        row.Objects.Should().ContainSingle().Which.Should().Match<DreamAdviceObject>(o => o.Title == "A title" && o.Id == article);
    }

    [Fact]
    public void TheTable_RoundTripsThroughTheApisJson_WithWireNamesAndNullsWhereARunDidNotLook()
    {
        DreamRunSummary a = Run("A"), b = Run("B");
        var table = Build([a, b], Look(a, 1, "k", "duplicate", 91, DreamAdviceKind.Duplicate), Look(b, 2, "k2", "unrelated", 4, null, DreamExaminedVerdict.Nothing));

        var json = DreamJson.Serialize(table);

        json.Should().Contain("\"candidateKey\":\"k\"").And.Contain("\"kind\":\"duplicate\"").And.Contain("\"outcome\":\"advice\"")
            .And.Contain("\"agreementBasis\":0").And.Contain($"\"{Id(b)}\":null");
    }
}
