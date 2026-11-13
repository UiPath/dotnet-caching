using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Locking;
using UiPath.Caching.Redis;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Redis;

// A layered hash reads the hash cache under it with GetCacheEntryAsync and writes it with SetAsync, or SetVersionedAsync where broadcast is on,
// so the replacement of a hash with a field the policy cannot read has to reach those paths as well as the direct one.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class ReadListLayeredIntegrationTests(RedisContainerFixture fixture)
{
    private static readonly byte[] JsonX = "\"x\""u8.ToArray();
    private static readonly byte[] FastX = [0x10, (byte)'x'];
    private static readonly byte[] FastMeta = [0x10, .. "{\"m\":\"v\"}"u8];

    private readonly PassthroughValueSerializer _serializer = new(0x10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<HashCacheSetOption, bool> Shapes() => new()
    {
        { HashCacheSetOption.HashReplace, false },
        { HashCacheSetOption.HashReplace, true },
        { HashCacheSetOption.KeyReplace, false },
        { HashCacheSetOption.KeyReplace, true },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task A_regenerated_layered_hash_replaces_a_field_the_policy_cannot_read_so_the_next_node_hits(HashCacheSetOption option, bool versioned)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var app = $"layered{Guid.NewGuid():N}"[..20];
        using var seeder = Build(app, versioned);
        using var subject = Build(app, versioned);
        using var peer = Build(app, versioned);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var key = Unique();
        var redisKey = await SeedAsync(seeder, raw, key, readableA: true);
        await raw.GetDatabase().HashSetAsync(redisKey, "b", JsonX);
        var runs = 0;
        var generated = new Dictionary<string, string?> { ["a"] = "generated" };

        var first = await Hashes(subject).GetOrAddAsync<string>(key, _ => Generate(ref runs, generated), DateTimeOffset.UtcNow.AddMinutes(5), option, Policy(subject), Ct);
        var next = await Hashes(peer).GetOrAddAsync<string>(key, _ => throw new InvalidOperationException("the regenerated hash did not hit"), DateTimeOffset.UtcNow.AddMinutes(5), option, Policy(peer), Ct);

        FieldA(first).Should().Be("generated");
        FieldA(next).Should().Be("generated");
        runs.Should().Be(1);
        (await raw.GetDatabase().HashExistsAsync(redisKey, "b")).Should().BeFalse("the field the policy cannot read does not outlive the regenerate");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_layered_hash_that_missed_with_nothing_unreadable_keeps_the_fields_it_does_not_write(bool versioned)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var app = $"layered{Guid.NewGuid():N}"[..20];
        using var seeder = Build(app, versioned);
        using var subject = Build(app, versioned);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var key = Unique();
        var redisKey = await SeedAsync(seeder, raw, key, readableA: false);
        await raw.GetDatabase().HashSetAsync(redisKey, "_metadata_", FastMeta);
        var runs = 0;

        await Hashes(subject).GetOrAddAsync<string>(key, _ => Generate(ref runs, new Dictionary<string, string?> { ["a"] = "generated" }), DateTimeOffset.UtcNow.AddMinutes(5), HashCacheSetOption.HashReplace, Policy(subject), Ct);

        runs.Should().Be(1);
        (await raw.GetDatabase().HashExistsAsync(redisKey, "_metadata_")).Should().BeTrue("a hash with no user field missed, and nothing was unreadable, so the caller's HashReplace is honored");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_hash_that_reads_clean_then_gains_a_field_the_policy_cannot_read_under_the_lock_is_replaced_whole(bool versioned)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var app = $"layered{Guid.NewGuid():N}"[..20];
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        using var seeder = Build(app, versioned);
        var key = Unique();
        var redisKey = await SeedAsync(seeder, raw, key, readableA: true);
        await raw.GetDatabase().KeyDeleteAsync(redisKey);
        var foreign = new ContentionReportingDistributedLock(contended: false) { OnAcquire = () => raw.GetDatabase().HashSet(redisKey, "b", JsonX) };
        using var subject = Build(app, versioned, foreign);
        var generated = new Dictionary<string, string?> { ["a"] = "generated" };

        var result = await Hashes(subject).GetOrAddAsync<string>(key, _ => Task.FromResult<IDictionary<string, string?>>(generated), DateTimeOffset.UtcNow.AddMinutes(5), HashCacheSetOption.HashReplace, Policy(subject, "fastonly-locked"), Ct);

        FieldA(result).Should().Be("generated");
        (await raw.GetDatabase().HashExistsAsync(redisKey, "b")).Should().BeFalse("the re-read under the lock found a field the policy cannot read, so the write replaces the key");
    }

    private static Task<IDictionary<string, string?>> Generate(ref int runs, IDictionary<string, string?> value)
    {
        runs++;
        return Task.FromResult(value);
    }

    private static string Unique() => $"it-{Guid.NewGuid():N}";

    private static string? FieldA(IDictionary<string, string?>? fields) => fields is not null && fields.TryGetValue("a", out var value) ? value : null;

    private static IHashCache Hashes(ServiceProvider provider) => provider.GetRequiredService<ICacheFactory>().CreateHashCache(KnownCacheProviderNames.InMemoryRedis);

    private static CachePolicy Policy(ServiceProvider provider, string name = "fastonly") => provider.GetRequiredService<ICachePolicyFactory>().Resolve(name)!;

    private static RedisKey RawKey(ConnectionMultiplexer raw, string key) =>
        raw.GetServer(raw.GetEndPoints()[0]).Keys(pattern: $"*{key}*").Single();

    private async Task<RedisKey> SeedAsync(ServiceProvider seeder, ConnectionMultiplexer raw, string key, bool readableA)
    {
        await Hashes(seeder).SetAsync(key, new Dictionary<string, string?> { ["a"] = "seed" }, new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(5)), policy: null, Ct);
        var redisKey = RawKey(raw, key);
        if (readableA)
        {
            await raw.GetDatabase().HashSetAsync(redisKey, "a", FastX);
        }
        else
        {
            await raw.GetDatabase().HashDeleteAsync(redisKey, "a");
        }

        return redisKey;
    }

    private ServiceProvider Build(string app, bool versioned, IDistributedLock? distributedLock = null)
    {
        var services = new ServiceCollection();
        services.AddCaching(
            b =>
            {
                b.AddRedisConnection(o => o.ConnectionString = fixture.ConnectionString);
                b.AddRedis(o => o.FormatNegotiation.Mode = FormatNegotiationMode.Force);
                b.AddValueSerializer("fast", _ => _serializer);
                b.AddInMemoryRedis(_ => { });
                if (versioned)
                {
                    b.AddBroadcast();
                }
            },
            o =>
            {
                o.AppShortName = app;
                o.Policies["fastonly"] = new CachePolicy { DistributedExpiration = TimeSpan.FromMinutes(5), Serializer = new SerializerPolicy { Write = "fast", Read = ["fast"] } };
                o.Policies["fastonly-locked"] = new CachePolicy { DistributedExpiration = TimeSpan.FromMinutes(5), Serializer = new SerializerPolicy { Write = "fast", Read = ["fast"] }, Lock = new LockProfile { DistributedLockEnabled = true } };
            });
        if (distributedLock is not null)
        {
            services.AddSingleton(distributedLock);
        }

        return services.BuildServiceProvider();
    }
}
