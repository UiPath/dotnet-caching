using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Config;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests;

public class MemoryOnlyReadMissTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_miss_returns_default_and_a_not_found_entry()
    {
        using var sut = CreateSut();

        (await sut.GetAsync<string>("k", policy: null, token: Ct)).Should().BeNull();
        (await sut.GetAsync<string>("k".ToCharArray().AsSpan(), policy: null, token: Ct)).Should().BeNull();
        (await sut.GetCacheEntryAsync<string>("k", policy: null, token: Ct)).Found.Should().BeFalse();
        (await sut.GetCacheEntryAsync<string>("k".ToCharArray().AsSpan(), policy: null, token: Ct)).Found.Should().BeFalse();
    }

    [Fact]
    public async Task A_hit_returns_the_value()
    {
        using var sut = CreateSut();
        await sut.SetAsync("k", "v", TimeSpan.FromMinutes(1), policy: null, token: Ct);

        (await sut.GetAsync<string>("k", policy: null, token: Ct)).Should().Be("v");
        (await sut.GetCacheEntryAsync<string>("k", policy: null, token: Ct)).Value.Should().Be("v");
    }

    [Fact]
    public async Task A_cancelled_token_still_cancels_the_read()
    {
        using var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await ((Func<Task>)(async () => await sut.GetAsync<string>("missing", policy: null, token: cts.Token))).Should().ThrowAsync<OperationCanceledException>();
        await ((Func<Task>)(async () => await sut.GetCacheEntryAsync<string>("missing", policy: null, token: cts.Token))).Should().ThrowAsync<OperationCanceledException>();
    }

    private static MultilayerCache CreateSut()
    {
        var options = new InMemoryCacheOptions();
        var cacheOptions = new CacheOptions { AppShortName = "test" };
        return new MultilayerCache(
            KnownCacheProviderNames.InMemory,
            NullCache.Instance,
            new MemoryCacheFactory(TimeProvider.System, NullLoggerFactory.Instance),
            NullChangeTokenFactory.Instance,
            NullTopicFactory.Instance,
            NullCacheEventFactory.Instance,
            NullTelemetryProvider.Instance,
            options,
            options,
            cacheOptions,
            distributedLock: NullDistributedLock.Instance,
            policyFactory: NullCachePolicyFactory.Instance,
            clock: TimeProvider.System,
            logger: NullLogger.Instance);
    }
}
