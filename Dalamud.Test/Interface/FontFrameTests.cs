using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Internal;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.ManagedFontAtlas.Internals;
using Dalamud.IoC.Internal;
using Dalamud.Utility;

using Xunit;

namespace Dalamud.Test.Interface;

[Collection("Shared texture frame services")]
public class FontFrameTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 512)]
    [InlineData(8, 128)]
    public void RepeatedAccessesReuseOnlyTheCurrentFrameHold(int count, int lookups)
    {
        using var fixture = new Fixture(count);
        for (var frame = 0; frame < 3; frame++)
        {
            for (var i = 0; i < lookups; i++)
            {
                var index = i % count;
                Assert.Equal(Pointer(index + 1), fixture.Fonts[index].LockUntilPostFrame());
            }

            Assert.Equal(count, fixture.Queued);
            foreach (var root in fixture.Roots)
                Assert.Equal(2, root.Count);
            fixture.Drain();
            foreach (var root in fixture.Roots)
                Assert.Equal(1, root.Count);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANewOwnerGetsItsOwnHoldEvenWhenThePointerMatches(bool samePointer)
    {
        using var fixture = new Fixture();
        var first = fixture.Font.LockUntilPostFrame();
        var replacement = fixture.NewRoot();
        var pointer = samePointer ? first : Pointer(2);
        fixture.Font.State.Substance = new Substance(fixture.Font.State, replacement, pointer);
        Assert.Equal(pointer, fixture.Font.LockUntilPostFrame());
        fixture.DropSeed(fixture.Roots[0]);
        Assert.Equal(1, fixture.Roots[0].Count);
        Assert.Equal(2, replacement.Count);
        fixture.Drain();
        Assert.Equal(0, fixture.Roots[0].Count);
        Assert.Equal(1, replacement.Count);
    }

    [Fact]
    public void PointerChangesAndTemporaryMissingFontsRemainVisible()
    {
        using var fixture = new Fixture();
        var substance = (Substance)fixture.Font.State.Substance!;
        Assert.Equal(Pointer(1), fixture.Font.LockUntilPostFrame());
        substance.Font = Pointer(2);
        Assert.Equal(Pointer(2), fixture.Font.LockUntilPostFrame());
        substance.Font = default;
        Assert.True(fixture.Font.LockUntilPostFrame().IsNull);
        substance.Font = Pointer(2);
        Assert.Equal(Pointer(2), fixture.Font.LockUntilPostFrame());
        fixture.Drain();
        Assert.Equal(1, fixture.Roots[0].Count);
    }

    [Fact]
    public void AnUnavailableOrDisposedHandleCannotReturnItsCachedFont()
    {
        using var fixture = new Fixture();
        Assert.False(fixture.Font.LockUntilPostFrame().IsNull);
        var substance = fixture.Font.State.Substance;
        fixture.Font.State.Substance = null;
        Assert.True(fixture.Font.LockUntilPostFrame().IsNull);
        fixture.Font.State.Substance = substance;
        Assert.False(fixture.Font.LockUntilPostFrame().IsNull);
        fixture.Font.Dispose();
        fixture.DropSeed(fixture.Roots[0]);
        Assert.True(fixture.Font.LockUntilPostFrame().IsNull);
        Assert.True(fixture.Roots[0].Count > 0);
        fixture.Drain();
        Assert.Equal(0, fixture.Roots[0].Count);
    }

    [Fact]
    public void PublicLocksAndNewReferencesStayIndependentOfTheFrameCache()
    {
        using var fixture = new Fixture();
        var pointer = fixture.Font.LockUntilPostFrame();
        using var first = fixture.Font.Lock();
        using var second = fixture.Font.Lock();
        using var copy = second.NewRef();
        Assert.NotSame(first, second);
        Assert.NotSame(second, copy);
        first.Dispose();
        Assert.True(first.ImFont.IsNull);
        Assert.Equal(pointer, second.ImFont);
        Assert.Equal(pointer, copy.ImFont);
        Assert.Equal(pointer, fixture.Font.LockUntilPostFrame());
        fixture.Drain();
        Assert.Equal(pointer, second.ImFont);
        Assert.Equal(pointer, copy.ImFont);
    }

    [Fact]
    public void OffThreadLegacyAccessDoesNotShareTheMainThreadCache()
    {
        using var fixture = new Fixture();
        var warningField = typeof(FontHandle).GetField("nextNonMainThreadFontAccessWarningCheck", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previous = warningField.GetValue(null);
        warningField.SetValue(null, long.MaxValue); // Keep the existing warning throttle out of this isolated test.
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < 32; i++)
                    fixture.Font.LockUntilPostFrame();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        try
        {
            worker.Start();
            Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
            Assert.Null(failure);
            Assert.Equal(32, fixture.Queued);
            fixture.Drain();
            Assert.Equal(1, fixture.Roots[0].Count);
        }
        finally
        {
            warningField.SetValue(null, previous);
        }
    }

    [Fact]
    public void AnEndedFrameDoesNotRootItsOldAtlasThroughTheCache()
    {
        using var fixture = new Fixture();
        var root = CompleteTransientAtlas(fixture);
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(root.IsAlive);
        GC.KeepAlive(fixture.Font);
    }

    [Fact]
    public void MissingFontPushStillBalancesItsPrivateStack()
    {
        using var fixture = new Fixture();
        fixture.Font.State.Substance = null;
        using (fixture.Font.Push())
        using (fixture.Font.Push())
            Assert.Equal(0, fixture.Queued);
        fixture.Font.Push();
        fixture.Font.Pop();
        Assert.Equal(0, fixture.Queued);
    }

    [Fact]
    public void AnAbandonedQueueCanFinalizeItsLockWhileTheHandleSurvives()
    {
        var (font, root) = AbandonQueuedFrame();
        for (var i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.Equal(0, root.Count);
        font.Dispose();
        GC.KeepAlive(font);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (OfflineFont Font, Root Root) AbandonQueuedFrame()
    {
        using var fixture = new Fixture();
        fixture.Font.LockUntilPostFrame();
        var font = fixture.Font;
        var root = fixture.Roots[0];
        font.State.Substance = null;
        fixture.DropSeed(root);
        fixture.AbandonFrame = true;
        return (font, root);
    }

    [Fact]
    public void RepeatedFrameAccessAllocatesOnlyOneLockPerFrame()
    {
        using var fixture = new Fixture();
        for (var frame = 0; frame < 100; frame++)
        {
            for (var i = 0; i < 128; i++)
                fixture.Font.LockUntilPostFrame();
            fixture.Drain();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < 1000; frame++)
        {
            for (var i = 0; i < 128; i++)
                fixture.Font.LockUntilPostFrame();
            fixture.Drain();
        }

        Assert.Equal(32_000, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CompleteTransientAtlas(Fixture fixture)
    {
        var root = new Root();
        fixture.Font.State.Substance = new Substance(fixture.Font.State, root, Pointer(20));
        fixture.Font.LockUntilPostFrame();
        fixture.Drain();
        fixture.Font.State.Substance = null;
        root.Release();
        return new WeakReference(root);
    }

    private static ImFontPtr Pointer(nint value) => Unsafe.As<nint, ImFontPtr>(ref value);

    private sealed class Fixture : IDisposable
    {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly FieldInfo ContainerField = typeof(Service<ServiceContainer>).GetField("instanceTcs", BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly FieldInfo InterfaceField = typeof(Service<InterfaceManager>).GetField("instanceTcs", BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly FieldInfo ThreadFlag = typeof(ThreadSafety).GetField("threadStaticIsMainThread", BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly Action<InterfaceManager> Post = typeof(InterfaceManager).GetMethod("PostImGuiRender", PrivateInstance)!.CreateDelegate<Action<InterfaceManager>>();
        private readonly object oldContainer;
        private readonly object oldInterface;
        private readonly bool oldMain = ThreadSafety.IsMainThread;
        private readonly ConcurrentBag<IDisposable> deferred = new();
        private readonly HashSet<Root> seeds = [];
        private readonly InterfaceManager manager;

        public Fixture(int count = 1)
        {
            // Owned service fields and opaque font tokens; the pointer values are never dereferenced.
            this.oldContainer = ContainerField.GetValue(null)!;
            var container = new System.Threading.Tasks.TaskCompletionSource<ServiceContainer>();
            container.SetResult(new ServiceContainer());
            ContainerField.SetValue(null, container);
            this.oldInterface = InterfaceField.GetValue(null)!;
            this.manager = (InterfaceManager)RuntimeHelpers.GetUninitializedObject(typeof(InterfaceManager));
            Set(this.manager, "deferredDisposeDisposables", this.deferred);
            Set(this.manager, "deferredDisposeTextures", new ConcurrentBag<IDeferredDisposable>());
            Set(this.manager, "runAfterImGuiRender", new ConcurrentQueue<Action>());
            var provided = new System.Threading.Tasks.TaskCompletionSource<InterfaceManager>();
            provided.SetResult(this.manager);
            InterfaceField.SetValue(null, provided);
            ThreadSafety.MarkMainThread();
            this.Fonts = new OfflineFont[count];
            this.Roots = new Root[count];
            for (var i = 0; i < count; i++)
            {
                this.Roots[i] = this.NewRoot();
                var fontManager = new Manager();
                fontManager.Substance = new Substance(fontManager, this.Roots[i], Pointer(i + 1));
                this.Fonts[i] = new OfflineFont(fontManager);
            }
        }

        public OfflineFont[] Fonts { get; }

        public Root[] Roots { get; }

        public OfflineFont Font => this.Fonts[0];

        public int Queued => this.deferred.Count;

        public bool AbandonFrame { get; set; }

        public Root NewRoot()
        {
            var root = new Root();
            this.seeds.Add(root);
            return root;
        }

        public void DropSeed(Root root)
        {
            if (this.seeds.Remove(root))
                root.Release();
        }

        public void Drain() => Post(this.manager);

        public void Dispose()
        {
            try
            {
                if (!this.AbandonFrame)
                {
                    foreach (var font in this.Fonts)
                        font.Dispose();
                    this.Drain();
                }
                foreach (var root in this.seeds)
                    root.Release();
            }
            finally
            {
                InterfaceField.SetValue(null, this.oldInterface);
                ContainerField.SetValue(null, this.oldContainer);
                ThreadFlag.SetValue(null, this.oldMain);
            }
        }

        private static void Set(object target, string name, object value) => typeof(InterfaceManager).GetField(name, PrivateInstance)!.SetValue(target, value);
    }

    private sealed class OfflineFont(IFontHandleManager manager) : FontHandle(manager)
    {
        public IFontHandleManager State => this.Manager;
    }

    private sealed class Root : IRefCountable
    {
        private int count = 1;

        public int Count => Volatile.Read(ref this.count);

        public int AddRef() => IRefCountable.AlterRefCount(1, ref this.count, out var value) == IRefCountable.RefCountResult.StillAlive
            ? value
            : throw new ObjectDisposedException(nameof(Root));

        public int Release() => IRefCountable.AlterRefCount(-1, ref this.count, out var value) != IRefCountable.RefCountResult.AlreadyDisposed
            ? value
            : throw new ObjectDisposedException(nameof(Root));
    }

    private sealed class Manager : IFontHandleManager
    {
        public event Action? RebuildRecommend { add { } remove { } }
        public string Name => "Owned font fixture";
        public IFontHandleSubstance? Substance { get; set; }
        public void FreeFontHandle(IFontHandle handle) { }
        public IFontHandleSubstance NewSubstance(IRefCountable root) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class Substance(IFontHandleManager manager, Root root, ImFontPtr font) : IFontHandleSubstance
    {
        public ImFontPtr Font { get; set; } = font;
        public IRefCountable DataRoot => root;
        public IFontHandleManager Manager => manager;
        public ICollection<FontHandle> RelevantHandles => Array.Empty<FontHandle>();
        public ImFontPtr GetFontPtr(IFontHandle handle) => this.Font;
        public Exception? GetBuildException(IFontHandle handle) => null;
        public void OnPreBuild(IFontAtlasBuildToolkitPreBuild toolkit) { }
        public void OnPreBuildCleanup(IFontAtlasBuildToolkitPreBuild toolkit) { }
        public void OnPostBuild(IFontAtlasBuildToolkitPostBuild toolkit) { }
        public void Dispose() { }
    }
}
