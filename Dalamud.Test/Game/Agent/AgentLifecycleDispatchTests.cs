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
