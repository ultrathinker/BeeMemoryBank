namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// Turns a Dismiss into its rule (plan 3.4): <c>advice</c> this fingerprint; <c>object</c> this kind for the thing the
/// advice is about; <c>folder</c> this kind in a folder's subtree; <c>kind</c> this kind everywhere. The thing and the
/// folder are picked the way the Dismiss dialog of the Dream page names them (dream-model.js, dismissOptions): the first
/// object of the advice, and the first folder it names that is not a destination, or else the folder of its first note
/// that is not a destination, so the rule is the one the person read. DreamRuleSet matches it so that it always covers the
/// advice it was made from, and never a destination alone.
/// </summary>
public static class DreamDismissRules
{
    public const int MaxReasonLength = 500;

    /// <summary>The days a rule may last: 180, or null for good.</summary>
    public static readonly int[] AllowedExpiryDays = [180];

    /// <summary>The rule, or why there can be none (in English, for a 400).</summary>
    public static (DreamRuleRecord? Rule, string? Error) Build(DreamAdviceRecord advice, DreamDismissRequest request, int? userId, DateTime nowUtc)
    {
        if (request.ExpiresDays is { } days && !AllowedExpiryDays.Contains(days))
            return (null, "expiresDays must be 180 or null (for good).");
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason is { Length: > MaxReasonLength }) return (null, $"The reason may be at most {MaxReasonLength} characters.");

        var payload = advice.Payload;
        var basis = new DreamRulePayload(Title: payload.Title, Reason: reason, AdviceId: advice.Id);
        DreamRulePayload rulePayload;
        switch (request.Scope)
        {
            case DreamRuleScope.Advice:
                rulePayload = basis with { Fingerprint = payload.Fingerprint };
                break;
            case DreamRuleScope.Object:
                if (ObjectOf(payload) is not { } o) return (null, "This advice is not about a note, folder or tag.");
                rulePayload = o.Type switch
                {
                    DreamObjectType.Tag => basis with { TagName = o.Name },
                    DreamObjectType.Folder => basis with { ObjectId = o.Id, Path = o.Path },
                    _ => basis with { ObjectId = o.Id, Path = o.TreePath },
                };
                if (rulePayload.ObjectId is null && rulePayload.TagName is null && rulePayload.Path is null)
                    return (null, "This advice is not about a note, folder or tag.");
                break;
            case DreamRuleScope.Folder:
                if (FolderOf(payload) is not { } path) return (null, "This advice is not about anything in a folder.");
                rulePayload = basis with { Path = path };
                break;
            case DreamRuleScope.Kind:
                rulePayload = basis;
                break;
            default:
                return (null, "Unknown scope.");
        }

        return (new DreamRuleRecord
        {
            Id = Guid.NewGuid(),
            Scope = request.Scope,
            Kind = request.Scope == DreamRuleScope.Advice ? null : advice.Kind,
            CreatedAt = nowUtc,
            CreatedByUserId = userId,
            ExpiresAt = request.ExpiresDays is { } d ? nowUtc.AddDays(d) : null,
            Payload = rulePayload,
        }, null);
    }

    /// <summary>The thing "anything of this kind about this ..." names: the advice's first object.</summary>
    internal static DreamAdviceObject? ObjectOf(DreamAdvicePayload payload) => payload.Objects.FirstOrDefault();

    /// <summary>The folder "this kind of advice in ..." names: the first folder of the advice that is not a destination, else
    /// the folder of its first note that is not a destination.</summary>
    internal static string? FolderOf(DreamAdvicePayload payload) =>
        payload.Objects.FirstOrDefault(o => o.Type == DreamObjectType.Folder && o.Role != DreamObjectRole.Target && !string.IsNullOrEmpty(o.Path))?.Path
        ?? payload.Objects.FirstOrDefault(o => o.Type == DreamObjectType.Article && o.Role != DreamObjectRole.Target && !string.IsNullOrEmpty(o.TreePath))?.TreePath;
}
