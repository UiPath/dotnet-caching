using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>
/// The typed façade. <see cref="Cache{T}"/> owns two things the untyped surface does not: the key
/// strategy that namespaces keys per type, and the policy snapshot taken at construction. Both must
/// apply to a conditional add exactly as they do to <c>SetAsync</c>, or a caller electing a winner
/// under <c>Cache&lt;A&gt;</c> would collide with one under <c>Cache&lt;B&gt;</c>.
/// </summary>
public class CacheOfTTryAddTests(ITestContextAccessor testContextAccessor)
{
    private CancellationToken Ct => testContextAccessor.Current.CancellationToken;

    [Fact]
    public async Task TryAdd_forwards_to_the_inner_cache_and_reports_the_win()
    {
        var inner = new DictionaryCache();
        var sut = new Cache<string>(inner);

        (await sut.TryAddAsync("k", "first", Ct)).Should().BeTrue();
        (await sut.TryAddAsync("k", "second", Ct)).Should().BeFalse();
        inner.TryAddCalls.Should().Be(2);
    }

    [Fact]
    public async Task TryAdd_does_not_overwrite_the_winner()
    {
        var inner = new DictionaryCache();
        var sut = new Cache<string>(inner);

        await sut.TryAddAsync("k", "first", Ct);
        await sut.TryAddAsync("k", "second", Ct);

        (await sut.GetAsync("k", Ct)).Should().Be("first");
    }

    [Fact]
    public async Task TryAdd_applies_the_type_key_strategy()
    {
        var inner = new DictionaryCache();
        var keyStrategy = Substitute.For<ICacheKeyStrategy>();
        keyStrategy.GetCacheKey<string>(Arg.Any<CacheKey>()).Returns((CacheKey)"namespaced:k");
        var sut = new Cache<string>(inner, keyStrategy);

        await sut.TryAddAsync("k", "v", Ct);

        inner.Contains("namespaced:k").Should().BeTrue();
        inner.Contains("k").Should().BeFalse("an unnamespaced claim could collide with another type's key");
    }

    [Fact]
    public async Task TryAdd_passes_the_constructed_policy_through()
    {
        var policy = new CachePolicy { DistributedExpiration = TimeSpan.FromMinutes(11) };
        var inner = Substitute.For<ICache>();
        inner.TryAddAsync<string?>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var sut = new Cache<string>(inner, cacheKeyStrategy: null, policy);

        await sut.TryAddAsync("k", "v", Ct);

        await inner.Received(1).TryAddAsync<string?>(Arg.Any<CacheKey>(), "v", policy, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryAdd_forwards_both_expiration_shapes_with_the_policy()
    {
        var policy = new CachePolicy { DistributedExpiration = TimeSpan.FromMinutes(11) };
        var inner = Substitute.For<ICache>();
        var sut = new Cache<string>(inner, cacheKeyStrategy: null, policy);
        var ttl = TimeSpan.FromMinutes(3);
        var absolute = DateTimeOffset.UtcNow.AddMinutes(3);

        await sut.TryAddAsync("k", "v", ttl, Ct);
        await sut.TryAddAsync("k", "v", absolute, Ct);

        await inner.Received(1).TryAddAsync<string?>(Arg.Any<CacheKey>(), "v", ttl, policy, Arg.Any<CancellationToken>());
        await inner.Received(1).TryAddAsync<string?>(Arg.Any<CacheKey>(), "v", absolute, policy, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TryAdd_blocking_forwarder_matches_the_async_result()
    {
        var inner = new DictionaryCache();
        ICache<string> sut = new Cache<string>(inner);

        sut.TryAdd("k", "first", Ct).Should().BeTrue();
        sut.TryAdd("k", "second", Ct).Should().BeFalse();
    }
}
