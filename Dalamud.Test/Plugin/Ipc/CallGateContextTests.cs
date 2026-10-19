using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Plugin.Internal.Types;
using Dalamud.Plugin.Internal.Types.Manifest;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Ipc.Internal;

using Xunit;

namespace Dalamud.Test.Plugin.Ipc;

public class CallGateContextTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TopLevelCallsKeepFreshContextsAndCallerMetadata(bool owned)
    {
        var channel = new CallGateChannel("offline-context");
        var gate = Gate<CallGatePubSub<bool>>(channel, owned ? Owner("Caller") : null);
        var contexts = new List<IpcContext>();
        gate.RegisterAction(() =>
        {
            var context = Assert.IsType<IpcContext>(gate.GetContext());
            Assert.Equal(owned ? "Caller" : null, context.SourcePlugin?.Name);
            contexts.Add(context);
        });
        Assert.Null(gate.GetContext());
        gate.InvokeAction();
        Assert.Null(gate.GetContext());
        gate.InvokeAction();
        Assert.Null(gate.GetContext());
        Assert.NotSame(contexts[0], contexts[1]);
        if (owned)
            Assert.NotSame(contexts[0].SourcePlugin, contexts[1].SourcePlugin);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedActionRestoresOuterFunctionContext(bool throwing)
    {
        var channel = new CallGateChannel("offline-context");
        var outer = Gate<CallGatePubSub<bool>>(channel, Owner("Outer"));
        var inner = Gate<CallGatePubSub<bool>>(channel, Owner("Inner"));
        var expected = new InvalidOperationException("Expected inner failure");
        inner.RegisterAction(() =>
        {
            Assert.Equal("Inner", inner.GetContext()!.SourcePlugin!.Name);
            if (throwing)
                throw expected;
        });
        outer.RegisterFunc(() =>
        {
            var context = outer.GetContext();
            Assert.Equal("Outer", context!.SourcePlugin!.Name);
            if (throwing)
                Assert.Same(expected, Assert.Throws<TargetInvocationException>(inner.InvokeAction).InnerException);
            else
                inner.InvokeAction();
            Assert.Same(context, outer.GetContext());
            return true;
        });
        Assert.True(outer.InvokeFunc());
        Assert.Null(outer.GetContext());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedFunctionRestoresOuterActionContext(bool throwing)
    {
        var channel = new CallGateChannel("offline-context");
        var outer = Gate<CallGatePubSub<int>>(channel, Owner("Outer"));
        var inner = Gate<CallGatePubSub<int>>(channel, Owner("Inner"));
        var expected = new InvalidOperationException("Expected inner failure");
        inner.RegisterFunc(() =>
        {
            Assert.Equal("Inner", inner.GetContext()!.SourcePlugin!.Name);
            if (throwing)
                throw expected;
            return 41;
        });
        outer.RegisterAction(() =>
        {
            var context = outer.GetContext();
            if (throwing)
                Assert.Same(expected, Assert.Throws<TargetInvocationException>(() => inner.InvokeFunc()).InnerException);
            else
                Assert.Equal(41, inner.InvokeFunc());
            Assert.Same(context, outer.GetContext());
        });
        outer.InvokeAction();
        Assert.Null(outer.GetContext());
    }

    [Fact]
    public void ValidationAndInvocationFailuresRestoreAnExistingContext()
    {
        var channel = new CallGateChannel("offline-context");
        var gate = Gate<CallGatePubSub<bool>>(channel);
        var previous = new IpcContext();
        channel.SetInvocationContext(previous);
        Assert.Throws<IpcNotReadyError>(gate.InvokeAction);
        Assert.Same(previous, gate.GetContext());
        Assert.Throws<IpcNotReadyError>(() => gate.InvokeFunc());
        Assert.Same(previous, gate.GetContext());
        channel.Action = (Action<int>)(_ => { });
        channel.Func = (Func<int, bool>)(_ => true);
        Assert.Throws<IpcLengthMismatchError>(gate.InvokeAction);
        Assert.Same(previous, gate.GetContext());
        Assert.Throws<IpcLengthMismatchError>(() => gate.InvokeFunc());
        Assert.Same(previous, gate.GetContext());
        var expected = new InvalidOperationException("Expected provider failure");
        gate.RegisterAction(() => throw expected);
        gate.RegisterFunc(() => throw expected);
        Assert.Same(expected, Assert.Throws<TargetInvocationException>(gate.InvokeAction).InnerException);
        Assert.Same(previous, gate.GetContext());
        Assert.Same(expected, Assert.Throws<TargetInvocationException>(() => gate.InvokeFunc()).InnerException);
        Assert.Same(previous, gate.GetContext());
    }

    [Fact]
    public void DifferentChannelsKeepTheirContextsIndependent()
    {
        var outer = Gate<CallGatePubSub<bool>>(new CallGateChannel("outer"), Owner("Outer"));
        var inner = Gate<CallGatePubSub<bool>>(new CallGateChannel("inner"), Owner("Inner"));
        IpcContext? expected = null;
        inner.RegisterFunc(() =>
        {
            Assert.Same(expected, outer.GetContext());
            Assert.Equal("Inner", inner.GetContext()!.SourcePlugin!.Name);
            return true;
        });
        outer.RegisterAction(() =>
        {
            expected = outer.GetContext();
            Assert.Null(inner.GetContext());
            Assert.True(inner.InvokeFunc());
            Assert.Null(inner.GetContext());
            Assert.Same(expected, outer.GetContext());
        });
        outer.InvokeAction();
        Assert.Null(outer.GetContext());
    }

    [Fact]
    public void MessagesKeepTheirExistingAmbientContextBehavior()
    {
        var gate = Gate<CallGatePubSub<bool>>(new CallGateChannel("offline-context"));
        IpcContext? seen = null;
        gate.Subscribe(() => seen = gate.GetContext());
        gate.SendMessage();
        Assert.Null(seen);
        gate.RegisterAction(() =>
        {
            var context = gate.GetContext();
            gate.SendMessage();
            Assert.Same(context, seen);
        });
        gate.InvokeAction();
        Assert.NotNull(seen);
        Assert.Null(gate.GetContext());
    }

    [Fact]
    public void ParallelCallersHaveSeparateThreadLocalContexts()
    {
        var channel = new CallGateChannel("offline-context");
        var provider = Gate<CallGatePubSub<bool>>(channel);
        using var expected = new ThreadLocal<string>();
        provider.RegisterAction(() =>
        {
            var context = provider.GetContext();
            Assert.Equal(expected.Value, context!.SourcePlugin!.Name);
            Thread.Yield();
            Assert.Same(context, provider.GetContext());
        });
        Parallel.For(0, 8, i =>
        {
            var name = "Caller" + i;
            expected.Value = name;
            var caller = Gate<CallGatePubSub<bool>>(channel, Owner(name));
            for (var iteration = 0; iteration < 100; iteration++)
            {
                caller.InvokeAction();
                Assert.Null(caller.GetContext());
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmCallsAllocateOnlyFreshContextMetadata(bool owned)
    {
        var gate = Gate<CallGatePubSub<bool>>(new CallGateChannel("offline-context"), owned ? Owner("Caller") : null);
        gate.RegisterAction(() => { });
        for (var i = 0; i < 4096; i++)
            gate.InvokeAction();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            gate.InvokeAction();
        Assert.Equal(owned ? 48_000 : 24_000, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void AsyncResultDoesNotExtendTheSynchronousCallContext()
    {
        var gate = Gate<CallGatePubSub<Task<int>>>(new CallGateChannel("offline-context"));
        var completion = new TaskCompletionSource<int>();
        IpcContext? seen = null;
        gate.RegisterFunc(() => { seen = gate.GetContext(); return completion.Task; });
        Assert.Same(completion.Task, gate.InvokeFunc());
        Assert.NotNull(seen);
        Assert.False(completion.Task.IsCompleted);
        Assert.Null(gate.GetContext());
        completion.SetResult(7);
    }

    private static T Gate<T>(CallGateChannel channel, LocalPlugin? owner = null) where T : CallGatePubSubBase
    {
        // Bypass only global service lookup and plugin filesystem setup; invocation paths remain real.
        var gate = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        typeof(CallGatePubSubBase).GetProperty("Channel", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(gate, channel);
        typeof(CallGatePubSubBase).GetProperty("OwningPlugin", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(gate, owner);
        return gate;
    }

    private static LocalPlugin Owner(string name)
    {
        var owner = (LocalPlugin)RuntimeHelpers.GetUninitializedObject(typeof(LocalPlugin));
        var manifest = (LocalPluginManifest)RuntimeHelpers.GetUninitializedObject(typeof(LocalPluginManifest));
        manifest.Name = name;
        manifest.InternalName = name;
        typeof(LocalPlugin).GetField("manifest", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, manifest);
        return owner;
    }
}
