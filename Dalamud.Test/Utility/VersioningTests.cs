using System;
using System.Threading.Tasks;

using Dalamud.Utility;

using Xunit;

namespace Dalamud.Test.Utility;

public class VersioningTests
{
    [Fact]
    public void VersionValuesMatchTheLoadedFrameworkAssembly()
    {
        var expected = typeof(Versioning).Assembly.GetName().Version!;
        Assert.Equal(expected, Versioning.GetAssemblyVersionParsed());
        Assert.Equal(expected.ToString(), Versioning.GetAssemblyVersion());
    }

    [Fact]
    public void WarmVersionQueriesAllocateNothing()
    {
        var version = Versioning.GetAssemblyVersionParsed();
        var text = Versioning.GetAssemblyVersion();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 128; i++)
        {
            if (Versioning.GetAssemblyVersionParsed() != version || Versioning.GetAssemblyVersion() != text)
                throw new InvalidOperationException("Loaded framework version changed.");
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void ConcurrentQueriesShareImmutableValues()
    {
        var version = Versioning.GetAssemblyVersionParsed();
        var text = Versioning.GetAssemblyVersion();
        Parallel.For(0, 64, _ =>
        {
            Assert.Same(version, Versioning.GetAssemblyVersionParsed());
            Assert.Same(text, Versioning.GetAssemblyVersion());
        });
    }
}
