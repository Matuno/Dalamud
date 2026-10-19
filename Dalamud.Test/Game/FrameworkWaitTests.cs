using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Game;
using Dalamud.Utility;

using Xunit;

namespace Dalamud.Test.Game;

public class FrameworkWaitTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task WaitsKeepDeferralContextResultsAndBatching(int kind)
    {
        for (var wait = 0; wait < 4; wait++)
        {
            using var fixture = new FrameworkFixture();
            var ambient = new AsyncLocal<string?> { Value = "captured" };
            string? seen = null;
            var hidden = false;
            var calls = 0;
            Task? nested = null;
            var task = Schedule(fixture.Framework, kind, () =>
            {
                calls++;
                seen = ambient.Value;
                hidden = TaskScheduler.Current == TaskScheduler.Default;
                nested = fixture.Framework.RunOnTick(static () => { }, cancellationToken: CancellationToken.None);
            }, wait, CancellationToken.None);
            Assert.Equal(0, calls);
            if (wait == 0 || kind >= 2)
                Assert.Null(task.AsyncState);
            ambient.Value = "changed";
            fixture.Ready(wait);
            fixture.Drain();
            Assert.True(task.IsCompletedSuccessfully);
            if (task is Task<int> result)
                Assert.Equal(7, await result);
            Assert.Equal(1, calls);
            Assert.Equal("captured", seen);
            Assert.True(hidden);
            Assert.NotNull(nested);
            Assert.False(nested.IsCompleted);
            fixture.Drain();
            Assert.True(nested.IsCompletedSuccessfully);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CancellationStillCompletesBeforeTheSchedulerDrains(int kind)
    {
        for (var wait = 0; wait < 4; wait++)
        {
            foreach (var preCanceled in new[] { false, true })
            {
                using var fixture = new FrameworkFixture();
                using var cancellation = new CancellationTokenSource();
                var calls = 0;
                if (preCanceled)
                    cancellation.Cancel();
                var task = Schedule(fixture.Framework, kind, () => calls++, wait, cancellation.Token, longDelay: true);
                cancellation.Cancel();
                Assert.True(task.IsCanceled);
                Assert.Equal(cancellation.Token, (await Assert.ThrowsAsync<TaskCanceledException>(() => task)).CancellationToken);
                fixture.Tick();
                fixture.Drain();
                Assert.Equal(0, calls);
            }

            using var defaultFixture = new FrameworkFixture();
            var pending = Schedule(defaultFixture.Framework, kind, static () => { }, wait, CancellationToken.None, longDelay: true);
            defaultFixture.Destroy();
            Assert.True(pending.IsCanceled);
        }

        using var independent = new FrameworkFixture();
        using var caller = new CancellationTokenSource();
        var independentTask = Schedule(independent.Framework, kind, static () => { }, 1, caller.Token);
        independent.Destroy();
        independent.Ready(1);
        independent.Drain();
        Assert.True(independentTask.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CallbackErrorsAndNullsKeepTheirDeferredBehavior(int kind)
    {
        foreach (var wait in new[] { 1, 2, 3 })
        {
            using var fixture = new FrameworkFixture();
            var expected = new InvalidOperationException("Expected callback error");
            var task = Schedule(fixture.Framework, kind, () => throw expected, wait, CancellationToken.None);
            fixture.Ready(wait);
            fixture.Drain();
            Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => task));

            var missing = Schedule(fixture.Framework, kind, null, wait, CancellationToken.None);
            fixture.Ready(wait);
            fixture.Drain();
            await Assert.ThrowsAsync<NullReferenceException>(() => missing);

            using var cancellation = new CancellationTokenSource();
            var canceled = Schedule(fixture.Framework, kind, () =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }, wait, cancellation.Token);
            fixture.Ready(wait);
            fixture.Drain();
            Assert.True(canceled.IsCanceled);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AttachedChildrenStillDelayCompletion(int kind)
    {
        foreach (var wait in new[] { 1, 2, 3 })
        {
            using var fixture = new FrameworkFixture();
            Task? child = null;
            var task = Schedule(fixture.Framework, kind, () => child = new Task(static () => { }, CancellationToken.None, TaskCreationOptions.AttachedToParent), wait, CancellationToken.None);
            fixture.Ready(wait);
            fixture.Drain();
            Assert.NotNull(child);
            Assert.False(task.IsCompleted);
            child.RunSynchronously(TaskScheduler.Default);
            await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncResultsKeepTheirOwnCompletionAndCancellation(bool generic)
    {
        foreach (var wait in new[] { 1, 2, 3 })
        {
            using var fixture = new FrameworkFixture();
            using var cancellation = new CancellationTokenSource();
            var completion = new TaskCompletionSource<int>();
            Task task = generic
                ? fixture.Framework.RunOnTick<int>((Func<Task<int>>)(() => completion.Task), Delay(wait), wait is 1 or 3 ? 1 : 0, cancellation.Token)
                : fixture.Framework.RunOnTick((Func<Task>)(() => completion.Task), Delay(wait), wait is 1 or 3 ? 1 : 0, cancellation.Token);
            fixture.Ready(wait);
            fixture.Drain();
            Assert.False(task.IsCompleted);
            cancellation.Cancel();
            Assert.False(task.IsCompleted);
            completion.SetResult(42);
            Assert.True(task.IsCompletedSuccessfully);
            if (generic)
                Assert.Equal(42, await (Task<int>)task);

            Task missing = generic
                ? fixture.Framework.RunOnTick<int>((Func<Task<int>>)(() => null!), Delay(wait), wait is 1 or 3 ? 1 : 0, CancellationToken.None)
                : fixture.Framework.RunOnTick((Func<Task>)(() => null!), Delay(wait), wait is 1 or 3 ? 1 : 0, CancellationToken.None);
            fixture.Ready(wait);
            fixture.Drain();
            Assert.True(missing.IsCanceled);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void StartupNegativeTicksAndInvalidTimesKeepTheirBehavior(int kind)
    {
        using var fixture = new FrameworkFixture();
        fixture.Scheduler.BoundThread = null;
        var calls = 0;
        var beforeFirstTick = Schedule(fixture.Framework, kind, () => calls++, 1, CancellationToken.None);
        Assert.Equal(0, calls);
        fixture.Drain();
        Assert.True(beforeFirstTick.IsCompletedSuccessfully);
        fixture.Scheduler.BoundThread = Thread.CurrentThread;

        var negative = kind switch
        {
            0 => fixture.Framework.RunOnTick((Action)(() => calls++), delayTicks: -1, cancellationToken: CancellationToken.None),
            1 => fixture.Framework.RunOnTick<int>((Func<int>)(() => ++calls), delayTicks: -1, cancellationToken: CancellationToken.None),
            2 => fixture.Framework.RunOnTick((Func<Task>)(() => { calls++; return Task.CompletedTask; }), delayTicks: -1, cancellationToken: CancellationToken.None),
            _ => fixture.Framework.RunOnTick<int>((Func<Task<int>>)(() => Task.FromResult(++calls)), delayTicks: -1, cancellationToken: CancellationToken.None),
        };
        Assert.Equal(1, calls);
        fixture.Drain();
        Assert.True(negative.IsCompletedSuccessfully);
        Assert.Equal(2, calls);
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = Schedule(fixture.Framework, kind, static () => { }, 2, CancellationToken.None, invalidDelay: true); });
    }

    [Fact]
    public void SingleWaitsDoNotAllocateATwoTaskJoin()
    {
        foreach (var wait in new[] { 1, 2 })
        {
            using var fixture = new FrameworkFixture();
            Action callback = static () => { };
            var delay = wait == 2 ? TimeSpan.FromMinutes(10) : default;
            for (var i = 0; i < 1000; i++)
                fixture.Framework.RunOnTick(callback, delay, wait == 1 ? 1 : 0, CancellationToken.None);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
                fixture.Framework.RunOnTick(callback, delay, wait == 1 ? 1 : 0, CancellationToken.None);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.InRange(allocated, 0, wait == 1 ? 640_000 : 760_000);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletedWaitDoesNotKeepTheCallbackAlive(bool generic)
    {
        foreach (var wait in new[] { 1, 2, 3 })
        {
            var (task, owner) = CompleteCapturedCallback(generic, wait);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.False(owner.IsAlive);
            Assert.Null(task.AsyncState);
            GC.KeepAlive(task);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task Task, WeakReference Owner) CompleteCapturedCallback(bool generic, int wait)
    {
        using var fixture = new FrameworkFixture();
        var owner = new object();
        Task task = generic
            ? fixture.Framework.RunOnTick<int>((Func<int>)(() => { GC.KeepAlive(owner); return 7; }), Delay(wait), wait is 1 or 3 ? 1 : 0, CancellationToken.None)
            : fixture.Framework.RunOnTick((Action)(() => GC.KeepAlive(owner)), Delay(wait), wait is 1 or 3 ? 1 : 0, CancellationToken.None);
        fixture.Ready(wait);
        fixture.Drain();
        Assert.True(task.IsCompletedSuccessfully);
        return (task, new WeakReference(owner));
    }

    private static TimeSpan Delay(int wait) => wait is 2 or 3 ? TimeSpan.FromMilliseconds(5) : default;

    private static Task Schedule(Framework framework, int kind, Action? callback, int wait, CancellationToken token = default, bool longDelay = false, bool invalidDelay = false)
    {
        var delay = invalidDelay ? TimeSpan.FromMilliseconds(-2) : longDelay && wait is 2 or 3 ? TimeSpan.FromMinutes(10) : Delay(wait);
        var ticks = wait is 1 or 3 ? 1 : 0;
        return kind switch
        {
            0 => framework.RunOnTick(callback!, delay, ticks, token),
            1 => framework.RunOnTick<int>(callback is null ? null! : (Func<int>)(() => { callback(); return 7; }), delay, ticks, token),
            2 => framework.RunOnTick(callback is null ? (Func<Task>)null! : () => { callback(); return Task.CompletedTask; }, delay, ticks, token),
            _ => framework.RunOnTick<int>(callback is null ? null! : (Func<Task<int>>)(() => { callback(); return Task.FromResult(7); }), delay, ticks, token),
        };
    }

    private sealed class FrameworkFixture : IDisposable
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly CancellationTokenSource destroyed = new();
        private readonly ConcurrentDictionary<TaskCompletionSource, (ulong Expire, CancellationToken CancellationToken)> delayed = new(1, 10007);
        private readonly ConcurrentDictionary<Task, byte> queued;
        private ulong tick;

        public FrameworkFixture()
        {
            // Supply owned managed state without installing native framework hooks or starting game services.
            this.Framework = (Framework)RuntimeHelpers.GetUninitializedObject(typeof(Framework));
            this.Scheduler = new ThreadBoundTaskScheduler(Thread.CurrentThread);
            this.queued = (ConcurrentDictionary<Task, byte>)typeof(ThreadBoundTaskScheduler).GetField("scheduledTasks", PrivateInstance)!.GetValue(this.Scheduler)!;
            this.Set("frameworkDestroyed", this.destroyed);
            this.Set("frameworkThreadTaskScheduler", this.Scheduler);
            this.Set("tickDelayedTaskCompletionSources", this.delayed);
            this.Set("<FrameworkThreadTaskFactory>k__BackingField", new TaskFactory(
                this.destroyed.Token, TaskCreationOptions.None, TaskContinuationOptions.None, this.Scheduler));
        }

        public Framework Framework { get; }

        public ThreadBoundTaskScheduler Scheduler { get; }

        public void Destroy() => this.destroyed.Cancel();

        public void Drain() => this.Scheduler.Run();

        public void Ready(int wait)
        {
            if (wait is 1 or 3)
                this.Tick();
            Assert.True(SpinWait.SpinUntil(() => !this.queued.IsEmpty, TimeSpan.FromSeconds(5)), "Expected callback was not queued");
        }

        public void Tick()
        {
            this.Set("tickCounter", ++this.tick);
            foreach (var (source, (expiry, token)) in this.delayed)
            {
                if (token.IsCancellationRequested)
                    source.TrySetCanceled(token);
                else if (expiry <= this.tick)
                    source.TrySetResult();
                else
                    continue;
                this.delayed.TryRemove(source, out _);
            }
        }

        public void Dispose()
        {
            this.destroyed.Cancel();
            foreach (var source in this.delayed.Keys)
                source.TrySetCanceled(this.destroyed.Token);
            this.delayed.Clear();
            this.Scheduler.Run();
            this.destroyed.Dispose();
        }

        private void Set(string name, object value) => typeof(Framework).GetField(name, PrivateInstance)!.SetValue(this.Framework, value);
    }
}
