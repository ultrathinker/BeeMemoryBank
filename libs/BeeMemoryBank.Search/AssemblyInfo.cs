using System.Runtime.CompilerServices;

// Exposes internal members (notably IndexBuilder.SearchRankedReference, the authoritative BM25
// reference implementation kept for the differential parity oracle) to the Search test assembly.
[assembly: InternalsVisibleTo("BeeMemoryBank.Search.Tests")]
