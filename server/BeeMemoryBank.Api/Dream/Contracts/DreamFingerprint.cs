using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// The hashes that tell one advice or one candidate from another (plan 3.2, 3.4).
/// <list type="bullet">
/// <item><see cref="Of"/>: an advice's fingerprint, the first 32 hex of SHA-256 over
/// <c>kind|action.type|sorted ids or normalized names and paths</c>. An open (new, in_chat) advice with the same
/// fingerprint makes a new one a repeat; a dismiss rule of scope advice matches it. The engine and the stored payload
/// both take it from <see cref="OfDraft"/>, so that the two can never disagree.</item>
/// <item><see cref="CandidateKey"/>: the examined registry's key, SHA-256 over <c>task|sorted ids</c>.</item>
/// <item><see cref="ContentMarker"/>: what a candidate looked like when examined (the articles' LamportTs, or the
/// tree text): a different marker makes it worth examining again.</item>
/// </list>
/// Only ids, normalized names and paths go in; the hashes are lowercase hex.
///
/// <para><b>What makes two advice the same (decided in package A1).</b> The fingerprint names the set of things an
/// action touches, not the role each one plays in it. "Merge into X" and "merge into Y" are different advice when X and
/// Y are different things: merging folder /A into /B is not merging /A into /C, moving a note to /Kids is not moving it to
/// /Recipes, and dismissing one leaves the other free to come. But when the same set of tags, folders or notes is merged
/// with only the survivor swapped (merge "howto" into "how-to" against "how-to" into "howto"; keep note A against keep note B;
/// trash one exact copy against the other), it is one advice: the question for the person is "are these one thing", the
/// survivor is a detail of the answer that the rule (most used tag, fewer references) or the model may pick differently
/// from one night to the next. Telling them apart would put two cards that undo each other in the open list, and a
/// person who dismissed "merge these" would be asked the same thing again the night the counts tip the other way. A
/// person who wants nothing more about a tag, folder or note at all has the dismiss scope <c>object</c>.</para>
/// </summary>
public static class DreamFingerprint
{
    /// <summary>The fingerprint of a draft: what the engine compares for repeats and dismiss rules, and what the stored
    /// payload carries. Always this, never <see cref="Of"/> with fewer arguments: a note's fingerprint needs its objects.</summary>
    public static string OfDraft(DreamAdviceDraft draft) => Of(draft.Kind, draft.Action, draft.Objects, draft.Title);

    /// <param name="objects">Used only for an action with no target of its own (note): what the note is about.</param>
    /// <param name="title">For a note about nothing in particular: without it every such note would be one advice.</param>
    public static string Of(DreamAdviceKind kind, DreamAction action, IEnumerable<DreamAdviceObject>? objects = null, string? title = null)
    {
        var keys = new List<string>();
        if (action.Type == DreamActionType.Note)
        {
            foreach (var o in objects ?? [])
            {
                if (o.Id is { } objectId) keys.Add(Id(objectId));
                else if (o.Path != null) keys.Add(NormalizePath(o.Path));
                else if (o.Name != null) keys.Add(NormalizeName(o.Name));
            }
            if (keys.Count == 0 && title != null) keys.Add(NormalizeName(title));
        }
        foreach (var id in action.ArticleIdsNamed()) keys.Add(Id(id));
        if (action.FolderId is { } folderId) keys.Add(Id(folderId));
        foreach (var path in new[] { action.ToPath, action.Path, action.From, action.Into })
            if (path != null) keys.Add(NormalizePath(path));
        foreach (var name in new[] { action.NewName, action.Target })
            if (name != null) keys.Add(NormalizeName(name));
        if (action.Sources != null) keys.AddRange(action.Sources.Select(NormalizeName));
        if (action.Names != null) keys.AddRange(action.Names.Select(NormalizeName));
        keys.Sort(StringComparer.Ordinal);

        // A name may hold the separator itself: tags "a|b" and "c" must not read as tags "a" and "b|c".
        var text = $"{DreamWire.ToWire(kind)}|{DreamWire.ToWire(action.Type)}|{string.Join('|', keys.Select(Escape))}";
        return Hex(text, 32);
    }

    /// <summary>The examined registry's key of a model candidate. The ids are sorted, so a pair is the same candidate
    /// whichever article comes first.</summary>
    public static string CandidateKey(string task, IEnumerable<Guid> ids)
    {
        var sorted = ids.Select(Id).Distinct().OrderBy(s => s, StringComparer.Ordinal);
        return Hex($"{task}|{string.Join('|', sorted)}", 64);
    }

    /// <summary>The content marker of articles: their LamportTs, in id order.</summary>
    public static string ContentMarker(IEnumerable<(Guid Id, long LamportTs)> articles)
    {
        var parts = articles
            .OrderBy(a => Id(a.Id), StringComparer.Ordinal)
            .Select(a => $"{Id(a.Id)}:{a.LamportTs}");
        return Hex(string.Join(',', parts), 32);
    }

    /// <summary>The content marker of a text the task built from metadata (the tree_review input lines).</summary>
    public static string ContentMarker(string text) => Hex(text, 32);

    /// <summary>A tag or folder name for comparison: trimmed, lower-case invariant.</summary>
    public static string NormalizeName(string name) => name.Trim().ToLowerInvariant();

    /// <summary>A tree path for comparison: canonical when it can be, then lower-case invariant.</summary>
    public static string NormalizePath(string path) =>
        (TreePathCanonicalizer.TryCanonicalize(path) ?? path.Trim()).ToLowerInvariant();

    private static string Id(Guid id) => id.ToString("D");

    /// <summary>A key with <c>\</c> and <c>|</c> escaped. A key without either is unchanged, so the fingerprints of every
    /// ordinary advice stay what they were.</summary>
    private static string Escape(string key) =>
        key.Contains('|') || key.Contains('\\') ? key.Replace("\\", "\\\\").Replace("|", "\\|") : key;

    private static string Hex(string text, int length) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..length];
}
