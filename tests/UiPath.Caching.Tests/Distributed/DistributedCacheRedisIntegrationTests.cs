#if NET9_0_OR_GREATER
using System.Buffers;
#endif
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Distributed;
using UiPath.Caching.Redis;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.Distributed;

/// <summary>
/// Exercises the recommended tier against a real Redis. The other adapter tests substitute
/// <see cref="IHashCache"/> or run on the InMemory tier, so the hash wire layout, the physical keyspace and
/// metadata-only refresh are only verified here — through StackExchange.Redis, as deployments use it.
/// </summary>
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class DistributedCacheRedisIntegrationTests(RedisContainerFixture fixture)
{
    private const string AppShortName = "dcit";

    private static ServiceProvider Build(string connectionString) =>
        new ServiceCollection()
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = connectionString);
                    b.AddRedis(_ => { });
                    b.AddDistributedCache(KnownCacheProviderNames.Redis);
                },
                o => o.AppShortName = AppShortName)
            .BuildServiceProvider();

    private static string Unique() => $"it-{Guid.NewGuid():N}";

    private Task<ConnectionMultiplexer> ConnectAsync() =>
        ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);

    [Fact]
    public async Task Round_trips_a_payload_through_redis()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var token = TestContext.Current.CancellationToken;
        var key = Unique();
        using var provider = Build(fixture.ConnectionString);
        var cache = provider.GetRequiredService<IDistributedCache>();

        await cache.SetAsync(key, [1, 2, 3], new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10),
        }, token);

        (await cache.GetAsync(key, token)).Should().Equal(1, 2, 3);
        (await cache.GetAsync(key.ToUpperInvariant(), token)).Should().BeNull("keys are case-sensitive");

        await cache.RemoveAsync(key, token);
        (await cache.GetAsync(key, token)).Should().BeNull();
    }

    /// <summary>The wire layout: a hash with the payload raw and the two expiration fields beside it.</summary>
    [Fact]
    public async Task Stores_a_hash_with_the_documented_fields_in_its_own_keyspace()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var token = TestContext.Current.CancellationToken;
        var key = Unique();
        using var provider = Build(fixture.ConnectionString);
        var cache = provider.GetRequiredService<IDistributedCache>();
        await using var multiplexer = await ConnectAsync();
        var database = multiplexer.GetDatabase();

        await cache.SetAsync(key, [7, 8], new DistributedCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromMinutes(20),
            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(2),
        }, token);

        var redisKey = $"{AppShortName}:{UiPathDistributedCacheOptions.DefaultRedisKeyDifferentiator}:{UiPathDistributedCacheOptions.DefaultKeyPrefix}:{key}";
        (await database.KeyTypeAsync(redisKey)).Should().Be(RedisType.Hash);
        (await database.HashGetAsync(redisKey, "data")).Should().Be((RedisValue)new byte[] { 7, 8 });
        ((long?)await database.HashGetAsync(redisKey, "sldexp")).Should().Be(TimeSpan.FromMinutes(20).Ticks);
        ((long?)await database.HashGetAsync(redisKey, "absexp")).Should().BePositive();
        (await database.KeyTimeToLiveAsync(redisKey)).Should().NotBeNull();

        // Nothing lands in the application's own hash keyspace.
        var applicationKey = $"{AppShortName}:{RedisTypePrefixes.Hash}:{key}";
        (await database.KeyExistsAsync(applicationKey)).Should().BeFalse();

        await cache.RemoveAsync(key, token);
        (await database.KeyExistsAsync(redisKey)).Should().BeFalse();
    }

    /// <summary>An empty payload is a hit, not a miss — the field is stored zero-length and read back as such.</summary>
    [Fact]
    public async Task Empty_payload_round_trips_as_empty()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var token = TestContext.Current.CancellationToken;
        var key = Unique();
        using var provider = Build(fixture.ConnectionString);
        var cache = provider.GetRequiredService<IDistributedCache>();

        await cache.SetAsync(key, [], new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
        }, token);

        (await cache.GetAsync(key, token)).Should().NotBeNull().And.BeEmpty();

        await cache.RemoveAsync(key, token);
    }

#if NET9_0_OR_GREATER
    /// <summary>
    /// The buffer half against real Redis. The point is the wire: a segmented write has to arrive as one
    /// contiguous <c>data</c> field, or the array half — and any other client — reads a truncated payload.
    /// </summary>
    [Fact]
    public async Task Buffer_half_round_trips_through_redis_on_the_same_wire_layout()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var token = TestContext.Current.CancellationToken;
        var key = Unique();
        using var provider = Build(fixture.ConnectionString);
        var cache = provider.GetRequiredService<IDistributedCache>();
        var buffered = cache.Should().BeAssignableTo<IBufferDistributedCache>().Subject;
        await using var multiplexer = await ConnectAsync();
        var database = multiplexer.GetDatabase();
        var redisKey = $"{AppShortName}:{UiPathDistributedCacheOptions.DefaultRedisKeyDifferentiator}:{UiPathDistributedCacheOptions.DefaultKeyPrefix}:{key}";

        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var first = new BufferSegment(payload.AsMemory(0, 2), runningIndex: 0);
        var second = first.Append(payload.AsMemory(2));

        await buffered.SetAsync(key, new ReadOnlySequence<byte>(first, 0, second, second.Memory.Length),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) }, token);

        ((byte[]?)await database.HashGetAsync(redisKey, "data")).Should().Equal(payload, "stored raw and contiguous");
        (await cache.GetAsync(key, token)).Should().Equal(payload);

        var destination = new ArrayBufferWriter<byte>();
        (await buffered.TryGetAsync(key, destination, token)).Should().BeTrue();
        destination.WrittenSpan.ToArray().Should().Equal(payload);

        await cache.RemoveAsync(key, token);
        (await buffered.TryGetAsync(key, new ArrayBufferWriter<byte>(), token)).Should().BeFalse();
    }

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public BufferSegment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new BufferSegment(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
#endif

    /// <summary>Refresh extends the TTL without transferring the payload, and the absolute deadline still caps it.</summary>
    [Fact]
    public async Task Refresh_extends_the_ttl_and_the_absolute_deadline_caps_it()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var token = TestContext.Current.CancellationToken;
        var key = Unique();
        using var provider = Build(fixture.ConnectionString);
        var cache = provider.GetRequiredService<IDistributedCache>();
        await using var multiplexer = await ConnectAsync();
        var database = multiplexer.GetDatabase();
        var redisKey = $"{AppShortName}:{UiPathDistributedCacheOptions.DefaultRedisKeyDifferentiator}:{UiPathDistributedCacheOptions.DefaultKeyPrefix}:{key}";

        await cache.SetAsync(key, [1], new DistributedCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromMinutes(30),
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(45),
        }, token);

        await database.KeyExpireAsync(redisKey, TimeSpan.FromMinutes(5));
        (await database.KeyTimeToLiveAsync(redisKey)).Should().BeCloseTo(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30));

        await cache.RefreshAsync(key, token);

        var afterRefresh = await database.KeyTimeToLiveAsync(redisKey);
        afterRefresh.Should().NotBeNull();
        afterRefresh!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(10), "the sliding window was reapplied");
        afterRefresh.Value.Should().BeLessThan(TimeSpan.FromMinutes(46), "the absolute deadline caps the slide");

        await cache.RemoveAsync(key, token);
    }

    /// <summary>
    /// The provider sets <see cref="RedisCacheOptions.AwaitRefresh"/>, so the extension is in effect when the
    /// call returns and the result reports whether it applied — neither is true fire-and-forget.
    /// </summary>
    [Fact]
    public async Task Refresh_is_applied_and_reported_before_it_returns()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var token = TestContext.Current.CancellationToken;
        var key = Unique();
        using var provider = Build(fixture.ConnectionString);
        var hash = provider.GetRequiredKeyedService<IHashCache>(
            DistributedCacheCollectionExtensions.DistributedCacheServiceKey);
        var cacheKey = new CacheKey($"{UiPathDistributedCacheOptions.DefaultKeyPrefix}:{key}", CacheKeyCasing.Sensitive);
        await using var multiplexer = await ConnectAsync();
        var database = multiplexer.GetDatabase();
        var redisKey = $"{AppShortName}:{UiPathDistributedCacheOptions.DefaultRedisKeyDifferentiator}:{cacheKey.Name}";

        await hash.SetAsync(cacheKey, new Dictionary<string, byte[]?> { ["data"] = [1] }, TimeSpan.FromMinutes(5), null, token);

        var applied = await hash.RefreshAsync<byte[]>(cacheKey, DateTimeOffset.UtcNow.AddMinutes(30), null, token);

        applied.Should().BeTrue("the reply is observed, so the result is meaningful");
        (await database.KeyTimeToLiveAsync(redisKey)).Should()
            .BeGreaterThan(TimeSpan.FromMinutes(10), "no polling needed once the reply is awaited");

        var absent = new CacheKey($"{UiPathDistributedCacheOptions.DefaultKeyPrefix}:{Unique()}", CacheKeyCasing.Sensitive);
        (await hash.RefreshAsync<byte[]>(absent, DateTimeOffset.UtcNow.AddMinutes(30), null, token))
            .Should().BeFalse("a missing key is now distinguishable from a hit");

        await hash.RemoveAsync<byte[]>(cacheKey, token);
    }
}
