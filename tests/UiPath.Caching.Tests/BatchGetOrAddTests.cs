using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

public class BatchGetOrAddTests(ITestContextAccessor testContextAccessor)
{
    private static readonly long[] States1 = [1L];
    private static readonly long[] States7 = [7L];
    private static readonly long[] States1And2 = [1L, 2L];
    private static readonly long[] States2And3 = [2L, 3L];
    private static readonly long[] States1To3 = [1L, 2L, 3L];
    private static readonly long[] States3Then1Then2 = [3L, 1L, 2L];
    private static readonly string?[] Gen1Twice = ["gen:1", "gen:1"];

    private static KeyValuePair<CacheKey, long>[] Entries(params long[] ids) =>
        ids.Select(id => new KeyValuePair<CacheKey, long>((CacheKey)$"user:{id}", id)).ToArray();

    private static Func<long[], CancellationToken, Task<KeyValuePair<long, string?>[]>> Generator(
        List<long[]> observed, Func<long, string?>? produce = null, params long[] omit)
    {
        produce ??= id => "gen:" + id;
        return (ids, _) =>
        {
            observed.Add(ids);
            return Task.FromResult(ids.Where(id => !omit.Contains(id))
                .Select(id => new KeyValuePair<long, string?>(id, produce(id))).ToArray());
        };
    }

    [Fact]
    public async Task Generator_receives_states_not_keys()
    {
        var fake = new DictionaryCache();
        ICache cache = fake;
        fake.Seed<string>("user:1", "A");
        var observed = new List<long[]>();

        var result = await cache.GetOrAddAsync<string, long>(
            Entries(1, 2, 3), Generator(observed), null, testContextAccessor.Current.CancellationToken);

        observed.Single().Should().Equal(States2And3, "only the states of missing entries reach the generator");
        result.Select(r => r.Key).Should().Equal(States1To3, "results are keyed by state, in request order");
        result.Select(r => r.Value).Should().Equal("A", "gen:2", "gen:3");
        fake.SetKeySets.Single().Should().BeEquivalentTo(new[] { (CacheKey)"user:2", (CacheKey)"user:3" },
            "the cache is still written by key");
    }

    [Fact]
    public async Task All_hits_do_not_invoke_the_generator()
    {
        var fake = new DictionaryCache();
        ICache cache = fake;
        fake.Seed<string>("user:1", "A");
        fake.Seed<string>("user:2", "B");
        var observed = new List<long[]>();

        var result = await cache.GetOrAddAsync<string, long>(
            Entries(1, 2), Generator(observed), null, testContextAccessor.Current.CancellationToken);

        observed.Should().BeEmpty();
        fake.SetCalls.Should().Be(0);
        result.Select(r => r.Value).Should().Equal("A", "B");
    }

    [Fact]
    public async Task Duplicate_states_collapse_and_order_is_first_occurrence()
    {
        var fake = new DictionaryCache();
        ICache cache = fake;
        var observed = new List<long[]>();

        var result = await cache.GetOrAddAsync<string, long>(
            Entries(3, 1, 3, 2), Generator(observed), null, testContextAccessor.Current.CancellationToken);

        result.Select(r => r.Key).Should().Equal(States3Then1Then2);
        observed.Single().Should().Equal(States3Then1Then2);
    }

    [Fact]
    public async Task Two_states_sharing_one_key_both_get_the_value_but_the_generator_is_asked_once()
    {
        var fake = new DictionaryCache();
        ICache cache = fake;
        var observed = new List<long[]>();

        KeyValuePair<CacheKey, long>[] entries =
        [
            new((CacheKey)"shared", 1L),
            new((CacheKey)"shared", 2L),
        ];

        var result = await cache.GetOrAddAsync<string, long>(
            entries, Generator(observed), null, testContextAccessor.Current.CancellationToken);

        observed.Single().Should().Equal(States1, "one request per distinct KEY, carrying the first state for it");
        result.Select(r => r.Key).Should().Equal(States1And2, "one result per distinct STATE");
        result.Select(r => r.Value).Should().Equal(Gen1Twice, "both states carry the one generated value");
        fake.SetKeySets.Single().Should().Equal(new[] { (CacheKey)"shared" });
    }

    [Fact]
    public async Task One_state_under_two_keys_keeps_the_first_key_and_drops_the_second()
    {
        var fake = new DictionaryCache();
        ICache cache = fake;
        var observed = new List<long[]>();

        KeyValuePair<CacheKey, long>[] entries =
        [
            new((CacheKey)"user:7", 7L),
            new((CacheKey)"other:7", 7L),
        ];

        var result = await cache.GetOrAddAsync<string, long>(
            entries, Generator(observed), null, testContextAccessor.Current.CancellationToken);

        result.Select(r => r.Key).Should().Equal(States7, "one result per distinct STATE");
        observed.Single().Should().Equal(States7, "the generator is asked once, about the one state");
        fake.SetKeySets.Single().Should().Equal(new[] { (CacheKey)"user:7" }, "only the first key for the state is written");
        fake.Contains("other:7").Should().BeFalse("the dropped entry's key never reaches the cache");
    }

    [Fact]
    public async Task State_omitted_by_the_generator_returns_default_and_is_not_cached()
    {
        var fake = new DictionaryCache();
        ICache cache = fake;
        var observed = new List<long[]>();
        var token = testContextAccessor.Current.CancellationToken;

        var result = await cache.GetOrAddAsync<string, long>(
            Entries(1, 2), Generator(observed, omit: 2L), null, token);

        result.Single(r => r.Key == 2L).Value.Should().BeNull();
        fake.Contains("user:2").Should().BeFalse("an omitted state must not be written");
        await cache.GetOrAddAsync<string, long>(Entries(2), Generator(observed), null, token);
        observed.Should().HaveCount(2, "the omitted entry must miss again on the next call");
    }

    [Fact]
    public async Task Explicit_null_from_the_generator_is_handed_to_SetAsync()
    {
        var fake = new DictionaryCache { CacheNullValues = false };
        ICache cache = fake;
        var observed = new List<long[]>();

        var result = await cache.GetOrAddAsync<string, long>(
            Entries(1), Generator(observed, produce: _ => null), null, testContextAccessor.Current.CancellationToken);

        result.Single().Value.Should().BeNull();
        fake.SetKeySets.Single().Should().Equal(new[] { (CacheKey)"user:1" },
            "null policy belongs to the implementation, so the pair is still handed to SetAsync");
        fake.Contains("user:1").Should().BeFalse("this implementation drops nulls when CacheNullValues is off");
    }

    [Fact]
    public async Task States_the_generator_returns_but_was_not_asked_for_are_ignored()
    {
        var fake = new DictionaryCache();
        ICache cache = fake;

        var result = await cache.GetOrAddAsync<string, long>(
            Entries(1),
            (_, _) => Task.FromResult<KeyValuePair<long, string?>[]>([new(1L, "A"), new(99L, "rogue")]),
            null,
            testContextAccessor.Current.CancellationToken);

        result.Should().HaveCount(1);
        fake.SetKeySets.Single().Should().Equal(new[] { (CacheKey)"user:1" },
            "the rogue pair has no key of its own, so it must be excluded from the write");
    }

    [Fact]
    public async Task Empty_entries_short_circuits()
    {
        var fake = new DictionaryCache();
        ICache cache = fake;
        var observed = new List<long[]>();

        var result = await cache.GetOrAddAsync<string, long>(
            [], Generator(observed), null, testContextAccessor.Current.CancellationToken);

        result.Should().BeEmpty();
        fake.GetCacheEntriesCalls.Should().Be(0);
        observed.Should().BeEmpty();
    }

    [Fact]
    public async Task Null_arguments_and_non_cacheable_types_throw()
    {
        var fake = new DictionaryCache();
        ICache cache = fake;
        var observed = new List<long[]>();
        var token = testContextAccessor.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await cache.GetOrAddAsync<string, long>(null!, Generator(observed), null, token));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await cache.GetOrAddAsync<string, long>(Entries(1), null!, null, token));
        await Assert.ThrowsAsync<NotCacheableException>(async () =>
            await cache.GetOrAddAsync<int, long>(
                Entries(1), (_, _) => Task.FromResult<KeyValuePair<long, int>[]>([]), null, token));
    }

    [Fact]
    public async Task Cancellation_token_reaches_the_generator()
    {
        var fake = new DictionaryCache();
        ICache cache = fake;
        using var cts = new CancellationTokenSource();
        CancellationToken observedToken = default;

        await cache.GetOrAddAsync<string, long>(
            Entries(1),
            (ids, ct) => { observedToken = ct; return Task.FromResult(ids.Select(id => new KeyValuePair<long, string?>(id, "v")).ToArray()); },
            null,
            cts.Token);

        observedToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task Cancellation_from_the_generator_propagates_to_the_caller()
    {
        var fake = new DictionaryCache();
        ICache cache = fake;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await cache.GetOrAddAsync<string, long>(
                Entries(1),
                (_, ct) => Task.FromException<KeyValuePair<long, string?>[]>(new OperationCanceledException(ct)),
                null,
                cts.Token));

        fake.Contains("user:1").Should().BeFalse("a cancelled generator must not write anything");
    }

    [Fact]
    public async Task NullCache_runs_the_generator_and_returns_its_values()
    {
        var observed = new List<long[]>();

        var result = await ((ICache)NullCache.Instance).GetOrAddAsync<string, long>(
            Entries(1, 2), Generator(observed), null, testContextAccessor.Current.CancellationToken);

        observed.Single().Should().Equal(States1And2);
        result.Select(r => r.Value).Should().Equal("gen:1", "gen:2");
    }
}
