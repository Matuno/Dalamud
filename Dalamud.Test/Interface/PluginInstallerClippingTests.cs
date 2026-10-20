using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

using Dalamud.Interface.Internal.Windows.PluginInstaller;

using Xunit;

namespace Dalamud.Test.Interface;

public class PluginInstallerClippingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(9999)]
    public void ExpandedRowsAlwaysUseTheFullDrawPath(int index)
    {
        // No ImGui context: expanded entries must bypass native clipping entirely.
        var window = (PluginInstallerWindow)RuntimeHelpers.GetUninitializedObject(typeof(PluginInstallerWindow));
        typeof(PluginInstallerWindow).GetField("openPluginCollapsibles", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, new List<int> { index });
        var skip = typeof(PluginInstallerWindow).GetMethod("TrySkipClosedPluginHeader", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(false, skip.Invoke(window, [index]));
    }
}
