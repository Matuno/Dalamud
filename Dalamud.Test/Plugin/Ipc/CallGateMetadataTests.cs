using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Ipc.Internal;

using Newtonsoft.Json.Linq;

using Xunit;

namespace Dalamud.Test.Plugin.Ipc;

public class CallGateMetadataTests
{
    [Fact]
    public void ChangingProvidersKeepsSignatureAndReadyChecks()
    {
        var channel = new CallGateChannel("offline-test");
        Assert.Throws<IpcNotReadyError>(() => channel.InvokeFunc<int>(null));
        var result = 0;
        channel.Action = (Action<int>)(x => result = x);
        channel.InvokeAction([7]);
        Assert.Equal(7, result);
        channel.Action = (Action<int, int>)((x, y) => result = x + y);
        Assert.Throws<IpcLengthMismatchError>(() => channel.InvokeAction([7]));
        channel.InvokeAction([7, 8]);
        Assert.Equal(15, result);
        channel.Action = null;
        Assert.Throws<IpcNotReadyError>(() => channel.InvokeAction([7]));
        channel.Func = (Func<bool>)(() => true);
        Assert.True(channel.InvokeFunc<bool>(null));
        Assert.True(channel.InvokeFunc<bool>([]));
        Assert.Throws<IpcLengthMismatchError>(() => channel.InvokeFunc<bool>([1]));
    }

    [Fact]
    public void NullabilityAndBaseTypeValidationArePreserved()
    {
        var channel = new CallGateChannel("offline-test") { Action = (Action<int>)(_ => { }) };
        Assert.Throws<IpcValueNullError>(() => channel.InvokeAction([null]));
        var calls = 0;
        channel.Action = (Action<int?>)(x => { Assert.Null(x); calls++; });
        channel.InvokeAction([null]);
        channel.Action = (Action<string?>)(x => { Assert.Null(x); calls++; });
        channel.InvokeAction([null]);
        var derived = new DerivedValue();
        channel.Action = (Action<BaseValue>)(x => Assert.Same(derived, x));
        channel.InvokeAction([derived]);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ConversionAndProviderExceptionsKeepTheirExistingPaths()
    {
        var channel = new CallGateChannel("offline-test");
        channel.Func = (Func<int, int>)(value => value + 1);
        object?[] args = [7L];
        Assert.Equal(8, channel.InvokeFunc<int>(args));
        Assert.IsType<int>(args[0]);
        Assert.Equal(8L, channel.InvokeFunc<long>([7]));
        var expected = new InvalidOperationException("Expected IPC provider failure");
        channel.Func = (Func<int, int>)(_ => throw expected);
        var error = Assert.Throws<TargetInvocationException>(() => channel.InvokeFunc<int>([7]));
        Assert.Same(expected, error.InnerException);
    }

    [Fact]
    public void ObjectDestinationStillUsesSerializationInsteadOfDirectAssignment()
    {
        var original = new ObjectValue { Number = 7 };
        var channel = new CallGateChannel("offline-test")
        {
            Action = (Action<object>)(value =>
            {
                Assert.NotSame(original, value);
                Assert.Equal(7, Assert.IsType<JObject>(value)[nameof(ObjectValue.Number)]!.Value<int>());
            }),
        };
        channel.InvokeAction([original]);
    }

    [Fact]
    public void MessageSnapshotAndDifferentSubscriberSignaturesArePreserved()
    {
        var channel = new CallGateChannel("offline-test");
        var sum = 0;
        Action<int> later = x => sum += x * 10;
        Action<int> first = x => { sum += x; channel.Subscribe(later); };
        channel.Subscribe(first);
        channel.SendMessage([2]);
        Assert.Equal(2, sum);
        channel.SendMessage([2]);
        Assert.Equal(24, sum);
        channel.Unsubscribe(first);
        channel.SendMessage([2]);
        Assert.Equal(44, sum);
        channel.Unsubscribe(later);
        channel.Subscribe((Action<int, int>)((_, _) => { }));
        Assert.Throws<IpcLengthMismatchError>(() => channel.SendMessage([2]));
    }

    [Fact]
    public void ConcurrentCallsCanPopulateAndReuseMetadata()
    {
        var count = 0;
        var channel = new CallGateChannel("offline-test") { Action = (Action<int>)(x => Interlocked.Add(ref count, x)) };
        Parallel.For(0, 32, _ =>
        {
            object?[] args = [1];
            for (var i = 0; i < 100; i++)
                channel.InvokeAction(args);
        });
        Assert.Equal(3200, count);
    }

    [Fact]
    public void WarmPrimitiveActionCallsDoNotAllocateSignatureArrays()
    {
        var channel = new CallGateChannel("offline-test") { Action = (Action<int>)(_ => { }) };
        object?[] args = [7];
        for (var i = 0; i < 4096; i++)
            channel.InvokeAction(args);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            channel.InvokeAction(args);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void UnregisteredCollectibleProviderIsNotRootedByMetadata()
    {
        var channel = new CallGateChannel("offline-test");
        var assembly = InvokeCollectibleProvider(channel);
        for (var i = 0; i < 5 && assembly.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(assembly.IsAlive);
        GC.KeepAlive(channel);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference InvokeCollectibleProvider(CallGateChannel channel)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("OfflineIpcProvider"), AssemblyBuilderAccess.RunAndCollect);
        var typeBuilder = assembly.DefineDynamicModule("Module").DefineType("Provider", TypeAttributes.Public);
        var methodBuilder = typeBuilder.DefineMethod("Invoke", MethodAttributes.Public | MethodAttributes.Static, typeof(void), [typeof(int)]);
        methodBuilder.GetILGenerator().Emit(OpCodes.Ret);
        var type = typeBuilder.CreateType()!;
        channel.Action = type.GetMethod("Invoke")!.CreateDelegate<Action<int>>();
        channel.InvokeAction([7]);
        channel.Action = null;
        return new WeakReference(type.Assembly);
    }

    private class BaseValue;

    private sealed class DerivedValue : BaseValue;

    private sealed class ObjectValue
    {
        public int Number { get; set; }
    }
}
