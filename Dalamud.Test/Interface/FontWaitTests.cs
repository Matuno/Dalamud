using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.ManagedFontAtlas.Internals;
using Dalamud.Utility;

using Xunit;

namespace Dalamud.Test.Interface;

public class FontWaitTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDetachesWaitHandlers(bool alreadyCanceled)
    {
        using var font = new OfflineFont(new Manager());
        using var cancellation = new CancellationTokenSource();
        if (alreadyCanceled)
            cancellation.Cancel();
        var wait = font.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        AssertDetached(font);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FontSignalCompletesWaitAndDetachesHandlers(bool cancelable)
    {
        using var font = new OfflineFont(new Manager());
        using var cancellation = new CancellationTokenSource();
        var wait = font.WaitAsync(cancelable ? cancellation.Token : CancellationToken.None);
        Assert.False(wait.IsCompleted);
        font.InvokeImFontChanged(null!); // Wait handlers do not read the native font argument.
        Assert.Same(font, await wait);
        AssertDetached(font);
        cancellation.Cancel();
        Assert.True(wait.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DisposalFaultsPendingWaitAndClearsHandlers()
    {
        var manager = new Manager();
        var font = new OfflineFont(manager);
        var wait = font.WaitAsync(CancellationToken.None);
        font.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => wait);
        Assert.Equal(1, manager.Freed);
        AssertDetached(font);
        Assert.Throws<ObjectDisposedException>(() => { _ = font.WaitAsync(CancellationToken.None); });
    }

    [Fact]
    public async Task SignalDuringAvailabilityRecheckIsObserved()
    {
        var manager = new Manager();
        using var font = new OfflineFont(manager);
        var reads = 0;
        manager.OnRead = () =>
        {
            if (++reads == 2)
                font.InvokeImFontChanged(null!);
        };
        Assert.Same(font, await font.WaitAsync(CancellationToken.None));
        AssertDetached(font);
    }

    [Fact]
    public async Task FailedAvailabilityRecheckDetachesHandlers()
    {
        var manager = new Manager();
        using var font = new OfflineFont(manager);
        var expected = new InvalidOperationException("Expected availability failure");
        var reads = 0;
        manager.OnRead = () =>
        {
            if (++reads == 2)
                throw expected;
        };
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => font.WaitAsync(CancellationToken.None)));
        AssertDetached(font);
    }

    [Fact]
    public async Task RacingCancellationAndFontSignalsLeaveNoHandlers()
    {
        for (var i = 0; i < 32; i++)
        {
            using var font = new OfflineFont(new Manager());
            using var cancellation = new CancellationTokenSource();
            var wait = font.WaitAsync(cancellation.Token);
            await Task.WhenAll(
                Task.Run(() => cancellation.Cancel(), TestContext.Current.CancellationToken),
                Task.Run(() => font.InvokeImFontChanged(null!), TestContext.Current.CancellationToken));
            try
            {
                Assert.Same(font, await wait);
            }
            catch (OperationCanceledException)
            {
                Assert.True(cancellation.IsCancellationRequested);
            }

            AssertDetached(font);
        }
    }

    private static void AssertDetached(FontHandle font)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        Assert.Null(typeof(FontHandle).GetField("ImFontChanged", flags)!.GetValue(font));
        Assert.Null(typeof(FontHandle).GetField("Disposed", flags)!.GetValue(font));
    }

    private sealed class OfflineFont(IFontHandleManager manager) : FontHandle(manager);

    private sealed class Manager : IFontHandleManager
    {
        public event Action? RebuildRecommend { add { } remove { } }

        public string Name => "offline-test";

        public Action? OnRead { get; set; }

        public int Freed { get; private set; }

        public IFontHandleSubstance? Substance
        {
            get { this.OnRead?.Invoke(); return null; }
            set => throw new NotSupportedException();
        }

        public void FreeFontHandle(IFontHandle handle) => this.Freed++;

        public IFontHandleSubstance NewSubstance(IRefCountable root) => throw new NotSupportedException();

        public void Dispose() { }
    }
}
