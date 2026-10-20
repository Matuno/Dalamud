using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using Dalamud.Plugin;
using Dalamud.Plugin.Internal;
using Dalamud.Plugin.Internal.Types;
using Dalamud.Plugin.Internal.Types.Manifest;

using Xunit;

namespace Dalamud.Test;

public class PluginMembershipSnapshotTests
{
    [Fact]
    public void UnchangedMembershipReusesAnImmutableSnapshot()
    {
        var manager = CreateManager(CreatePlugin("first"), CreatePlugin("second"));
        var snapshot = manager.ExposedInstalledPlugins;
        Assert.Same(snapshot, manager.ExposedInstalledPlugins);
        Assert.Equal(new[] { "first", "second" }, snapshot.Select(x => x.InternalName));
        var collection = Assert.IsAssignableFrom<ICollection<IExposedPlugin>>(snapshot);
        Assert.True(collection.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => collection.Clear());
        Assert.Equal(2, snapshot.Count());
    }

    [Fact]
    public void ExistingWrappersReflectCurrentPluginStateAndVersion()
    {
        var plugin = CreatePlugin("fixture");
        var manager = CreateManager(plugin);
        var snapshot = manager.ExposedInstalledPlugins;
        var wrapper = Assert.Single(snapshot);
        Assert.False(wrapper.IsLoaded);
        typeof(LocalPlugin).GetProperty(nameof(LocalPlugin.State))!.SetValue(plugin, PluginState.Loaded);
        var manifest = (LocalPluginManifest)plugin.Manifest;
        manifest.AssemblyVersion = new Version(2, 0);
        Assert.Same(snapshot, manager.ExposedInstalledPlugins);
        Assert.True(wrapper.IsLoaded);
        Assert.Equal(new Version(2, 0), wrapper.Version);
    }

    [Fact]
    public void RemovalInvalidatesBeforeNotifyingAndPreservesOlderMembership()
    {
        var first = CreatePlugin("first");
        var second = CreatePlugin("second");
        var manager = CreateManager(first, second);
        var old = manager.ExposedInstalledPlugins;
        string[]? observed = null;
        manager.OnInstalledPluginsChanged += () => observed = manager.ExposedInstalledPlugins.Select(x => x.InternalName).ToArray();
        manager.RemovePlugin(first);
        Assert.NotSame(old, manager.ExposedInstalledPlugins);
        Assert.Equal(new[] { "second" }, observed);
        Assert.Equal(new[] { "first", "second" }, old.Select(x => x.InternalName));
        Assert.Equal("second", Assert.Single(manager.ExposedInstalledPlugins).InternalName);
    }

    [Fact]
    public void ConcurrentReadersShareOneSnapshot()
    {
        var manager = CreateManager(CreatePlugin("fixture"));
        var snapshots = new IEnumerable<IExposedPlugin>[32];
        Parallel.For(0, snapshots.Length, i => snapshots[i] = manager.ExposedInstalledPlugins);
        Assert.All(snapshots, snapshot => Assert.Same(snapshots[0], snapshot));
    }

    [Fact]
    public void WarmSnapshotQueriesDoNotAllocate()
    {
        var manager = CreateManager(CreatePlugin("fixture"));
        for (var i = 0; i < 32; i++)
            GC.KeepAlive(manager.ExposedInstalledPlugins);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1024; i++)
            GC.KeepAlive(manager.ExposedInstalledPlugins);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static LocalPlugin CreatePlugin(string name)
    {
        var plugin = (LocalPlugin)RuntimeHelpers.GetUninitializedObject(typeof(LocalPlugin));
        SetField(plugin, "manifest", new LocalPluginManifest { Name = name, InternalName = name, AssemblyVersion = new Version(1, 0) });
        return plugin;
    }

    private static PluginManager CreateManager(params LocalPlugin[] plugins)
    {
        // Skip game startup and filesystem constructors; exercise the real snapshot/removal code with managed fixtures.
        var manager = (PluginManager)RuntimeHelpers.GetUninitializedObject(typeof(PluginManager));
        SetField(manager, "installedPluginsList", plugins.ToList());
        foreach (var name in new[] { "pluginListLock", "availablePluginsList", "updatablePluginsList" })
        {
            var field = typeof(PluginManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
            field.SetValue(manager, Activator.CreateInstance(field.FieldType));
        }

        return manager;
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
