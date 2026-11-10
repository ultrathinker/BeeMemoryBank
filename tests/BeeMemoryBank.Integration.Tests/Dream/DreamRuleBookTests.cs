using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Dream;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The "View all rules" dialog of the Dream page (Dream UX round 2, package A): GET /api/dream/rulebook describes every built-in
/// rule and every model task the node really runs, every kind of advice is described somewhere, the page has a label for each,
/// and the numbers in the words are the code's, not a copy.
/// </summary>
public class DreamRuleBookTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _node = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _node.InitializeNodeAsync(password: "dreamRuleBookPw1!");
        _client = _node.CreateClient();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _node.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task EveryDetector_EveryModelTask_AndEveryKindOfAdvice_IsDescribed()
    {
        var book = await _client.GetFromJsonAsync<JsonElement>("/api/dream/rulebook");
        var rules = book.GetProperty("rules").EnumerateArray().ToList();
        var tasks = book.GetProperty("modelTasks").EnumerateArray().ToList();

        using var scope = _node.Services.CreateScope();
        var detectors = scope.ServiceProvider.GetServices<IDreamDetector>().Select(d => d.Name).ToList();
        detectors.Should().HaveCount(7);
        rules.Select(r => r.GetProperty("id").GetString()).Should().BeEquivalentTo(detectors, "the dialog lists exactly the rules the node runs");
        foreach (var rule in rules)
        {
            var id = rule.GetProperty("id").GetString();
            rule.GetProperty("described").GetBoolean().Should().BeTrue($"the rule {id} has no description in DreamRuleBook");
            foreach (var field in new[] { "name", "finds", "example", "score", "apply", "never" })
                rule.GetProperty(field).GetString().Should().NotBeNullOrWhiteSpace($"{id}.{field}");
            rule.GetProperty("how").GetArrayLength().Should().BeGreaterThan(0, id);
            rule.GetProperty("thresholds").GetArrayLength().Should().BeGreaterThan(0, id);
            rule.GetProperty("kinds").GetArrayLength().Should().BeGreaterThan(0, id);
            rule.GetProperty("kindDetails").EnumerateArray().Should().OnlyContain(k => k.GetProperty("applyModes").GetArrayLength() > 0, id);
        }

        var modelTasks = scope.ServiceProvider.GetServices<IDreamModelTask>().ToList();
        tasks.Select(t => t.GetProperty("id").GetString()).Should().BeEquivalentTo(modelTasks.Select(t => t.Name));
        foreach (var task in tasks)
        {
            var id = task.GetProperty("id").GetString();
            task.GetProperty("described").GetBoolean().Should().BeTrue($"the model task {id} has no description in DreamRuleBook");
            task.GetProperty("promptVersion").GetString().Should().Be(modelTasks.Single(t => t.Name == id).PromptVersion);
            foreach (var field in new[] { "name", "asked", "answers" })
                task.GetProperty(field).GetString().Should().NotBeNullOrWhiteSpace($"{id}.{field}");
            task.GetProperty("candidates").GetArrayLength().Should().BeGreaterThan(0, id);
            task.GetProperty("checks").GetArrayLength().Should().BeGreaterThan(0, id);
        }

        var described = rules.Concat(tasks).SelectMany(x => x.GetProperty("kinds").EnumerateArray().Select(k => k.GetString())).ToHashSet();
        described.Should().BeEquivalentTo(DreamWire.NamesOf<DreamAdviceKind>(), "a kind of advice the dialog does not explain is a card nobody can look up");

        book.GetProperty("limits").GetArrayLength().Should().BeGreaterThan(5);
        book.GetProperty("bands").EnumerateArray().Select(b => b.GetProperty("band").GetString()).Should().Equal("high", "medium", "low");
        book.GetProperty("skips").EnumerateArray().Select(s => s.GetString()).Should().Contain(s => s!.Contains("second password"));
    }

    [Fact]
    public void TheNumbers_AreReadFromTheCode_NotCopiedIntoTheText()
    {
        using var scope = _node.Services.CreateScope();
        var detectors = scope.ServiceProvider.GetServices<IDreamDetector>().ToList();
        var tasks = scope.ServiceProvider.GetServices<IDreamModelTask>().ToList();
        var t = DreamThresholds.Default with { MisplacedMinStrongNeighbours = 13, TagVariantsScore = 37, Tier2TopK = 9, UnusedTagsMaxNames = 77 };
        var settings = DreamSettings.Default with { MinScore = 43, MaxAdviceScheduled = 21 };

        var book = DreamRuleBook.Build(detectors, tasks, t, settings);
        var text = JsonSerializer.Serialize(book, DreamJson.Options);

        book.Rules.Single(r => r.Id == "TagVariants").Score.Should().Be("Always 37.");
        book.Rules.Single(r => r.Id == "MisplacedArticle").How.Should().Contain(h => h.Contains("at least 13 strong neighbours"));
        book.Rules.Single(r => r.Id == "UnusedTags").How.Should().Contain(h => h.Contains("at most 77"));
        book.ModelTasks.Single(x => x.Id == DreamNames.PairVerdictTask).Candidates.Should().Contain(c => c.Contains("9 nearest"));
        book.Limits.Should().Contain(new DreamRuleBookFact("Lowest score kept", "43")).And.Contain(new DreamRuleBookFact("Advice in a night run", "21"));
        text.Should().Contain(DreamNames.FolderPromptVersion).And.Contain(PairVerdictTask.MaxOutputTokens.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TheSimilarityRange_IsTheOneTheIndexKeepsItIn_AndTheBandsHaveNoNumberOfTheirOwn()
    {
        // The index really gives the whole range: opposite vectors the lowest value, vectors of one direction the highest (a longer vector does not make it more).
        Guid a = Guid.NewGuid(), opposite = Guid.NewGuid(), same = Guid.NewGuid();
        var index = new DreamNeighbourIndex(new Dictionary<Guid, float[]> { [a] = [1f, 0f], [opposite] = [-3f, 0f], [same] = [2f, 0f] });
        var row = index.NeighboursOf(a, 5);
        row.Single(n => n.Id == opposite).Similarity.Should().Be(DreamNeighbourIndex.MinSimilarity);
        row.Single(n => n.Id == same).Similarity.Should().Be(DreamNeighbourIndex.MaxSimilarity);

        using var scope = _node.Services.CreateScope();
        var book = DreamRuleBook.Build(scope.ServiceProvider.GetServices<IDreamDetector>(), scope.ServiceProvider.GetServices<IDreamModelTask>(),
            DreamThresholds.Default, DreamSettings.Default);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        book.Similarity.Should().Contain(s => s.Contains(
            $"between {DreamNeighbourIndex.MinSimilarity.ToString("0.0##", inv)} (opposite) and {DreamNeighbourIndex.MaxSimilarity.ToString("0.0##", inv)} (the same direction)"));
        book.Similarity.Should().NotContain(s => s.Contains("from 0 to 1"));

        // The bands are the ones of the code, and their words carry no boundary of their own (there was a "90 and up" nobody defined).
        book.Bands.Should().Equal(
            new DreamRuleBookBand(DreamScoreBand.High, DreamThresholds.HighBandMin, 100, book.Bands[0].Meaning),
            new DreamRuleBookBand(DreamScoreBand.Medium, DreamThresholds.MediumBandMin, DreamThresholds.HighBandMin - 1, book.Bands[1].Meaning),
            new DreamRuleBookBand(DreamScoreBand.Low, 0, DreamThresholds.MediumBandMin - 1, book.Bands[2].Meaning));
        book.Bands.Should().OnlyContain(b => !b.Meaning.Any(char.IsDigit));
        DreamScore.BandOf(DreamThresholds.HighBandMin).Should().Be(DreamScoreBand.High);
        DreamScore.BandOf(DreamThresholds.HighBandMin - 1).Should().Be(DreamScoreBand.Medium);
        DreamScore.BandOf(DreamThresholds.MediumBandMin - 1).Should().Be(DreamScoreBand.Low);
    }

    /// <summary>
    /// The book is built twice, once with the defaults and once with every threshold and setting changed to a value no text could hold by accident. A number
    /// that reads the same in both is not read from them: it is typed into the words, or it is a constant of a task. Those are listed below, each with its
    /// reason, so a new number typed by hand (a "90 and up") fails here and has to be explained or taken from the code.
    /// </summary>
    [Fact]
    public void NoNumberOfTheBook_IsTypedByHand_ThatTheCodeHasAValueFor()
    {
        using var scope = _node.Services.CreateScope();
        var detectors = scope.ServiceProvider.GetServices<IDreamDetector>().ToList();
        var tasks = scope.ServiceProvider.GetServices<IDreamModelTask>().ToList();
        var plain = DreamRuleBook.Build(detectors, tasks, DreamThresholds.Default, DreamSettings.Default);
        var changed = DreamRuleBook.Build(detectors, tasks, Distinct(new DreamThresholds()), Distinct(new DreamSettings()));
        var before = Strings(plain);
        var after = Strings(changed);
        after.Count.Should().Be(before.Count, "the two books have the same shape");

        var sameInBoth = new Dictionary<string, string>();
        for (var i = 0; i < before.Count; i++)
        {
            var x = Numbers(before[i]);
            var y = Numbers(after[i]);
            y.Count.Should().Be(x.Count, $"the same numbers in the same places: \"{before[i]}\" / \"{after[i]}\"");
            for (var j = 0; j < x.Count; j++)
                if (x[j] == y[j]) sameInBoth.TryAdd(x[j], before[i]);
        }

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string Num(long n) => n.ToString("#,0", inv);
        var constants = new[]
        {
            // the constants of the model tasks, which the book reads from the tasks themselves
            Num(PairVerdictTask.MaxOutputTokens), Num(FolderReviewTask.MaxOutputTokens), Num(TreeReviewTask.MaxOutputTokens),
            Num(FolderReviewTask.RenameScoreCap), Num(FolderReviewTask.MinShownNotes), Num(FolderReviewTask.MinSubfolderNotes),
            Num(FolderReviewTask.MaxSplitNotes), Num(TreeReviewTask.MinFolders),
            // the bounds of a similarity
            Math.Abs(DreamNeighbourIndex.MinSimilarity).ToString("0.0##", inv), Math.Abs(DreamNeighbourIndex.MaxSimilarity).ToString("0.0##", inv),
        };
        var structural = new Dictionary<string, string>
        {
            ["0"] = "none: 0 notes in an empty folder, a score from 0 to 100, no excluded folder",
            ["1"] = "none, 1 to the most; Tier 1; the first note; a prompt version name; one run at a time; one more try",
            ["2"] = "the smallest group of tags that is a variant; Tier 2",
            ["3"] = "Tier 3 (the names of the three tiers of candidates)",
            ["100"] = "the top of a score, and the 100 in round(100 x similarity)",
            ["30"] = "days in the trash: a literal of CleanupService, checked below",
            ["256"] = "SHA-256, the name of the hash",
        };
        sameInBoth.Keys.Except(constants).Except(structural.Keys).Should().BeEmpty(
            "these numbers are typed into the words of the rule book: " + string.Join(" | ", sameInBoth.Where(p => !constants.Contains(p.Key) && !structural.ContainsKey(p.Key)).Select(p => $"{p.Key} in \"{p.Value}\"")));

        // "30 days" is the one number here that the code has no name for: the cleanup of the node purges a deleted note after this long.
        File.ReadAllText(Path.Combine(DreamPageScripts.RepoRoot, "libs", "BeeMemoryBank.Sync", "CleanupService.cs")).Should().Contain("now.AddDays(-30)");
    }

    private static T Distinct<T>(T value) where T : class
    {
        var seed = 0;
        foreach (var p in typeof(T).GetProperties().Where(p => p.CanWrite && p.GetIndexParameters().Length == 0))
        {
            seed++;
            if (p.PropertyType == typeof(int)) p.SetValue(value, 7000 + seed * 13);
            else if (p.PropertyType == typeof(long)) p.SetValue(value, 9_000_000L + seed * 17);
            else if (p.PropertyType == typeof(double)) p.SetValue(value, 0.3 + seed * 0.0137);
            else if (p.PropertyType == typeof(TimeSpan)) p.SetValue(value, TimeSpan.FromDays(40 + seed));
            else if (p.Name == "ScheduleStart") p.SetValue(value, "17:45");
            else if (p.Name == "ExcludedFolderIds") p.SetValue(value, new List<Guid> { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() });
        }
        return value;
    }

    private static List<string> Strings(object book)
    {
        var found = new List<string>();
        void Walk(System.Text.Json.Nodes.JsonNode? node)
        {
            switch (node)
            {
                case System.Text.Json.Nodes.JsonValue v when v.TryGetValue<string>(out var s): found.Add(s); break;
                case System.Text.Json.Nodes.JsonObject o: foreach (var item in o) Walk(item.Value); break;
                case System.Text.Json.Nodes.JsonArray a: foreach (var item in a) Walk(item); break;
            }
        }
        Walk(JsonSerializer.SerializeToNode(book, DreamJson.Options));
        return found;
    }

    private static List<string> Numbers(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, @"\d+(?:,\d{3})*(?:\.\d+)?").Select(m => m.Value).ToList();

    [Fact]
    public void ThePage_HasALabelForEveryKind_AndTheDialogThatShowsTheBook()
    {
        var model = File.ReadAllText(DreamPageScripts.ModelJs);
        foreach (var kind in DreamWire.NamesOf<DreamAdviceKind>())
            model.Should().Contain($"{kind}: {{ label: '", $"the kind filter and the dialog name {kind} the same way");

        File.ReadAllText(DreamPageScripts.Markup).Should().Contain("id=\"dlg-dream-rules\"").And.Contain("View all rules");
        File.ReadAllText(DreamPageScripts.PageJs).Should().Contain("'/rulebook'");
    }
}
