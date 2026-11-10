using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BeeMemoryBank.Api.Dream;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The shared contracts of the Dream packages (plan 3.4, 3.5, 3.8): wire names, the advice JSON shape, the fingerprint and keys, the settings
/// defaults and ranges, the score formulas and the run budget. Pure logic, no node.
/// </summary>
public class DreamContractsTests
{
    /// <summary>Every enumeration of Dream/Contracts (DreamEnums.cs).</summary>
    private static readonly Type[] Enums =
    [
        typeof(DreamRunTrigger), typeof(DreamRunMode), typeof(DreamRunListMode), typeof(DreamRunState), typeof(DreamRunOutcome), typeof(DreamPhase),
        typeof(DreamAdviceKind), typeof(DreamAdviceSource), typeof(DreamApplyMode), typeof(DreamAdviceStatus), typeof(DreamAdviceSort),
        typeof(DreamScoreBand), typeof(DreamActionType), typeof(DreamPreconditionType), typeof(DreamEvidenceType), typeof(DreamObjectRole),
        typeof(DreamObjectType), typeof(DreamRuleScope), typeof(DreamEventKind), typeof(DreamExaminedVerdict), typeof(DreamPromptTarget),
    ];

    /// <summary>The API's own serializer options (ConfigureHttpJsonOptions): web defaults plus a JsonStringEnumConverter without a policy.</summary>
    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public void EveryEnumValue_HasAUniqueSnakeCaseWireName_ThatRoundTrips()
    {
        foreach (var type in Enums)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in Enum.GetValues(type))
            {
                var name = (string)typeof(DreamWire).GetMethod(nameof(DreamWire.ToWire))!.MakeGenericMethod(type).Invoke(null, [value])!;
                name.Should().MatchRegex("^[a-z][a-z0-9_]*$", $"{type.Name}.{value}");
                names.Add(name).Should().BeTrue($"{type.Name} has '{name}' once");

                var args = new object?[] { name, null };
                ((bool)typeof(DreamWire).GetMethod(nameof(DreamWire.TryParse))!.MakeGenericMethod(type).Invoke(null, args)!).Should().BeTrue();
                args[1].Should().Be(value);

                // The API writes the same name through its global converter.
                JsonSerializer.Serialize(value, type, ApiJson).Should().Be($"\"{name}\"");
                JsonSerializer.Deserialize($"\"{name}\"", type, ApiJson).Should().Be(value);
            }
        }
    }

    [Fact]
    public void WireNames_AreThePlansWords()
    {
        DreamWire.ToWire(DreamAdviceStatus.InChat).Should().Be("in_chat");
        DreamWire.ToWire(DreamAdviceKind.DuplicateExact).Should().Be("duplicate_exact");
        DreamWire.ToWire(DreamRunOutcome.HeavyOperation).Should().Be("heavy_operation");
        DreamWire.ToWire(DreamActionType.DeleteTags).Should().Be("delete_tags");
        DreamWire.ToWire(DreamRunTrigger.Schedule).Should().Be("schedule");
        DreamWire.NamesOf<DreamAdviceKind>().Should().HaveCount(15);
        DreamWire.NamesOf<DreamRunOutcome>().Should().BeEquivalentTo(
            ["completed", "advice_limit", "time_limit", "budget_limit", "stopped", "locked", "heavy_operation", "provider_error", "interrupted", "failed", "skipped"]);
        DreamWire.TryParse<DreamAdviceStatus>("InChat", out _).Should().BeFalse("only the wire name parses");
        DreamWire.TryParse<DreamAdviceStatus>(null, out _).Should().BeFalse();
    }

    [Fact]
    public void TheAdvicePayload_HasTheFlatShapeOfPlan34_AndRoundTrips()
    {
        var a = Guid.NewGuid();
        var payload = new DreamAdvicePayload
        {
            Fingerprint = DreamFingerprint.Of(DreamAdviceKind.MoveArticle, DreamAction.MoveArticle(a, "/Kids/Puzzles")),
            Title = "Move one note",
            Summary = "Its neighbours live elsewhere.",
            Evidence = [new DreamEvidence(DreamEvidenceType.Neighbours, "9 of 12 strong neighbours are in /Kids/Puzzles", 0.75)],
            Objects = [new DreamAdviceObject { Role = DreamObjectRole.Subject, Type = DreamObjectType.Article, Id = a, Title = "T", LamportTs = 7 }],
            Action = DreamAction.MoveArticle(a, "/Kids/Puzzles"),
            Preconditions = [DreamPrecondition.ArticleUnchanged(a, 7)],
        };

        var json = DreamJson.Serialize(payload);
        var node = JsonNode.Parse(json)!.AsObject();
        node["v"]!.GetValue<int>().Should().Be(1);
        node["action"]!["type"]!.GetValue<string>().Should().Be("move_article");
        node["action"]!["articleId"]!.GetValue<string>().Should().Be(a.ToString("D"));
        node["action"]!["toPath"]!.GetValue<string>().Should().Be("/Kids/Puzzles");
        node["objects"]![0]!["role"]!.GetValue<string>().Should().Be("subject");
        node["preconditions"]![0]!["type"]!.GetValue<string>().Should().Be("article_unchanged");
        node["evidence"]![0]!["type"]!.GetValue<string>().Should().Be("neighbours");
        node.Should().ContainKey("familyKey").And.ContainKey("references").And.ContainKey("model").And.ContainKey("applied").And.ContainKey("lastError");

        DreamJson.Deserialize<DreamAdvicePayload>(json).Should().BeEquivalentTo(payload);
        payload.ArticleIds().Should().Equal([a]);
    }

    [Fact]
    public void TheFingerprint_Is32Hex_IgnoresOrderAndCase_AndTellsKindsApart()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var fp = DreamFingerprint.Of(DreamAdviceKind.DuplicateExact, DreamAction.TrashArticle(a, b));
        fp.Should().MatchRegex("^[0-9a-f]{32}$");
        DreamFingerprint.Of(DreamAdviceKind.DuplicateExact, DreamAction.TrashArticle(b, a)).Should().Be(fp, "the sorted ids, not their roles");
        DreamFingerprint.Of(DreamAdviceKind.NearDuplicate, DreamAction.ReviewPair(a, b))
            .Should().NotBe(DreamFingerprint.Of(DreamAdviceKind.Overlap, DreamAction.ReviewPair(a, b)));

        DreamFingerprint.Of(DreamAdviceKind.MergeTags, DreamAction.MergeTags(["How To", "howto"], "how-to"))
            .Should().Be(DreamFingerprint.Of(DreamAdviceKind.MergeTags, DreamAction.MergeTags(["HOWTO", " how to "], "How-To")));
        DreamFingerprint.Of(DreamAdviceKind.DeleteEmptyFolder, DreamAction.DeleteFolder("/Old/Empty/", a))
            .Should().Be(DreamFingerprint.Of(DreamAdviceKind.DeleteEmptyFolder, DreamAction.DeleteFolder("/old/empty", a)));
    }

    [Fact]
    public void CandidateKeys_ContentMarkers_AndFamilyKeys_AreStable()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var key = DreamFingerprint.CandidateKey(DreamNames.PairVerdictTask, [a, b]);
        key.Should().MatchRegex("^[0-9a-f]{64}$");
        DreamFingerprint.CandidateKey(DreamNames.PairVerdictTask, [b, a]).Should().Be(key);
        DreamFingerprint.CandidateKey(DreamNames.FolderReviewTask, [a, b]).Should().NotBe(key);

        var marker = DreamFingerprint.ContentMarker([(a, 5), (b, 9)]);
        DreamFingerprint.ContentMarker([(b, 9), (a, 5)]).Should().Be(marker);
        DreamFingerprint.ContentMarker([(a, 6), (b, 9)]).Should().NotBe(marker, "an edit makes a new look worth paying for");

        DreamNames.PairFamilyKey(a, b).Should().Be(DreamNames.PairFamilyKey(b, a)).And.StartWith("pair|");
    }

    [Fact]
    public void TheSettingsDefaults_AreThePlans_AndTheRangesHold()
    {
        var d = DreamSettings.Default;
        d.Validate().Should().BeNull();
        (d.ScheduleEnabled, d.ScheduleStart, d.ScheduleWindowMinutes, d.MaxAdviceScheduled, d.MaxAdviceManual, d.MaxOpenAdvice)
            .Should().Be((false, "03:00", 120, 10, 50, 100));
        (d.MinScore, d.ManualMaxMinutes, d.MaxModelCalls, d.MaxTokens, d.UseModel, d.AnalysisModelId)
            .Should().Be((40, 30, 200, 1_500_000L, false, (Guid?)null));
        d.ExcludedFolderIds.Should().BeEmpty();

        (d with { MaxAdviceScheduled = 0 }).Validate().Should().Contain("maxAdviceScheduled");
        (d with { MaxAdviceScheduled = 500 }).Validate().Should().BeNull();
        (d with { MaxAdviceManual = 1001 }).Validate().Should().Contain("maxAdviceManual");
        (d with { MaxAdviceManual = 0 }).Validate().Should().BeNull("0 is no limit");
        (d with { MinScore = 101 }).Validate().Should().Contain("minScore");
        (d with { ScheduleStart = "25:00" }).Validate().Should().Contain("scheduleStart");
        (d with { ScheduleStart = "3:00" }).Validate().Should().Contain("scheduleStart", "HH:mm, two digits");
    }

    [Fact]
    public void ScoresAndBands_FollowThePlansFormulas()
    {
        var t = DreamThresholds.Default;
        DreamScore.BandOf(80).Should().Be(DreamScoreBand.High);
        DreamScore.BandOf(79).Should().Be(DreamScoreBand.Medium);
        DreamScore.BandOf(60).Should().Be(DreamScoreBand.Medium);
        DreamScore.BandOf(59).Should().Be(DreamScoreBand.Low);
        DreamScore.NearDuplicate(0.96, t).Should().Be(71);
        DreamScore.NearDuplicate(0.99, t).Should().Be(74);
        DreamScore.NearDuplicate(1.0, t).Should().Be(75);
        DreamScore.NearDuplicate(0.5, t).Should().Be(50);
        DreamScore.Misplaced(0.6, t).Should().Be(70);
        DreamScore.Misplaced(1.0, t).Should().Be(90);
        t.TestFolderNames.Should().Contain(["test", "tests", "tmp", "temp"]).And.HaveCount(6);
        t.TestFolderNames.Should().Contain("\u0442\u0435\u0441\u0442\u044B");
    }

    [Fact]
    public void TheRunBudget_AdmitsACallOnlyInsideBothLimits()
    {
        var budget = new DreamRunBudget(maxModelCalls: 2, maxTokens: 1000);
        budget.TryReserve(600).Should().BeTrue();
        budget.TryReserve(1001).Should().BeFalse();
        budget.Record(500, 100);
        budget.TokensUsed.Should().Be(600);
        budget.TryReserve(400).Should().BeTrue();
        budget.TryReserve(401).Should().BeFalse();
        budget.Record(10, 10);
        budget.ModelCalls.Should().Be(2);
        budget.Exhausted.Should().BeTrue();
        budget.TryReserve(1).Should().BeFalse("the call limit is reached");
        DreamRunBudget.EstimateTokens(new string('s', 30), new string('u', 270), 300).Should().Be(400);
    }

    [Fact]
    public void ArticleIds_ReachAReferenceSampleNamedNowhereElse()
    {
        var a = Guid.NewGuid();
        var payload = new DreamAdvicePayload { References = new DreamReferences(true, 1, [new DreamReferenceSample(a, "title")]) };

        payload.ArticleIds().Should().Equal([a]);
    }

    [Fact]
    public void Fingerprint_NotesAboutDifferentThings_AreDifferentAdvice()
    {
        var kind = DreamAdviceKind.StructureNote;

        DreamFingerprint.Of(kind, DreamAction.Note(), [DreamAdviceObject.ForFolder(null, "/A")])
            .Should().NotBe(DreamFingerprint.Of(kind, DreamAction.Note(), [DreamAdviceObject.ForFolder(null, "/B")]));
        DreamFingerprint.Of(kind, DreamAction.Note(), [], "first note")
            .Should().NotBe(DreamFingerprint.Of(kind, DreamAction.Note(), [], "second note"));
    }
}
