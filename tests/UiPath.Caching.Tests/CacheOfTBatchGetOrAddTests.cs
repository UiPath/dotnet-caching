using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

public class CacheOfTBatchGetOrAddTests(ITestContextAccessor testContextAccessor)
{
    /// <summary>Prefixes every key.</summary>
    private sealed class PrefixStrategy : ICacheKeyStrategy
    {
        public CacheKey GetCacheKey<T>(CacheKey key) => "p:" + key.Name;
    }

    private static readonly long[] States2 = [2L];
    private static readonly long[] States1And2 = [1L, 2L];

    private static KeyValuePair<CacheKey, long>[] Entries(params long[] ids) =>
        ids.Select(id => new KeyValuePair<CacheKey, long>((CacheKey)$"user:{id}", id)).ToArray();

    [Fact]
    public async Task State_passes_through_the_key_strategy_untouched()
    {
        var inner = new DictionaryCache();
        var sut = new Cache<string>(inner, new PrefixStrategy());
        var observed = new List<long[]>();

        var result = await sut.GetOrAddAsync(
            Entries(1, 2),
            (ids, _) => { observed.Add(ids); return Task.FromResult(ids.Select(id => new KeyValuePair<long, string?>(id, "v:" + id)).ToArray()); },
            testContextAccessor.Current.CancellationToken);

        observed.Single().Should().Equal(States1And2, "the generator sees states, which the strategy never touches");
        result.Select(r => r.Key).Should().Equal(States1And2);
        result.Select(r => r.Value).Should().Equal("v:1", "v:2");
        inner.Contains("p:user:1").Should().BeTrue("the inner cache is written with mapped keys");
        inner.Contains("user:1").Should().BeFalse();
    }

    [Fact]
    public async Task Hits_are_returned_under_their_states()
    {
        var inner = new DictionaryCache();
        inner.Seed<string>("p:user:1", "A");
        var sut = new Cache<string>(inner, new PrefixStrategy());
        var observed = new List<long[]>();

        var result = await sut.GetOrAddAsync(
            Entries(1, 2),
            (ids, _) => { observed.Add(ids); return Task.FromResult<KeyValuePair<long, string?>[]>([new(2L, "B")]); },
            testContextAccessor.Current.CancellationToken);

        observed.Single().Should().Equal(States2);
        result.Should().Equal(
            new KeyValuePair<long, string?>(1L, "A"),
            new KeyValuePair<long, string?>(2L, "B"));
    }

    [Fact]
    public async Task Distinct_states_colliding_onto_one_mapped_key_both_survive()
    {
        var inner = new DictionaryCache();
        var sut = new Cache<string>(inner, new CollapsingStrategy());
        var observed = new List<long[]>();

        var result = await sut.GetOrAddAsync(
            Entries(1, 2),
            (ids, _) => { observed.Add(ids); return Task.FromResult(ids.Select(id => new KeyValuePair<long, string?>(id, "shared")).ToArray()); },
            testContextAccessor.Current.CancellationToken);

        result.Select(r => r.Key).Should().Equal(States1And2, "one result per distinct state, even when keys collide");
        result.Select(r => r.Value).Should().Equal("shared", "shared");
    }

    private sealed class CollapsingStrategy : ICacheKeyStrategy
    {
        public CacheKey GetCacheKey<T>(CacheKey key) => "collapsed";
    }

    [Fact]
    public async Task Expiration_overloads_are_callable()
    {
        var inner = new DictionaryCache();
        var sut = new Cache<string>(inner);
        var token = testContextAccessor.Current.CancellationToken;

        static Task<KeyValuePair<long, string?>[]> Gen(long[] ids, CancellationToken _) =>
            Task.FromResult(ids.Select(id => new KeyValuePair<long, string?>(id, "v")).ToArray());

        await sut.GetOrAddAsync(Entries(1), Gen, TimeSpan.FromMinutes(1), token);
        await sut.GetOrAddAsync(Entries(2), Gen, DateTimeOffset.UtcNow.AddMinutes(1), token);

        inner.Contains("user:1").Should().BeTrue();
        inner.Contains("user:2").Should().BeTrue();
    }

    [Fact]
    public void Sync_GetOrAdd_keeps_the_key_only_shape()
    {
        var inner = new DictionaryCache();
        ICache<string> sut = new Cache<string>(inner);

        var result = sut.GetOrAdd(
            [(CacheKey)"a", (CacheKey)"b"],
            keys => keys.Select(k => new KeyValuePair<CacheKey, string?>(k, "v:" + k.Name)).ToArray(),
            testContextAccessor.Current.CancellationToken);

        result.Select(r => r.Value).Should().Equal("v:a", "v:b");
    }
}
