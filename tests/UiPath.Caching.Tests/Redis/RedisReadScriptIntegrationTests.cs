using System.Net;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;
using UiPath.Caching.Config;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Tests.Redis;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class RedisReadScriptIntegrationTests(RedisContainerFixture fixture)
{
    // Redis stamps expirations with its own clock, which can drift from the test host's (a container VM, for one).
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reads_return_values_and_expirations_whether_or_not_the_server_allows_scripts(bool scriptsAllowed)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var admin = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var server = admin.GetServer(admin.GetEndPoints()[0]);
        var user = $"it-noscript-{Guid.NewGuid():N}";
        var connectionString = fixture.ConnectionString;
        if (!scriptsAllowed)
        {
            await server.ExecuteAsync("ACL", "SETUSER", user, "on", ">pw", "~*", "&*", "+@all", "-@scripting");
            connectionString += $",user={user},password=pw";
        }

        try
        {
            using var provider = Build(connectionString);
            var factory = provider.GetRequiredService<ICacheFactory>();
            var cache = factory.CreateCache(KnownCacheProviderNames.Redis);
            var hashCache = factory.CreateHashCache(KnownCacheProviderNames.Redis);
            var tag = Guid.NewGuid().ToString("N");
            CacheKey key = $"{{{tag}}}a";
            CacheKey missing = $"{{{tag}}}b";
            var expiration = DateTimeOffset.UtcNow.AddMinutes(5);
            var fields = new Dictionary<string, string?> { ["f"] = "v" };

            (await cache.SetAsync(key, "value", expiration, policy: null, Ct)).Should().BeTrue();
            (await hashCache.SetAsync(key, fields, expiration, policy: null, Ct)).Should().BeTrue();

            var single = await cache.GetCacheEntryAsync<string>(key, policy: null, Ct);
            var batch = await cache.GetCacheEntriesAsync<string>([key, missing], policy: null, Ct);
            var hash = await hashCache.GetCacheEntryAsync<string>(key, policy: null, Ct);
            var hashMiss = await hashCache.GetCacheEntryAsync<string>(missing, policy: null, Ct);

            single.Value.Should().Be("value");
            single.Expiration.Should().BeCloseTo(expiration, ClockSkew);
            batch[0].Value.Value.Should().Be("value");
            batch[0].Value.Expiration.Should().BeCloseTo(expiration, ClockSkew);
            batch[1].Value.Found.Should().BeFalse();
            hash.Value.Should().BeEquivalentTo(fields);
            hash.Expiration.Should().BeCloseTo(expiration, ClockSkew);
            hashMiss.Found.Should().BeFalse();
        }
        finally
        {
            if (!scriptsAllowed)
            {
                await server.ExecuteAsync("ACL", "DELUSER", user);
            }
        }
    }

    [Fact]
    public async Task Script_reads_find_keys_under_the_app_name_and_the_connection_prefix()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var run = Guid.NewGuid().ToString("N");
        var prefix = $"{{pfx-{run}}}:";
        var app = $"app{run[..8]}";
        using var provider = Build(fixture.ConnectionString, app, new PrefixingConnector(mux, prefix));
        var factory = provider.GetRequiredService<ICacheFactory>();
        var cache = factory.CreateCache(KnownCacheProviderNames.Redis);
        var hashCache = factory.CreateHashCache(KnownCacheProviderNames.Redis);
        var expiration = DateTimeOffset.UtcNow.AddMinutes(5);
        var fields = new Dictionary<string, string?> { ["f"] = "v" };

        (await cache.SetAsync("a", "value", expiration, policy: null, Ct)).Should().BeTrue();
        (await hashCache.SetAsync("h", fields, expiration, policy: null, Ct)).Should().BeTrue();

        var rawKeys = mux.GetServer(mux.GetEndPoints()[0]).Keys(pattern: $"*{run}*").Select(k => k.ToString()).ToList();
        rawKeys.Should().HaveCount(2).And.OnlyContain(k => k.StartsWith(prefix, StringComparison.Ordinal) && k.Contains(app, StringComparison.Ordinal));
        (await cache.GetCacheEntryAsync<string>("a", policy: null, Ct)).Value.Should().Be("value");
        var batch = await cache.GetCacheEntriesAsync<string>(["a", "b"], policy: null, Ct);
        batch[0].Value.Value.Should().Be("value");
        batch[0].Value.Expiration.Should().BeCloseTo(expiration, ClockSkew);
        batch[1].Value.Found.Should().BeFalse();
        var hash = await hashCache.GetCacheEntryAsync<string>("h", policy: null, Ct);
        hash.Value.Should().BeEquivalentTo(fields);
        hash.Expiration.Should().BeCloseTo(expiration, ClockSkew);

        // The same keys under another app name, or without the connection prefix, are other entries.
        using var otherApp = Build(fixture.ConnectionString, app + "x", new PrefixingConnector(mux, prefix));
        (await otherApp.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis).GetCacheEntryAsync<string>("a", policy: null, Ct)).Found.Should().BeFalse();
        using var unprefixed = Build(fixture.ConnectionString, app);
        (await unprefixed.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis).GetCacheEntryAsync<string>("a", policy: null, Ct)).Found.Should().BeFalse();
    }

    [Fact]
    public async Task A_read_with_the_default_options_sends_a_get_and_a_pttl_and_no_script_or_transaction()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var admin = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString + ",allowAdmin=true");
        using var provider = Build(fixture.ConnectionString, readScripts: new RedisCacheOptions().ReadScriptsEnabled);
        var cache = provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
        var tag = Guid.NewGuid().ToString("N");
        CacheKey withTtl = $"{{{tag}}}ttl";
        CacheKey noTtl = $"{{{tag}}}forever";
        CacheKey missing = $"{{{tag}}}missing";
        var expiration = DateTimeOffset.UtcNow.AddMinutes(5);
        (await cache.SetAsync(withTtl, "a", expiration, policy: null, Ct)).Should().BeTrue();
        (await cache.SetAsync(noTtl, "b", expiration, policy: null, Ct)).Should().BeTrue();
        var rawKey = admin.GetServer(admin.GetEndPoints()[0]).Keys(pattern: $"*{tag}*forever*").Single();
        (await admin.GetDatabase().KeyPersistAsync(rawKey)).Should().BeTrue();
        // Warms the connection so the counted reads carry nothing else.
        await cache.GetCacheEntryAsync<string>(missing, policy: null, Ct);

        var before = await CommandCallsAsync(admin);
        var ttl = await cache.GetCacheEntryAsync<string>(withTtl, policy: null, Ct);
        var forever = await cache.GetCacheEntryAsync<string>(noTtl, policy: null, Ct);
        var miss = await cache.GetCacheEntryAsync<string>(missing, policy: null, Ct);
        var after = await CommandCallsAsync(admin);

        ttl.Value.Should().Be("a");
        ttl.Expiration.Should().BeCloseTo(expiration, ClockSkew);
        forever.Value.Should().Be("b");
        forever.Expiration.Should().Be(DateTimeOffset.MaxValue);
        miss.Found.Should().BeFalse();
        Delta(before, after, "get").Should().Be(3);
        Delta(before, after, "pttl").Should().Be(3);
        Delta(before, after, "multi").Should().Be(0);
        Delta(before, after, "exec").Should().Be(0);
        Delta(before, after, "evalsha_ro").Should().Be(0);
        Delta(before, after, "eval_ro").Should().Be(0);
    }

    [Fact]
    public async Task A_read_with_read_scripts_enabled_uses_the_script_on_redis_7()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipWhen(fixture.ServerVersion.Major < 7, "The read script needs PEXPIRETIME, which Redis 7.0 added.");
        using var admin = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString + ",allowAdmin=true");
        using var provider = Build(fixture.ConnectionString, readScripts: true);
        var cache = provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
        CacheKey key = $"{{{Guid.NewGuid():N}}}script";
        var expiration = DateTimeOffset.UtcNow.AddMinutes(5);
        (await cache.SetAsync(key, "a", expiration, policy: null, Ct)).Should().BeTrue();
        await cache.GetCacheEntryAsync<string>(key, policy: null, Ct);

        var before = await CommandCallsAsync(admin);
        var entry = await cache.GetCacheEntryAsync<string>(key, policy: null, Ct);
        var after = await CommandCallsAsync(admin);

        entry.Value.Should().Be("a");
        entry.Expiration.Should().BeCloseTo(expiration, ClockSkew);
        (Delta(before, after, "evalsha_ro") + Delta(before, after, "eval_ro")).Should().Be(1);
        Delta(before, after, "pttl").Should().Be(0);
        Delta(before, after, "multi").Should().Be(0);
    }

    [Fact]
    public async Task A_hash_read_with_the_default_options_sends_hgetall_or_hmget_and_a_pttl_and_no_transaction()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var admin = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString + ",allowAdmin=true");
        using var provider = Build(fixture.ConnectionString, readScripts: new RedisCacheOptions().ReadScriptsEnabled);
        var hashCache = provider.GetRequiredService<ICacheFactory>().CreateHashCache(KnownCacheProviderNames.Redis);
        var tag = Guid.NewGuid().ToString("N");
        CacheKey key = $"{{{tag}}}h";
        CacheKey missing = $"{{{tag}}}missing";
        var expiration = DateTimeOffset.UtcNow.AddMinutes(5);
        var fields = new Dictionary<string, string?> { ["f"] = "v", ["g"] = "w" };
        (await hashCache.SetAsync(key, fields, expiration, policy: null, Ct)).Should().BeTrue();
        await hashCache.GetCacheEntryAsync<string>(missing, policy: null, Ct);

        var before = await CommandCallsAsync(admin);
        var entry = await hashCache.GetCacheEntryAsync<string>(key, policy: null, Ct);
        var miss = await hashCache.GetCacheEntryAsync<string>(missing, policy: null, Ct);
        var subset = await hashCache.GetAsync<string>(key, ["f"], policy: null, Ct);
        var after = await CommandCallsAsync(admin);

        entry.Value.Should().BeEquivalentTo(fields);
        entry.Expiration.Should().BeCloseTo(expiration, ClockSkew);
        miss.Found.Should().BeFalse();
        subset.Should().BeEquivalentTo(new Dictionary<string, string?> { ["f"] = "v" });
        Delta(before, after, "hgetall").Should().Be(2);
        Delta(before, after, "hmget").Should().Be(1);
        Delta(before, after, "pttl").Should().Be(2);
        Delta(before, after, "multi").Should().Be(0);
        Delta(before, after, "exec").Should().Be(0);
        Delta(before, after, "evalsha_ro").Should().Be(0);
        Delta(before, after, "eval_ro").Should().Be(0);
    }

    private static async Task<Dictionary<string, long>> CommandCallsAsync(IConnectionMultiplexer admin)
    {
        var sections = await admin.GetServer(admin.GetEndPoints()[0]).InfoAsync("commandstats");
        return sections.SelectMany(section => section).ToDictionary(
            stat => stat.Key.Replace("cmdstat_", string.Empty, StringComparison.Ordinal),
            stat => long.Parse(stat.Value.Split(',')[0]["calls=".Length..], System.Globalization.CultureInfo.InvariantCulture));
    }

    private static long Delta(Dictionary<string, long> before, Dictionary<string, long> after, string command) =>
        after.GetValueOrDefault(command) - before.GetValueOrDefault(command);

    private static ServiceProvider Build(string connectionString, string app = "readscript", IRedisConnector? connector = null, bool readScripts = true)
    {
        var services = new ServiceCollection();
        if (connector is not null)
        {
            services.AddSingleton(connector);
        }

        return services
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = connectionString);
                    b.AddRedis(o => o.ReadScriptsEnabled = readScripts);
                },
                o => o.AppShortName = app)
            .BuildServiceProvider();
    }

    /// <summary>An application's own connector whose database prefixes every key, as <c>WithKeyPrefix</c> does.</summary>
    private sealed class PrefixingConnector(IConnectionMultiplexer mux, string prefix) : IRedisConnector
    {
        public event EventHandler? OnConnectionFailed
        {
            add { }
            remove { }
        }

        public event EventHandler? OnConnectionRestored
        {
            add { }
            remove { }
        }

        public event EventHandler? OnReconnected
        {
            add { }
            remove { }
        }

        public bool IsConnected => mux.IsConnected;

        public Version Version => mux.GetServer(mux.GetEndPoints()[0]).Version;

        public IDatabase Database => mux.GetDatabase().WithKeyPrefix(prefix);

        public ISubscriber Subscriber => mux.GetSubscriber();

        public void ForceReconnect()
        {
        }

        public EndPoint[] GetEndPoints(bool configuredOnly = false) => mux.GetEndPoints(configuredOnly);

        public void Dispose()
        {
        }
    }
}
