using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Game;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using Dalamud.Utility;

using Xunit;

namespace Dalamud.Test.Game;

public class LifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyDispatchDoesNotDeferLaterRegistration(bool agent)
    {
        using var fixture = new LifecycleFixture(agent);
        fixture.Invoke(0);
        Assert.False(fixture.IsInvoking);
        fixture.Add(0, false, () => { });
        Assert.True(fixture.HasEvent(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedDispatchKeepsMutationsDeferredUntilOuterReturn(bool agent)
    {
        using var fixture = new LifecycleFixture(agent);
        var deferred = false;
        var lateCalls = 0;
        object? outer = null;
        fixture.Add(1, false, () => { });
        outer = fixture.Add(0, true, () =>
        {
            fixture.Invoke(1);
            fixture.Invoke(2); // Missing inner event must also preserve the outer guard.
            fixture.Remove(outer!);
            fixture.Add(2, false, () => lateCalls++);
            deferred = fixture.IsInvoking && fixture.HasEvent(0) && !fixture.HasEvent(2);
        });

        fixture.Invoke(0);
        Assert.True(deferred);
        Assert.False(fixture.IsInvoking);
        Assert.Equal(0, lateCalls);
        fixture.Drain();
        Assert.False(fixture.HasEvent(0));
        Assert.True(fixture.HasEvent(2));
        fixture.Invoke(2);
        Assert.Equal(1, lateCalls);
        Assert.False(fixture.IsInvoking);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingLastListenersPrunesOnlyEmptyBuckets(bool agent)
    {
        using var fixture = new LifecycleFixture(agent);
        var global = fixture.Add(0, true, () => { });
        var first = fixture.Add(0, false, () => { });
        var second = fixture.Add(0, false, () => { });
        Assert.Equal(2, fixture.BucketCount(0));
        fixture.Remove(first);
        Assert.Equal(2, fixture.BucketCount(0));
        fixture.Remove(second);
        Assert.Equal(1, fixture.BucketCount(0));
        fixture.Remove(global);
        Assert.False(fixture.HasEvent(0));
        fixture.Remove(global);
        Assert.False(fixture.HasEvent(0));
        fixture.Add(0, false, () => { });
        Assert.Equal(1, fixture.BucketCount(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExceptionsPreserveDispatchOrderAndRestoreGuard(bool agent)
    {
        using var fixture = new LifecycleFixture(agent);
        var calls = new List<string>();
        fixture.Add(0, true, () =>
        {
            calls.Add("throwing global");
            throw new InvalidOperationException("Expected fixture exception");
        });
        fixture.Add(0, true, () => calls.Add("global"));
        fixture.Add(0, false, () => calls.Add("specific"));
        fixture.Invoke(0);
        Assert.Equal(3, calls.Count);
        Assert.Equal("specific", calls[^1]);
        Assert.False(fixture.IsInvoking);
        Assert.Throws<NullReferenceException>(() => fixture.Invoke(0, invalidArgs: true));
        Assert.False(fixture.IsInvoking);
    }

    private sealed class LifecycleFixture : IDisposable
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly bool agent;
        private readonly object service;
        private readonly CancellationTokenSource destroyed = new();
        private readonly ThreadBoundTaskScheduler scheduler = new(Thread.CurrentThread);

        public LifecycleFixture(bool agent)
        {
            this.agent = agent;
            // Bypass game-hook constructors; execute real dispatch and framework scheduling over owned state.
            var framework = (Framework)RuntimeHelpers.GetUninitializedObject(typeof(Framework));
            SetField(framework, "frameworkDestroyed", this.destroyed);
            SetField(framework, "frameworkThreadTaskScheduler", this.scheduler);
            SetField(framework, "<FrameworkThreadTaskFactory>k__BackingField", new TaskFactory(
                this.destroyed.Token, TaskCreationOptions.None, TaskContinuationOptions.None, this.scheduler));
            this.service = RuntimeHelpers.GetUninitializedObject(agent ? typeof(AgentLifecycle) : typeof(AddonLifecycle));
            SetField(this.service, "framework", framework);
            object listeners = agent
                ? new Dictionary<AgentEvent, Dictionary<AgentId, HashSet<AgentLifecycleEventListener>>>()
                : new Dictionary<AddonEvent, Dictionary<string, HashSet<AddonLifecycleEventListener>>>();
            SetField(this.service, "<EventListeners>k__BackingField", listeners);
        }

        public bool IsInvoking => (bool)this.service.GetType().GetField("isInvokingListeners", PrivateInstance)!.GetValue(this.service)!;

        private IDictionary Listeners => (IDictionary)this.service.GetType().GetProperty("EventListeners", PrivateInstance)!.GetValue(this.service)!;

        public bool HasEvent(int phase) => this.Listeners.Contains(this.Event(phase));

        public int BucketCount(int phase) => ((IDictionary)this.Listeners[this.Event(phase)]!).Count;

        public object Add(int phase, bool global, Action callback)
        {
            if (this.agent)
            {
                var listener = new AgentLifecycleEventListener((AgentEvent)this.Event(phase), (AgentId)(global ? uint.MaxValue : 1), (_, _) => callback());
                ((AgentLifecycle)this.service).RegisterListener(listener);
                return listener;
            }
            else
            {
                var listener = new AddonLifecycleEventListener((AddonEvent)this.Event(phase), global ? string.Empty : "Target", (_, _) => callback());
                ((AddonLifecycle)this.service).RegisterListener(listener);
                return listener;
            }
        }

        public void Remove(object listener)
        {
            if (this.agent)
                ((AgentLifecycle)this.service).UnregisterListener((AgentLifecycleEventListener)listener);
            else
                ((AddonLifecycle)this.service).UnregisterListener((AddonLifecycleEventListener)listener);
        }

        public void Invoke(int phase, bool invalidArgs = false)
        {
            if (this.agent)
                ((AgentLifecycle)this.service).InvokeListenersSafely((AgentEvent)this.Event(phase), invalidArgs ? null! : new AgentArgs { AgentId = (AgentId)1 });
            else
                ((AddonLifecycle)this.service).InvokeListenersSafely((AddonEvent)this.Event(phase), invalidArgs ? null! : new AddonArgs { AddonName = "Target" });
        }

        public void Drain() => this.scheduler.Run();

        public void Dispose()
        {
            this.Drain();
            this.destroyed.Dispose();
        }

        private static void SetField(object instance, string name, object value)
            => instance.GetType().GetField(name, PrivateInstance)!.SetValue(instance, value);

        private object Event(int phase) => this.agent
            ? (object)(phase switch { 0 => AgentEvent.PreUpdate, 1 => AgentEvent.PreShow, _ => AgentEvent.PreHide })
            : phase switch { 0 => AddonEvent.PreDraw, 1 => AddonEvent.PreUpdate, _ => AddonEvent.PostUpdate };
    }
}
