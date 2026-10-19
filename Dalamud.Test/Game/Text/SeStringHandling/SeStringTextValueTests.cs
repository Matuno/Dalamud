using System;
using System.IO;
using System.Text;

using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

using Xunit;

namespace Dalamud.Test.Game.Text.SeStringHandling;

public class SeStringTextValueTests
{
    [Fact]
    public void ExtractionKeepsOrderAndReflectsPayloadMutations()
    {
        var first = new TextPayload(null);
        var last = new TextPayload("世界");
        var value = new SeString(first, new UIForegroundPayload(17), new TextPayload(string.Empty), last, null!);
        Assert.Equal("世界", value.TextValue);
        first.Text = "Hello ";
        Assert.Equal("Hello 世界", value.TextValue);
        last.Text = "new";
        Assert.Equal("Hello new", value.TextValue);
        value.Payloads.Remove(first);
        Assert.Equal("new", value.TextValue);
        value.Payloads.Clear();
        Assert.Equal(string.Empty, value.TextValue);
    }

    [Fact]
    public void EveryProviderIsReadOnceInOrder()
    {
        var calls = new StringBuilder();
        var value = new SeString(
            new DynamicText(() => { calls.Append('1'); return null; }),
            new DynamicText(() => { calls.Append('2'); return "A"; }),
            new DynamicText(() => { calls.Append('3'); return string.Empty; }),
            new DynamicText(() => { calls.Append('4'); return "B"; }));
        Assert.Equal("AB", value.TextValue);
        Assert.Equal("1234", calls.ToString());
    }

    [Fact]
    public void ProviderFailuresAndMutationDuringEnumerationRemainVisible()
    {
        var expected = new InvalidOperationException("Expected provider failure");
        var failing = new SeString(new DynamicText(() => throw expected));
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() => failing.TextValue));
        var mutating = new SeString();
        mutating.Payloads.Add(new DynamicText(() => { mutating.Payloads.Clear(); return "text"; }));
        Assert.Throws<InvalidOperationException>(() => mutating.TextValue);
    }

    [Fact]
    public void SingleTextExtractionDoesNotAllocateTemporaryObjects()
    {
        var value = new SeString(new TextPayload("Example Character"));
        for (var i = 0; i < 4096; i++)
            _ = value.TextValue;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            _ = value.TextValue;
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private sealed class DynamicText(Func<string?> read) : Payload, ITextProvider
    {
        public override PayloadType Type => PayloadType.RawText;

        public string Text => read()!;

        protected override byte[] EncodeImpl() => throw new NotSupportedException();

        protected override void DecodeImpl(BinaryReader reader, long endOfStream) => throw new NotSupportedException();
    }
}
