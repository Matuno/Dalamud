using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;

using Xunit;

namespace Dalamud.Test.Game.Agent;

public class AgentLifecycleDispatchTests
{
    private static readonly FieldInfo InvokingField = typeof(AgentLifecycle).GetField("isInvokingListeners", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingEventPreservesInvocationState(bool wasInvoking)
    {
        var lifecycle = Create(wasInvoking);
        lifecycle.InvokeListenersSafely(AgentEvent.PreUpdate, null!);
        Assert.Equal(wasInvoking, InvokingField.GetValue(lifecycle));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArgumentFailureRestoresInvocationState(bool wasInvoking)
    {
        var lifecycle = Create(wasInvoking);
        lifecycle.EventListeners[AgentEvent.PreUpdate] = new()
        {
            [(AgentId)1] = new(),
        };
        Assert.Throws<NullReferenceException>(() => lifecycle.InvokeListenersSafely(AgentEvent.PreUpdate, null!));
        Assert.Equal(wasInvoking, InvokingField.GetValue(lifecycle));
    }

    [Fact]
    public void NestedDispatchKeepsOuterInvocationActive()
    {
        var lifecycle = Create(false);
        var states = new List<bool>();
        var args = new AgentArgs { AgentId = (AgentId)1 };
        lifecycle.EventListeners[AgentEvent.PostUpdate] = new()
        {
            [(AgentId)uint.MaxValue] = [new(AgentEvent.PostUpdate, (AgentId)uint.MaxValue, (_, _) => states.Add((bool)InvokingField.GetValue(lifecycle)!))],
        };
        lifecycle.EventListeners[AgentEvent.PreUpdate] = new()
        {
            [(AgentId)uint.MaxValue] = [new(AgentEvent.PreUpdate, (AgentId)uint.MaxValue, (_, _) =>
            {
                states.Add((bool)InvokingField.GetValue(lifecycle)!);
                lifecycle.InvokeListenersSafely(AgentEvent.PostUpdate, args);
                states.Add((bool)InvokingField.GetValue(lifecycle)!);
            })],
        };
        lifecycle.InvokeListenersSafely(AgentEvent.PreUpdate, args);
        Assert.Equal(new[] { true, true, true }, states);
        Assert.Equal(false, InvokingField.GetValue(lifecycle));
    }

    [Fact]
    public void UnregistrationDropsOnlyEmptyBuckets()
    {
        var lifecycle = Create(false);
        var register = typeof(AgentLifecycle).GetMethod("RegisterListenerMethod", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<AgentLifecycleEventListener>>(lifecycle);
        var unregister = typeof(AgentLifecycle).GetMethod("UnregisterListenerMethod", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<AgentLifecycleEventListener>>(lifecycle);
        var first = new AgentLifecycleEventListener(AgentEvent.PreUpdate, (AgentId)1, (_, _) => { });
        var second = new AgentLifecycleEventListener(AgentEvent.PreUpdate, (AgentId)1, (_, _) => { });
        var global = new AgentLifecycleEventListener(AgentEvent.PreUpdate, (AgentId)uint.MaxValue, (_, _) => { });
        var other = new AgentLifecycleEventListener(AgentEvent.PostUpdate, (AgentId)2, (_, _) => { });
        register(first);
        register(second);
        register(global);
        register(other);

        unregister(first);
        Assert.Same(second, Assert.Single(lifecycle.EventListeners[AgentEvent.PreUpdate][(AgentId)1]));
        unregister(second);
        Assert.False(lifecycle.EventListeners[AgentEvent.PreUpdate].ContainsKey((AgentId)1));
        Assert.Same(global, Assert.Single(lifecycle.EventListeners[AgentEvent.PreUpdate][(AgentId)uint.MaxValue]));
        unregister(global);
        Assert.False(lifecycle.EventListeners.ContainsKey(AgentEvent.PreUpdate));
        Assert.True(lifecycle.EventListeners.ContainsKey(AgentEvent.PostUpdate));
        unregister(global);
        register(first);
        Assert.Same(first, Assert.Single(lifecycle.EventListeners[AgentEvent.PreUpdate][(AgentId)1]));
    }

    private static AgentLifecycle Create(bool wasInvoking)
    {
        // Skip native agent hooks and the framework-service constructor.
        var lifecycle = (AgentLifecycle)RuntimeHelpers.GetUninitializedObject(typeof(AgentLifecycle));
        typeof(AgentLifecycle).GetField("<EventListeners>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(lifecycle, new Dictionary<AgentEvent, Dictionary<AgentId, HashSet<AgentLifecycleEventListener>>>());
        InvokingField.SetValue(lifecycle, wasInvoking);
        return lifecycle;
    }
}
