using System.Text.Json.Serialization;

namespace BeeMemoryBank.Api.Dream;

// Every enumeration of the Dream feature. The wire and database form of each value is the snake_case name in its
// JsonStringEnumMemberName attribute: the API's global JsonStringEnumConverter writes it, the store writes it to the
// text columns of chat.db through DreamWire.ToWire, and DreamWire.TryParse reads it back (query strings included).
// A value is never renamed: rows of older runs hold the old text.

/// <summary>dream_run.trigger: who started the run.</summary>
public enum DreamRunTrigger
{
    [JsonStringEnumMemberName("schedule")] Schedule,
    [JsonStringEnumMemberName("manual")] Manual,
}

/// <summary>dream_run.mode. A trial run neither reads nor writes the examined registry, its advice stays out of the
/// open list and out of duplicate suppression (plan 3.3).</summary>
public enum DreamRunMode
{
    [JsonStringEnumMemberName("normal")] Normal,
    [JsonStringEnumMemberName("trial")] Trial,
}

/// <summary>Filter of <c>GET /api/dream/runs</c>.</summary>
public enum DreamRunListMode
{
    [JsonStringEnumMemberName("all")] All,
    [JsonStringEnumMemberName("normal")] Normal,
    [JsonStringEnumMemberName("trial")] Trial,
}

/// <summary>dream_run.state: queued -> running -> finished, or running -> stopping -> finished.</summary>
public enum DreamRunState
{
    [JsonStringEnumMemberName("queued")] Queued,
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("stopping")] Stopping,
    [JsonStringEnumMemberName("finished")] Finished,
}

/// <summary>dream_run.outcome, set when the run is finished (plan 3.3).</summary>
public enum DreamRunOutcome
{
    /// <summary>The candidates ran out.</summary>
    [JsonStringEnumMemberName("completed")] Completed,
    [JsonStringEnumMemberName("advice_limit")] AdviceLimit,
    /// <summary>The time limit or the schedule window ran out.</summary>
    [JsonStringEnumMemberName("time_limit")] TimeLimit,
    /// <summary>The model call or token budget ran out.</summary>
    [JsonStringEnumMemberName("budget_limit")] BudgetLimit,
    [JsonStringEnumMemberName("stopped")] Stopped,
    /// <summary>The vault was locked or sealed while the run worked.</summary>
    [JsonStringEnumMemberName("locked")] Locked,
    /// <summary>A heavy operation began (key rotation, restore, compaction, node encryption).</summary>
    [JsonStringEnumMemberName("heavy_operation")] HeavyOperation,
    [JsonStringEnumMemberName("provider_error")] ProviderError,
    /// <summary>The process went away; found by a stale heartbeat.</summary>
    [JsonStringEnumMemberName("interrupted")] Interrupted,
    [JsonStringEnumMemberName("failed")] Failed,
    /// <summary>A scheduled run that did not start; the reason is in error_code (DreamErrorCodes).</summary>
    [JsonStringEnumMemberName("skipped")] Skipped,
}

/// <summary>dream_run.phase, in the order a run goes through them (plan 3.5).</summary>
public enum DreamPhase
{
    [JsonStringEnumMemberName("embeddings")] Embeddings,
    [JsonStringEnumMemberName("detectors")] Detectors,
    [JsonStringEnumMemberName("pairs")] Pairs,
    [JsonStringEnumMemberName("folders")] Folders,
    [JsonStringEnumMemberName("tree")] Tree,
}

/// <summary>dream_advice.kind (plan 3.4).</summary>
public enum DreamAdviceKind
{
    [JsonStringEnumMemberName("duplicate_exact")] DuplicateExact,
    [JsonStringEnumMemberName("near_duplicate")] NearDuplicate,
    [JsonStringEnumMemberName("duplicate")] Duplicate,
    [JsonStringEnumMemberName("superseded")] Superseded,
    [JsonStringEnumMemberName("overlap")] Overlap,
    [JsonStringEnumMemberName("contradiction")] Contradiction,
    [JsonStringEnumMemberName("move_article")] MoveArticle,
    [JsonStringEnumMemberName("split_folder")] SplitFolder,
    [JsonStringEnumMemberName("rename_folder")] RenameFolder,
    [JsonStringEnumMemberName("merge_folders")] MergeFolders,
    [JsonStringEnumMemberName("structure_note")] StructureNote,
    [JsonStringEnumMemberName("merge_tags")] MergeTags,
    [JsonStringEnumMemberName("delete_unused_tags")] DeleteUnusedTags,
    [JsonStringEnumMemberName("delete_empty_folder")] DeleteEmptyFolder,
    [JsonStringEnumMemberName("test_leftovers")] TestLeftovers,
}

/// <summary>dream_advice.source: a detector without a model, or the model step.</summary>
public enum DreamAdviceSource
{
    [JsonStringEnumMemberName("rule")] Rule,
    [JsonStringEnumMemberName("model")] Model,
}

/// <summary>dream_advice.apply_mode: <c>code</c> has Apply (chat and prompt too), <c>chat</c> has Do it with AI and
/// the prompt, <c>prompt</c> only the prompt. Every advice has a prompt.</summary>
public enum DreamApplyMode
{
    [JsonStringEnumMemberName("code")] Code,
    [JsonStringEnumMemberName("chat")] Chat,
    [JsonStringEnumMemberName("prompt")] Prompt,
}

/// <summary>dream_advice.status (plan 3.4).</summary>
public enum DreamAdviceStatus
{
    [JsonStringEnumMemberName("new")] New,
    /// <summary>Held while Apply runs (double-click guard); older than ten minutes it returns to new.</summary>
    [JsonStringEnumMemberName("applying")] Applying,
    [JsonStringEnumMemberName("in_chat")] InChat,
    [JsonStringEnumMemberName("applied")] Applied,
    [JsonStringEnumMemberName("dismissed")] Dismissed,
    [JsonStringEnumMemberName("expired")] Expired,
}

/// <summary>Sort order of <c>GET /api/dream/advice</c>.</summary>
public enum DreamAdviceSort
{
    [JsonStringEnumMemberName("score")] Score,
    [JsonStringEnumMemberName("date")] Date,
}

/// <summary>The interface band of a score: high from 80, medium 60..79, low below 60 (DreamScore.BandOf).</summary>
public enum DreamScoreBand
{
    [JsonStringEnumMemberName("high")] High,
    [JsonStringEnumMemberName("medium")] Medium,
    [JsonStringEnumMemberName("low")] Low,
}

/// <summary><c>action.type</c> of an advice (plan 3.4). The parameters of each are on DreamAction.</summary>
public enum DreamActionType
{
    /// <summary>articleId, keepId</summary>
    [JsonStringEnumMemberName("trash_article")] TrashArticle,
    /// <summary>aId, bId</summary>
    [JsonStringEnumMemberName("review_pair")] ReviewPair,
    /// <summary>keepId, otherId</summary>
    [JsonStringEnumMemberName("merge_articles")] MergeArticles,
    /// <summary>currentId, oldId</summary>
    [JsonStringEnumMemberName("mark_superseded")] MarkSuperseded,
    /// <summary>articleId, toPath</summary>
    [JsonStringEnumMemberName("move_article")] MoveArticle,
    /// <summary>toPath, articleIds</summary>
    [JsonStringEnumMemberName("move_articles")] MoveArticles,
    /// <summary>path, newName</summary>
    [JsonStringEnumMemberName("rename_folder")] RenameFolder,
    /// <summary>from, into</summary>
    [JsonStringEnumMemberName("merge_folders")] MergeFolders,
    /// <summary>no parameters</summary>
    [JsonStringEnumMemberName("note")] Note,
    /// <summary>sources, target</summary>
    [JsonStringEnumMemberName("merge_tags")] MergeTags,
    /// <summary>names</summary>
    [JsonStringEnumMemberName("delete_tags")] DeleteTags,
    /// <summary>path, folderId</summary>
    [JsonStringEnumMemberName("delete_folder")] DeleteFolder,
    /// <summary>path</summary>
    [JsonStringEnumMemberName("review_folder")] ReviewFolder,
}

/// <summary><c>preconditions[].type</c> (plan 3.4).</summary>
public enum DreamPreconditionType
{
    /// <summary>id, lamportTs</summary>
    [JsonStringEnumMemberName("article_unchanged")] ArticleUnchanged,
    /// <summary>id</summary>
    [JsonStringEnumMemberName("article_exists")] ArticleExists,
    /// <summary>id, path</summary>
    [JsonStringEnumMemberName("article_in_path")] ArticleInPath,
    /// <summary>path</summary>
    [JsonStringEnumMemberName("folder_exists")] FolderExists,
    /// <summary>path</summary>
    [JsonStringEnumMemberName("folder_empty")] FolderEmpty,
    /// <summary>name</summary>
    [JsonStringEnumMemberName("tag_exists")] TagExists,
    /// <summary>name</summary>
    [JsonStringEnumMemberName("tag_unused")] TagUnused,
}

/// <summary><c>evidence[].type</c> (plan 3.4).</summary>
public enum DreamEvidenceType
{
    [JsonStringEnumMemberName("neighbours")] Neighbours,
    [JsonStringEnumMemberName("similarity")] Similarity,
    [JsonStringEnumMemberName("quote")] Quote,
    [JsonStringEnumMemberName("count")] Count,
    [JsonStringEnumMemberName("note")] Note,
}

/// <summary><c>objects[].role</c>.</summary>
public enum DreamObjectRole
{
    [JsonStringEnumMemberName("subject")] Subject,
    [JsonStringEnumMemberName("other")] Other,
    [JsonStringEnumMemberName("target")] Target,
}

/// <summary><c>objects[].type</c>.</summary>
public enum DreamObjectType
{
    [JsonStringEnumMemberName("article")] Article,
    [JsonStringEnumMemberName("folder")] Folder,
    [JsonStringEnumMemberName("tag")] Tag,
}

/// <summary>dream_rule.scope: this fingerprint; this kind for this article, folder or tag; this kind in a folder's
/// subtree; this kind everywhere.</summary>
public enum DreamRuleScope
{
    [JsonStringEnumMemberName("advice")] Advice,
    [JsonStringEnumMemberName("object")] Object,
    [JsonStringEnumMemberName("folder")] Folder,
    [JsonStringEnumMemberName("kind")] Kind,
}

/// <summary>dream_run_event.kind.</summary>
public enum DreamEventKind
{
    [JsonStringEnumMemberName("phase")] Phase,
    /// <summary>One examined candidate: its data is a DreamCandidateEventData (the compare table is built from it).</summary>
    [JsonStringEnumMemberName("candidate")] Candidate,
    [JsonStringEnumMemberName("advice")] Advice,
    [JsonStringEnumMemberName("model_call")] ModelCall,
    [JsonStringEnumMemberName("skip")] Skip,
    [JsonStringEnumMemberName("error")] Error,
    [JsonStringEnumMemberName("info")] Info,
}

/// <summary>dream_examined.verdict: what one look at a candidate gave.</summary>
public enum DreamExaminedVerdict
{
    [JsonStringEnumMemberName("advice")] Advice,
    [JsonStringEnumMemberName("nothing")] Nothing,
    [JsonStringEnumMemberName("error")] Error,
}

/// <summary><c>target</c> of <c>GET /api/dream/advice/{id}/prompt</c>: a personal agent, or the built-in chat.</summary>
public enum DreamPromptTarget
{
    [JsonStringEnumMemberName("agent")] Agent,
    [JsonStringEnumMemberName("chat")] Chat,
}
