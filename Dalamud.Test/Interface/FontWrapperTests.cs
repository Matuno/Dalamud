using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;

using Xunit;

namespace Dalamud.Test.Interface;

public class FontWrapperTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadyFontWaitCompletesInlineAndReturnsWrapper(bool tokenOverload)
    {
        var inner = new InnerFont();
        inner.Completion.SetResult(inner);
        using var wrapper = Wrap(inner);
        var wait = Wait(wrapper, tokenOverload);
        Assert.True(wait.IsCompletedSuccessfully);
        Assert.Same(wrapper, await wait);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FailedInnerWaitDoesNotReportAReadyFont(bool tokenOverload, bool canceled)
    {
        var inner = new InnerFont();
        using var wrapper = Wrap(inner);
        var wait = Wait(wrapper, tokenOverload);
        if (canceled)
        {
            inner.Completion.SetCanceled(CancellationToken.None);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        }
        else
        {
            var expected = new InvalidOperationException("Expected inner font failure");
            inner.Completion.SetException(expected);
            Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => wait));
        }
    }

    [Fact]
    public async Task CanceledCallerDoesNotReceiveAnAlreadyReadyWrapper()
    {
        var inner = new InnerFont();
        inner.Completion.SetResult(inner);
        using var wrapper = Wrap(inner);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wrapper.WaitAsync(cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposingWrapperDoesNotDisposeOrCancelItsSharedFont(bool tokenOverload)
    {
        var inner = new InnerFont();
        var wrapper = Wrap(inner);
        var pending = Wait(wrapper, tokenOverload);
        Assert.Equal(1, inner.Subscribers);
        wrapper.Dispose();
        Assert.Equal(0, inner.Subscribers);
        Assert.False(inner.Disposed);
        inner.Completion.SetResult(inner);
        Assert.Same(wrapper, await pending);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Wait(wrapper, tokenOverload));
    }

    private static Task<IFontHandle> Wait(IFontHandle wrapper, bool tokenOverload)
        => tokenOverload ? wrapper.WaitAsync(CancellationToken.None) : wrapper.WaitAsync();

    private static IFontHandle Wrap(IFontHandle inner)
        => (IFontHandle)Activator.CreateInstance(typeof(UiBuilder).GetNestedType("FontHandleWrapper", BindingFlags.NonPublic)!, inner)!;

    private sealed class InnerFont : IFontHandle
    {
        public event IFontHandle.ImFontChangedDelegate ImFontChanged
        {
            add => this.Subscribers++;
            remove => this.Subscribers--;
        }

        public TaskCompletionSource<IFontHandle> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? LoadException => null;

        public bool Available => this.Completion.Task.IsCompletedSuccessfully;

        public int Subscribers { get; private set; }

        public bool Disposed { get; private set; }

        public Task<IFontHandle> WaitAsync() => this.Completion.Task;

        public Task<IFontHandle> WaitAsync(CancellationToken token) => this.Completion.Task;

        public ILockedImFont? TryLock(out string? message) => throw new NotSupportedException();

        public ILockedImFont Lock() => throw new NotSupportedException();

        public IDisposable Push() => throw new NotSupportedException();

        public void Pop() => throw new NotSupportedException();

        public void Dispose() => this.Disposed = true;
    }
}
