using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

using Xunit;

namespace Dalamud.Test.Game.Text.SeStringHandling;

public class SeStringPlainTextTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Example Character")]
    [InlineData("日本語 café 😀")]
    [InlineData("line\r\nline\t\u0003end")]
    [InlineData("before\0after")]
    public void TextMatchesExistingStreamDecoder(string text)
        => CompareToStreamDecoder(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void ArbitraryPlainBytesKeepReplacementTextAndExactOriginalBytes()
    {
        var random = new Random(7051);
        for (var sample = 0; sample < 1000; sample++)
        {
            var bytes = new byte[random.Next(1, 100)];
            random.NextBytes(bytes);
            for (var i = 0; i < bytes.Length; i++)
                if (bytes[i] == 2)
                    bytes[i] = 3;
            CompareToStreamDecoder(bytes);
        }
    }

    [Fact]
    public void StructuredPayloadsRetainTheirOriginalDecoder()
    {
        var message = new SeString(new TextPayload("before"), new UIForegroundPayload(17), new TextPayload("after"));
        CompareToStreamDecoder(message.EncodeWithNullTerminator());
    }

    [Fact]
    public void ParsedTextOwnsItsBytesAndReencodesEdits()
    {
        var bytes = new byte[] { 0xFF, (byte)'A', 0xC0, 0xAF };
        var original = (byte[])bytes.Clone();
        var parsed = SeString.Parse(bytes);
        var payload = Assert.IsType<TextPayload>(Assert.Single(parsed.Payloads));
        Assert.False(payload.Dirty);
        Array.Clear(bytes);
        Assert.Equal(original, payload.Encode());
        Assert.Equal(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(original)), payload.Encode(true));
        payload.Text = "changed";
        Assert.True(payload.Dirty);
        Assert.Equal(Encoding.UTF8.GetBytes("changed"), parsed.Encode());
        Assert.False(payload.Dirty);
    }

    [Fact]
    public void EachParseKeepsIndependentMutablePayloads()
    {
        var bytes = Encoding.UTF8.GetBytes("text");
        var first = SeString.Parse(bytes);
        var second = SeString.Parse(bytes);
        Assert.NotSame(first.Payloads, second.Payloads);
        Assert.NotSame(first.Payloads[0], second.Payloads[0]);
        ((TextPayload)first.Payloads[0]).Text = "edited";
        Assert.Equal("text", second.TextValue);
        Assert.Empty(SeString.Parse((byte[])null!).Payloads);
    }

    [Fact]
    public void PlainNameParsingAvoidsStreamDecoderAllocations()
    {
        var bytes = Encoding.UTF8.GetBytes("Example Character");
        for (var i = 0; i < 4096; i++)
            GC.KeepAlive(SeString.Parse(bytes));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            GC.KeepAlive(SeString.Parse(bytes));
        // Allow the returned objects and owned byte cache; the old stream path used 640 bytes per call.
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 300_000);
    }

    private static void CompareToStreamDecoder(byte[] bytes)
    {
        var length = Array.IndexOf(bytes, (byte)0);
        if (length < 0)
            length = bytes.Length;
        using var stream = new MemoryStream(bytes, 0, length, false);
        using var reader = new BinaryReader(stream);
        var reference = new List<Payload>();
        while (stream.Position < stream.Length)
            reference.Add(Payload.Decode(reader));
        var actual = SeString.Parse(bytes);
        Assert.Equal(reference.Count, actual.Payloads.Count);
        for (var i = 0; i < reference.Count; i++)
        {
            Assert.Equal(reference[i].Type, actual.Payloads[i].Type);
            Assert.Equal(reference[i].Dirty, actual.Payloads[i].Dirty);
            if (reference[i] is TextPayload text)
                Assert.Equal(text.Text, Assert.IsType<TextPayload>(actual.Payloads[i]).Text);
            Assert.Equal(reference[i].Encode(), actual.Payloads[i].Encode());
            Assert.Equal(reference[i].Encode(true), actual.Payloads[i].Encode(true));
        }
    }
}
