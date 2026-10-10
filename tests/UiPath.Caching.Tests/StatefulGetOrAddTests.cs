using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

public class StatefulGetOrAddTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Func<Request, CancellationToken, Task<string?>> Unexpected { get; } = static (_, _) => throw new InvalidOperationException("the generator ran on a hit");

    [Fact]
    public async Task A_multilayer_miss_runs_the_generator_with_its_state_once_and_stores_the_value()
    {
        using var cache = InMemoryMultilayer.Cache();
        var request = new Request("a", 1);
        var calls = 0;

        (await cache.GetOrAddAsync<string, Request>("k:plain", request, (r, ct) => { calls++; return Load(r, ct); }, Ct)).Should().Be("a:1");
        (await cache.GetOrAddAsync<string, Request>("k:plain", request, Unexpected, Ct)).Should().Be("a:1");
        (await cache.GetOrAddAsync<string, Request>("k:span", request, Load, TimeSpan.FromMinutes(5), Ct)).Should().Be("a:1");
        (await cache.GetOrAddAsync<string, Request>("k:when", request, Load, DateTimeOffset.UtcNow.AddMinutes(5), Ct)).Should().Be("a:1");
        (await cache.GetOrAddAsync<string, Request>("k:policy", request, Load, (CachePolicy?)null, Ct)).Should().Be("a:1");

        calls.Should().Be(1);
        (await cache.GetAsync<string>("k:span", policy: null, Ct)).Should().Be("a:1");
    }

    [Fact]
    public async Task The_expiration_a_stateful_miss_is_given_is_the_one_the_entry_carries()
    {
        using var cache = InMemoryMultilayer.Cache();
        var request = new Request("a", 1);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(30);

        await cache.GetOrAddAsync<string, Request>("k:when", request, Load, deadline, Ct);
        await cache.GetOrAddAsync<string, Request>("k:span", request, Load, TimeSpan.FromMinutes(30), Ct);

        (await cache.GetCacheEntryAsync<string>("k:when", policy: null, Ct)).Expiration.Should().Be(deadline);
        (await cache.GetCacheEntryAsync<string>("k:span", policy: null, Ct)).Expiration.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(30), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_stateful_miss_rejects_the_expiration_the_closure_form_rejects()
    {
        using var cache = InMemoryMultilayer.Cache();
        var request = new Request("a", 1);

        var span = async () => await cache.GetOrAddAsync<string, Request>("k", request, Load, TimeSpan.Zero, Ct);
        var when = async () => await cache.GetOrAddAsync<string, Request>("k", request, Load, DateTimeOffset.UtcNow.AddMinutes(-1), Ct);

        await span.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await when.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task A_stateful_generator_must_not_be_null()
    {
        using var cache = InMemoryMultilayer.Cache();
        var typed = new Cache<string>(cache);

        var key = async () => await cache.GetOrAddAsync<string, Request>("k", new Request("a", 1), null!, Ct);
        var typedKey = async () => await typed.GetOrAddAsync<Request>("k", new Request("a", 1), null!, Ct);

        await key.Should().ThrowAsync<ArgumentNullException>();
        await typedKey.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task A_typed_cache_runs_the_stateful_generator_under_its_own_key_strategy()
    {
        using var cache = InMemoryMultilayer.Cache();
        var typed = new Cache<string>(cache, new PrefixCacheKeyStrategy("app"));
        var request = new Request("a", 1);

        (await typed.GetOrAddAsync("k:plain", request, Load, Ct)).Should().Be("a:1");
        (await typed.GetOrAddAsync("k:plain", request, Unexpected, Ct)).Should().Be("a:1");
        (await typed.GetOrAddAsync("k:span", request, Load, TimeSpan.FromMinutes(5), Ct)).Should().Be("a:1");
        (await typed.GetOrAddAsync("k:when", request, Load, DateTimeOffset.UtcNow.AddMinutes(5), Ct)).Should().Be("a:1");
        SpanCalls(typed).Should().Be("a:1");

        (await typed.GetAsync("k:plain", Ct)).Should().Be("a:1");
        (await cache.GetAsync<string>("k:plain", policy: null, Ct)).Should().BeNull("the typed cache stores under its own strategy's key");
    }

    [Fact]
    public async Task A_stateful_hit_past_the_rehydrate_threshold_refreshes_with_its_state()
    {
        using var cache = InMemoryMultilayer.Cache();
        var policy = new CachePolicy
        {
            DistributedExpiration = TimeSpan.FromSeconds(2),
            RehydrateEnabled = true,
            Rehydrate = new RehydrateOptions { Threshold = 0.05, BaseCooldown = TimeSpan.FromMilliseconds(10), MaxCooldown = TimeSpan.FromSeconds(1), TimeoutFraction = 0.5, Name = "stateful" },
        };
        var refreshed = new TaskCompletionSource<Request>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new Request("a", 7);
        await cache.GetOrAddAsync<string, Request>("k", request, Load, policy, Ct);

        await Task.Delay(300, Ct);
        await cache.GetOrAddAsync<string, Request>("k", request, (r, _) => { refreshed.TrySetResult(r); return Task.FromResult<string?>("fresh"); }, policy, Ct);

        (await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Should().BeSameAs(request);
    }

    [Fact]
    public async Task A_cache_without_the_capability_runs_the_generator_with_its_state_through_the_closure_overloads()
    {
        var cache = Substitute.For<ICache>();
        ValueTaskStubs.Returns(
            cache.GetOrAddAsync(Arg.Any<CacheKey>(), Arg.Any<Func<CancellationToken, Task<string?>>>(), TimeSpan.FromSeconds(1), Arg.Any<CachePolicy?>(), Ct),
            call => new ValueTask<string?>(call.Arg<Func<CancellationToken, Task<string?>>>()(Ct)));
        var typed = Substitute.For<ICache<string>>();
        ValueTaskStubs.Returns(
            typed.GetOrAddAsync(Arg.Any<CacheKey>(), Arg.Any<Func<CancellationToken, Task<string?>>>(), Ct),
            call => new ValueTask<string?>(call.Arg<Func<CancellationToken, Task<string?>>>()(Ct)));

        (await cache.GetOrAddAsync<string, Request>("k", new Request("a", 2), Load, TimeSpan.FromSeconds(1), Ct)).Should().Be("a:2");
        (await typed.GetOrAddAsync("k", new Request("a", 3), Load, Ct)).Should().Be("a:3");
        SpanFallback(cache).Should().Be("a:2");
    }

#if NET9_0_OR_GREATER
    [Fact]
    public async Task A_warm_stateful_get_or_add_that_hits_locally_allocates_nothing()
    {
        using var cache = InMemoryMultilayer.Cache();
        var typed = new Cache<string>(cache, new PrefixCacheKeyStrategy("app"));
        var request = new Request("a", 1);
        await cache.GetOrAddAsync<string, Request>("k", request, Load, Ct);
        await typed.GetOrAddAsync("k", request, Load, Ct);
        CacheKey key = "k";

        AllocatedBy(() => cache.GetOrAddAsync<string, Request>(key, request, Unexpected, Ct)).Should().Be(0, "the CacheKey form");
        AllocatedBy(() => cache.GetOrAddAsync<string, Request>(key, request, Unexpected, TimeSpan.FromMinutes(1), Ct)).Should().Be(0, "the TimeSpan form");
        AllocatedBy(() => cache.GetOrAddAsync<string, Request>(key, request, Unexpected, DateTimeOffset.UtcNow.AddMinutes(1), Ct)).Should().Be(0, "the DateTimeOffset form");
        AllocatedBy(() => SpanHit(cache, request, withExpiration: false)).Should().Be(0, "the span form");
        AllocatedBy(() => SpanHit(cache, request, withExpiration: true)).Should().Be(0, "the span form with an expiration");
        AllocatedBy(() => typed.GetOrAddAsync(key, request, Unexpected, Ct)).Should().Be(0, "the typed CacheKey form");
        AllocatedBy(() => SpanHit(typed, request)).Should().Be(0, "the typed span form");
    }

    private static void SpanHit(MultilayerCache cache, Request request, bool withExpiration)
    {
        Span<char> key = stackalloc char[8];
        "k".AsSpan().CopyTo(key);
        _ = SpanReads.Complete(withExpiration
            ? cache.GetOrAddAsync<string, Request>(key[..1], request, Unexpected, TimeSpan.FromMinutes(1), null, Ct)
            : cache.GetOrAddAsync<string, Request>(key[..1], request, Unexpected, (CachePolicy?)null, Ct));
    }

    private static void SpanHit(Cache<string> typed, Request request)
    {
        Span<char> key = stackalloc char[8];
        "k".AsSpan().CopyTo(key);
        _ = SpanReads.Complete(typed.GetOrAddAsync(key[..1], request, Unexpected, Ct));
    }

    private static long AllocatedBy(Func<ValueTask<string?>> read) => AllocatedBy(() => _ = SpanReads.Complete(read()));

    /// <summary>The fewest bytes the current thread allocates across a thousand warm calls in any of ten rounds, so a round that tiering or the runtime itself allocates in does not fail a call that allocates nothing.</summary>
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

    private static Task<string?> Load(Request request, CancellationToken token) => Task.FromResult<string?>($"{request.Id}:{request.Version}");

    private static string? SpanCalls(Cache<string> typed)
    {
        Span<char> key = "k:plain".ToCharArray();
        return typed.GetOrAddAsync(key, new Request("a", 1), Unexpected, Ct).AsTask().GetAwaiter().GetResult();
    }

    private static string? SpanFallback(ICache cache)
    {
        Span<char> key = "k".ToCharArray();
        return cache.GetOrAddAsync<string, Request>(key, new Request("a", 2), Load, TimeSpan.FromSeconds(1), (CachePolicy?)null, Ct).AsTask().GetAwaiter().GetResult();
    }

    private sealed record Request(string Id, int Version);
}
