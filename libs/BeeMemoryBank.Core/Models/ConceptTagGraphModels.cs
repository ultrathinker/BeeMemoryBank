namespace BeeMemoryBank.Core.Models;

public record ConceptTagGraphNode(string Name, int ArticleCount, string Group, int TotalNeighbors);

public record ConceptTagGraphData(List<ConceptTagGraphNode> Nodes, List<ConceptGraphEdge> Edges);

public record ConceptTagEdgeStats(
    long TotalTags,
    long NodesWithEdges,
    long TotalEdgeRows,
    long UniquePairs,
    int MaxWeight,
    double AvgWeight);

public record ConceptTagEdgeRebuildReport(
    long BeforeEdgeRows,
    long ExpectedEdgeRows,
    long AfterEdgeRows,
    bool WasConsistent,
    bool Rebuilt,
    long DurationMs);
