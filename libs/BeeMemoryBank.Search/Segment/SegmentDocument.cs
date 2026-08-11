namespace BeeMemoryBank.Search.Segment;

/// <summary>
/// One document's contribution to a segment being built by <see cref="SegmentWriter"/>: its
/// zero-based doc id, the two identifiers a later query engine needs to translate a match back
/// into a real article and apply folder-based ACL filtering, and its (already tokenized/stemmed)
/// terms. This format has no opinion on what produced the terms -- tokenization and stemming are
/// entirely the caller's concern.
/// </summary>
/// <param name="DocId">
/// Zero-based document id. Across one call to <see cref="SegmentWriter.Build"/>, every value in
/// <c>0..N-1</c> must appear exactly once (N being the total document count) -- this is what lets
/// the doc table be a flat, directly-indexed array instead of needing its own lookup structure.
/// </param>
/// <param name="ArticleId">The real article this document represents.</param>
/// <param name="FolderId">The folder the article lives in.</param>
/// <param name="Terms">
/// The document's terms, in any order, duplicates expected and welcome: multiple occurrences of
/// the same term collapse into a single posting with a term-frequency count.
/// </param>
public sealed record SegmentDocument(int DocId, Guid ArticleId, Guid FolderId, IEnumerable<string> Terms);
