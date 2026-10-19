using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Dalamud.Interface.Textures.Internal;
using Dalamud.Interface.Textures.Internal.SharedImmediateTextures;

using Xunit;

namespace Dalamud.Test.Interface.Textures;

public class SharedFileTextureTests
{
    [Fact]
    public void FileStringAndFileInfoUseTheSameCanonicalCacheKeys()
    {
        using var canceled = new CancellationTokenSource();
        var manager = CreateManager(canceled);
        foreach (var path in new[]
        {
            "icon.png", @".\icons\..\icon.png", @"C:\icons\..\icon.png", @"C:/icons/icon.png",
            @"C:icon.png", @"\icons\icon.png", @"\\server\share\icons\icon.png",
            @"\\?\C:\icons\icon.png", @"C:\icons\日本語.png", @"C:\icons\file with spaces.png",
        })
        {
            var file = new FileInfo(path);
            var fromString = manager.GetFromFile(path);
            Assert.Same(manager.GetFromFile(file), fromString);
            Assert.Same(manager.GetFromFileAbsolute(file.FullName), fromString);
        }

        Assert.Throws<ArgumentNullException>(() => manager.GetFromFile((string)null!));
        Assert.Throws<ArgumentException>(() => manager.GetFromFile(string.Empty));
        Assert.Throws<ArgumentException>(() => manager.GetFromFile("bad\0path"));
        canceled.Cancel();
        Assert.Throws<ObjectDisposedException>(() => manager.GetFromFile((string)null!));
    }

    [Fact]
    public void WarmAbsolutePathLookupsDoNotCreateFileInfoObjects()
    {
        using var canceled = new CancellationTokenSource();
        var manager = CreateManager(canceled);
        var path = Path.GetFullPath("shared-texture-allocation-check.png");
        var expected = manager.GetFromFile(path);
        for (var i = 0; i < 4096; i++)
            _ = manager.GetFromFile(path);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            _ = manager.GetFromFile(path);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Same(expected, manager.GetFromFile(path));
    }

    private static TextureManager.SharedTextureManager CreateManager(CancellationTokenSource cancellation)
    {
        // Placeholder lookup needs no texture loading, release thread, filesystem access or native device.
        var manager = (TextureManager.SharedTextureManager)RuntimeHelpers.GetUninitializedObject(typeof(TextureManager.SharedTextureManager));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(TextureManager.SharedTextureManager).GetField("disposingCancellationTokenSource", flags)!.SetValue(manager, cancellation);
        typeof(TextureManager.SharedTextureManager).GetField("fileDict", flags)!.SetValue(manager, new ConcurrentDictionary<string, SharedImmediateTexture>());
        return manager;
    }
}
