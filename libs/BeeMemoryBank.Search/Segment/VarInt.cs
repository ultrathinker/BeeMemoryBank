namespace BeeMemoryBank.Search.Segment;

/// <summary>
/// Unsigned LEB128-style variable-length integer encoding used by the postings block: 7 payload
/// bits per byte, with the high bit set meaning "another byte follows". Small values (the common
/// case for docId deltas and term frequencies) cost a single byte; nothing in the format ever
/// needs more than the 10 bytes required for a full 64-bit value.
/// </summary>
public static class VarInt
{
    /// <summary>Appends the varint encoding of <paramref name="value"/> to <paramref name="buffer"/>.</summary>
    public static void WriteUInt64(List<byte> buffer, ulong value)
    {
        while (value >= 0x80)
        {
            buffer.Add((byte)(value | 0x80));
            value >>= 7;
        }

        buffer.Add((byte)value);
    }

    /// <summary>
    /// Reads one varint out of <paramref name="source"/> starting at <paramref name="offset"/>,
    /// advancing <paramref name="offset"/> past the bytes it consumed. Deliberately takes a
    /// <see cref="ReadOnlyMemory{T}"/> rather than a <see cref="ReadOnlySpan{T}"/> so callers can
    /// use this from iterator methods (a <see cref="Span{T}"/> local cannot survive a
    /// <c>yield return</c>, but the span created inside this method never needs to).
    /// </summary>
    public static ulong ReadUInt64(ReadOnlyMemory<byte> source, ref int offset)
    {
        ReadOnlySpan<byte> span = source.Span;
        ulong result = 0;
        int shift = 0;

        while (true)
        {
            byte b = span[offset++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                break;
            }

            shift += 7;
        }

        return result;
    }
}
