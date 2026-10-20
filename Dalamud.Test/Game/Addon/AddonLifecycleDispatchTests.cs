using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

using Xunit;

namespace Dalamud.Test.Game.Addon;

public class AddonLifecycleDispatchTests
{
    private static readonly FieldInfo InvokingField = typeof(AddonLifecycle).GetField("isInvokingListeners", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingEventPreservesInvocationState(bool wasInvoking)
    {
        var lifecycle = Create(wasInvoking);
        lifecycle.InvokeListenersSafely(AddonEvent.PreDraw, null!);
        Assert.Equal(wasInvoking, InvokingField.GetValue(lifecycle));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArgumentFailureRestoresInvocationState(bool wasInvoking)
    {
        var lifecycle = Create(wasInvoking);
        lifecycle.EventListeners[AddonEvent.PreDraw] = new()
        {
            ["Fixture"] = new(),
        };

        // A null managed argument fails before any native addon pointer is read.
        Assert.Throws<NullReferenceException>(() => lifecycle.InvokeListenersSafely(AddonEvent.PreDraw, null!));
        Assert.Equal(wasInvoking, InvokingField.GetValue(lifecycle));
    }

    [Fact]
    public void NestedDispatchKeepsOuterInvocationActive()
    {
        var lifecycle = Create(false);
        var states = new List<bool>();
        // The null-safe default pointer returns an empty span without reading game memory.
        var args = new AddonArgs();
        lifecycle.EventListeners[AddonEvent.PreUpdate] = new()
        {
            [string.Empty] = [new(AddonEvent.PreUpdate, string.Empty, (_, _) => states.Add((bool)InvokingField.GetValue(lifecycle)!))],
        };
        lifecycle.EventListeners[AddonEvent.PreDraw] = new()
        {
            [string.Empty] = [new(AddonEvent.PreDraw, string.Empty, (_, _) =>
            {
                states.Add((bool)InvokingField.GetValue(lifecycle)!);
                lifecycle.InvokeListenersSafely(AddonEvent.PreUpdate, args);
                states.Add((bool)InvokingField.GetValue(lifecycle)!);
            })],
        };

        lifecycle.InvokeListenersSafely(AddonEvent.PreDraw, args);
        Assert.NotEmpty(states);
        Assert.All(states, state => Assert.True(state));
        Assert.Equal(false, InvokingField.GetValue(lifecycle));
    }

    private static AddonLifecycle Create(bool wasInvoking)
    {
        // Skip the native hook and framework-service constructor.
        var lifecycle = (AddonLifecycle)RuntimeHelpers.GetUninitializedObject(typeof(AddonLifecycle));
        typeof(AddonLifecycle).GetField("<EventListeners>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(lifecycle, new Dictionary<AddonEvent, Dictionary<string, HashSet<AddonLifecycleEventListener>>>());
        InvokingField.SetValue(lifecycle, wasInvoking);
        return lifecycle;
    }
}
