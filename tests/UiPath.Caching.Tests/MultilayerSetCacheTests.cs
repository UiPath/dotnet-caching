using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Locking;

namespace UiPath.Caching.Tests;

public class MultilayerSetCacheTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IMemoryCacheFactory MemoryFactory() => new MemoryCacheFactory(null, NullLoggerFactory.Instance);

    // The inner (L2) is always a real store; the InMemory and InMemoryRedis providers differ only in
    // what they pass as L2. A substitute stands in for it here.
    private static (MultilayerSetCache Sut, ISetCache L2) CreateSut()
    {
        var l2 = Substitute.For<ISetCache>();
        var sut = new MultilayerSetCache(
            KnownCacheProviderNames.InMemoryRedis, l2,
            MemoryFactory(), new SystemJsonSerializerProxy(), new InMemoryRedisQueueCacheOptions(),
            NullLocalLock.Instance,
            localMaxExpiration: TimeSpan.FromMinutes(5));
        return (sut, l2);
    }

    private static void SetupMembers(ISetCache l2, params string?[] members)
    {
        IReadOnlyCollection<string?> snapshot = members.ToList();
        l2.MembersAsync<string>(default, default, Ct)
            .ReturnsForAnyArgs(_ => new ValueTask<IReadOnlyCollection<string?>>(snapshot));
    }

    [Fact]
    public void Name_reflects_provider()
    {
        var (sut, _) = CreateSut();
        sut.Name.Should().Be("InMemoryRedis");
    }

    [Fact]
    public async Task Members_are_served_from_L1_after_first_fetch()
    {
        var (sut, l2) = CreateSut();
        SetupMembers(l2, "a", "b");

        var first = await sut.MembersAsync<string>("k", token: Ct);
        var second = await sut.MembersAsync<string>("k", token: Ct);

        first.Should().BeEquivalentTo(new[] { "a", "b" });
        second.Should().BeEquivalentTo(new[] { "a", "b" });
        await l2.ReceivedWithAnyArgs(1).MembersAsync<string>(default, default, Ct);
    }

    [Fact]
    public async Task Add_writes_through_into_the_local_snapshot()
    {
        var (sut, l2) = CreateSut();
        SetupMembers(l2, "a");
        l2.AddAsync<string>(default, default(string)!, default, Ct).ReturnsForAnyArgs(_ => new ValueTask<bool>(true));

        await sut.MembersAsync<string>("k", token: Ct);              // primes L1
        await sut.AddAsync("k", "b", token: Ct);                     // must patch L1, not evict it
        var members = await sut.MembersAsync<string>("k", token: Ct); // must be served from L1

        members.Should().BeEquivalentTo(new[] { "a", "b" });
        (await sut.ContainsItemAsync("k", "b", Ct)).Should().BeTrue();
        await l2.ReceivedWithAnyArgs(1).MembersAsync<string>(default, default, Ct);
    }

    [Fact]
    public async Task Add_many_writes_through_into_the_local_snapshot()
    {
        var (sut, l2) = CreateSut();
        SetupMembers(l2, "a");
        // The batch overloads normalize into the DateTimeOffset one before hitting L2.
        l2.AddAsync<string>(default, default(IEnumerable<string>)!, default(DateTimeOffset?), default(CachePolicy), Ct).ReturnsForAnyArgs(_ => new ValueTask<long>(2));

        await sut.MembersAsync<string>("k", token: Ct);              // primes L1
        await sut.AddAsync("k", (IEnumerable<string>)new[] { "b", "c" }, (CachePolicy?)null, Ct);
        var members = await sut.MembersAsync<string>("k", token: Ct);

        members.Should().BeEquivalentTo(new[] { "a", "b", "c" });
        await l2.ReceivedWithAnyArgs(1).MembersAsync<string>(default, default, Ct);
    }

    [Fact]
    public async Task Add_does_not_create_a_partial_local_snapshot()
    {
        var (sut, l2) = CreateSut();
        SetupMembers(l2, "a", "b");
        l2.AddAsync<string>(default, default(string)!, default, Ct).ReturnsForAnyArgs(_ => new ValueTask<bool>(true));

        await sut.AddAsync("k", "b", token: Ct);                     // no snapshot cached: must not create one
        var members = await sut.MembersAsync<string>("k", token: Ct); // must fetch the whole set from L2

        members.Should().BeEquivalentTo(new[] { "a", "b" });
        await l2.ReceivedWithAnyArgs(1).MembersAsync<string>(default, default, Ct);
    }

    [Fact]
    public async Task Pop_removes_the_popped_value_from_the_local_snapshot()
    {
        var (sut, l2) = CreateSut();
        SetupMembers(l2, "a", "b");
        l2.PopAsync<string>(default, default(CachePolicy?), Ct).ReturnsForAnyArgs(_ => new ValueTask<string?>("a"));

        await sut.MembersAsync<string>("k", token: Ct);              // primes L1
        var popped = await sut.PopAsync<string>("k", token: Ct);
        var members = await sut.MembersAsync<string>("k", token: Ct); // must be served from the patched L1

        popped.Should().Be("a");
        members.Should().BeEquivalentTo(new[] { "b" });
        await l2.ReceivedWithAnyArgs(1).MembersAsync<string>(default, default, Ct);
    }

    [Fact]
    public async Task RemoveItem_removes_the_value_from_the_local_snapshot()
    {
        var (sut, l2) = CreateSut();
        SetupMembers(l2, "a", "b");
        l2.RemoveItemAsync<string>(default, default(string)!, Ct).ReturnsForAnyArgs(_ => new ValueTask<bool>(true));

        await sut.MembersAsync<string>("k", token: Ct);              // primes L1
        await sut.RemoveItemAsync("k", "a", Ct);
        var members = await sut.MembersAsync<string>("k", token: Ct);

        members.Should().BeEquivalentTo(new[] { "b" });
        await l2.ReceivedWithAnyArgs(1).MembersAsync<string>(default, default, Ct);
    }

    [Fact]
    public async Task Remove_evicts_the_local_snapshot()
    {
        var (sut, l2) = CreateSut();
        SetupMembers(l2, "a");
        l2.RemoveAsync<string>(default, Ct).ReturnsForAnyArgs(_ => new ValueTask<bool>(true));

        await sut.MembersAsync<string>("k", token: Ct);   // primes L1
        await sut.RemoveAsync<string>("k", Ct);
        await sut.MembersAsync<string>("k", token: Ct);   // must re-read from L2

        await l2.ReceivedWithAnyArgs(2).MembersAsync<string>(default, default, Ct);
    }

    [Fact]
    public async Task ContainsItem_Count_and_Contains_use_L1_when_cached()
    {
        var (sut, l2) = CreateSut();
        SetupMembers(l2, "a", "b");

        await sut.MembersAsync<string>("k", token: Ct);   // primes L1

        (await sut.ContainsItemAsync("k", "a", Ct)).Should().BeTrue();
        (await sut.CountAsync<string>("k", Ct)).Should().Be(2);
        (await sut.ContainsAsync<string>("k", Ct)).Should().BeTrue();

        await l2.DidNotReceiveWithAnyArgs().ContainsItemAsync<string>(default, default(string)!, Ct);
        await l2.DidNotReceiveWithAnyArgs().CountAsync<string>(default, Ct);
    }

    [Fact]
    public async Task Reads_fall_through_to_inner_when_not_cached()
    {
        var (sut, l2) = CreateSut();
        l2.CountAsync<string>(default, Ct).ReturnsForAnyArgs(_ => new ValueTask<long>(7));

        (await sut.CountAsync<string>("k", Ct)).Should().Be(7);
        await l2.ReceivedWithAnyArgs(1).CountAsync<string>(default, Ct);
    }

    // Inner implementing IConnectionState is picked up by the connection monitor, mirroring how
    // MultilayerCacheBase resolves the monitor from its inner cache.
    private static (MultilayerSetCache Sut, ISetCache L2) CreateMonitoredSut(bool connected, bool useLocalOnlyWhenDisconnected)
    {
        var l2 = Substitute.For<ISetCache, IConnectionState>();
        ((IConnectionState)l2).IsConnected.Returns(connected);
        var sut = new MultilayerSetCache(
            KnownCacheProviderNames.InMemoryRedis, l2,
            MemoryFactory(), new SystemJsonSerializerProxy(), new InMemoryRedisQueueCacheOptions(),
            NullLocalLock.Instance,
            localMaxExpiration: TimeSpan.FromMinutes(5),
            connectionMonitorEnabled: true,
            useLocalOnlyWhenDisconnected: useLocalOnlyWhenDisconnected,
            localMaxExpirationDisconnected: TimeSpan.FromSeconds(30));
        return (sut, l2);
    }

    [Fact]
    public async Task Disconnected_add_with_local_only_writes_to_L1_and_skips_inner()
    {
        var (sut, l2) = CreateMonitoredSut(connected: false, useLocalOnlyWhenDisconnected: true);

        (await sut.AddAsync("k", "x", token: Ct)).Should().BeTrue();
        (await sut.MembersAsync<string>("k", token: Ct)).Should().BeEquivalentTo(new[] { "x" });
        (await sut.ContainsItemAsync("k", "x", Ct)).Should().BeTrue();

        await l2.DidNotReceiveWithAnyArgs().AddAsync<string>(default, default(string)!, default, Ct);
        await l2.DidNotReceiveWithAnyArgs().MembersAsync<string>(default, default, Ct);
    }

    [Fact]
    public async Task Disconnected_pop_and_remove_with_local_only_operate_on_L1()
    {
        var (sut, l2) = CreateMonitoredSut(connected: false, useLocalOnlyWhenDisconnected: true);
        await sut.AddAsync("k", (IEnumerable<string>)new[] { "a", "b", "c" }, (CachePolicy?)null, Ct);

        var popped = await sut.PopAsync<string>("k", token: Ct);
        new[] { "a", "b", "c" }.Should().Contain(popped!);
        (await sut.RemoveItemAsync("k", new[] { "a", "b", "c" }.First(v => v != popped), Ct)).Should().BeTrue();
        (await sut.CountAsync<string>("k", Ct)).Should().Be(1);

        await l2.DidNotReceiveWithAnyArgs().PopAsync<string>(default, default(CachePolicy?), Ct);
        await l2.DidNotReceiveWithAnyArgs().RemoveItemAsync<string>(default, default(string)!, Ct);
    }

    [Fact]
    public async Task Disconnected_read_without_local_only_evicts_the_snapshot_and_returns_default()
    {
        var (sut, l2) = CreateMonitoredSut(connected: true, useLocalOnlyWhenDisconnected: false);
        SetupMembers(l2, "a");

        await sut.MembersAsync<string>("k", token: Ct);   // primes L1 while connected
        ((IConnectionState)l2).IsConnected.Returns(false);

        (await sut.MembersAsync<string>("k", token: Ct)).Should().BeEmpty();
        await l2.ReceivedWithAnyArgs(1).MembersAsync<string>(default, default, Ct);
    }

    [Fact]
    public async Task Disconnected_without_local_only_still_writes_through_to_inner()
    {
        var (sut, l2) = CreateMonitoredSut(connected: false, useLocalOnlyWhenDisconnected: false);
        l2.AddAsync<string>(default, default(string)!, default, Ct).ReturnsForAnyArgs(_ => new ValueTask<bool>(true));

        (await sut.AddAsync("k", "x", token: Ct)).Should().BeTrue();

        await l2.ReceivedWithAnyArgs(1).AddAsync<string>(default, default(string)!, default, Ct);
    }
}
