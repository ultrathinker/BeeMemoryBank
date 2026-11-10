using System.Globalization;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// What the "View all rules" dialog of the Dream page says (GET /api/dream/rulebook): every built-in rule and every model
/// task, in plain words, with the numbers they work with. The words are written here from what each class does; every
/// number comes from the code at request time (<see cref="DreamThresholds"/>, the settings, the tasks' own constants, the
/// kind table of <see cref="DreamDraftChecks"/>), so the dialog cannot drift from the run. A detector or a task that has no
/// description here is still listed, marked as not described, and DreamRuleBookTests fails on it.
/// </summary>
internal static class DreamRuleBook
{
    public static DreamRuleBookResponse Build(IEnumerable<IDreamDetector> detectors, IEnumerable<IDreamModelTask> tasks,
        DreamThresholds t, DreamSettings settings)
    {
        var rules = detectors.Select(d => Rules.TryGetValue(d.Name, out var describe)
                ? describe(t) with { Id = d.Name }
                : Undescribed(d.Name))
            .Select(r => r with { KindDetails = r.Kinds.Select(DreamDraftChecks.KindOf).ToList() })
            .ToList();
        var modelTasks = tasks.Select(task => Tasks.TryGetValue(task.Name, out var describe)
                ? describe(t) with { Id = task.Name, PromptVersion = task.PromptVersion }
                : new DreamRuleBookTask { Id = task.Name, Name = task.Name, PromptVersion = task.PromptVersion, Described = false })
            .Select(x => x with { KindDetails = x.Kinds.Select(DreamDraftChecks.KindOf).ToList() })
            .ToList();

        return new DreamRuleBookResponse
        {
            Intro =
            [
                "A run looks at the notes in two ways, each of which can be switched off on the start form: the built-in rules, which work here on this " +
                "node and never send anything anywhere, and the AI model, which sends the text of notes to the model chosen in the settings.",
                "Neither of them changes anything. Each finding is an advice with a score; a change happens only when you press a button on its card.",
                "The night run always uses the built-in rules, and the AI model too when it is turned on in the settings.",
            ],
            Similarity =
            [
                "Several rules and the pair verdict compare notes by meaning. Every note gets a vector here on this node (the embedding model of the " +
                $"search); the similarity of two notes is the cosine of their vectors, kept between {Dec(DreamNeighbourIndex.MinSimilarity)} (opposite) and " +
                $"{Dec(DreamNeighbourIndex.MaxSimilarity)} (the same direction). Notes about one subject lie near the top.",
                "Similarities are squeezed together near the top of the scale, so a fixed number says little on its own. Z says how unusual a " +
                "similarity is for this note: how many standard deviations it lies above the note's average similarity to every other note.",
                $"When more than {Percent(t.MaxShareWithoutVector)} of the notes have no vector yet, everything based on similarity is skipped in that run " +
                "(the embedding step of a run prepares missing vectors first).",
            ],
            Skips =
            [
                "Notes locked with a second password: never read, never sent, never named, and checked again right before any text is read.",
                "Notes subscribed from other nodes, and their folders.",
                $"Folders you excluded in the Dream settings, and everything below them ({Count(settings.ExcludedFolderIds.Count, "folder")} excluded now).",
                "Notes and folders in the trash.",
                "A note that changes during the run (locked, moved into an excluded folder, deleted) is left out from that moment.",
                "Advice that a dismiss rule covers is not offered again; the run counts it as left out.",
                "Advice that is already open is not made a second time.",
                $"Advice scoring below the minimum score of the settings ({settings.MinScore}) is not kept.",
            ],
            Rules = rules,
            ModelTasks = modelTasks,
            Limits = Limits(t, settings),
            Bands =
            [
                new DreamRuleBookBand(DreamScoreBand.High, DreamThresholds.HighBandMin, 100,
                    "Probably worth doing: the benefit is plain."),
                new DreamRuleBookBand(DreamScoreBand.Medium, DreamThresholds.MediumBandMin, DreamThresholds.HighBandMin - 1,
                    "Worth a look; you decide."),
                new DreamRuleBookBand(DreamScoreBand.Low, 0, DreamThresholds.MediumBandMin - 1,
                    "Debatable, or a weak signal."),
            ],
        };
    }

    // ── The built-in rules ──────────────────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, Func<DreamThresholds, DreamRuleBookRule>> Rules = new(StringComparer.Ordinal)
    {
        ["TagVariants"] = t => new DreamRuleBookRule
        {
            Name = "Tag variants",
            Kinds = [DreamAdviceKind.MergeTags],
            Finds = "Tags that are one tag spelled in several ways.",
            Example = "how-to, howto and How To on different notes.",
            How =
            [
                $"Every tag name is lower-cased, and white space and the characters {RemovedChars(t)} are taken out.",
                "Tags whose names are then equal form a group; every group of two or more tags is one advice.",
                "The tag with the most notes is the one to keep (on a tie, the name that sorts first); the others are merged into it.",
            ],
            Thresholds = [Fact("Characters ignored besides white space", RemovedChars(t)), Fact("Tags in a group", "2 or more")],
            Score = $"Always {t.TagVariantsScore}.",
            Apply = "Merges each variant into the tag to keep, through the ordinary tag service: every note that had a variant gets the kept tag, " +
                    "and the variants are deleted. A variant that is gone by then is left alone and said so.",
            Never = "Never touches the text of a note, never renames the kept tag, never merges tags whose names differ in letters.",
            Skips = ["A tag carried only by notes Dream does not read is not listed at all."],
        },
        ["UnusedTags"] = t => new DreamRuleBookRule
        {
            Name = "Unused tags",
            Kinds = [DreamAdviceKind.DeleteUnusedTags],
            Finds = "Tags that no note uses any more.",
            Example = "A tag 'old-draft' that stayed behind when its last note was deleted.",
            How =
            [
                "A tag with no active note is unused; a note in the trash does not count as using it.",
                $"All unused tags go into one advice per run, naming at most {t.UnusedTagsMaxNames} of them (the first by name); the rest come in a later run.",
            ],
            Thresholds = [Fact("Tags named in one advice", Num(t.UnusedTagsMaxNames))],
            Score = $"Always {t.UnusedTagsScore}.",
            Apply = "Deletes each named tag that is still unused at that moment, through the tag service.",
            Never = "Never deletes a tag that a note was given after the advice was made: it is checked again right before each delete.",
            Skips = ["A tag carried only by notes Dream does not read is not unused: it is not listed."],
        },
        ["EmptyFolder"] = t => new DreamRuleBookRule
        {
            Name = "Empty folders",
            Kinds = [DreamAdviceKind.DeleteEmptyFolder],
            Finds = "Folders with no notes and no subfolders.",
            Example = "/Projects/Old plan, emptied when its notes moved on.",
            How =
            [
                "A folder is empty when no active note is in it or below it and no folder has it as its parent.",
                "Notes Dream does not read still count: a folder that holds only locked or excluded notes is not empty.",
                $"A folder named like a test folder ({TestNames(t)}) scores higher.",
            ],
            Thresholds = [Fact("Notes in it and below it", "0"), Fact("Subfolders", "0")],
            Score = $"{t.EmptyFolderScore}; {t.EmptyTestFolderScore} when the name looks like a test folder.",
            Apply = "Deletes the folder through the folder service, only when it is still the same folder and still empty at that moment. " +
                    "It can be restored from the trash.",
            Never = "Never deletes a folder that holds a note or a subfolder, the root, or a system folder.",
            Skips = ["The root and system folders."],
        },
        ["TestLeftovers"] = t => new DreamRuleBookRule
        {
            Name = "Test leftovers",
            Kinds = [DreamAdviceKind.TestLeftovers],
            Finds = $"Small folders named like a test folder near the top of the tree (the first {t.TestLeftoversMaxDepth} levels).",
            Example = "/test with three scratch notes made while trying the app.",
            How =
            [
                $"The folder's name, with leading underscores dropped, is compared without case with: {TestNames(t)}.",
                $"It must be on level 1 to {t.TestLeftoversMaxDepth} and hold 1 to {t.TestLeftoversMaxArticles} notes in it and below it.",
                "An empty test folder is left to the empty-folder rule.",
            ],
            Thresholds =
            [
                Fact("Folder names", TestNames(t)), Fact("Deepest level", Num(t.TestLeftoversMaxDepth)),
                Fact("Notes in it and below it", $"1 to {t.TestLeftoversMaxArticles}"),
            ],
            Score = $"Always {t.TestLeftoversScore}.",
            Apply = "There is no Apply button: the advice asks for a review, with a prompt for your own agent (or for you).",
            Never = "Never deletes or moves anything by itself.",
            Skips = ["Folders deeper than the levels above, and folders with more notes than that (those are real folders, whatever the name)."],
        },
        ["ExactDuplicate"] = t => new DreamRuleBookRule
        {
            Name = "Exact duplicates",
            Kinds = [DreamAdviceKind.DuplicateExact],
            Finds = "Two notes with the same text.",
            Example = "A note imported twice, once into /Imported.",
            How =
            [
                $"A pair is looked at when the titles are equal (lower-cased, white space folded) or when their similarity is at least {Dec(t.ExactDuplicateMinSimilarity)}.",
                "It is an exact duplicate only when the two texts, lower-cased and with white space folded, have the same SHA-256 hash.",
                "Of the two, the copy that fewer other notes link to (by its id) is the one to trash; at equal links, the later one.",
            ],
            Thresholds = [Fact("Similarity that makes a pair worth a look", Dec(t.ExactDuplicateMinSimilarity)), Fact("Texts", "identical after folding")],
            Score = $"Always {t.ExactDuplicateScore}.",
            Apply = "When no other note names the copy, Apply moves it to the trash (restorable for 30 days) and keeps the other; the links are " +
                    "looked for again right before. When other notes name the copy, or the search that counts them is unavailable, there is no Apply " +
                    "button: the advice goes to the AI chat or to a prompt, so the links can be fixed first.",
            Never = "Never edits or merges the text; the note that stays is not changed.",
            Skips = ["Notes whose text cannot be read."],
        },
        ["NearDuplicate"] = t => new DreamRuleBookRule
        {
            Name = "Near duplicates",
            Kinds = [DreamAdviceKind.NearDuplicate],
            RunsWhen = "Only in a run without the AI model: with the model, the pair verdict judges these pairs instead, and its verdict replaces " +
                       "an open near-duplicate advice about the same pair.",
            Finds = "Two notes that say nearly the same, but not word for word.",
            Example = "The same recipe written down twice, once with an extra step.",
            How =
            [
                $"A pair is a candidate when the similarity is at least {Dec(t.DuplicateCandidateMinSimilarity)} or its Z is at least {Dec(t.DuplicateCandidateMinZ)}.",
                "Exact duplicates are left to the exact-duplicate rule.",
            ],
            Thresholds =
            [
                Fact("Similarity", $"{Dec(t.DuplicateCandidateMinSimilarity)} or more"), Fact("or Z", $"{Dec(t.DuplicateCandidateMinZ)} or more"),
            ],
            Score = $"round(100 x similarity) - {t.NearDuplicateScoreOffset}, kept between {t.NearDuplicateScoreMin} and {t.NearDuplicateScoreMax}.",
            Apply = "There is no Apply button: read both notes and keep one if they say the same, in the AI chat (Open in AI chat) or with a prompt.",
            Never = "Never merges or deletes anything by itself.",
            Skips = ["Pairs whose texts are identical (those are exact duplicates)."],
        },
        ["MisplacedArticle"] = t => new DreamRuleBookRule
        {
            Name = "Notes in the wrong folder",
            Kinds = [DreamAdviceKind.MoveArticle],
            Finds = "A note whose closest notes by meaning are almost all in another folder.",
            Example = "A puzzle for children in /Inbox while twelve like it are in /Kids/Puzzles.",
            How =
            [
                $"Its strong neighbours are the {t.StrongNeighbourTopK} nearest notes whose Z is at least {Dec(t.StrongNeighbourMinZ)}.",
                $"It needs at least {t.MisplacedMinStrongNeighbours} strong neighbours; at most {Percent(t.MisplacedMaxOwnShare)} of them may be in its own folder.",
                $"At least {Percent(t.MisplacedMinShare)} of them must be directly in one other folder, G; G is not a parent of its folder and not a system folder.",
                $"The note must not have changed in the last {Count(t.MisplacedQuietDays, "day")}: a note being worked on is left alone.",
            ],
            Thresholds =
            [
                Fact("Nearest notes looked at", Num(t.StrongNeighbourTopK)), Fact("Z of a strong neighbour", $"{Dec(t.StrongNeighbourMinZ)} or more"),
                Fact("Strong neighbours needed", Num(t.MisplacedMinStrongNeighbours)), Fact("Share in folder G", $"{Percent(t.MisplacedMinShare)} or more"),
                Fact("Share in its own folder", $"{Percent(t.MisplacedMaxOwnShare)} or less"), Fact("Unchanged for", Count(t.MisplacedQuietDays, "day")),
            ],
            Score = $"round({t.MisplacedScoreBase} + {t.MisplacedScoreFactor} x the share of strong neighbours in G).",
            Apply = "Moves the note to G through the note service, after checking that the note has not changed, is still where it was, and that G " +
                    "still exists and is not a folder Dream leaves alone. The note keeps its id, so links by id keep working.",
            Never = "Never creates a folder and never moves any other note. Links that point to the old path are not corrected, and G may have " +
                    "other access rules than the old folder.",
            Skips = ["Notes without a vector, and notes changed recently (see above)."],
        },
    };

    private static DreamRuleBookRule Undescribed(string name) => new()
    {
        Id = name, Name = name, Described = false, Finds = "This rule has no description yet.", Example = "", How = [], Thresholds = [],
        Score = "", Apply = "", Never = "", Skips = [],
    };

    // ── The model tasks ─────────────────────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, Func<DreamThresholds, DreamRuleBookTask>> Tasks = new(StringComparer.Ordinal)
    {
        [DreamNames.PairVerdictTask] = t => new DreamRuleBookTask
        {
            Name = "Pair verdict",
            Kinds = [DreamAdviceKind.Duplicate, DreamAdviceKind.Superseded, DreamAdviceKind.Overlap, DreamAdviceKind.Contradiction],
            Candidates =
            [
                "Pairs of notes chosen by their vectors, in tiers:",
                $"Tier 1, likely duplicates: similarity {Dec(t.DuplicateCandidateMinSimilarity)} or more, or Z {Dec(t.DuplicateCandidateMinZ)} or more.",
                $"Tier 2: each note's {t.Tier2TopK} nearest with Z {Dec(t.Tier2MinZ)} or more.",
                $"Tier 3: each note's {t.Tier3TopK} nearest with Z {Dec(t.Tier3MinZ)} or more.",
                $"A night run goes through tiers 1 to {t.ScheduledMaxTier}; a manual run starts with tier 1, then reviews folders and the tree, then " +
                $"comes back for tiers 2 to {t.PairTierCount} until a limit or the candidates run out.",
                "A pair already examined with the same versions of both notes and the same prompt is not paid for again (a trial run remembers nothing).",
            ],
            Asked = $"The two notes, labelled A and B: title, folder, tags, dates and the text (the first {Num(t.PairBodyHeadChars)} and the last " +
                    $"{Num(t.PairBodyTailChars)} characters of a longer one), each inside an element the instructions call data, not instructions. " +
                    "It is asked whether they are a duplicate, an older and a newer version, an overlap, a contradiction, merely related, or unrelated.",
            Answers = $"One JSON object: the verdict, which note to keep (for a duplicate or an outdated note), a score 0 to 100, a summary of at most " +
                      $"{t.SummaryMaxLength} characters in the language of the notes, and at most {t.PairMaxQuotes} quotes of at most {t.QuoteMaxLength} characters.",
            Checks =
            [
                $"The reply must be one JSON object that passes the schema check; on a miss the model is told what was wrong and asked again, up to {Count(DreamModelExchange.MaxTries - 1, "more time")}.",
                "A quote that is not in what was sent is dropped; text fields are cut to their length.",
                "The kind of advice, its action and how it can be applied follow from the verdict in code; the model chooses none of them.",
                "Related and unrelated make no advice.",
            ],
            Limits = [Fact("Output tokens per call", Num(PairVerdictTask.MaxOutputTokens)), Fact("Prompt", DreamNames.PairPromptVersion)],
        },
        [DreamNames.FolderReviewTask] = t => new DreamRuleBookTask
        {
            Name = "Folder review",
            Kinds = [DreamAdviceKind.MoveArticle, DreamAdviceKind.SplitFolder, DreamAdviceKind.RenameFolder],
            Candidates =
            [
                $"Folders with at least {t.FolderReviewMinArticles} notes directly in them, each looked at once per run.",
                "The folders where the most notes look out of place by their vectors come first, then the larger ones (a trial run with a sample uses its own order).",
                $"A folder with fewer than {FolderReviewTask.MinShownNotes} readable notes is not sent.",
            ],
            Asked = $"The folder's path, its subfolders, parent and siblings, and the folders that hold notes on similar subjects (at most " +
                    $"{t.FolderReviewMaxNearbyFolders} folders), and up to {t.FolderReviewMaxArticles} notes, the most recently changed first: a label, the " +
                    $"title, up to {t.FolderReviewMaxTagsPerArticle} tags, the dates and the first {t.FolderReviewSnippetChars} characters. It is asked to judge " +
                    "a note against the reason the folder exists, not against its topic.",
            Answers = $"Up to {t.FolderReviewMaxSuggestions} suggestions: move a note to a listed folder, put at least {FolderReviewTask.MinSubfolderNotes} " +
                      "notes into a new subfolder, or rename the folder, each with a score and a summary.",
            Checks =
            [
                "Notes are named by their label only, so an id the model makes up has nowhere to go; a destination must be a folder the input listed.",
                "A suggestion that does not fit (an unknown note, a path that was not listed, a name that is taken) is dropped on its own.",
                $"A rename never scores above {FolderReviewTask.RenameScoreCap}; a new subfolder takes at most {FolderReviewTask.MaxSplitNotes} notes.",
            ],
            Limits = [Fact("Output tokens per call", Num(FolderReviewTask.MaxOutputTokens)), Fact("Prompt", DreamNames.FolderPromptVersion)],
        },
        [DreamNames.TreeReviewTask] = t => new DreamRuleBookTask
        {
            Name = "Tree review",
            Kinds = [DreamAdviceKind.MergeFolders, DreamAdviceKind.RenameFolder, DreamAdviceKind.StructureNote],
            Candidates =
            [
                $"The whole folder tree, once: only when it has at least {TreeReviewTask.MinFolders} folders and has changed since the last look, and in a " +
                $"normal run not again within {Count(t.TreeReviewMinDays, "day")}.",
            ],
            Asked = $"A map of folder paths and note counts, no note text: at most {t.TreeReviewMaxLines} folders, {t.TreeReviewMaxDepth} levels deep, " +
                    "without system and excluded folders.",
            Answers = $"Up to {t.TreeReviewMaxSuggestions} suggestions: merge two folders that are plainly the same, rename a folder with a placeholder name, " +
                      "or a note about a structure that is hard to use.",
            Checks =
            [
                "Every path in a suggestion must be on the map; one that is not is dropped on its own.",
                $"A merge or a rename never scores above {FolderReviewTask.RenameScoreCap}: it changes a path other tools may write to.",
            ],
            Limits = [Fact("Output tokens per call", Num(TreeReviewTask.MaxOutputTokens)), Fact("Prompt", DreamNames.TreePromptVersion)],
        },
    };

    // ── Limits ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<DreamRuleBookFact> Limits(DreamThresholds t, DreamSettings s) =>
    [
        Fact("Advice in a night run", Num(s.MaxAdviceScheduled)),
        Fact("Advice in a manual run", s.MaxAdviceManual > 0 ? Num(s.MaxAdviceManual) : "no limit"),
        Fact("Share of the advice the rules may take when the AI model also runs", Percent(t.DetectorShareOfAdviceWithModel)),
        Fact("Time of a manual run", s.ManualMaxMinutes > 0 ? Count(s.ManualMaxMinutes, "minute") : "no limit"),
        Fact("Time of a night run", $"its window: {Count(s.ScheduleWindowMinutes, "minute")} from {s.ScheduleStart}"),
        Fact("Model calls per run", Num(s.MaxModelCalls)),
        Fact("Tokens per run, an estimate (in and out)", Num(s.MaxTokens)),
        Fact("Lowest score kept", Num(s.MinScore)),
        Fact("Open advice at which the night run is skipped", Num(s.MaxOpenAdvice)),
        Fact("Log of a run kept for", Count((int)t.EventRetention.TotalDays, "day")),
        Fact("Runs at a time", "1; a run stops when the vault is locked or a heavy operation (a key change, a restore) begins"),
    ];

    // ── Formatting ──────────────────────────────────────────────────────────────────────────────────────────────

    private static DreamRuleBookFact Fact(string name, string value) => new(name, value);

    private static string Num(long n) => n.ToString("#,0", CultureInfo.InvariantCulture);

    private static string Dec(double x) => x.ToString("0.0##", CultureInfo.InvariantCulture);

    private static string Percent(double share) => (share * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Count(int n, string noun) => $"{Num(n)} {noun}{(n == 1 ? "" : "s")}";

    private static string RemovedChars(DreamThresholds t)
    {
        var shown = t.TagNormalizeRemovedChars.Where(c => !char.IsWhiteSpace(c)).Select(c => c.ToString()).ToList();
        return shown.Count == 0 ? "none" : string.Join(" ", shown);
    }

    private static string TestNames(DreamThresholds t) => string.Join(", ", t.TestFolderNames);
}
