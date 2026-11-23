using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// A group that has not started, and S0, use the router's per-group path with the same allocations and no extra commands.
public sealed class KeyMoveHotPathTests
{
    private const int Calls = 2000;

    private static readonly Item Value = new("v");

    [Fact]
    public void Unaffected_caches_match_plain_client_allocations_and_touch_no_data_connection()
    {
        var plain = new PlainPathStub();
        foreach (var rules in new[] { GroupRules.Unaffected, S0Rules(), UnmanagedRules() })
        {
            var plane = new StubPlane("g", new EpochGate()) { Rules = rules };
            var client = new KeyMoveClient<Item>(new KeyMoveClientParts<Item>("g", "c", false, k => "L" + k, k => "N" + k, ItemCodec.Instance, ItemCodec.Instance, plain, plane, TimeProvider.System, () => TimeSpan.Zero));

            var direct = Measure(plain);
            var routed = Measure(client);

            routed.Should().Be(direct);
            plane.DataReads.Should().Be(0);
        }
    }

    [Fact]
    public void The_rules_memo_returns_the_same_object_without_allocating_until_it_lapses()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var memo = new RulesMemo(clock, TimeSpan.FromMilliseconds(100));
        var reads = 0;
        GroupRules Read(string _)
        {
            reads++;
            return S0Rules();
        }

        Func<string, GroupRules> read = Read;
        var first = memo.Get("g", read);
        var different = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Calls; i++)
        {
            different += ReferenceEquals(memo.Get("g", read), first) ? 0 : 1;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        reads.Should().Be(1);
        different.Should().Be(0);
        allocated.Should().Be(0);
        clock.Advance(TimeSpan.FromMilliseconds(101));
        memo.Get("g", read).Should().NotBeSameAs(first);
        reads.Should().Be(2);
    }

    private static GroupRules S0Rules() =>
        new(true, ReadRule.Follow, WriteRule.Follow, new ActedState("S0", "fwd", 1, "id", 0, false), null, false, true, [], new HashSet<string> { "c" });

    private static GroupRules UnmanagedRules() =>
        new(true, ReadRule.Follow, WriteRule.Follow, new ActedState("S3", "fwd", 1, "id", 1, false), null, false, true, [], new HashSet<string> { "other" });

    // Allocation only adds: the smallest of several rounds is what the code itself allocates, so a round that a collection, a tier-up or a contended thread inflated does not decide the comparison.
    private static long Measure(IPathClient<Item> client)
    {
        Run(client, 50);
        var smallest = long.MaxValue;
        for (var round = 0; round < 5; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            Run(client, Calls);
            smallest = Math.Min(smallest, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return smallest;
    }

    private static void Run(IPathClient<Item> client, int calls)
    {
        for (var i = 0; i < calls; i++)
        {
            Complete(client.ReadAsync("k", CancellationToken.None));
            Complete(client.SetAsync("k", Value, null, CancellationToken.None));
            Complete(client.RemoveAsync("k", CancellationToken.None));
            Complete(client.FillAsync("k", Value, null, null, CancellationToken.None));
        }
    }

    // Each call is consumed once: a completed one is read in place, so the measure holds no Task of its own; any other is waited for.
    private static void Complete<T>(ValueTask<T> pending)
    {
        if (pending.IsCompletedSuccessfully)
        {
            _ = pending.Result;
            return;
        }

        pending.AsTask().GetAwaiter().GetResult();
    }
}
