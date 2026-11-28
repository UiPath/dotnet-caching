#pragma warning disable CA2012 // NSubstitute setups call the ValueTask-returning members without consuming the result.
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Config;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Broadcast;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>The plain library path of the layered hash cache against a read that reloads while the store changes.</summary>
public class MultilayerHashCacheMigrationTests
{
    private static readonly CachePolicy Policy = new() { LocalExpiration = TimeSpan.FromMinutes(10), LocalExpirationDisconnected = TimeSpan.FromSeconds(30) };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("write")]
    [InlineData("delete")]
    [InlineData("fields")]
    [InlineData("expiry")]
    public async Task A_read_that_reloads_while_the_store_changes_cannot_put_the_old_hash_back_even_with_no_broadcast(string operation)
    {
        var written = new Dictionary<string, string?> { ["f"] = "new" };
        var proxy = DispatchProxy.Create<IHashCache, GatedInner>();
        var inner = (GatedInner)(object)proxy;
        inner.Stored = new Dictionary<string, string?> { ["f"] = "old" };
        inner.After = operation == "write" ? written : null;
        using var cache = Build(proxy);

        var mutation = operation switch
        {
            "write" => ((IMigrationHashCache)cache).WriteAsync("k", written, new HashCacheEntryOptions(), Policy, Ct).AsTask(),
            "delete" => ((IMigrationHashCache)cache).DeleteAsync<string>("k", Ct).AsTask(),
            "expiry" => ((IMigrationHashCache)cache).RenewExpiryAsync<string>("k", new HashCacheEntryOptions(), Policy, Ct).AsTask(),
            _ => ((IMigrationHashCache)cache).DeleteFieldsAsync<string>("k", ["f"], Ct).AsTask(),
        };
        await inner.Entered.Task.WaitAsync(Ct);

        // The store call is in flight: this read loads the old hash from the store, and must not keep it.
        (await cache.GetAsync<string>("k", Policy, Ct))["f"].Should().Be("old");
        inner.Gate.SetResult();
        (await mutation.WaitAsync(Ct)).Should().Be(StoreOutcome.Committed);

        var after = await cache.GetAsync<string>("k", Policy, Ct);
        if (operation == "write")
        {
            after.Should().BeEquivalentTo(written);
        }
        else
        {
            after.Should().BeEmpty("the local copy the reload made is not the store's");
        }
    }

    private static MultilayerHashCache Build(IHashCache inner)
    {
        var options = new InMemoryRedisCacheOptions();
        var events = Substitute.For<ICacheEventFactory>();
        events.Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CacheEventData>(), Arg.Any<string?>())
            .Returns(call => new TestCacheEvent { Type = call.ArgAt<string>(1), Data = call.ArgAt<CacheEventData>(2), Source = new Uri("urn:test") });
        var provider = Substitute.For<ITopicProvider>();
        provider.Enabled.Returns(false);
        var topics = Substitute.For<ITopicFactory>();
        topics.Get(Arg.Any<string?>()).Returns(provider);
        return RunTables.Track(new MultilayerHashCache(
            KnownCacheProviderNames.InMemoryRedis,
            inner,
            new MemoryCacheFactory(new ManualTimeProvider(), NullLoggerFactory.Instance),
            NullChangeTokenFactory.Instance,
            topics,
            events,
            Substitute.For<ICachingTelemetryProvider>(),
            options,
            options,
            new CacheOptions { AppShortName = "test", ConnectionMonitorEnabled = false },
            NullDistributedLock.Instance,
            NullCachePolicyFactory.Instance,
            new ManualTimeProvider(),
            NullLogger.Instance));
    }

    /// <summary>A store whose mutations wait at a gate once they have begun, and whose reads answer what it holds.</summary>
    internal class GatedInner : DispatchProxy, IMigrationHashCache
    {
        public Dictionary<string, string?>? Stored { get; set; }

        public Dictionary<string, string?>? After { get; set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<HashSnapshotRead<T>> ReadSnapshotAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<StoreOutcome> WriteAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default) => PausedAsync();

        public ValueTask<StoreOutcome> DeleteAsync<T>(CacheKey cacheKey, CancellationToken token = default) => PausedAsync();

        public ValueTask<StoreOutcome> DeleteFieldsAsync<T>(CacheKey cacheKey, string[] fields, CancellationToken token = default) => PausedAsync();

        public ValueTask<StoreOutcome> RenewAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default) => PausedAsync();

        public ValueTask<StoreOutcome> RenewExpiryAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default) => PausedAsync();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Name switch
        {
            "get_Name" => "Redis",
            nameof(IHashCache.GetCacheEntryAsync) => new ValueTask<ICacheEntry<IDictionary<string, string?>>>(new TestCacheEntry<IDictionary<string, string?>>
            {
                Value = Stored is null ? null : new Dictionary<string, string?>(Stored),
                Expiration = Stored is null ? DateTimeOffset.MinValue : new ManualTimeProvider().GetUtcNow().AddHours(1),
            }),
            _ => throw new NotSupportedException(targetMethod.Name),
        };

        private async ValueTask<StoreOutcome> PausedAsync()
        {
            Entered.SetResult();
            await Gate.Task;
            Stored = After;
            return StoreOutcome.Committed;
        }
    }
}
