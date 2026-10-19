using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.Internal.SharedImmediateTextures;
using Dalamud.Interface.Textures.TextureWraps;

using Xunit;

namespace Dalamud.Test.Interface.Textures;

public class SharedTextureRentalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingRentalHoldsOneReferenceUntilDisposed(bool cancelable)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Texture.RentAsync(cancelable ? cancellation.Token : default);
        Assert.False(pending.IsCompleted);
        Assert.Equal(2, fixture.Texture.RefCountForDebug);
        Assert.True(fixture.Texture.ContentQueried);
        Assert.False(fixture.Texture.IsOpportunistic);
        fixture.Load.SetResult(fixture.Wrap);
        using (var rented = await pending)
        {
            Assert.Equal(fixture.Wrap.Width, rented.Width);
            Assert.Equal(fixture.Wrap.Height, rented.Height);
            Assert.Equal(2, fixture.Texture.RefCountForDebug);
        }

        Assert.Equal(1, fixture.Texture.RefCountForDebug);
        Assert.False(fixture.Wrap.Disposed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LoadFailureReleasesOnlyTheRentalReference(bool cancelable, bool canceledLoad)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Texture.RentAsync(cancelable ? cancellation.Token : default);
        if (canceledLoad)
        {
            fixture.Load.SetCanceled(CancellationToken.None);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        else
        {
            var expected = new InvalidOperationException("Expected texture load failure");
            fixture.Load.SetException(expected);
            Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => pending));
        }

        Assert.Equal(1, fixture.Texture.RefCountForDebug);
    }

    [Fact]
    public async Task AlreadyCanceledRentalDoesNotAcquireAReference()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Texture.RentAsync(cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, fixture.Texture.RefCountForDebug);
        Assert.False(fixture.Texture.ContentQueried);
        Assert.True(fixture.Texture.IsOpportunistic);
    }

    [Fact]
    public async Task CancelingOneRenterDoesNotCancelTheSharedLoadOrOtherRenter()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var canceled = fixture.Texture.RentAsync(cancellation.Token);
        var other = fixture.Texture.RentAsync(CancellationToken.None);
        Assert.Equal(3, fixture.Texture.RefCountForDebug);
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(2, fixture.Texture.RefCountForDebug);
        Assert.False(fixture.Load.Task.IsCompleted);
        Assert.False(other.IsCompleted);
        fixture.Load.SetResult(fixture.Wrap);
        using (await other)
            Assert.Equal(2, fixture.Texture.RefCountForDebug);
        Assert.Equal(1, fixture.Texture.RefCountForDebug);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionAndCancellationKeepReferencesBalanced(bool completeFirst)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Texture.RentAsync(cancellation.Token);
        if (completeFirst)
        {
            fixture.Load.SetResult(fixture.Wrap);
            cancellation.Cancel();
        }
        else
        {
            cancellation.Cancel();
            fixture.Load.SetResult(fixture.Wrap);
        }

        try
        {
            using var rented = await pending;
            Assert.Equal(2, fixture.Texture.RefCountForDebug);
        }
        catch (OperationCanceledException exception)
        {
            Assert.False(completeFirst);
            Assert.Equal(cancellation.Token, exception.CancellationToken);
        }

        Assert.Equal(1, fixture.Texture.RefCountForDebug);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            // Model one existing owner's reference, avoiding the native loader and all service initialization.
            this.Texture = FileSystemSharedImmediateTexture.CreatePlaceholder("offline-rental-test.png");
            typeof(SharedImmediateTexture).GetField("refCount", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this.Texture, 1);
            typeof(SharedImmediateTexture).GetField("resourceReleased", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this.Texture, false);
            this.Texture.UnderlyingWrap = this.Load.Task;
        }

        public SharedImmediateTexture Texture { get; }

        public TaskCompletionSource<IDalamudTextureWrap> Load { get; } = new();

        public FakeWrap Wrap { get; } = new();

        public void Dispose()
        {
            this.Load.TrySetCanceled();
            this.Texture.Release();
        }
    }

    private sealed class FakeWrap : IDalamudTextureWrap
    {
        public ImTextureID Handle => default;

        public int Width => 2;

        public int Height => 3;

        public bool Disposed { get; private set; }

        public void Dispose() => this.Disposed = true;
    }
}
