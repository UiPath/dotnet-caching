using Moq;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

public class GeneratedExpirationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_miss_stores_the_value_until_the_expiration_the_generator_returns()
    {
        using var cache = InMemoryMultilayer.Cache();
        var deadline = DateTimeOffset.UtcNow.AddMinutes(7);
        var calls = 0;

        var first = await cache.GetOrAddWithExpirationAsync<string>("token", _ => { calls++; return Task.FromResult(new GeneratedValue<string>("jwt", deadline)); }, Ct);
        var second = await cache.GetOrAddWithExpirationAsync<string>("token", _ => throw new InvalidOperationException("the generator ran on a hit"), Ct);

        first.Should().Be("jwt");
        second.Should().Be("jwt");
        calls.Should().Be(1);
        (await cache.GetCacheEntryAsync<string>("token", policy: null, Ct)).Expiration.Should().Be(deadline);
    }

    [Fact]
    public async Task A_value_without_an_expiration_takes_the_policy_lifetime()
    {
        using var cache = InMemoryMultilayer.Cache();
        var policy = new CachePolicy { DistributedExpiration = TimeSpan.FromMinutes(20) };

        await cache.GetOrAddWithExpirationAsync<string>("token", _ => Task.FromResult(new GeneratedValue<string>("jwt")), policy, Ct);

        (await cache.GetCacheEntryAsync<string>("token", policy: null, Ct)).Expiration.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(20), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_value_that_has_already_expired_is_returned_and_not_stored()
    {
        using var cache = InMemoryMultilayer.Cache();
        var calls = 0;
        Task<GeneratedValue<string>> Generate(CancellationToken _)
        {
            calls++;
            return Task.FromResult(new GeneratedValue<string>("jwt", DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        (await cache.GetOrAddWithExpirationAsync<string>("token", Generate, Ct)).Should().Be("jwt");
        (await cache.GetOrAddWithExpirationAsync<string>("token", Generate, Ct)).Should().Be("jwt");

        calls.Should().Be(2);
        (await cache.ContainsAsync<string>("token", Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task The_inner_tier_is_written_with_the_returned_expiration_and_not_at_all_once_it_has_passed()
    {
        var inner = Substitute.For<ICache>();
        inner.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<string?> { Expiration = DateTimeOffset.MinValue });
        inner.SetAsync<string?>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(true);
        using var cache = InMemoryMultilayer.Cache(inner: inner);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(7);

        await cache.GetOrAddWithExpirationAsync<string>("live", _ => Task.FromResult(new GeneratedValue<string>("jwt", deadline)), Ct);
        await cache.GetOrAddWithExpirationAsync<string>("stale", _ => Task.FromResult(new GeneratedValue<string>("jwt", DateTimeOffset.UtcNow.AddMinutes(-1))), Ct);

        await inner.Received(1).SetAsync<string?>(new CacheKey("live"), "jwt", deadline, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        await inner.DidNotReceive().SetAsync<string?>(new CacheKey("stale"), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_null_value_is_stored_only_when_null_values_are_cached()
    {
        using var skipping = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheNullValues = false });
        using var caching = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheNullValues = true });
        var deadline = DateTimeOffset.UtcNow.AddMinutes(7);

        (await skipping.GetOrAddWithExpirationAsync<string>("k", _ => Task.FromResult(new GeneratedValue<string>(null, deadline)), Ct)).Should().BeNull();
        (await caching.GetOrAddWithExpirationAsync<string>("k", _ => Task.FromResult(new GeneratedValue<string>(null, deadline)), Ct)).Should().BeNull();

        (await skipping.ContainsAsync<string>("k", Ct)).Should().BeFalse();
        (await caching.ContainsAsync<string>("k", Ct)).Should().BeTrue();
        (await caching.GetCacheEntryAsync<string>("k", policy: null, Ct)).Expiration.Should().Be(deadline);
    }

    [Fact]
    public async Task Concurrent_misses_run_the_generator_once()
    {
        using var cache = InMemoryMultilayer.Cache();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var deadline = DateTimeOffset.UtcNow.AddMinutes(7);

        async Task<GeneratedValue<string>> Generate(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return new GeneratedValue<string>("jwt", deadline);
        }

        var reads = Enumerable.Range(0, 5).Select(_ => cache.GetOrAddWithExpirationAsync<string>("token", Generate, Ct).AsTask()).ToArray();
        await Task.Delay(100, Ct);
        release.SetResult();

        (await Task.WhenAll(reads)).Should().AllBe("jwt");
        calls.Should().Be(1);
    }

    [Fact]
    public async Task A_generator_that_outlasts_the_factory_timeout_times_out()
    {
        using var cache = InMemoryMultilayer.Cache();
        var policy = new CachePolicy { FactoryTimeout = TimeSpan.FromMilliseconds(50) };

        var act = async () => await cache.GetOrAddWithExpirationAsync("token", Slow, policy, Ct);

        await act.Should().ThrowAsync<TimeoutException>();

        static async Task<GeneratedValue<string>> Slow(CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return new GeneratedValue<string>("never");
        }
    }

    [Fact]
    public async Task A_typed_cache_stores_under_its_own_key_strategy()
    {
        using var cache = InMemoryMultilayer.Cache();
        var typed = new Cache<string>(cache, new PrefixCacheKeyStrategy("app"));
        var deadline = DateTimeOffset.UtcNow.AddMinutes(7);

        (await typed.GetOrAddWithExpirationAsync("token", _ => Task.FromResult(new GeneratedValue<string>("jwt", deadline)), Ct)).Should().Be("jwt");

        (await typed.ExpireTimeAsync("token", Ct)).Should().Be(deadline);
    }

    [Fact]
    public async Task A_generator_must_not_be_null()
    {
        using var cache = InMemoryMultilayer.Cache();

        var act = async () => await cache.GetOrAddWithExpirationAsync<string>("token", null!, Ct);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task A_cache_without_the_capability_reads_then_sets_with_the_returned_expiration()
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(7);
        var cache = Substitute.For<ICache>();
        cache.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ICacheEntry<string?>>(new TestCacheEntry<string?> { Expiration = DateTimeOffset.MinValue }));
        cache.SetAsync<string>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(true);
        cache.SetAsync<string>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(true);
        var typed = Substitute.For<ICache<string>>();
        typed.SetAsync(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(true);
        typed.SetAsync(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(true);

        (await cache.GetOrAddWithExpirationAsync<string>("a", _ => Task.FromResult(new GeneratedValue<string>("jwt", deadline)), Ct)).Should().Be("jwt");
        (await cache.GetOrAddWithExpirationAsync<string>("b", _ => Task.FromResult(new GeneratedValue<string>("jwt")), Ct)).Should().Be("jwt");
        (await typed.GetOrAddWithExpirationAsync("a", _ => Task.FromResult(new GeneratedValue<string>("jwt", deadline)), Ct)).Should().Be("jwt");
        (await typed.GetOrAddWithExpirationAsync("b", _ => Task.FromResult(new GeneratedValue<string>("jwt")), Ct)).Should().Be("jwt");

        await cache.Received(1).SetAsync<string>(new CacheKey("a"), "jwt", deadline, Arg.Any<CachePolicy?>(), Ct);
        await cache.Received(1).SetAsync<string>(new CacheKey("b"), "jwt", Arg.Any<CachePolicy?>(), Ct);
        await typed.Received(1).SetAsync(new CacheKey("a"), "jwt", deadline, Ct);
        await typed.Received(1).SetAsync(new CacheKey("b"), "jwt", Ct);
    }

    [Fact]
    public async Task A_cache_without_the_capability_returns_a_hit_without_running_the_generator()
    {
        var cache = Substitute.For<ICache>();
        cache.GetCacheEntryAsync<string>(new CacheKey("a"), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ICacheEntry<string?>>(new TestCacheEntry<string?> { Value = "cached", Expiration = DateTimeOffset.UtcNow.AddMinutes(1) }));
        var typed = Substitute.For<ICache<string>>();
        ValueTaskStubs.Returns(typed.GetAsync(new CacheKey("a"), Ct), "cached");

        (await cache.GetOrAddWithExpirationAsync<string>("a", _ => throw new InvalidOperationException("the generator ran on a hit"), Ct)).Should().Be("cached");
        (await typed.GetOrAddWithExpirationAsync("a", _ => throw new InvalidOperationException("the generator ran on a hit"), Ct)).Should().Be("cached");
    }

    [Fact]
    public async Task A_cache_without_the_capability_returns_a_value_that_has_already_expired_without_failing()
    {
        var cache = Substitute.For<ICache>();
        cache.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ICacheEntry<string?>>(new TestCacheEntry<string?> { Expiration = DateTimeOffset.MinValue }));
        ValueTaskStubs.Returns(
            cache.SetAsync<string>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
            _ => throw new ArgumentOutOfRangeException("expiration"));
        var typed = Substitute.For<ICache<string>>();
        ValueTaskStubs.Returns(
            typed.SetAsync(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()),
            _ => throw new ArgumentOutOfRangeException("expiration"));
        var past = new GeneratedValue<string>("jwt", DateTimeOffset.UtcNow.AddMinutes(-1));

        (await cache.GetOrAddWithExpirationAsync<string>("a", _ => Task.FromResult(past), Ct)).Should().Be("jwt");
        (await typed.GetOrAddWithExpirationAsync("a", _ => Task.FromResult(past), Ct)).Should().Be("jwt");
    }

    [Fact]
    public async Task A_moq_proxy_is_read_then_set_too()
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(7);
        var cache = new Mock<ICache>();
        cache.Setup(c => c.GetCacheEntryAsync<string>(new CacheKey("a"), It.IsAny<CachePolicy?>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<ICacheEntry<string?>>(new TestCacheEntry<string?> { Expiration = DateTimeOffset.MinValue }));
        cache.Setup(c => c.SetAsync<string>(new CacheKey("a"), "jwt", deadline, It.IsAny<CachePolicy?>(), It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(true));
        var typed = new Mock<ICache<string>>();
        typed.Setup(c => c.GetAsync(new CacheKey("a"), It.IsAny<CancellationToken>())).Returns(new ValueTask<string?>((string?)null));
        typed.Setup(c => c.SetAsync(new CacheKey("a"), "jwt", deadline, It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(true));

        (await cache.Object.GetOrAddWithExpirationAsync<string>("a", _ => Task.FromResult(new GeneratedValue<string>("jwt", deadline)), Ct)).Should().Be("jwt");
        (await typed.Object.GetOrAddWithExpirationAsync("a", _ => Task.FromResult(new GeneratedValue<string>("jwt", deadline)), Ct)).Should().Be("jwt");

        cache.Verify(c => c.SetAsync<string>(new CacheKey("a"), "jwt", deadline, It.IsAny<CachePolicy?>(), It.IsAny<CancellationToken>()), Times.Once);
        typed.Verify(c => c.SetAsync(new CacheKey("a"), "jwt", deadline, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_cache_without_the_capability_keeps_a_cached_default_value()
    {
        var cache = Substitute.For<ICache>();
        cache.GetCacheEntryAsync<int>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ICacheEntry<int>>(new TestCacheEntry<int> { Value = 0, Expiration = DateTimeOffset.UtcNow.AddMinutes(1) }));
        var typed = Substitute.For<ICache<int>>();
        typed.GetAsync(Arg.Any<CacheKey>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<int>(0));
        typed.ContainsAsync(Arg.Any<CacheKey>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<bool>(true));
        var generated = 0;
        Task<GeneratedValue<int>> Generate(CancellationToken _)
        {
            generated++;
            return Task.FromResult(new GeneratedValue<int>(42));
        }

        (await cache.GetOrAddWithExpirationAsync<int>("k", Generate, Ct)).Should().Be(0);
        (await typed.GetOrAddWithExpirationAsync("k", Generate, Ct)).Should().Be(0);

        generated.Should().Be(0);
    }

    [Fact]
    public async Task A_cache_without_the_capability_generates_on_a_miss_that_reads_as_the_default()
    {
        var cache = Substitute.For<ICache>();
        cache.GetCacheEntryAsync<int>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ICacheEntry<int>>(new TestCacheEntry<int> { Expiration = DateTimeOffset.MinValue }));
        cache.SetAsync<int>(Arg.Any<CacheKey>(), 42, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<bool>(true));
        var typed = Substitute.For<ICache<int>>();
        typed.GetAsync(Arg.Any<CacheKey>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<int>(0));
        typed.SetAsync(Arg.Any<CacheKey>(), 42, Arg.Any<CancellationToken>()).Returns(new ValueTask<bool>(true));

        (await cache.GetOrAddWithExpirationAsync<int>("k", _ => Task.FromResult(new GeneratedValue<int>(42)), Ct)).Should().Be(42);
        (await typed.GetOrAddWithExpirationAsync("k", _ => Task.FromResult(new GeneratedValue<int>(42)), Ct)).Should().Be(42);
    }

    [Fact]
    public async Task A_mock_that_adds_the_capability_is_reached_through_it()
    {
        var cache = Substitute.For<ICache, IGeneratedExpirationCache>();
        ValueTaskStubs.Returns(
            ((IGeneratedExpirationCache)cache).GetOrAddWithExpirationAsync(Arg.Any<CacheKey>(), Arg.Any<Func<CancellationToken, Task<GeneratedValue<string>>>>(), Arg.Any<CachePolicy?>(), Ct),
            "jwt");

        (await cache.GetOrAddWithExpirationAsync<string>("token", _ => Task.FromResult(new GeneratedValue<string>("x")), Ct)).Should().Be("jwt");
    }

    [Fact]
    public async Task The_null_cache_returns_the_generated_value()
    {
        (await NullCache.Instance.GetOrAddWithExpirationAsync<string>("token", _ => Task.FromResult(new GeneratedValue<string>("jwt", DateTimeOffset.UtcNow.AddMinutes(1))), Ct)).Should().Be("jwt");
    }
}
