using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using Dalamud.Game.Text.SeStringHandling;

using Xunit;

namespace Dalamud.Test.Game.Text.SeStringHandling;

public class PackedIntegerEncodingTests
{
    private static readonly Func<uint, byte[]> Encode = typeof(Payload).GetMethod("MakeInteger", BindingFlags.Static | BindingFlags.NonPublic)!
        .CreateDelegate<Func<uint, byte[]>>();

    private static readonly Func<BinaryReader, uint> Decode = typeof(Payload).GetMethod("GetInteger", BindingFlags.Static | BindingFlags.NonPublic)!
        .CreateDelegate<Func<BinaryReader, uint>>();

    [Fact]
    public void BoundariesAndSeededValuesKeepTheirBytesAndOwnership()
    {
        uint[] boundaries = [0, 1, 0xCE, 0xCF, 0xD0, 0xFF, 0x100, 0xFFFF, 0x10000, 0x1000000, 0x12003400, uint.MaxValue];
        foreach (var value in boundaries)
            Check(value);
        var random = new Random(20261020);
        for (var sample = 0; sample < 1000; sample++)
            Check((uint)random.NextInt64(0, 1L << 32));

        var first = Encode(0x12003400);
        var second = Encode(0x12003400);
        Assert.NotSame(first, second);
        Array.Clear(first);
        Assert.Equal(LegacyEncode(0x12003400), second);
    }

    [Fact]
    public void LargeValuesAllocateOnlyTheReturnedBuffer()
    {
        for (var i = 0; i < 1024; i++)
            GC.KeepAlive(Encode(uint.MaxValue));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 256; i++)
            GC.KeepAlive(Encode(uint.MaxValue));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 40 * 256);
    }

    private static void Check(uint value)
    {
        var encoded = Encode(value);
        Assert.Equal(LegacyEncode(value), encoded);
        using var stream = new MemoryStream(encoded);
        using var reader = new BinaryReader(stream);
        Assert.Equal(value, Decode(reader));
        Assert.Equal(encoded.Length, stream.Position);
    }

    private static byte[] LegacyEncode(uint value)
    {
        if (value < 0xCF)
            return [(byte)(value + 1)];
        var bytes = BitConverter.GetBytes(value);
        var result = new List<byte> { 0xF0 };
        for (var i = 3; i >= 0; i--)
        {
            if (bytes[i] != 0)
            {
                result.Add(bytes[i]);
                result[0] |= (byte)(1 << i);
            }
        }

        result[0]--;
        return result.ToArray();
    }
}
