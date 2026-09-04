using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Config;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests;

/// <summary>
/// The guard itself. <see cref="CacheExpirationGuardTests"/> covers it reaching the write surface.
/// </summary>
public class CacheExpirationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-3600)]
    public void ThrowIfNotPositive_rejects_a_non_positive_duration(int seconds)
    {
        var act = () => CacheExpiration.ThrowIfNotPositive(TimeSpan.FromSeconds(seconds));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ThrowIfNotPositive_returns_the_value_it_accepts()
    {
        CacheExpiration.ThrowIfNotPositive(TimeSpan.FromTicks(1)).Should().Be(TimeSpan.FromTicks(1));
        CacheExpiration.ThrowIfNotPositive(TimeSpan.MaxValue).Should().Be(TimeSpan.MaxValue);
    }

    [Fact]
    public void ThrowIfNotPositive_names_the_parameter_it_was_given()
    {
        var expiration = TimeSpan.Zero;

        var act = () => CacheExpiration.ThrowIfNotPositive(expiration);

        act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be(nameof(expiration));
    }

    [Fact]
    public void ThrowIfNotFuture_rejects_now_and_the_past()
    {
        var now = DateTimeOffset.UtcNow;

        var atNow = () => CacheExpiration.ThrowIfNotFuture(now, now);
        var beforeNow = () => CacheExpiration.ThrowIfNotFuture(now.AddTicks(-1), now);

        atNow.Should().Throw<ArgumentOutOfRangeException>();
        beforeNow.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// <see cref="DateTimeOffset.MaxValue"/> is how the providers spell "no TTL", so the guard must
    /// let it through rather than treating the sentinel as a bad argument.
    /// </summary>
    [Fact]
    public void ThrowIfNotFuture_accepts_the_unbounded_sentinel()
    {
        var now = DateTimeOffset.UtcNow;

        CacheExpiration.ThrowIfNotFuture(DateTimeOffset.MaxValue, now).Should().Be(DateTimeOffset.MaxValue);
        CacheExpiration.ThrowIfNotFuture(now.AddTicks(1), now).Should().Be(now.AddTicks(1));
    }

    [Fact]
    public void ToDuration_measures_the_deadline_from_now()
    {
        var now = DateTimeOffset.UtcNow;

        CacheExpiration.ToDuration(now.AddMinutes(5), now).Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void ToDuration_rejects_a_deadline_that_has_passed()
    {
        var now = DateTimeOffset.UtcNow;

        var act = () => CacheExpiration.ToDuration(now.AddMinutes(-5), now);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

/// <summary>
/// The guard on the write surface, exercised through a real in-memory cache so what is covered is
/// the contract rather than one implementation's plumbing. Every write overload that takes an
/// expiration is here, because the point of dropping the nullable is that the rejection is uniform.
/// </summary>
public class CacheExpirationGuardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan[] NonPositive = [TimeSpan.Zero, TimeSpan.FromMinutes(-5)];

    private static MultilayerCache CreateSut()
    {
        var options = new InMemoryCacheOptions();
        var cacheOptions = new CacheOptions { AppShortName = "test" };
        return new MultilayerCache(
            KnownCacheProviderNames.InMemory,
            NullCache.Instance,
            new MemoryCacheFactory(null, NullLoggerFactory.Instance),
            NullChangeTokenFactory.Instance,
            NullTopicFactory.Instance,
            NullCacheEventFactory.Instance,
            NullTelemetryProvider.Instance,
            options,
            options,
            cacheOptions,
            localLock: new AsyncKeyedLocalLock(Options.Create(cacheOptions)),
            distributedLock: NullDistributedLock.Instance,
            policyFactory: NullCachePolicyFactory.Instance,
            logger: NullLogger.Instance);
    }

    private static DateTimeOffset Past => DateTimeOffset.UtcNow.AddMinutes(-5);

    private static async Task Rejects(Func<Task> write)
    {
        (await write.Should().ThrowAsync<ArgumentOutOfRangeException>()).And.ParamName.Should().Be("expiration");
    }

    [Fact]
    public async Task SetAsync_rejects_a_non_positive_duration()
    {
        using var sut = CreateSut();

        foreach (var duration in NonPositive)
        {
            await Rejects(async () => await sut.SetAsync("k", "v", duration, policy: null, Ct));
        }

        (await sut.GetAsync<string>("k", policy: null, token: Ct)).Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_rejects_a_deadline_that_has_passed()
    {
        using var sut = CreateSut();

        await Rejects(async () => await sut.SetAsync("k", "v", Past, policy: null, Ct));

        (await sut.GetAsync<string>("k", policy: null, token: Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Batch_SetAsync_rejects_a_bad_expiration()
    {
        using var sut = CreateSut();
        KeyValuePair<CacheKey, string?>[] pairs = [new("k", "v")];

        await Rejects(async () => await sut.SetAsync(pairs, TimeSpan.Zero, policy: null, Ct));
        await Rejects(async () => await sut.SetAsync(pairs, Past, policy: null, Ct));

        (await sut.GetAsync<string>("k", policy: null, token: Ct)).Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_rejects_a_bad_expiration()
    {
        using var sut = CreateSut();

        await Rejects(async () => await sut.RefreshAsync<string>("k", TimeSpan.Zero, policy: null, Ct));
        await Rejects(async () => await sut.RefreshAsync<string>("k", Past, policy: null, Ct));
    }

    /// <summary>The generator must not run either — a bad lifetime is caught before any work.</summary>
    [Fact]
    public async Task GetOrAddAsync_rejects_a_bad_expiration_without_calling_the_generator()
    {
        using var sut = CreateSut();
        var called = false;
        Task<string?> generator(CancellationToken _)
        {
            called = true;
            return Task.FromResult<string?>("v");
        }

        await Rejects(async () => await sut.GetOrAddAsync("k", generator, TimeSpan.Zero, policy: null, Ct));
        await Rejects(async () => await sut.GetOrAddAsync("k", generator, Past, policy: null, Ct));

        called.Should().BeFalse();
    }

    [Fact]
    public async Task Batch_GetOrAddAsync_rejects_a_bad_expiration()
    {
        using var sut = CreateSut();
        KeyValuePair<CacheKey, string>[] entries = [new("k", "s")];

        await Rejects(async () => await sut.GetOrAddAsync<string, string>(
            entries, (_, _) => Task.FromResult<KeyValuePair<string, string?>[]>([new("s", "v")]), TimeSpan.Zero, policy: null, Ct));
        await Rejects(async () => await sut.GetOrAddAsync<string, string>(
            entries, (_, _) => Task.FromResult<KeyValuePair<string, string?>[]>([new("s", "v")]), Past, policy: null, Ct));
    }

    /// <summary>The overload without an expiration is the supported way to ask for the policy default.</summary>
    [Fact]
    public async Task Omitting_the_expiration_still_writes()
    {
        using var sut = CreateSut();

        (await sut.SetAsync("k", "v", policy: null, Ct)).Should().BeTrue();
        (await sut.GetAsync<string>("k", policy: null, token: Ct)).Should().Be("v");
    }
}
