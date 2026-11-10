using System.Globalization;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// The one row of dream_settings (plan 3.2), with its defaults. The schedule is off by default and is turned on per
/// node: with several full nodes it belongs on one of them, or each spends and keeps its own list.
/// </summary>
public sealed record DreamSettings
{
    public bool ScheduleEnabled { get; init; }
    /// <summary>Node-local time HH:mm the nightly window opens.</summary>
    public string ScheduleStart { get; init; } = "03:00";
    public int ScheduleWindowMinutes { get; init; } = 120;
    /// <summary>New advice per scheduled run.</summary>
    public int MaxAdviceScheduled { get; init; } = 10;
    /// <summary>New advice per manual run; 0 = no limit.</summary>
    public int MaxAdviceManual { get; init; } = 50;
    /// <summary>At this many open (new, in_chat) advice the nightly run is skipped (open_list_full).</summary>
    public int MaxOpenAdvice { get; init; } = 100;
    /// <summary>Advice scoring below this is not kept.</summary>
    public int MinScore { get; init; } = 40;
    /// <summary>Time limit of a manual run; 0 = no limit.</summary>
    public int ManualMaxMinutes { get; init; } = 30;
    public int MaxModelCalls { get; init; } = 200;
    /// <summary>Estimated input plus output tokens per run. An estimate, not a hard cap: a request is admitted by an upper
    /// estimate of its size (characters / 3 plus the output limit) and then charged what the provider reports, so the last
    /// request can take a run a little past it. Where it is shown to a person it is called an estimate.</summary>
    public long MaxTokens { get; init; } = 1_500_000;
    /// <summary>False: rules only, nothing leaves the node. True: the model step too.</summary>
    public bool UseModel { get; init; }
    /// <summary>chat_model.id of the analysis model; null = the chat's default text model.</summary>
    public Guid? AnalysisModelId { get; init; }
    /// <summary>Folders whose subtrees Dream neither reads nor names.</summary>
    public IReadOnlyList<Guid> ExcludedFolderIds { get; init; } = [];
    public DateTime? UpdatedAt { get; init; }
    public int? UpdatedBy { get; init; }

    public static DreamSettings Default { get; } = new();

    // Accepted ranges (PUT /api/dream/settings answers 400 outside them). The plan fixes the two advice limits; the rest
    // are generous bounds that only keep a typo from meaning "forever".
    public static readonly (int Min, int Max) ScheduleWindowRange = (15, 720);
    public static readonly (int Min, int Max) MaxAdviceScheduledRange = (1, 500);
    public static readonly (int Min, int Max) MaxAdviceManualRange = (0, 1000);
    public static readonly (int Min, int Max) MaxOpenAdviceRange = (1, 10_000);
    public static readonly (int Min, int Max) MinScoreRange = (0, 100);
    public static readonly (int Min, int Max) ManualMaxMinutesRange = (0, 1440);
    public static readonly (int Min, int Max) MaxModelCallsRange = (1, 10_000);
    public static readonly (long Min, long Max) MaxTokensRange = (1_000, 100_000_000);
    public const int MaxExcludedFolders = 200;

    /// <summary>The first value out of range, as an English message naming the field, or null when all are valid.</summary>
    public string? Validate()
    {
        if (!TryParseStart(ScheduleStart, out _)) return "scheduleStart must be a time HH:mm.";
        if (Out(ScheduleWindowMinutes, ScheduleWindowRange)) return Range("scheduleWindowMinutes", ScheduleWindowRange);
        if (Out(MaxAdviceScheduled, MaxAdviceScheduledRange)) return Range("maxAdviceScheduled", MaxAdviceScheduledRange);
        if (Out(MaxAdviceManual, MaxAdviceManualRange)) return Range("maxAdviceManual", MaxAdviceManualRange);
        if (Out(MaxOpenAdvice, MaxOpenAdviceRange)) return Range("maxOpenAdvice", MaxOpenAdviceRange);
        if (Out(MinScore, MinScoreRange)) return Range("minScore", MinScoreRange);
        if (Out(ManualMaxMinutes, ManualMaxMinutesRange)) return Range("manualMaxMinutes", ManualMaxMinutesRange);
        if (Out(MaxModelCalls, MaxModelCallsRange)) return Range("maxModelCalls", MaxModelCallsRange);
        if (MaxTokens < MaxTokensRange.Min || MaxTokens > MaxTokensRange.Max)
            return $"maxTokens must be between {MaxTokensRange.Min} and {MaxTokensRange.Max}.";
        if (ExcludedFolderIds.Count > MaxExcludedFolders) return $"At most {MaxExcludedFolders} excluded folders.";
        return null;

        static bool Out(int v, (int Min, int Max) r) => v < r.Min || v > r.Max;
        static string Range(string name, (int Min, int Max) r) => $"{name} must be between {r.Min} and {r.Max}.";
    }

    /// <summary>Parses HH:mm (00:00..23:59).</summary>
    public static bool TryParseStart(string? text, out TimeOnly start) =>
        TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out start);
}
