using System.Globalization;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// The 409 of an action on an advice that is no longer open (another tab, another person, the sweep): it says what happened
/// to the advice and when, and carries its status and the time so the page can redraw the card and say it in local time.
/// <c>error</c> and <c>code</c> are those of every other ErrorResponse; the codes are the ones the endpoints always used
/// (<c>expired</c> for an advice that expired or that counts as done after the chat, <c>not_applicable</c> otherwise).
/// </summary>
public sealed record DreamAdviceConflictResponse(string Error, string Code, DreamAdviceStatus Status, DateTime? StatusAt, string? StatusNote);

internal static class DreamAdviceConflicts
{
    /// <summary>Applied, dismissed, expired, or being applied right now: no button acts on it.</summary>
    public static bool IsClosed(DreamAdviceStatus status) =>
        status is DreamAdviceStatus.Applied or DreamAdviceStatus.Dismissed or DreamAdviceStatus.Expired or DreamAdviceStatus.Applying;

    /// <summary>The 409 for an advice that is closed.</summary>
    public static IResult For(DreamAdviceRecord advice) =>
        Results.Json(Body(advice), statusCode: StatusCodes.Status409Conflict);

    public static DreamAdviceConflictResponse Body(DreamAdviceRecord advice)
    {
        var note = DreamStore.IsApplyAttempt(advice.StatusNote) ? null : advice.StatusNote;
        var code = advice.Status == DreamAdviceStatus.Expired || IsDoneAfterChat(advice) ? DreamErrorCodes.Expired : DreamErrorCodes.NotApplicable;
        return new DreamAdviceConflictResponse(Message(advice), code, advice.Status, advice.StatusAt, note);
    }

    /// <summary>What happened to the advice, in a sentence with its date (UTC: the page says it again in local time).</summary>
    public static string Message(DreamAdviceRecord advice)
    {
        var on = advice.StatusAt is { } at ? " on " + When(at) : "";
        return advice.Status switch
        {
            DreamAdviceStatus.Applied when IsDoneAfterChat(advice) =>
                "A note it is about changed after it went to the chat, so it counts as done" + (advice.StatusAt is { } t ? $" ({When(t)})." : "."),
            DreamAdviceStatus.Applied when IsMarkedDone(advice) => $"This advice was already marked done{on}.",
            DreamAdviceStatus.Applied => $"This advice was already applied{on}.",
            DreamAdviceStatus.Dismissed => $"This advice was already dismissed{on}.",
            DreamAdviceStatus.Expired => $"This advice expired{on}: {Reason(advice)}",
            DreamAdviceStatus.Applying => "This advice is being applied right now.",
            DreamAdviceStatus.InChat => "This advice is in the chat.",
            _ => "This advice is open.",
        };
    }

    private static bool IsDoneAfterChat(DreamAdviceRecord advice) =>
        advice.Status == DreamAdviceStatus.Applied && advice.StatusNote == DreamStatusNotes.ChangedAfterChat;

    // Mark done writes its note since this round; an advice marked done before that is told by the result it was given.
    private static bool IsMarkedDone(DreamAdviceRecord advice) =>
        advice.StatusNote == DreamStatusNotes.MarkedDone
        || advice.Payload.Applied?.Result?.StartsWith("Marked as done", StringComparison.Ordinal) == true;

    private static string Reason(DreamAdviceRecord advice)
    {
        var reason = advice.StatusNote switch
        {
            null or "" => "what it describes has changed.",
            DreamStatusNotes.Superseded => "a newer verdict on the same pair of notes replaced it.",
            var text => text,
        };
        // The reason is a sentence of its own; after the colon it reads on in lower case.
        if (reason.Length > 1 && char.IsUpper(reason[0]) && char.IsLower(reason[1])) reason = char.ToLowerInvariant(reason[0]) + reason[1..];
        return reason.EndsWith('.') ? reason : reason + ".";
    }

    private static string When(DateTime at) =>
        DateTime.SpecifyKind(at, DateTimeKind.Utc).ToString("d MMM yyyy, HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
