using System.Text.Json.Nodes;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// The "ask, read, and once more on a bad reply" loop of folder_review and tree_review, the same as pair_verdict's:
/// the model gets at most two tries, the second one with the field-level error of the first (never its own words), and
/// every call and every rejected reply is a line of the run's journal without any text of a note.
/// </summary>
internal static class DreamModelExchange
{
    /// <summary>The first try and one more, with what was wrong with the first reply (the rule book says it from here).</summary>
    internal const int MaxTries = 2;

    internal sealed record Outcome<T>(T? Value, string? Error, long PromptTokens, long CompletionTokens) where T : class;

    /// <param name="logLabel">"folder_review" or "tree_review": the start of the journal lines.</param>
    /// <param name="read">Reads one reply: the value, or null with what was wrong with it.</param>
    public static async Task<Outcome<T>> AskAsync<T>(IDreamModelClient model, DreamRunContext ctx, string system, string user,
        int maxOutputTokens, string logLabel, IReadOnlyList<Guid> articleIds, int tier,
        Func<string?, (T? Value, string? Error)> read, CancellationToken ct) where T : class
    {
        long promptTokens = 0, completionTokens = 0;
        string? error = null;
        for (var attempt = 0; attempt < MaxTries; attempt++)
        {
            var text = attempt == 0 ? user : RetryUserText(user, error);
            var answer = await model.CompleteAsync(new DreamModelRequest(ctx.ModelId!, system, text, maxOutputTokens, ctx.Budget, ctx.ModelRowId), ct);
            promptTokens += answer.PromptTokens;
            completionTokens += answer.CompletionTokens;
            await ctx.Log.WriteAsync(DreamEventKind.ModelCall, $"{logLabel} model call", articleIds, new JsonObject
            {
                ["tier"] = tier,
                ["attempt"] = attempt + 1,
                ["promptTokens"] = answer.PromptTokens,
                ["completionTokens"] = answer.CompletionTokens,
                // Which model of which provider answered (a fallback's when the analysis model's provider failed); names, never text.
                ["model"] = answer.Model,
                ["provider"] = answer.Provider,
            }, ct);

            var (value, problem) = read(answer.Content);
            if (value != null) return new Outcome<T>(value, null, promptTokens, completionTokens);
            // A reply the output limit stopped says so, instead of "empty" or "no JSON object".
            error = answer.CutOff ? DreamJsonReader.CutOffError : problem;
            await ctx.Log.WriteAsync(DreamEventKind.Error, $"{logLabel} reply rejected: {error}", articleIds, null, ct);
        }
        return new Outcome<T>(null, error, promptTokens, completionTokens);
    }

    /// <summary>The user text of the one retry: the first text and what was wrong with the reply (a field-level error or
    /// the cut-off, never the model's own words).</summary>
    internal static string RetryUserText(string user, string? error) => error == DreamJsonReader.CutOffError
        ? user + "\n\nYour previous reply was cut off by the output limit before the JSON object was finished. Think briefly and answer with one short JSON object that follows the schema in the instructions."
        : user + $"\n\nYour previous reply was rejected: {error} Answer again with one JSON object that follows the schema in the instructions.";

    /// <summary>The error of a candidate whose replies were all rejected (<see cref="Outcome{T}.Error"/>).</summary>
    internal static string FailureText(string? error) => error == DreamJsonReader.CutOffError
        ? "The model reply was cut off by the output limit."
        : $"The model reply did not match the schema: {error}";
}

/// <summary>
/// When a task last examined anything, from the examined registry (dream_examined.examined_at, UTC); null when never.
/// DreamStore implements it. tree_review requires it to keep to "at most once in
/// <see cref="DreamThresholds.TreeReviewMinDays"/> days".
/// </summary>
public interface IDreamExaminedLookup
{
    Task<DateTime?> LastExaminedAtAsync(string task, CancellationToken ct);
}
