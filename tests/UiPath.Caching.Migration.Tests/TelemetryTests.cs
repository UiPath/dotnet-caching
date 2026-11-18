using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class TelemetryTests : RouterTestBase
{
    [Fact]
    public async Task Counters_carry_the_cache_group_path_and_reason()
    {
        using var meters = new MeterCollector(CacheName);
        var node = NewNode();

        await node.Cache.GetOrAddAsync("k", _ => Task.FromResult<Item?>(new Item("v")), default, Ct);
        await node.Cache.GetOrAddAsync("k", _ => Task.FromResult<Item?>(new Item("v")), default, Ct);
        await node.Cache.GetOrAddAsync("k", _ => Task.FromResult<Item?>(new Item("v")), default, Ct);

        meters.Count("miss").Should().Be(1);
        meters.Count("factory_runs").Should().Be(1);
        meters.Count("redis_hit").Should().Be(1);
        meters.Count("local_hit").Should().Be(1);
        meters.Count("miss", "group", "g").Should().Be(1);
        meters.Count("miss", "path", "library").Should().Be(1);
        meters.Count("miss", "path_reason", "flag").Should().Be(1);
    }

    [Fact]
    public async Task An_unknown_write_counts_a_pending_mutation()
    {
        using var meters = new MeterCollector(CacheName);
        var node = NewNode();
        node.LibraryBackend.WriteOutcome = WriteOutcome.Unknown;

        await node.Cache.SetAsync("k", new Item("v"), default, Ct);

        meters.Count("pending").Should().Be(1);
    }
}
