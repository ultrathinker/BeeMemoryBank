using BeeMemoryBank.Search.Segment;

namespace BeeMemoryBank.Search.Tests.Segment;

public class VarIntTests
{
    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(127UL)] // largest 1-byte value
    [InlineData(128UL)] // smallest 2-byte value
    [InlineData(16383UL)] // largest 2-byte value
    [InlineData(16384UL)] // smallest 3-byte value
    [InlineData(int.MaxValue)]
    [InlineData(ulong.MaxValue)]
    public void WriteThenRead_RoundtripsExactValue(ulong value)
    {
        var buffer = new List<byte>();
        VarInt.WriteUInt64(buffer, value);

        int offset = 0;
        ulong result = VarInt.ReadUInt64(buffer.ToArray(), ref offset);

        result.Should().Be(value);
        offset.Should().Be(buffer.Count, "the reader should consume exactly the bytes the writer produced");
    }

    [Fact]
    public void Write_SmallValue_UsesSingleByte()
    {
        var buffer = new List<byte>();
        VarInt.WriteUInt64(buffer, 42);

        buffer.Should().ContainSingle();
    }

    [Fact]
    public void Write_ValueAboveSevenBits_SetsContinuationBitOnFirstByte()
    {
        var buffer = new List<byte>();
        VarInt.WriteUInt64(buffer, 300);

        buffer.Should().HaveCountGreaterThan(1);
        (buffer[0] & 0x80).Should().Be(0x80, "the first byte must signal continuation");
        (buffer[^1] & 0x80).Should().Be(0, "the last byte must not signal continuation");
    }

    [Fact]
    public void ReadUInt64_MultipleSequentialValues_AdvancesOffsetCorrectly()
    {
        var buffer = new List<byte>();
        ulong[] values = [0, 1, 300, 70000, ulong.MaxValue, 5];
        foreach (ulong value in values)
        {
            VarInt.WriteUInt64(buffer, value);
        }

        byte[] bytes = buffer.ToArray();
        int offset = 0;
        var decoded = new List<ulong>();
        while (offset < bytes.Length)
        {
            decoded.Add(VarInt.ReadUInt64(bytes, ref offset));
        }

        decoded.Should().Equal(values);
    }
}
