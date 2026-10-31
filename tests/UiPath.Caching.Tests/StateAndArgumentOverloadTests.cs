using Moq;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>The state overloads of the hash caches and of GetOrAddWithExpirationAsync, and the batch overloads that take one argument for the whole call.</summary>
public class StateAndArgumentOverloadTests
{
    private static readonly DateTimeOffset Deadline = DateTimeOffset.UtcNow.AddMinutes(7);

    private static readonly int[][] MissedBatches = [[2, 3], [4]];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Func<Request, CancellationToken, Task<IDictionary<string, string?>>> UnexpectedHash { get; } = static (_, _) => throw new InvalidOperationException("the generator ran on a hit");

    [Fact]
    public async Task A_hash_miss_runs_the_generator_with_its_state_once_and_a_hit_does_not_run_it()
    {
        using var cache = InMemoryMultilayer.HashCache();
        var request = new Request("a", 1);
        var calls = 0;

        (await cache.GetOrAddAsync<string, Request>("k", request, (r, ct) => { calls++; return LoadHash(r, ct); }, policy: null, Ct))["id"].Should().Be("a:1");
        (await cache.GetOrAddAsync<string, Request>("k", request, UnexpectedHash, policy: null, Ct))["id"].Should().Be("a:1");
        (await cache.GetOrAddAsync<string, Request>("k", request, UnexpectedHash, Ct))["id"].Should().Be("a:1");
        (SpanHash(cache, request, UnexpectedHash))["id"].Should().Be("a:1");

        calls.Should().Be(1);
    }

    [Fact]
    public async Task A_hash_state_miss_carries_the_expiration_and_set_option_it_is_given()
    {
        using var cache = InMemoryMultilayer.HashCache();
        var request = new Request("a", 1);

        await cache.GetOrAddAsync<string, Request>("k:when", request, LoadHash, Deadline, Ct);
        await cache.GetOrAddAsync<string, Request>("k:span", request, LoadHash, TimeSpan.FromMinutes(30), Ct);
        await cache.GetOrAddAsync<string, Request>("k:set", request, LoadHash, Deadline, HashCacheSetOption.KeyReplace, policy: null, Ct);

        (await cache.GetCacheEntryAsync<string>("k:when", policy: null, Ct)).Expiration.Should().BeCloseTo(Deadline, TimeSpan.FromSeconds(1));
        (await cache.GetCacheEntryAsync<string>("k:span", policy: null, Ct)).Expiration.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(30), TimeSpan.FromSeconds(30));
        (await cache.GetCacheEntryAsync<string>("k:set", policy: null, Ct)).Expiration.Should().BeCloseTo(Deadline, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Concurrent_hash_state_misses_on_one_key_run_one_generator()
    {
        using var cache = InMemoryMultilayer.HashCache();
        var calls = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IDictionary<string, string?>> Slow(Request request, CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            await release.Task.WaitAsync(token);
            return await LoadHash(request, token);
        }

        var callers = Enumerable.Range(0, 8).Select(_ => cache.GetOrAddAsync<string, Request>("k", new Request("a", 1), Slow, policy: null, Ct).AsTask()).ToArray();
        await Task.Delay(100, Ct);
        release.SetResult();

        (await Task.WhenAll(callers)).Should().AllSatisfy(values => values["id"].Should().Be("a:1"));
        calls.Should().Be(1);
    }

    [Fact]
    public async Task A_typed_hash_cache_runs_the_state_generator_under_its_own_key_strategy()
    {
        using var cache = InMemoryMultilayer.HashCache();
        var typed = new HashCache<string>(cache, new PrefixCacheKeyStrategy("app"));
        var request = new Request("a", 1);

        (await typed.GetOrAddAsync("k", request, LoadHash, Ct))["id"].Should().Be("a:1");
        (await typed.GetOrAddAsync("k", request, UnexpectedHash, TimeSpan.FromMinutes(5), Ct))["id"].Should().Be("a:1");
        (await ((IHashCache<string>)typed).GetOrAddAsync("k", request, UnexpectedHash, Deadline, Ct))["id"].Should().Be("a:1");
        (SpanHash(typed, request, UnexpectedHash))["id"].Should().Be("a:1");

        (await cache.GetAsync<string>("k", policy: null, Ct)).Should().BeEmpty("the typed cache stores under its own strategy's key");
    }

    [Theory]
    [InlineData(MockBehavior.Strict)]
    [InlineData(MockBehavior.Loose)]
    public async Task A_moq_hash_cache_serves_the_state_members_and_their_span_keys_from_its_state_setups(MockBehavior behavior)
    {
        var mock = new Mock<IHashCache>(behavior);
        mock.Setup(c => c.GetOrAddAsync(It.IsAny<CacheKey>(), It.IsAny<Request>(), It.IsAny<Func<Request, CancellationToken, Task<IDictionary<string, string?>>>>(), Deadline, HashCacheSetOption.KeyReplace, null, It.IsAny<CancellationToken>()))
            .Returns((CacheKey _, Request state, Func<Request, CancellationToken, Task<IDictionary<string, string?>>> generator, DateTimeOffset _, HashCacheSetOption? _, CachePolicy? _, CancellationToken token) => new ValueTask<IDictionary<string, string?>>(generator(state, token)));
        mock.Setup(c => c.GetOrAddAsync(It.IsAny<CacheKey>(), It.IsAny<Request>(), It.IsAny<Func<Request, CancellationToken, Task<IDictionary<string, string?>>>>(), null, It.IsAny<CancellationToken>()))
            .Returns((CacheKey _, Request state, Func<Request, CancellationToken, Task<IDictionary<string, string?>>> generator, CachePolicy? _, CancellationToken token) => new ValueTask<IDictionary<string, string?>>(generator(state, token)));
        var typed = new Mock<IHashCache<string>>(behavior);
        typed.Setup(c => c.GetOrAddAsync(It.IsAny<CacheKey>(), It.IsAny<Request>(), It.IsAny<Func<Request, CancellationToken, Task<IDictionary<string, string?>>>>(), It.IsAny<CancellationToken>()))
            .Returns((CacheKey _, Request state, Func<Request, CancellationToken, Task<IDictionary<string, string?>>> generator, CancellationToken token) => new ValueTask<IDictionary<string, string?>>(generator(state, token)));

        (await mock.Object.GetOrAddAsync<string, Request>("k", new Request("a", 2), LoadHash, Deadline, HashCacheSetOption.KeyReplace, policy: null, Ct))["id"].Should().Be("a:2");
        (SpanHash(mock.Object, new Request("a", 3), LoadHash))["id"].Should().Be("a:3");
        (await typed.Object.GetOrAddAsync("k", new Request("a", 4), LoadHash, Ct))["id"].Should().Be("a:4");
        (SpanHash(typed.Object, new Request("a", 5), LoadHash))["id"].Should().Be("a:5");
    }

    [Fact]
    public async Task An_nsubstitute_hash_cache_serves_the_state_members_from_its_state_setups()
    {
        var cache = Substitute.For<IHashCache>();
        ValueTaskStubs.Returns(
            cache.GetOrAddAsync(Arg.Any<CacheKey>(), Arg.Any<Request>(), Arg.Any<Func<Request, CancellationToken, Task<IDictionary<string, string?>>>>(), TimeSpan.FromSeconds(1), Arg.Any<CachePolicy?>(), Ct),
            call => new ValueTask<IDictionary<string, string?>>(call.Arg<Func<Request, CancellationToken, Task<IDictionary<string, string?>>>>()(call.Arg<Request>(), Ct)));
        var typed = Substitute.For<IHashCache<string>>();
        ValueTaskStubs.Returns(
            typed.GetOrAddAsync(Arg.Any<CacheKey>(), Arg.Any<Request>(), Arg.Any<Func<Request, CancellationToken, Task<IDictionary<string, string?>>>>(), Ct),
            call => new ValueTask<IDictionary<string, string?>>(call.Arg<Func<Request, CancellationToken, Task<IDictionary<string, string?>>>>()(call.Arg<Request>(), Ct)));

        (await cache.GetOrAddAsync<string, Request>("k", new Request("a", 5), LoadHash, TimeSpan.FromSeconds(1), Ct))["id"].Should().Be("a:5");
        (await typed.GetOrAddAsync("k", new Request("a", 6), LoadHash, Ct))["id"].Should().Be("a:6");
        (SpanHash(typed, new Request("a", 7), LoadHash))["id"].Should().Be("a:7");
    }

    [Fact]
    public async Task A_generated_expiration_miss_runs_the_generator_with_its_state_and_stores_until_the_expiration_it_returns()
    {
        using var cache = InMemoryMultilayer.Cache();
        var typed = new Cache<string>(cache, new PrefixCacheKeyStrategy("app"));
        var request = new Request("a", 1);
        var calls = 0;
        Task<GeneratedValue<string>> Generate(Request r, CancellationToken _)
        {
            calls++;
            return Task.FromResult(new GeneratedValue<string>($"{r.Id}:{r.Version}", Deadline));
        }

        (await cache.GetOrAddWithExpirationAsync<string, Request>("k", request, Generate, policy: null, Ct)).Should().Be("a:1");
        (await cache.GetOrAddWithExpirationAsync<string, Request>("k", request, Generate, Ct)).Should().Be("a:1");
        (await typed.GetOrAddWithExpirationAsync("k", request, Generate, Ct)).Should().Be("a:1");
        (await ((ICache<string>)typed).GetOrAddWithExpirationAsync("k", request, Generate, Ct)).Should().Be("a:1");

        calls.Should().Be(2, "one miss for each key strategy");
        (await cache.GetCacheEntryAsync<string>("k", policy: null, Ct)).Expiration.Should().Be(Deadline);
    }

    [Theory]
    [InlineData(MockBehavior.Strict)]
    [InlineData(MockBehavior.Loose)]
    public void A_moq_cache_serves_the_span_generated_state_members_from_its_state_setups(MockBehavior behavior)
    {
        var mock = new Mock<ICache>(behavior);
        mock.Setup(c => c.GetOrAddWithExpirationAsync(new CacheKey("jwt"), It.IsAny<int>(), It.IsAny<Func<int, CancellationToken, Task<GeneratedValue<string>>>>(), null, It.IsAny<CancellationToken>()))
            .Returns(async (CacheKey _, int state, Func<int, CancellationToken, Task<GeneratedValue<string>>> generator, CachePolicy? _, CancellationToken token) => (await generator(state, token)).Value);
        var typed = Substitute.For<ICache<string>>();
        ValueTaskStubs.Returns(
            typed.GetOrAddWithExpirationAsync(new CacheKey("jwt"), Arg.Any<int>(), Arg.Any<Func<int, CancellationToken, Task<GeneratedValue<string>>>>(), Arg.Any<CancellationToken>()),
            async call => (await call.Arg<Func<int, CancellationToken, Task<GeneratedValue<string>>>>()(call.Arg<int>(), Ct)).Value);

        SpanGenerated(mock.Object, 1).Should().Be("token:1");
        SpanGenerated(typed, 2).Should().Be("token:2");
    }

    [Fact]
    public async Task A_batch_with_an_argument_hands_it_and_the_missing_states_to_one_generator_run()
    {
        using var cache = InMemoryMultilayer.Cache();
        var typed = new Cache<string>(cache, new PrefixCacheKeyStrategy("app"));
        var loader = new Loader();
        await cache.SetAsync<string>("k:1", "cached", policy: null, Ct);

        var values = await cache.GetOrAddAsync<string, int, Loader>(Entries(1, 2, 3), loader, static (l, states, ct) => l.LoadAsync(states, ct), policy: null, Ct);
        var again = await cache.GetOrAddAsync<string, int, Loader>(Entries(1, 2, 3), loader, static (l, states, ct) => l.LoadAsync(states, ct), TimeSpan.FromMinutes(5), policy: null, Ct);
        var typedValues = await typed.GetOrAddAsync(Entries(4), loader, static (l, states, ct) => l.LoadAsync(states, ct), Deadline, Ct);

        values.Select(v => v.Value).Should().Equal("cached", "v:2", "v:3");
        again.Select(v => v.Value).Should().Equal("cached", "v:2", "v:3");
        typedValues.Single().Value.Should().Be("v:4");
        loader.Runs.Should().BeEquivalentTo(MissedBatches, options => options.WithStrictOrdering());
    }

    [Theory]
    [InlineData(MockBehavior.Strict)]
    [InlineData(MockBehavior.Loose)]
    public async Task A_moq_cache_serves_the_batch_argument_members_from_their_own_setups(MockBehavior behavior)
    {
        var mock = new Mock<ICache>(behavior);
        mock.Setup(c => c.GetOrAddAsync(It.IsAny<KeyValuePair<CacheKey, int>[]>(), It.IsAny<Loader>(), It.IsAny<Func<Loader, int[], CancellationToken, Task<KeyValuePair<int, string?>[]>>>(), null, It.IsAny<CancellationToken>()))
            .Returns((KeyValuePair<CacheKey, int>[] entries, Loader loader, Func<Loader, int[], CancellationToken, Task<KeyValuePair<int, string?>[]>> generator, CachePolicy? _, CancellationToken token) =>
                new ValueTask<KeyValuePair<int, string?>[]>(generator(loader, entries.Select(e => e.Value).ToArray(), token)));
        var typed = Substitute.For<ICache<string>>();
        ValueTaskStubs.Returns(
            typed.GetOrAddAsync(Arg.Any<KeyValuePair<CacheKey, int>[]>(), Arg.Any<Loader>(), Arg.Any<Func<Loader, int[], CancellationToken, Task<KeyValuePair<int, string?>[]>>>(), Arg.Any<CancellationToken>()),
            call => new ValueTask<KeyValuePair<int, string?>[]>(call.Arg<Func<Loader, int[], CancellationToken, Task<KeyValuePair<int, string?>[]>>>()(call.Arg<Loader>(), call.Arg<KeyValuePair<CacheKey, int>[]>().Select(e => e.Value).ToArray(), Ct)));
        var loader = new Loader();

        (await mock.Object.GetOrAddAsync<string, int, Loader>(Entries(1), loader, static (l, states, ct) => l.LoadAsync(states, ct), policy: null, Ct)).Single().Value.Should().Be("v:1");
        (await typed.GetOrAddAsync(Entries(2), loader, static (l, states, ct) => l.LoadAsync(states, ct), Ct)).Single().Value.Should().Be("v:2");
    }

#if NET9_0_OR_GREATER
    [Fact]
    public async Task A_warm_hash_state_hit_allocates_nothing()
    {
        using var cache = InMemoryMultilayer.HashCache();
        var typed = new HashCache<string>(cache, new PrefixCacheKeyStrategy("app"));
        var request = new Request("a", 1);
        CacheKey key = "k";
        await cache.GetOrAddAsync<string, Request>(key, request, LoadHash, policy: null, Ct);
        await typed.GetOrAddAsync(key, request, LoadHash, Ct);

        AllocatedBy(() => Complete(cache.GetOrAddAsync<string, Request>(key, request, UnexpectedHash, policy: null, Ct))).Should().Be(0, "the CacheKey form");
        AllocatedBy(() => Complete(cache.GetOrAddAsync<string, Request>(key, request, UnexpectedHash, TimeSpan.FromMinutes(1), policy: null, Ct))).Should().Be(0, "the TimeSpan form");
        AllocatedBy(() => Complete(((IHashCache)cache).GetOrAddAsync<string, Request>(key, request, UnexpectedHash, policy: null, Ct))).Should().Be(0, "the IHashCache member");
        AllocatedBy(() => SpanHash(cache, request, UnexpectedHash)).Should().Be(0, "the span form");
        AllocatedBy(() => Complete(typed.GetOrAddAsync(key, request, UnexpectedHash, Ct))).Should().Be(0, "the typed CacheKey form");
        AllocatedBy(() => Complete(((IHashCache<string>)typed).GetOrAddAsync(key, request, UnexpectedHash, Ct))).Should().Be(0, "the IHashCache<T> member");
        AllocatedBy(() => SpanHash(typed, request, UnexpectedHash)).Should().Be(0, "the typed span form");
    }

    [Fact]
    public async Task A_warm_generated_state_hit_allocates_nothing_and_a_batch_argument_hit_less_than_a_capturing_lambda()
    {
        using var cache = InMemoryMultilayer.Cache();
        var request = new Request("a", 1);
        var loader = new Loader();
        var entries = Entries(1, 2);
        await cache.GetOrAddWithExpirationAsync<string, Request>("g", request, Generated, policy: null, Ct);
        await cache.GetOrAddAsync<string, int, Loader>(entries, loader, static (l, states, ct) => l.LoadAsync(states, ct), policy: null, Ct);

        // Each lambda form captures a per-call local, as a caller's request data would be.
        var generatedState = AllocatedBy(() => Complete(cache.GetOrAddWithExpirationAsync<string, Request>("g", request, Generated, policy: null, Ct)));
        var generatedLambda = AllocatedBy(() =>
        {
            var r = request;
            Complete(cache.GetOrAddWithExpirationAsync<string>("g", ct => Generated(r, ct), policy: null, Ct));
        });
        var batchArgument = AllocatedBy(() => Complete(cache.GetOrAddAsync<string, int, Loader>(entries, loader, static (l, states, ct) => l.LoadAsync(states, ct), policy: null, Ct)));
        var batchLambda = AllocatedBy(() =>
        {
            var l = loader;
            Complete(cache.GetOrAddAsync<string, int>(entries, (states, ct) => l.LoadAsync(states, ct), policy: null, Ct));
        });

        generatedState.Should().Be(0);
        generatedLambda.Should().BeGreaterThan(0);
        batchArgument.Should().BeLessThan(batchLambda);
    }

    private static long AllocatedBy(Action read)
    {
        var fewest = long.MaxValue;
        for (var round = 0; round < 10 && fewest > 0; round++)
        {
            for (var i = 0; i < 1_000; i++)
            {
                read();
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1_000; i++)
            {
                read();
            }

            fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return fewest;
    }
#endif

    private static Task<IDictionary<string, string?>> LoadHash(Request request, CancellationToken token) =>
        Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?> { ["id"] = $"{request.Id}:{request.Version}" });

    private static Task<GeneratedValue<string>> Generated(Request request, CancellationToken token) =>
        Task.FromResult(new GeneratedValue<string>($"{request.Id}:{request.Version}", Deadline));

    private static KeyValuePair<CacheKey, int>[] Entries(params int[] ids) => [.. ids.Select(id => new KeyValuePair<CacheKey, int>($"k:{id}", id))];

    private static IDictionary<string, string?> SpanHash(IHashCache cache, Request request, Func<Request, CancellationToken, Task<IDictionary<string, string?>>> generator)
    {
        var text = "k";
        return Complete(cache.GetOrAddAsync<string, Request>($"{text}", request, generator, (CachePolicy?)null, Ct));
    }

    private static IDictionary<string, string?> SpanHash(IHashCache<string> cache, Request request, Func<Request, CancellationToken, Task<IDictionary<string, string?>>> generator)
    {
        var text = "k";
        return Complete(cache.GetOrAddAsync($"{text}", request, generator, Ct));
    }

    private static string? SpanGenerated(ICache cache, int state)
    {
        var text = "jwt";
        return Complete(cache.GetOrAddWithExpirationAsync<string, int>($"{text}", state, static (n, _) => Task.FromResult(new GeneratedValue<string>($"token:{n}", Deadline)), (CachePolicy?)null, Ct));
    }

    private static string? SpanGenerated(ICache<string> cache, int state)
    {
        var text = "jwt";
        return Complete(cache.GetOrAddWithExpirationAsync($"{text}", state, static (n, _) => Task.FromResult(new GeneratedValue<string>($"token:{n}", Deadline)), Ct));
    }

#pragma warning disable S5034 // read once: the result of a completed read, or the awaited one; AsTask on every read would allocate
    private static TResult Complete<TResult>(ValueTask<TResult> read) => read.IsCompletedSuccessfully ? read.Result : read.AsTask().GetAwaiter().GetResult();
#pragma warning restore S5034

    private sealed record Request(string Id, int Version);

    private sealed class Loader
    {
        public List<int[]> Runs { get; } = [];

        public Task<KeyValuePair<int, string?>[]> LoadAsync(int[] states, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Runs.Add(states);
            return Task.FromResult(states.Select(s => new KeyValuePair<int, string?>(s, $"v:{s}")).ToArray());
        }
    }
}
