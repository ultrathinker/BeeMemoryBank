using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Core.Tests;

public class ArticleDiffServiceTests
{
    private readonly ArticleDiffService _svc = new();

    [Fact]
    public void Diff_IdenticalBodies_ReturnsUnchanged()
    {
        var result = _svc.Diff("Hello\n\nWorld", "Hello\n\nWorld");

        result.Unchanged.Should().BeTrue();
        result.Blocks.Should().BeEmpty();
        result.Similarity.Should().Be(1.0);
        result.TooLarge.Should().BeFalse();
    }

    [Fact]
    public void Diff_SingleParagraphChanged_ReturnsOneModifyBlockWithHeading()
    {
        var oldBody = "# Title\n\nFirst paragraph.\n\nSecond paragraph.";
        var newBody = "# Title\n\nFirst paragraph changed.\n\nSecond paragraph.";

        var result = _svc.Diff(oldBody, newBody);

        result.Unchanged.Should().BeFalse();
        result.Blocks.Should().HaveCount(1);
        var block = result.Blocks[0];
        block.Op.Should().Be("modify");
        block.Old.Should().Be("First paragraph.");
        block.New.Should().Be("First paragraph changed.");
        block.Heading.Should().Be("# Title");
    }

    [Fact]
    public void Diff_AddedParagraph_ReturnsAddBlock()
    {
        // Enough surrounding unchanged blocks that a single addition doesn't itself
        // drag similarity below the tooLarge threshold — this test is about op
        // classification, not the size-gating behavior (covered separately).
        var oldBody = "First.\n\nSecond.\n\nThird.\n\nFourth.";
        var newBody = "First.\n\nSecond.\n\nThird.\n\nFourth.\n\nFifth.";

        var result = _svc.Diff(oldBody, newBody);

        result.TooLarge.Should().BeFalse();
        result.Blocks.Should().ContainSingle(b => b.Op == "add" && b.New == "Fifth." && b.Old == null);
    }

    [Fact]
    public void Diff_RemovedParagraph_ReturnsRemoveBlock()
    {
        var oldBody = "First.\n\nSecond.\n\nThird.\n\nFourth.\n\nFifth.";
        var newBody = "First.\n\nSecond.\n\nThird.\n\nFourth.";

        var result = _svc.Diff(oldBody, newBody);

        result.TooLarge.Should().BeFalse();
        result.Blocks.Should().ContainSingle(b => b.Op == "remove" && b.Old == "Fifth." && b.New == null);
    }

    [Fact]
    public void Diff_OneTableCellChanged_ReturnsSingleRowBlock()
    {
        var rows = Enumerable.Range(1, 12).Select(i => $"| Row {i} | Value {i} |").ToList();
        var oldBody = "| Col A | Col B |\n|---|---|\n" + string.Join("\n", rows);
        var newRows = rows.Select((r, idx) => idx == 5 ? "| Row 6 | CHANGED |" : r);
        var newBody = "| Col A | Col B |\n|---|---|\n" + string.Join("\n", newRows);

        var result = _svc.Diff(oldBody, newBody);

        result.Blocks.Should().HaveCount(1);
        result.Blocks[0].Op.Should().Be("modify");
        result.Blocks[0].Old.Should().Be("| Row 6 | Value 6 |");
        result.Blocks[0].New.Should().Be("| Row 6 | CHANGED |");
    }

    [Fact]
    public void Diff_ChangeInsideFencedCodeBlock_KeepsWholeBlockTogetherVerbatim()
    {
        var oldBody = "Text before.\n\n```csharp\nvar x = 1;\n\nConsole.WriteLine(x);\n```\n\nText after.";
        var newBody = "Text before.\n\n```csharp\nvar x = 2;\n\nConsole.WriteLine(x);\n```\n\nText after.";

        var result = _svc.Diff(oldBody, newBody);

        result.Blocks.Should().HaveCount(1);
        var block = result.Blocks[0];
        block.Op.Should().Be("modify");
        block.Old.Should().Be("```csharp\nvar x = 1;\n\nConsole.WriteLine(x);\n```");
        block.New.Should().Be("```csharp\nvar x = 2;\n\nConsole.WriteLine(x);\n```");
    }

    [Fact]
    public void Diff_CompleteRewrite_ReturnsTooLarge()
    {
        var oldBody = string.Join("\n\n", Enumerable.Range(1, 20).Select(i => $"Old paragraph number {i} with some filler text."));
        var newBody = string.Join("\n\n", Enumerable.Range(1, 20).Select(i => $"Completely rewritten paragraph {i} with different filler."));

        var result = _svc.Diff(oldBody, newBody);

        result.TooLarge.Should().BeTrue();
        result.Blocks.Should().BeEmpty();
        result.Similarity.Should().BeLessThan(0.6);
    }
}
