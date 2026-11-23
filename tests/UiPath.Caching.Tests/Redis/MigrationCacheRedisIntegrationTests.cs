using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Config;

namespace UiPath.Caching.Tests.Redis;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class MigrationCacheRedisIntegrationTests(RedisContainerFixture fixture) : IDisposable
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly string _app = $"mc{Guid.NewGuid():N}"[..10];
    private readonly List<ServiceProvider> _providers = [];
    private ConnectionMultiplexer? _raw;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDatabase Raw => (_raw ??= ConnectionMultiplexer.Connect(fixture.ConnectionString)).GetDatabase();

    [Fact]
    public async Task A_stored_value_is_found_with_its_remaining_lifetime()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Open();
        var key = Unique();
        (await cache.SetAsync(key, "v", Ttl, Ct)).Should().BeTrue();

        var read = await cache.AsMigrationCache().ReadSnapshotAsync<string>(key, null, Ct);

        read.Outcome.Should().Be(SnapshotOutcome.Found);
        read.Entry!.Value.Should().Be("v");
        read.Entry.Expiration.Should().BeCloseTo(DateTimeOffset.UtcNow + Ttl, TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task A_missing_key_is_absent()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Open();

        (await cache.AsMigrationCache().ReadSnapshotAsync<string>(Unique(), null, Ct)).Should().Be(new SnapshotRead<string>(SnapshotOutcome.Absent, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unreadable_value_is_reported_and_left_in_place_whatever_RemoveUnreadable_says(bool removeUnreadable)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Open(removeUnreadable: removeUnreadable);
        var key = Unique();
        (await cache.SetAsync(key, "v", Ttl, Ct)).Should().BeTrue();
        var redisKey = Find(key);
        byte[] foreign = [0x1E, 1, 2, 3];
        await Raw.StringSetAsync(redisKey, foreign, Ttl);

        var read = await cache.AsMigrationCache().ReadSnapshotAsync<string>(key, null, Ct);

        read.Should().Be(new SnapshotRead<string>(SnapshotOutcome.Unreadable, null));
        ((byte[]?)await Raw.StringGetAsync(redisKey)).Should().Equal(foreign);
    }

    [Fact]
    public async Task An_unreachable_server_fails_the_read_and_does_not_send_the_write()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Open("127.0.0.1:1,abortConnect=false,connectTimeout=50").AsMigrationCache();

        (await cache.ReadSnapshotAsync<string>("k", null, Ct)).Should().Be(new SnapshotRead<string>(SnapshotOutcome.Failed, null));
        (await cache.WriteAsync("k", "v", DateTimeOffset.UtcNow + Ttl, null, Ct)).Should().Be(StoreOutcome.NotSent);
        (await cache.DeleteAsync<string>("k", Ct)).Should().Be(StoreOutcome.NotSent);
    }

    [Fact]
    public async Task A_write_and_delete_are_committed_and_reach_the_store()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Open();
        var migration = cache.AsMigrationCache();
        var key = Unique();

        (await migration.WriteAsync(key, "v", DateTimeOffset.UtcNow + Ttl, null, Ct)).Should().Be(StoreOutcome.Committed);
        (await cache.GetAsync<string>(key, Ct)).Should().Be("v");
        (await Raw.KeyTimeToLiveAsync(Find(key)))!.Value.Should().BeCloseTo(Ttl, TimeSpan.FromSeconds(15));
        (await migration.DeleteAsync<string>(key, Ct)).Should().Be(StoreOutcome.Committed);
        (await cache.GetAsync<string>(key, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task A_write_and_delete_cancelled_before_sending_are_not_sent_and_leave_the_key_alone()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Open();
        var migration = cache.AsMigrationCache();
        var key = Unique();
        (await cache.SetAsync(key, "kept", Ttl, Ct)).Should().BeTrue();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        (await migration.WriteAsync(key, "new", DateTimeOffset.UtcNow + Ttl, null, cts.Token)).Should().Be(StoreOutcome.NotSent);
        (await migration.DeleteAsync<string>(key, cts.Token)).Should().Be(StoreOutcome.NotSent);

        (await cache.GetAsync<string>(key, Ct)).Should().Be("kept");
    }

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }

        _raw?.Dispose();
    }

    private static string Unique() => $"it-{Guid.NewGuid():N}";

    private RedisKey Find(string key)
    {
        _ = Raw;
        return RedisTestKeys.Keys(_raw!, $"*{key}*").Single();
    }

    private ICache Open(string? connection = null, bool removeUnreadable = true)
    {
        var provider = new ServiceCollection()
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = connection ?? fixture.ConnectionString);
                    b.AddRedis(o => o.RemoveUnreadable = removeUnreadable);

                },
                o => o.AppShortName = _app)
            .BuildServiceProvider();
        _providers.Add(provider);
        return provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
    }
}
