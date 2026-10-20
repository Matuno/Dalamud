using System;
using System.Reflection;
using System.Runtime.CompilerServices;

using Dalamud.Interface.Internal.Windows.PluginInstaller;
using Dalamud.Plugin.Internal.Types.Manifest;

using Xunit;

namespace Dalamud.Test.Interface;

public class PluginInstallerSearchTests
{
    [Theory]
    [InlineData(0, 13640)]
    [InlineData(1, 13020)]
    [InlineData(2, 12400)]
    [InlineData(3, 12400)]
    [InlineData(4, 12400)]
    [InlineData(5, 0)]
    [InlineData(6, 13640)]
    [InlineData(7, 0)]
    public void SearchRetainsFieldWeightsAndMaximum(int field, int expected)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        // Avoid window/game/service constructors for this managed scoring test.
        var window = (PluginInstallerWindow)RuntimeHelpers.GetUninitializedObject(typeof(PluginInstallerWindow));
        typeof(PluginInstallerWindow).GetField("searchText", flags)!.SetValue(window, "foo");
        var score = typeof(PluginInstallerWindow).GetMethod("GetManifestSearchScore", flags)!
                     .CreateDelegate<Func<IPluginManifest, int>>(window);
        var manifest = new RemotePluginManifest
        {
            Name = field is 0 or 6 ? "Foo" : field == 5 ? null! : "Other",
            InternalName = field is 1 or 6 ? "Foo" : field == 5 ? null! : "Other",
            Author = field is 2 or 6 ? "Foo" : null,
            Punchline = field is 3 or 6 ? "Foo" : null,
            Tags = field is 4 or 6 ? ["Other", "Foo"] : field == 7 ? [] : null,
        };
        Assert.Equal(expected, score(manifest));
    }
}
