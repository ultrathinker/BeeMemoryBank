using System.IO.Hashing;
using System.Text;

namespace BeeMemoryBank.Search.Segment;

/// <summary>
/// Computes the 64-bit hash used to order and locate entries in a segment's term dictionary.
/// </summary>
/// <param name="term">The term to hash.</param>
public delegate ulong TermHasher(string term);

/// <summary>
/// Default term hashing for the "BMBI" segment format: <see cref="XxHash64"/> over the term's
/// UTF-8 bytes. Exposed as a swappable <see cref="TermHasher"/> delegate (rather than hardcoded
/// into <see cref="SegmentWriter"/>/<see cref="SegmentReader"/>) so tests can substitute a
/// deliberately collision-prone hasher to exercise the hash-collision handling path without
/// needing to find a real 64-bit XxHash64 collision by brute force.
/// </summary>
public static class TermHashing
{
    /// <summary>The hasher every segment is built and read with unless a test overrides it.</summary>
    public static ulong Default(string term)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(term);
        return XxHash64.HashToUInt64(bytes);
    }
}
