using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

#pragma warning disable IDE0034 // default(CacheKey) is written out to pin the CacheKey overload; a bare default would bind differently

/// <summary>The <c>Span&lt;char&gt;</c> <c>GetOrAddWithExpirationAsync</c> answers as the <see cref="CacheKey"/> forms do, and a warm hit by span allocates nothing.</summary>
public class SpanGeneratedExpirationTests
{
    private static readonly DateTimeOffset Deadline = DateTimeOffset.UtcNow.AddMinutes(7);
    private static readonly Func<CancellationToken, Task<GeneratedValue<string>>> Unexpected = static _ => throw new InvalidOperationException("the generator ran on a hit");
    private static readonly Func<int, CancellationToken, Task<GeneratedValue<string>>> UnexpectedState = static (_, _) => throw new InvalidOperationException("the generator ran on a hit");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_bare_default_key_reaches_the_cache_as_default_CacheKey()
    {
        var cache = Substitute.For<ICache>();
        var typed = Substitute.For<ICache<string>>();

        await cache.GetOrAddWithExpirationAsync<string>(default, Generate, null, Ct);
        await cache.GetOrAddWithExpirationAsync(default, 1, static (_, _) => Generate(Ct), null, Ct);
        await typed.GetOrAddWithExpirationAsync(default, Generate, Ct);
        await typed.GetOrAddWithExpirationAsync(default, 1, static (_, _) => Generate(Ct), Ct);

        await cache.Received(1).GetOrAddWithExpirationAsync(default(CacheKey), Arg.Any<Func<CancellationToken, Task<GeneratedValue<string>>>>(), null, Ct);
        await cache.Received(1).GetOrAddWithExpirationAsync(default(CacheKey), 1, Arg.Any<Func<int, CancellationToken, Task<GeneratedValue<string>>>>(), null, Ct);
        await typed.Received(1).GetOrAddWithExpirationAsync(default(CacheKey), Arg.Any<Func<CancellationToken, Task<GeneratedValue<string>>>>(), Ct);
        await typed.Received(1).GetOrAddWithExpirationAsync(default(CacheKey), 1, Arg.Any<Func<int, CancellationToken, Task<GeneratedValue<string>>>>(), Ct);
    }

    [Fact]
    public async Task A_bare_default_key_reaches_the_inner_cache_as_default_CacheKey_through_Cache_of_T()
    {
        // Compiles only while Cache<T>'s CacheKey members outrank its span members (CS0121 otherwise).
        var inner = Substitute.For<ICache>();
        var typed = new Cache<string>(inner);

        await typed.GetOrAddWithExpirationAsync(default, Generate, Ct);
        await typed.GetOrAddWithExpirationAsync(default, 1, static (_, _) => Generate(Ct), Ct);

        await inner.Received(1).GetOrAddWithExpirationAsync(default(CacheKey), Arg.Any<Func<CancellationToken, Task<GeneratedValue<string>>>>(), Arg.Any<CachePolicy?>(), Ct);
        await inner.Received(1).GetOrAddWithExpirationAsync(default(CacheKey), 1, Arg.Any<Func<int, CancellationToken, Task<GeneratedValue<string>>>>(), Arg.Any<CachePolicy?>(), Ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_typed_cache_hands_its_own_policy_to_a_span_call(bool withState)
    {
        using var owner = InMemoryMultilayer.Cache();
        var typed = new Cache<string>(owner, new PrefixCacheKeyStrategy("app"), new CachePolicy { DistributedExpiration = TimeSpan.FromMinutes(20) });

        Complete(withState
            ? SpanTypedState(typed, "token", 1, static (_, _) => Task.FromResult(new GeneratedValue<string>("jwt")))
            : SpanTyped(typed, "token", static _ => Task.FromResult(new GeneratedValue<string>("jwt"))));

        (await owner.GetCacheEntryAsync<string>("app:token", policy: null, Ct)).Expiration.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(20), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_span_miss_stores_the_value_until_the_expiration_the_generator_returns_and_a_hit_does_not_generate()
    {
        using var cache = InMemoryMultilayer.Cache();
        var calls = 0;

        var first = Complete(Span(cache, " User:42 ", _ => { calls++; return Task.FromResult(new GeneratedValue<string>("jwt", Deadline)); }));
        var second = Complete(Span(cache, "user:42", Unexpected));

        first.Should().Be("jwt");
        second.Should().Be("jwt");
        calls.Should().Be(1);
        (await cache.GetCacheEntryAsync<string>("USER:42", policy: null, Ct)).Expiration.Should().Be(Deadline);
    }

    [Fact]
    public async Task A_span_state_miss_stores_under_the_key_and_a_hit_does_not_generate()
    {
        using var cache = InMemoryMultilayer.Cache();

        Complete(SpanState(cache, "user:42", 7, static (n, _) => Task.FromResult(new GeneratedValue<string>($"v{n}", Deadline)))).Should().Be("v7");
        Complete(SpanState(cache, " User:42 ", 8, UnexpectedState)).Should().Be("v7");

        (await cache.GetAsync<string>("user:42", policy: null, Ct)).Should().Be("v7");
        (await cache.GetCacheEntryAsync<string>("user:42", policy: null, Ct)).Expiration.Should().Be(Deadline);
    }

    [Fact]
    public async Task A_span_value_that_has_already_expired_is_returned_and_not_stored()
    {
        using var cache = InMemoryMultilayer.Cache();
        var calls = 0;
        Task<GeneratedValue<string>> Generate(CancellationToken _)
        {
            calls++;
            return Task.FromResult(new GeneratedValue<string>("jwt", DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        Complete(Span(cache, "token", Generate)).Should().Be("jwt");
        Complete(Span(cache, "token", Generate)).Should().Be("jwt");

        calls.Should().Be(2);
        (await cache.ContainsAsync<string>("token", Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task A_span_value_without_an_expiration_takes_the_policy_lifetime()
    {
        using var cache = InMemoryMultilayer.Cache();
        var policy = new CachePolicy { DistributedExpiration = TimeSpan.FromMinutes(20) };

        Complete(Span(cache, "token", static _ => Task.FromResult(new GeneratedValue<string>("jwt")), policy));

        (await cache.GetCacheEntryAsync<string>("token", policy: null, Ct)).Expiration.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(20), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_span_miss_runs_the_generator_once_under_concurrency()
    {
        using var cache = InMemoryMultilayer.Cache();
        var calls = 0;
        var gate = new TaskCompletionSource();
        async Task<GeneratedValue<string>> Generate(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return new GeneratedValue<string>("jwt", Deadline);
        }

        var first = Span(cache, "token", Generate).AsTask();
        var second = Span(cache, "token", Generate).AsTask();
        gate.SetResult();

        (await Task.WhenAll(first, second)).Should().AllBe("jwt");
        calls.Should().Be(1);
    }

    [Fact]
    public async Task A_span_call_rejects_an_empty_key_as_the_key_form_does()
    {
        using var cache = InMemoryMultilayer.Cache();

        var act = async () => await Span(cache, "  ", Unexpected);
        var byKey = async () => await cache.GetOrAddWithExpirationAsync<string>("  ", Unexpected, Ct);

        await byKey.Should().ThrowAsync<ArgumentNullException>();
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void A_span_state_call_rejects_a_null_generator()
    {
        using var cache = InMemoryMultilayer.Cache();

        var act = () => SpanState<int>(cache, "k", 1, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task A_span_call_with_a_cancelled_token_ends_as_the_key_form_does()
    {
        using var cache = InMemoryMultilayer.Cache();
        Complete(Span(cache, "warm", static _ => Task.FromResult(new GeneratedValue<string>("v", Deadline))));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var byKey = await Outcome(async () => await cache.GetOrAddWithExpirationAsync<string>("warm", Unexpected, cancelled.Token));
        var bySpan = await Outcome(async () => await Span(cache, "warm", Unexpected, token: cancelled.Token));

        bySpan.Should().Be(byKey);
    }

    [Fact]
    public async Task A_typed_cache_stores_a_span_key_under_its_own_strategy()
    {
        using var owner = InMemoryMultilayer.Cache();
        var typed = new Cache<string>(owner, new PrefixCacheKeyStrategy("app"));

        Complete(SpanTyped(typed, " User:42 ", static _ => Task.FromResult(new GeneratedValue<string>("v", Deadline)))).Should().Be("v");
        Complete(SpanTyped(typed, "user:42", Unexpected)).Should().Be("v");
        Complete(SpanTypedState(typed, "user:42", 1, UnexpectedState)).Should().Be("v");

        (await owner.GetAsync<string>("app:user:42", policy: null, Ct)).Should().Be("v");
        (await owner.GetCacheEntryAsync<string>("app:user:42", policy: null, Ct)).Expiration.Should().Be(Deadline);
    }

    [Fact]
    public async Task A_typed_cache_with_a_strategy_that_declines_the_span_still_stores_under_the_key_it_builds()
    {
        using var owner = InMemoryMultilayer.Cache();
        var typed = new Cache<string>(owner, new DecliningStrategy());

        Complete(SpanTypedState(typed, "user:42", 1, static (n, _) => Task.FromResult(new GeneratedValue<string>($"v{n}", Deadline)))).Should().Be("v1");

        (await typed.GetAsync("user:42", Ct)).Should().Be("v1");
    }

    [Fact]
    public async Task A_cache_without_the_capability_reads_then_sets_through_its_CacheKey_members()
    {
        var cache = new DictionaryCache();
        var calls = 0;

        Complete(Span(cache, " K ", _ => { calls++; return Task.FromResult(new GeneratedValue<string>("v", Deadline)); })).Should().Be("v");
        Complete(Span(cache, "k", Unexpected)).Should().Be("v");

        calls.Should().Be(1);
        cache.Read<string>("k").Should().Be("v");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_proxied_cache_serves_the_span_form_through_its_CacheKey_members()
    {
        var cache = Substitute.For<ICache>();
        var typed = Substitute.For<ICache<string>>();
        ValueTaskStubs.Returns(cache.GetOrAddWithExpirationAsync(new CacheKey("k"), Arg.Any<Func<CancellationToken, Task<GeneratedValue<string>>>>(), null, Ct), "hit");
        ValueTaskStubs.Returns(cache.GetOrAddWithExpirationAsync(new CacheKey("k"), 1, Arg.Any<Func<int, CancellationToken, Task<GeneratedValue<string>>>>(), null, Ct), "hit");
        ValueTaskStubs.Returns(typed.GetOrAddWithExpirationAsync(new CacheKey("k"), Arg.Any<Func<CancellationToken, Task<GeneratedValue<string>>>>(), Ct), "typed-hit");
        ValueTaskStubs.Returns(typed.GetOrAddWithExpirationAsync(new CacheKey("k"), 1, Arg.Any<Func<int, CancellationToken, Task<GeneratedValue<string>>>>(), Ct), "typed-hit");

        Complete(Span(cache, "k", Unexpected, token: Ct)).Should().Be("hit");
        Complete(SpanState(cache, "k", 1, UnexpectedState)).Should().Be("hit");
        Complete(SpanTyped(typed, "k", Unexpected)).Should().Be("typed-hit");
        Complete(SpanTypedState(typed, "k", 1, UnexpectedState)).Should().Be("typed-hit");
        await Task.CompletedTask;
    }

    [Fact]
    public void The_null_cache_returns_the_generated_value_by_span()
    {
        Complete(Span(NullCache.Instance, "k", static _ => Task.FromResult(new GeneratedValue<string>("v", Deadline)))).Should().Be("v");
        Complete(SpanState(NullCache.Instance, "k", 3, static (n, _) => Task.FromResult(new GeneratedValue<string>($"v{n}", Deadline)))).Should().Be("v3");
    }

    [Fact]
    public void A_miss_by_span_reads_the_local_tier_once()
    {
        var memory = new CountingMemoryCacheFactory();
        using var cache = InMemoryMultilayer.Cache(memory: memory);
        var noLocalLock = new CachePolicy { Lock = new LockProfile { LocalSingleFlightEnabled = false } };

        Complete(Span(cache, "k", static _ => Task.FromResult(new GeneratedValue<string>("v", Deadline)), noLocalLock)).Should().Be("v");

        memory.Lookups.Should().Be(1);
    }

    [Fact]
    public void A_state_miss_by_span_reads_the_local_tier_once()
    {
        var memory = new CountingMemoryCacheFactory();
        using var cache = InMemoryMultilayer.Cache(memory: memory);
        var noLocalLock = new CachePolicy { Lock = new LockProfile { LocalSingleFlightEnabled = false } };

        Complete(SpanState(cache, "k", 1, static (n, _) => Task.FromResult(new GeneratedValue<string>($"v{n}", Deadline)), noLocalLock)).Should().Be("v1");

        memory.Lookups.Should().Be(1);
    }

#if NET9_0_OR_GREATER
    [Fact]
    public async Task Warm_span_get_or_add_with_expiration_allocates_nothing()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheKeyStrategy = new PrefixCacheKeyStrategy("tier") });
        var typed = new Cache<string>(cache, new PrefixCacheKeyStrategy("app"));
        (await typed.SetAsync("user:42", "v", Ct)).Should().BeTrue();
        (await cache.SetAsync<string>("plain:1", "p", policy: null, Ct)).Should().BeTrue();

        AllocatedBy(() => Complete(Span(cache, "plain:1", Unexpected))).Should().Be(0);
        AllocatedBy(() => Complete(SpanState(cache, " Plain:1 ", 1, UnexpectedState))).Should().Be(0);
        AllocatedBy(() => Complete(SpanTyped(typed, "user:42", Unexpected))).Should().Be(0);
        AllocatedBy(() => Complete(SpanTypedState(typed, "user:42", 1, UnexpectedState))).Should().Be(0);
    }

    /// <summary>The fewest bytes the current thread allocates across a thousand warm calls in any of ten rounds: a per-call allocation shows in every round, a one-off from tiered JIT in one.</summary>
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

    private static Task<GeneratedValue<string>> Generate(CancellationToken _) => Task.FromResult(new GeneratedValue<string>("v", Deadline));

    private static async Task<string> Outcome(Func<Task<string?>> call)
    {
        try
        {
            return "value:" + await call();
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }

    private static string? Complete(ValueTask<string?> call) => SpanReads.Complete(call);

    private static ValueTask<string?> Span(ICache cache, string key, Func<CancellationToken, Task<GeneratedValue<string>>> generator, CachePolicy? policy = null, CancellationToken? token = null)
    {
        return cache.GetOrAddWithExpirationAsync($"{key}", generator, policy, token ?? Ct);
    }

    private static ValueTask<string?> SpanState<TState>(ICache cache, string key, TState state, Func<TState, CancellationToken, Task<GeneratedValue<string>>> generator, CachePolicy? policy = null)
    {
        return cache.GetOrAddWithExpirationAsync($"{key}", state, generator, policy, Ct);
    }

    private static ValueTask<string?> SpanTyped(ICache<string> cache, string key, Func<CancellationToken, Task<GeneratedValue<string>>> generator)
    {
        return cache.GetOrAddWithExpirationAsync($"{key}", generator, Ct);
    }

    private static ValueTask<string?> SpanTypedState<TState>(ICache<string> cache, string key, TState state, Func<TState, CancellationToken, Task<GeneratedValue<string>>> generator)
    {
        return cache.GetOrAddWithExpirationAsync($"{key}", state, generator, Ct);
    }

    private sealed class DecliningStrategy : ICacheKeyStrategy, ISpanCacheKeyStrategy
    {
        public CacheKey GetCacheKey<T>(CacheKey key) => key.WithName(key.Name + "#d");

        public bool TryGetCacheKey<T>(ReadOnlySpan<char> key, Span<char> destination, out int written)
        {
            written = 0;
            return false;
        }
    }
}
