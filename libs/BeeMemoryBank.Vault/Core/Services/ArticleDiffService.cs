using System.Text.RegularExpressions;
using DiffPlex;
using DiffPlex.Model;

namespace BeeMemoryBank.Core.Services;

public sealed class ArticleDiffBlock
{
    public required string Op { get; init; }
    public string? Heading { get; init; }
    public string? Old { get; init; }
    public string? New { get; init; }
}

public sealed class ArticleDiffResult
{
    public bool Unchanged { get; init; }
    public double Similarity { get; init; } = 1.0;
    public bool TooLarge { get; init; }
    public List<ArticleDiffBlock> Blocks { get; init; } = [];
}

/// <summary>
/// Markdown-block-level diff between two plain-text article bodies. No DB/crypto/DI dependency —
/// callers (MCP tool layer) decrypt both bodies first and pass plain strings in here.
/// </summary>
public partial class ArticleDiffService
{
    private const int MaxBlocks = 150;
    private const double MinSimilarity = 0.6;

    public ArticleDiffResult Diff(string oldBody, string newBody)
    {
        if (oldBody == newBody)
            return new ArticleDiffResult { Unchanged = true, Similarity = 1.0 };

        var oldBlocks = SplitIntoBlocks(oldBody);
        var newBlocks = SplitIntoBlocks(newBody);

        var diff = Differ.Instance.CreateDiffs(oldBody, newBody, false, false, new MarkdownBlockChunker());

        if (diff.DiffBlocks.Count == 0)
            return new ArticleDiffResult { Unchanged = true, Similarity = 1.0 };

        var blocks = new List<ArticleDiffBlock>();
        foreach (var block in diff.DiffBlocks)
        {
            var pairCount = Math.Min(block.DeleteCountA, block.InsertCountB);
            for (var k = 0; k < pairCount; k++)
            {
                var oldPiece = oldBlocks[block.DeleteStartA + k];
                var newPiece = newBlocks[block.InsertStartB + k];
                blocks.Add(new ArticleDiffBlock { Op = "modify", Heading = newPiece.Heading, Old = oldPiece.Text, New = newPiece.Text });
            }
            for (var k = pairCount; k < block.DeleteCountA; k++)
            {
                var oldPiece = oldBlocks[block.DeleteStartA + k];
                blocks.Add(new ArticleDiffBlock { Op = "remove", Heading = oldPiece.Heading, Old = oldPiece.Text, New = null });
            }
            for (var k = pairCount; k < block.InsertCountB; k++)
            {
                var newPiece = newBlocks[block.InsertStartB + k];
                blocks.Add(new ArticleDiffBlock { Op = "add", Heading = newPiece.Heading, Old = null, New = newPiece.Text });
            }
        }

        // LCS invariant: pieces retained unchanged in old == pieces retained unchanged in new.
        var totalDeleted = diff.DiffBlocks.Sum(b => b.DeleteCountA);
        var matched = oldBlocks.Count - totalDeleted;
        var maxCount = Math.Max(oldBlocks.Count, newBlocks.Count);
        var similarity = maxCount == 0 ? 1.0 : (double)matched / maxCount;

        var tooLarge = blocks.Count > MaxBlocks || similarity < MinSimilarity;

        return new ArticleDiffResult
        {
            Unchanged = false,
            Similarity = similarity,
            TooLarge = tooLarge,
            Blocks = tooLarge ? [] : blocks
        };
    }

    private readonly record struct MarkdownBlock(string Text, string? Heading);

    // Both the chunker DiffPlex drives and our own heading-tracking pass call this same
    // deterministic splitter, so DiffBlock indices from DiffPlex line up 1:1 with our block lists.
    private static List<MarkdownBlock> SplitIntoBlocks(string text)
    {
        var blocks = new List<MarkdownBlock>();
        if (string.IsNullOrEmpty(text)) return blocks;

        var lines = text.Split('\n');
        string? currentHeading = null;
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

            if (IsFenceLine(line))
            {
                var start = i;
                i++;
                while (i < lines.Length && !IsFenceLine(lines[i])) i++;
                if (i < lines.Length) i++; // include the closing fence line
                blocks.Add(new MarkdownBlock(string.Join('\n', lines[start..i]), currentHeading));
                continue;
            }

            if (IsHeadingLine(line))
            {
                blocks.Add(new MarkdownBlock(line, currentHeading));
                currentHeading = line;
                i++;
                continue;
            }

            if (IsTableRowLine(line))
            {
                blocks.Add(new MarkdownBlock(line, currentHeading));
                i++;
                continue;
            }

            var paraStart = i;
            while (i < lines.Length
                   && !string.IsNullOrWhiteSpace(lines[i])
                   && !IsFenceLine(lines[i])
                   && !IsHeadingLine(lines[i])
                   && !IsTableRowLine(lines[i]))
            {
                i++;
            }
            blocks.Add(new MarkdownBlock(string.Join('\n', lines[paraStart..i]), currentHeading));
        }

        return blocks;
    }

    private static bool IsFenceLine(string line) => line.TrimStart().StartsWith("```", StringComparison.Ordinal);
    private static bool IsTableRowLine(string line) => line.TrimStart().StartsWith('|');
    private static bool IsHeadingLine(string line) => HeadingRegex().IsMatch(line);

    [GeneratedRegex(@"^#{1,6}\s")]
    private static partial Regex HeadingRegex();

    private sealed class MarkdownBlockChunker : IChunker
    {
        public IReadOnlyList<string> Chunk(string text) => SplitIntoBlocks(text).Select(b => b.Text).ToArray();
    }
}
