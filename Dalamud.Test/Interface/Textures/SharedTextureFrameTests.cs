using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Internal;
using Dalamud.Interface.Textures.Internal;
using Dalamud.Interface.Textures.Internal.SharedImmediateTextures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.IoC.Internal;
using Dalamud.Utility;

using Xunit;

namespace Dalamud.Test.Interface.Textures;

[CollectionDefinition("Shared texture frame services", DisableParallelization = true)]
public class SharedTextureFrameCollection
{
}

[Collection("Shared texture frame services")]
public class SharedTextureFrameTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 512)]
    [InlineData(16, 256)]
    public void RepeatedQueriesShareOneFrameReference(int textures, int lookups)
    {
        using var fixture = new FrameFixture(textures);
        for (var frame = 0; frame < 3; frame++)
        {
            for (var i = 0; i < lookups; i++)
            {
                var texture = fixture.Textures[i % textures];
                Assert.True(texture.PublicUseInstance.TryGetWrap(out var wrap, out var error));
                Assert.Null(error);
                Assert.Equal(8, wrap.Width);
                Assert.Same(wrap, texture.GetWrapOrDefault(null));
                wrap.Dispose(); // A non-owning drawing wrapper still ignores Dispose.
            }

            Assert.Equal(textures, fixture.Queued);
            Assert.All(fixture.Textures, texture => Assert.Equal(3, texture.RefCountForDebug));
            fixture.Drain();
            Assert.Equal(0, fixture.Queued);
            Assert.All(fixture.Textures, texture => Assert.Equal(2, texture.RefCountForDebug));
            Assert.All(fixture.Wraps, wrap => Assert.Equal(0, wrap.DisposeCount));
        }
    }

    [Fact]
    public void ALoadCanCompleteBetweenQueriesInTheSameFrame()
    {
        using var fixture = new FrameFixture();
        var completion = new TaskCompletionSource<IDalamudTextureWrap>();
        fixture.Texture.UnderlyingWrap = completion.Task;
        Assert.False(fixture.Texture.TryGetWrap(out var missing, out var error));
        Assert.Null(missing);
        Assert.Null(error);
        Assert.Same(fixture.Wraps[0], fixture.Texture.GetWrapOrDefault(fixture.Wraps[0]));
        completion.SetResult(fixture.Wraps[0]);
        Assert.True(fixture.Texture.TryGetWrap(out var ready, out error));
        Assert.Equal(8, ready.Width);
        Assert.Null(error);
        Assert.Equal(1, fixture.Queued);
        fixture.Drain();
        Assert.Equal(2, fixture.Texture.RefCountForDebug);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedOrCanceledLoadsStillBalanceTheirFrameReference(bool canceled)
    {
        using var fixture = new FrameFixture();
        var completion = new TaskCompletionSource<IDalamudTextureWrap>();
        var expected = new InvalidOperationException("Expected owned load failure");
        if (canceled)
            completion.SetCanceled(CancellationToken.None);
        else
            completion.SetException(expected);
        fixture.Texture.UnderlyingWrap = completion.Task;
        for (var i = 0; i < 64; i++)
        {
            Assert.False(fixture.Texture.TryGetWrap(out var missing, out var error));
            Assert.Null(missing);
            if (canceled)
                Assert.Null(error);
            else
                Assert.Same(expected, Assert.IsType<AggregateException>(error).InnerException);
        }

        Assert.Equal(1, fixture.Queued);
        fixture.Drain();
        Assert.Equal(2, fixture.Texture.RefCountForDebug);
    }

    [Fact]
    public void InvalidationCannotReleaseATextureBeforeTheRenderDrain()
    {
        using var fixture = new FrameFixture();
        Assert.True(fixture.Texture.TryGetWrap(out var wrap, out _));
        fixture.DropSeeds();
        fixture.Texture.ReleaseSelfReference(true);
        Assert.Equal(1, fixture.Texture.RefCountForDebug);
        for (var i = 0; i < 512; i++)
            Assert.Same(wrap, fixture.Texture.GetWrapOrDefault(null));
        fixture.Texture.ReleaseSelfReference(true);
        Assert.Equal(0, fixture.Wraps[0].DisposeCount);
        Assert.Equal(8, wrap.Width);
        fixture.Drain();
        Assert.True(SpinWait.SpinUntil(() => fixture.Wraps[0].DisposeCount == 1, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, fixture.Texture.RefCountForDebug);
    }

    [Fact]
    public async Task RentalsAndCopiedReferencesOutliveTheFrameIndependently()
    {
        using var fixture = new FrameFixture();
        Assert.True(fixture.Texture.TryGetWrap(out var drawing, out _));
        var rental = await fixture.Texture.RentAsync(CancellationToken.None);
        var copy = drawing.CreateWrapSharingLowLevelResource();
        try
        {
            fixture.DropSeeds();
            fixture.Texture.ReleaseSelfReference(true);
            fixture.Drain();
            Assert.Equal(8, rental.Width);
            Assert.Equal(8, copy.Width);
            Assert.Equal(0, fixture.Wraps[0].DisposeCount);
        }
        finally
        {
            rental.Dispose();
            copy.Dispose();
        }

        Assert.True(SpinWait.SpinUntil(() => fixture.Wraps[0].DisposeCount == 1, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, fixture.Texture.RefCountForDebug);
    }

    [Fact]
    public void ConcurrentSelfReferenceReleaseKeepsTheFrameHoldAlive()
    {
        using var fixture = new FrameFixture();
        Assert.True(fixture.Texture.TryGetWrap(out var drawing, out _));
        fixture.DropSeeds();
        using var start = new ManualResetEventSlim();
        using var waiting = new ManualResetEventSlim();
        Exception? workerError = null;
        var worker = new Thread(() =>
        {
            waiting.Set();
            start.Wait(CancellationToken.None);
            try
            {
                for (var i = 0; i < 20_000; i++)
                    fixture.Texture.ReleaseSelfReference(true);
            }
            catch (Exception error)
            {
                workerError = error;
            }
        });
        worker.Start();
        try
        {
            Assert.True(waiting.Wait(TimeSpan.FromSeconds(5), CancellationToken.None));
            start.Set();
            for (var i = 0; i < 20_000; i++)
                Assert.Same(drawing, fixture.Texture.GetWrapOrDefault(null));
        }
        finally
        {
            start.Set();
            Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        }

        Assert.Null(workerError);
        Assert.Equal(0, fixture.Wraps[0].DisposeCount);
        fixture.Texture.ReleaseSelfReference(true);
        fixture.Drain();
        Assert.True(SpinWait.SpinUntil(() => fixture.Wraps[0].DisposeCount == 1, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void DrawingLookupsStillRejectOtherThreads()
    {
        using var fixture = new FrameFixture();
        Exception? error = null;
        var worker = new Thread(() =>
        {
            try
            {
                fixture.Texture.TryGetWrap(out _, out _);
            }
            catch (Exception exception)
            {
                error = exception;
            }
        });
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(0, fixture.Queued);
        Assert.Equal(1, fixture.Texture.RefCountForDebug);
    }

    private sealed class FrameFixture : IDisposable
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo ContainerField = typeof(Service<ServiceContainer>).GetField("instanceTcs", BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly FieldInfo InterfaceField = typeof(Service<InterfaceManager>).GetField("instanceTcs", BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly FieldInfo TextureField = typeof(Service<TextureManager>).GetField("instanceTcs", BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly FieldInfo ThreadFlag = typeof(ThreadSafety).GetField("threadStaticIsMainThread", BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly Action<InterfaceManager> Post = typeof(InterfaceManager).GetMethod("PostImGuiRender", PrivateInstance)!.CreateDelegate<Action<InterfaceManager>>();

        private readonly object oldContainer;
        private readonly object oldInterface;
        private readonly object oldTexture;
        private readonly bool oldMain = ThreadSafety.IsMainThread;
        private readonly ConcurrentBag<IDisposable> deferred = new();
        private readonly InterfaceManager manager;
        private bool seedsHeld = true;

        public FrameFixture(int textures = 1)
        {
            // Only service-locator fields are temporarily replaced; no native hooks or game services are constructed.
            this.oldContainer = ContainerField.GetValue(null)!;
            var container = new TaskCompletionSource<ServiceContainer>();
            container.SetResult(new ServiceContainer());
            ContainerField.SetValue(null, container);
            this.oldInterface = InterfaceField.GetValue(null)!;
            this.oldTexture = TextureField.GetValue(null)!;
            var forbiddenLoader = new TaskCompletionSource<TextureManager>();
            forbiddenLoader.SetException(new InvalidOperationException("Unexpected loader access in an owned frame fixture"));
            _ = forbiddenLoader.Task.Exception;
            TextureField.SetValue(null, forbiddenLoader);
            this.manager = (InterfaceManager)RuntimeHelpers.GetUninitializedObject(typeof(InterfaceManager));
            Set(this.manager, "deferredDisposeDisposables", this.deferred);
            Set(this.manager, "deferredDisposeTextures", new ConcurrentBag<IDeferredDisposable>());
            Set(this.manager, "runAfterImGuiRender", new ConcurrentQueue<Action>());
            var provided = new TaskCompletionSource<InterfaceManager>();
            provided.SetResult(this.manager);
            InterfaceField.SetValue(null, provided);
            ThreadSafety.MarkMainThread();
            this.Textures = new SharedImmediateTexture[textures];
            this.Wraps = new FakeWrap[textures];
            for (var i = 0; i < textures; i++)
            {
                this.Wraps[i] = new FakeWrap();
                var texture = FileSystemSharedImmediateTexture.CreatePlaceholder("owned-frame-" + i);
                Set(texture, "refCount", 1);
                Set(texture, "resourceReleased", false);
                texture.UnderlyingWrap = Task.FromResult<IDalamudTextureWrap>(this.Wraps[i]);
                this.Textures[i] = texture;
            }
        }

        public SharedImmediateTexture[] Textures { get; }

        public FakeWrap[] Wraps { get; }

        public SharedImmediateTexture Texture => this.Textures[0];

        public int Queued => this.deferred.Count;

        public void Drain() => Post(this.manager);

        public void DropSeeds()
        {
            if (!this.seedsHeld)
                return;
            this.seedsHeld = false;
            foreach (var texture in this.Textures)
                texture.Release();
        }

        public void Dispose()
        {
            try
            {
                this.Drain();
                foreach (var texture in this.Textures)
                    texture.ReleaseSelfReference(true);
                this.DropSeeds();
            }
            finally
            {
                InterfaceField.SetValue(null, this.oldInterface);
                TextureField.SetValue(null, this.oldTexture);
                ContainerField.SetValue(null, this.oldContainer);
                ThreadFlag.SetValue(null, this.oldMain);
            }
        }

        private static void Set(object target, string field, object value)
        {
            var type = target is SharedImmediateTexture ? typeof(SharedImmediateTexture) : typeof(InterfaceManager);
            type.GetField(field, PrivateInstance)!.SetValue(target, value);
        }
    }

    private sealed class FakeWrap : IDalamudTextureWrap
    {
        private int disposeCount;

        public ImTextureID Handle => default;

        public int Width => 8;

        public int Height => 4;

        public int DisposeCount => Volatile.Read(ref this.disposeCount);

        public void Dispose() => Interlocked.Increment(ref this.disposeCount);
    }
}
