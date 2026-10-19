using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Dalamud.Game.ClientState.Conditions;

using Xunit;

namespace Dalamud.Test.Game;

public class ConditionTests
{
    [Fact]
    public void QueriesMatchSetSemantics()
    {
        var values = new byte[Condition.MaxConditionEntries];
        var pinned = GCHandle.Alloc(values, GCHandleType.Pinned);
        try
        {
            // Supply an owned fake conditions buffer without starting game services.
            var condition = (Condition)RuntimeHelpers.GetUninitializedObject(typeof(Condition));
            typeof(Condition).GetProperty(nameof(Condition.Address))!.SetValue(condition, pinned.AddrOfPinnedObject());
            var random = new Random(19);

            for (var iteration = 0; iteration < 256; iteration++)
            {
                for (var i = 0; i < values.Length; i++)
                    values[i] = (byte)(random.Next(4) == 0 ? 1 : 0);

                var active = condition.AsReadOnlySet();
                var cases = new[]
                {
                    Array.Empty<ConditionFlag>(),
                    active.ToArray(),
                    active.Concat(active).ToArray(),
                    active.Skip(1).ToArray(),
                    new[] { (ConditionFlag)(-1), (ConditionFlag)Condition.MaxConditionEntries },
                    Enumerable.Range(0, 112).Select(_ => (ConditionFlag)random.Next(-1, Condition.MaxConditionEntries + 1)).ToArray(),
                };

                foreach (var flags in cases)
                {
                    Assert.Equal(!active.Intersect(flags).Any(), condition.AnyExcept(flags));
                    Assert.Equal(!active.Except(flags).Any(), condition.OnlyAny(flags));
                    Assert.Equal(active.SetEquals(flags), condition.EqualTo(flags));
                }
            }

            Array.Clear(values);
            Assert.True(condition.AnyExcept());
            Assert.True(condition.OnlyAny());
            Assert.True(condition.EqualTo());
            Assert.False(condition.EqualTo((ConditionFlag)(-1)));
            Assert.Throws<ArgumentNullException>(() => condition.AnyExcept(null!));
            Assert.Throws<ArgumentNullException>(() => condition.OnlyAny(null!));
            Assert.Throws<ArgumentNullException>(() => condition.EqualTo(null!));

            var query = new[] { (ConditionFlag)1, (ConditionFlag)10, (ConditionFlag)100 };
            foreach (var flag in query)
                values[(int)flag] = 1;
            for (var i = 0; i < 1000; i++)
                _ = condition.AnyExcept(query) | condition.OnlyAny(query) | condition.EqualTo(query);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
                _ = condition.AnyExcept(query) | condition.OnlyAny(query) | condition.EqualTo(query);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        finally
        {
            pinned.Free();
        }
    }
}
