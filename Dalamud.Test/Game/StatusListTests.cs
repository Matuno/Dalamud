using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

using Dalamud.Game.ClientState.Statuses;
using Dalamud.Utility;

using Xunit;

using CSStatus = FFXIVClientStructs.FFXIV.Client.Game.Status;
using CSStatusManager = FFXIVClientStructs.FFXIV.Client.Game.StatusManager;

namespace Dalamud.Test.Game;

public class StatusListTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(30, 0)]
    [InlineData(30, 3)]
    [InlineData(60, 3)]
    [InlineData(60, 60)]
    public void EnumerationKeepsSlotOrderAndSkipsOnlyEmptySlots(int length, int occupied)
    {
        using var fixture = new StatusFixture(length);
        var indices = Enumerable.Range(length - occupied, occupied).ToArray();
        foreach (var index in indices)
            fixture.Set(index, (ushort)(index + 1));
        if (length < 60)
            fixture.Set(length, 900);

        var list = fixture.List;
        var statuses = list.ToArray();
        Assert.Equal(indices.Select(index => (uint)index + 1), statuses.Select(status => status.StatusId));
        Assert.Equal(indices.Select(list.GetStatusAddress), statuses.Select(status => status.Address));
        Assert.Equal(statuses.Select(status => status.StatusId), ((IEnumerable)list).Cast<IStatus>().Select(status => status.StatusId));
        Assert.Equal(length, list.Length);
        Assert.Null(list[-1]);
        Assert.Null(list[length]);
        if (length > occupied)
            Assert.Equal(0u, list[0]!.StatusId);

        // CopyTo and indexed access include empty slots, unlike enumeration.
        var copy = new IStatus[length + 1];
        ((ICollection)list).CopyTo(copy, 1);
        for (var index = 0; index < length; index++)
        {
            Assert.NotNull(copy[index + 1]);
            Assert.Equal(list.GetStatusAddress(index), copy[index + 1].Address);
            Assert.Equal(list[index]!.StatusId, copy[index + 1].StatusId);
        }
    }

    [Fact]
    public void IterationAndReturnedWrappersKeepReadingLiveValues()
    {
        using var fixture = new StatusFixture(60);
        fixture.Set(0, 10);
        using var enumerator = fixture.List.GetEnumerator();
        Assert.True(enumerator.MoveNext());
        var first = enumerator.Current;
        fixture.Set(0, 11);
        fixture.Set(59, 20);
        Assert.Equal(11u, first.StatusId);
        Assert.True(enumerator.MoveNext());
        Assert.Equal(20u, enumerator.Current.StatusId);
        Assert.False(enumerator.MoveNext());
        Assert.Null(enumerator.Current);

        enumerator.Reset();
        Assert.True(enumerator.MoveNext());
        Assert.NotNull(enumerator.Current);
        Assert.NotSame(first, enumerator.Current);
        Assert.Equal(first.Address, enumerator.Current.Address);
        fixture.Length = 1;
        Assert.False(enumerator.MoveNext());
        fixture.Length = 60;
        enumerator.Reset();
        Assert.True(enumerator.MoveNext());
        fixture.Set(59, 0);
        Assert.False(enumerator.MoveNext());
    }

    [Fact]
    public void NativeReadsStillRequireTheMainThread()
    {
        using var fixture = new StatusFixture(60);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var enumerator = fixture.List.GetEnumerator();
                enumerator.MoveNext();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.Start();
        thread.Join();
        Assert.IsType<InvalidOperationException>(failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(60)]
    public void EmptySlotsDoNotAllocateStatusWrappers(int occupied)
    {
        using var fixture = new StatusFixture(60);
        for (var i = 0; i < occupied; i++)
            fixture.Set(59 - i, (ushort)(i + 1));
        for (var i = 0; i < 4096; i++)
            Count(fixture.List);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            Count(fixture.List);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, (40L + (24L * occupied)) * 1000);

        static int Count(StatusList list)
        {
            var count = 0;
            foreach (var status in list)
                count++;
            return count;
        }
    }

    private sealed class StatusFixture : IDisposable
    {
        private static readonly int StatusOffset = Marshal.OffsetOf<CSStatusManager>("_status").ToInt32();
        private static readonly int LengthOffset = Marshal.OffsetOf<CSStatusManager>(nameof(CSStatusManager.NumValidStatuses)).ToInt32();
        private static readonly int StatusSize = Marshal.SizeOf<CSStatus>();

        private readonly bool wasMainThread = ThreadSafety.IsMainThread;
        private readonly byte[] bytes = new byte[Marshal.SizeOf<CSStatusManager>()];
        private readonly GCHandle pin;

        public StatusFixture(int length)
        {
            // Only owned pinned memory is accessed; no game services or native functions are started.
            this.pin = GCHandle.Alloc(this.bytes, GCHandleType.Pinned);
            this.List = new StatusList(this.pin.AddrOfPinnedObject());
            this.Length = length;
            ThreadSafety.MarkMainThread();
        }

        public StatusList List { get; }

        public int Length
        {
            set => this.bytes[LengthOffset] = checked((byte)value);
        }

        public void Set(int index, ushort id)
            => BitConverter.TryWriteBytes(this.bytes.AsSpan(StatusOffset + (index * StatusSize)), id);

        public void Dispose()
        {
            this.pin.Free();
            typeof(ThreadSafety).GetField("threadStaticIsMainThread", BindingFlags.Static | BindingFlags.NonPublic)!
                .SetValue(null, this.wasMainThread);
        }
    }
}
