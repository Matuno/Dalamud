using System;

using Dalamud.Utility;

using Xunit;

namespace Dalamud.Test.Utility;

public class FuzzyMatcherTests
{
    [Theory]
    [InlineData(null, "foo")]
    [InlineData("", "foo")]
    [InlineData("foo", "foo")]
    [InlineData("foo", "f_o_o")]
    [InlineData("bar", "foo")]
    [InlineData("日本", "日本語")]
    [InlineData("🧰", "a🧰b")]
    [InlineData("\t", "x\ty")]
    [InlineData("é", "café")]
    [InlineData("a\nb", "a\nb")]
    public void UnseparatedTermPreservesFuzzyScore(string? term, string value)
    {
        var fuzzy = new FuzzyMatcher(term!, MatchMode.Fuzzy);
        var parts = new FuzzyMatcher(term!, MatchMode.FuzzyParts);
        Assert.Equal(fuzzy.Matches(value), parts.Matches(value));
    }

    [Fact]
    public void UnseparatedTermConstructionDoesNotAllocate()
    {
        for (var i = 0; i < 1000; i++)
            _ = new FuzzyMatcher("foo", MatchMode.FuzzyParts).Matches("foobar");

        var before = GC.GetAllocatedBytesForCurrentThread();
        var checksum = 0;
        for (var i = 0; i < 10000; i++)
            checksum += new FuzzyMatcher("foo", MatchMode.FuzzyParts).Matches("foobar");

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(checksum > 0);
        Assert.Equal(0, allocated);
    }
}
