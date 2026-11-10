using System.Text.Json;
using Dapper;

namespace BeeMemoryBank.Api.Dream;

// dream_settings: one row, id = 1, created with the defaults by DreamSchema. Plaintext.
public sealed partial class DreamStore
{
    public async Task<DreamSettings> GetSettingsAsync()
    {
        using var conn = OpenConnection();
        var row = await conn.QuerySingleOrDefaultAsync<SettingsRow>(
            """
            SELECT schedule_enabled AS ScheduleEnabled, schedule_start AS ScheduleStart,
                   schedule_window_minutes AS ScheduleWindowMinutes, max_advice_scheduled AS MaxAdviceScheduled,
                   max_advice_manual AS MaxAdviceManual, max_open_advice AS MaxOpenAdvice, min_score AS MinScore,
                   manual_max_minutes AS ManualMaxMinutes, max_model_calls AS MaxModelCalls, max_tokens AS MaxTokens,
                   use_model AS UseModel, analysis_model_id AS AnalysisModelId,
                   excluded_folder_ids AS ExcludedFolderIds, updated_at AS UpdatedAt, updated_by AS UpdatedBy
            FROM dream_settings WHERE id = 1
            """);
        return row is null ? DreamSettings.Default : row.ToSettings();
    }

    /// <summary>Replaces the row. The caller has validated <paramref name="settings"/>.</summary>
    public async Task SaveSettingsAsync(DreamSettings settings, int? userId)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            """
            UPDATE dream_settings
            SET schedule_enabled = @ScheduleEnabled, schedule_start = @ScheduleStart,
                schedule_window_minutes = @ScheduleWindowMinutes, max_advice_scheduled = @MaxAdviceScheduled,
                max_advice_manual = @MaxAdviceManual, max_open_advice = @MaxOpenAdvice, min_score = @MinScore,
                manual_max_minutes = @ManualMaxMinutes, max_model_calls = @MaxModelCalls, max_tokens = @MaxTokens,
                use_model = @UseModel, analysis_model_id = @Model, excluded_folder_ids = @Excluded,
                updated_at = @Now, updated_by = @UserId
            WHERE id = 1
            """,
            new
            {
                settings.ScheduleEnabled, settings.ScheduleStart, settings.ScheduleWindowMinutes, settings.MaxAdviceScheduled,
                settings.MaxAdviceManual, settings.MaxOpenAdvice, settings.MinScore, settings.ManualMaxMinutes,
                settings.MaxModelCalls, settings.MaxTokens, settings.UseModel,
                Model = Id(settings.AnalysisModelId),
                Excluded = JsonSerializer.Serialize(settings.ExcludedFolderIds.Select(Id)),
                Now = UtcNow(),
                UserId = userId,
            });
    }

    private sealed class SettingsRow
    {
        public long ScheduleEnabled { get; set; }
        public string ScheduleStart { get; set; } = "03:00";
        public long ScheduleWindowMinutes { get; set; }
        public long MaxAdviceScheduled { get; set; }
        public long MaxAdviceManual { get; set; }
        public long MaxOpenAdvice { get; set; }
        public long MinScore { get; set; }
        public long ManualMaxMinutes { get; set; }
        public long MaxModelCalls { get; set; }
        public long MaxTokens { get; set; }
        public long UseModel { get; set; }
        public string? AnalysisModelId { get; set; }
        public string ExcludedFolderIds { get; set; } = "[]";
        public string? UpdatedAt { get; set; }
        public long? UpdatedBy { get; set; }

        public DreamSettings ToSettings() => new()
        {
            ScheduleEnabled = ScheduleEnabled != 0,
            ScheduleStart = ScheduleStart,
            ScheduleWindowMinutes = (int)ScheduleWindowMinutes,
            MaxAdviceScheduled = (int)MaxAdviceScheduled,
            MaxAdviceManual = (int)MaxAdviceManual,
            MaxOpenAdvice = (int)MaxOpenAdvice,
            MinScore = (int)MinScore,
            ManualMaxMinutes = (int)ManualMaxMinutes,
            MaxModelCalls = (int)MaxModelCalls,
            MaxTokens = MaxTokens,
            UseModel = UseModel != 0,
            AnalysisModelId = Guid.TryParse(AnalysisModelId, out var model) ? model : null,
            ExcludedFolderIds = ParseIds(ExcludedFolderIds),
            UpdatedAt = ParseTimeOrNull(UpdatedAt),
            UpdatedBy = (int?)UpdatedBy,
        };

        // An excluded list that cannot be read must not silently become "nothing is excluded": the caller would then
        // read and send folders the owner fenced off. It throws, and the run ends failed instead.
        private static IReadOnlyList<Guid> ParseIds(string json) =>
            (JsonSerializer.Deserialize<List<string>>(json) ?? []).Select(Guid.Parse).ToList();
    }
}
