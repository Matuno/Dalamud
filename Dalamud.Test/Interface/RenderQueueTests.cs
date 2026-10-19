using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using Dalamud.Interface.Internal;

using Xunit;

namespace Dalamud.Test.Interface;

public class RenderQueueTests
{
    private static readonly Action<ConcurrentQueue<Action>> RunQueue = typeof(InterfaceManager)
        .GetMethod("RunRenderQueue", BindingFlags.Static | BindingFlags.NonPublic)!
        .CreateDelegate<Action<ConcurrentQueue<Action>>>();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JobsAddedDuringDrainWaitForNextBatch(bool before)
    {
        var (manager, queue) = CreateManager(before);
        var calls = new List<int>();
        Task? added = null;
        var first = Enqueue(manager, before, () =>
        {
            calls.Add(1);
            added = Enqueue(manager, before, () => calls.Add(3));
        });
        var second = Enqueue(manager, before, () => calls.Add(2));

        RunQueue(queue);
        Assert.Equal(new[] { 1, 2 }, calls);
        Assert.True(first.IsCompletedSuccessfully && second.IsCompletedSuccessfully);
        Assert.False(added!.IsCompleted);
        RunQueue(queue);
        Assert.Equal(new[] { 1, 2, 3 }, calls);
        Assert.True(added.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FeedbackAndConcurrentProducersCannotExtendCurrentBatch(bool before)
    {
        var (manager, queue) = CreateManager(before);
        var calls = 0;
        Action? again = null;
        again = () =>
        {
            calls++;
            if (calls < 4)
                Enqueue(manager, before, again!);
        };
        Enqueue(manager, before, again);
        RunQueue(queue);
        Assert.Equal(1, calls);
        for (var i = 0; i < 3; i++)
            RunQueue(queue);
        Assert.Equal(4, calls);
        Assert.Empty(queue);

        var produced = new List<Task>();
        Enqueue(manager, before, () => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
                produced.Add(Enqueue(manager, before, () => calls++));
        }).GetAwaiter().GetResult());
        RunQueue(queue);
        Assert.Equal(4, calls);
        Assert.Equal(100, queue.Count);
        Assert.All(produced, task => Assert.False(task.IsCompleted));
        RunQueue(queue);
        Assert.Equal(104, calls);
        Assert.All(produced, task => Assert.True(task.IsCompletedSuccessfully));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResultsFaultsAndFifoOrderArePreserved(bool before)
    {
        var (manager, queue) = CreateManager(before);
        var calls = new List<int>();
        var expected = new InvalidOperationException("Expected queue test failure");
        var first = Enqueue(manager, before, () => calls.Add(1));
        var failed = Enqueue(manager, before, () => throw expected);
        var canceled = Enqueue(manager, before, () => throw new OperationCanceledException());
        var result = before
            ? manager.RunBeforeImGuiRender(() => { calls.Add(2); return 42; })
            : manager.RunAfterImGuiRender(() => { calls.Add(2); return 42; });
        RunQueue(queue);
        Assert.Equal(new[] { 1, 2 }, calls);
        Assert.True(first.IsCompletedSuccessfully);
        Assert.Same(expected, failed.Exception!.InnerException);
        Assert.True(canceled.IsFaulted); // Keep the existing queued-callback cancellation contract.
        Assert.Equal(42, await result);
        Assert.Empty(queue);
        RunQueue(queue);
        Assert.Empty(queue);
    }

    private static (InterfaceManager Manager, ConcurrentQueue<Action> Queue) CreateManager(bool before)
    {
        var manager = (InterfaceManager)RuntimeHelpers.GetUninitializedObject(typeof(InterfaceManager));
        var queue = new ConcurrentQueue<Action>();
        typeof(InterfaceManager).GetField(before ? "runBeforeImGuiRender" : "runAfterImGuiRender", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, queue);
        return (manager, queue);
    }

    private static Task Enqueue(InterfaceManager manager, bool before, Action action)
        => before ? manager.RunBeforeImGuiRender(action) : manager.RunAfterImGuiRender(action);
}
