using System.Text;
using System.Text.Json;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.KeyMove;
using UiPath.Caching.MessagePack;
using UiPath.Caching.Migration.Tests.Support;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The codecs UseKeyMove binds: the in-place side follows the router's library cache (UseEnvelope), the native side the application's, and a promotion with them is readable by the library.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveCodecTests(RedisFixture fixture) : IAsyncLifetime
{
    // The application's key strategy writes a native key as the app name, the string keyspace and the caller key.
    private const string Library = "kmk:";
    private const string AppPrefix = Library + RedisKeyspaces.String + ":";

    private static readonly TimeSpan Life = TimeSpan.FromMinutes(5);
    private static readonly string RunTag = Guid.NewGuid().ToString("N")[..8];

    private readonly string _unique = Guid.NewGuid().ToString("N")[..10];
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<(RedisKey Node, string Field)> _entries = [];
    private ConnectionMultiplexer? _redis;

    public enum InPlace
    {
        Envelope,
        MessagePack,
        NoEnvelope,
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDatabase Db => _redis!.GetDatabase();

    private string Group => $"kc{_unique}";

    private string InPlacePrefix => $"kcl{RunTag}{_unique}:";

    public async ValueTask InitializeAsync()
    {
        if (fixture.Enabled)
        {
            _redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString + ",allowAdmin=true");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers)
        {
            await provider.DisposeAsync();
        }

        if (_redis is not null)
        {
            foreach (var (node, field) in _entries)
            {
                await Db.HashDeleteAsync(node, field);
            }

            foreach (var key in _redis.GetServer(_redis.GetEndPoints()[0]).Keys(pattern: $"*{_unique}*"))
            {
                await Db.KeyDeleteAsync(key);
            }

            await _redis.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(InPlace.Envelope)]
    [InlineData(InPlace.MessagePack)]
    [InlineData(InPlace.NoEnvelope)]
    public async Task The_in_place_codec_writes_and_reads_what_the_routers_library_path_does_in_the_format_the_router_is_configured_with(InPlace inPlace)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var (provider, _, client) = await HostAsync(inPlace);
        var value = new Item("v1");

        await WriteOnLibraryPathAsync(provider, "k", value);

        var stored = (byte[]?)await Db.StringGetAsync(L("k"));
        stored.Should().Equal(OldClientBytes(inPlace, value), "the router's library path writes the configured in-place format");
        client.Parts.InPlace.Encode(value).Should().Equal(stored);
        client.Parts.InPlace.Decode(stored).Should().Be(Decoded<Item>.Of(value));
        client.Parts.InPlace.Decode(OldClientBytes(inPlace, new Item("other"))).Value.Should().Be(new Item("other"), "bytes the older client wrote read the same way");
        client.Parts.InPlace.Decode([0x1E, 1, 2, 3]).Class.Should().Be(DecodeClass.Unreadable);
        client.Parts.InPlace.Decode([]).Class.Should().Be(DecodeClass.Null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_native_codec_is_the_applications_pipeline_with_its_effective_format_and_the_in_place_codec_keeps_following_the_router(bool messagePackDefault)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var (provider, _, client) = await HostAsync(InPlace.Envelope, messagePackDefault);
        var app = provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
        var value = new Item("native");

        await app.SetAsync(CacheKey.Verbatim(AppKey("k")), value, Life, Ct);
        await WriteOnLibraryPathAsync(provider, "k", value);

        var native = (byte[]?)await Db.StringGetAsync(N("k"));
        client.Parts.Native.Encode(value).Should().Equal(native);
        client.Parts.Native.Decode(native!).Should().Be(Decoded<Item>.Of(value));
        client.Parts.Native.Decode([0x1E, 1, 2, 3]).Class.Should().Be(DecodeClass.Unreadable);
        native![0].Should().Be(messagePackDefault ? (byte)MessagePackFormats.Value : (byte)'{');
        client.Parts.InPlace.Encode(value).Should().Equal((byte[]?)await Db.StringGetAsync(L("k")), "the in-place side is the router's, not the application's");
    }

    [Theory]
    [InlineData(InPlace.Envelope)]
    [InlineData(InPlace.MessagePack)]
    [InlineData(InPlace.NoEnvelope)]
    public async Task A_promotion_from_L_to_N_with_the_bound_codecs_is_read_back_by_the_library_with_the_lifetime_kept(InPlace inPlace)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var (provider, _, client) = await HostAsync(inPlace);
        await using var kit = await KeyMoveKit.CreateAsync(fixture, inPlace: client.Parts.InPlace, native: client.Parts.Native, nativePrefix: AppPrefix + AppKey(string.Empty));
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        var value = new Item("promoted");
        await kit.Db.StringSetAsync(kit.L("k"), OldClientBytes(inPlace, value), Life);

        var read = await kit.Client.ReadAsync("k", Ct);

        read.Kind.Should().Be(ReadOutcome.Found);
        read.Value.Should().Be(value);
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse("the promotion moved it");
        var app = provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
        (await app.GetAsync<Item>(CacheKey.Verbatim(AppKey("k")), Ct)).Should().Be(value);
        (await app.AsMigrationCache().ReadSnapshotAsync<Item>(CacheKey.Verbatim(AppKey("k")), null, Ct)).Entry!.Expiration.Should().BeCloseTo(DateTimeOffset.UtcNow + Life, TimeSpan.FromSeconds(15), "the expiry comes from the source's px");
        (await kit.Db.KeyTimeToLiveAsync(kit.N("k")))!.Value.Should().BeGreaterThan(Life - TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_promotion_leaves_an_unreadable_in_place_value_where_it_is()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var (_, _, client) = await HostAsync(InPlace.Envelope);
        await using var kit = await KeyMoveKit.CreateAsync(fixture, inPlace: client.Parts.InPlace, native: client.Parts.Native, nativePrefix: AppPrefix + AppKey(string.Empty));
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "{not an envelope", Life);

        var read = await kit.Client.ReadAsync("k", Ct);

        read.Kind.Should().Be(ReadOutcome.Unreadable);
        (await kit.ExistsAsync(kit.L("k"))).Should().BeTrue();
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    // A cached null on L moves when N caches nulls; otherwise it is a miss for the caller and a droppable entry: the promotion deletes it and the next read reloads it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_cached_null_on_L_moves_when_N_caches_nulls_and_is_dropped_otherwise(bool nativeCachesNulls)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var (provider, _, client) = await HostAsync(InPlace.Envelope, cacheNulls: nativeCachesNulls);
        await using var kit = await KeyMoveKit.CreateAsync(fixture, inPlace: client.Parts.InPlace, native: client.Parts.Native, nativePrefix: AppPrefix + AppKey(string.Empty));
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), RedisValue.EmptyString, Life);

        var read = await kit.Client.ReadAsync("k", Ct);

        client.Parts.Native.EncodeNull().Should().Equal(nativeCachesNulls ? [] : null);
        client.Parts.InPlace.Decode([]).Class.Should().Be(DecodeClass.Null);
        read.Kind.Should().Be(ReadOutcome.Absent, "a cached null is a miss for the caller");
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse("moved or dropped, it is no longer on L");
        (await kit.ExistsAsync(kit.N("k"))).Should().Be(nativeCachesNulls);
        kit.Hints().Count.Should().Be(nativeCachesNulls ? 0 : 1, "a dropped null is a miss like any other: N is read again and the hint is set");
        if (nativeCachesNulls)
        {
            // What the library itself writes for a null, and reads back as a cached null.
            var app = provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
            await app.SetAsync<Item>(CacheKey.Verbatim(AppKey("written")), null, Life, Ct);
            ((byte[]?)await kit.Db.StringGetAsync(AppPrefix + AppKey("written"))).Should().Equal((byte[]?)await kit.Db.StringGetAsync(kit.N("k")));
            var snapshot = await app.AsMigrationCache().ReadSnapshotAsync<Item>(CacheKey.Verbatim(AppKey("k")), null, Ct);
            snapshot.Outcome.Should().Be(SnapshotOutcome.Found);
            snapshot.Entry!.Value.Should().BeNull();
        }
    }

    // A cached null in N is the answer: the union read stops there and never falls back to an older value on L.
    [Theory]
    [InlineData("absent", true, false)]
    [InlineData("null", true, false)]
    [InlineData("value", true, false)]
    [InlineData("value", false, true)]
    public async Task A_cached_null_in_N_stops_the_union_read_when_N_caches_nulls(string source, bool nativeCachesNulls, bool servesL)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var (_, _, client) = await HostAsync(InPlace.Envelope, cacheNulls: nativeCachesNulls);
        await using var kit = await KeyMoveKit.CreateAsync(fixture, inPlace: client.Parts.InPlace, native: client.Parts.Native, nativePrefix: AppPrefix + AppKey(string.Empty));
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S4");
        await kit.Db.StringSetAsync(kit.N("k"), RedisValue.EmptyString, Life);
        var old = new Item("old");
        switch (source)
        {
            case "null":
                await kit.Db.StringSetAsync(kit.L("k"), RedisValue.EmptyString, Life);
                break;
            case "value":
                await kit.Db.StringSetAsync(kit.L("k"), LegacyEnvelope.Write(old), Life);
                break;
        }

        var read = await kit.Client.ReadAsync("k", Ct);

        read.Kind.Should().Be(servesL ? ReadOutcome.Found : ReadOutcome.Absent);
        read.Value.Should().Be(servesL ? old : null);
    }

    // The promotion's publish finds N created by someone else and N is a cached null: the null is the answer, not the L value that was captured.
    [Fact]
    public async Task A_publish_that_finds_a_cached_null_in_N_answers_the_null_and_not_the_captured_L_value()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var (_, _, client) = await HostAsync(InPlace.Envelope, cacheNulls: true);
        await using var kit = await KeyMoveKit.CreateAsync(fixture, inPlace: client.Parts.InPlace, native: client.Parts.Native, nativePrefix: AppPrefix + AppKey(string.Empty));
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), LegacyEnvelope.Write(new Item("captured")), Life);
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.Publish)
            {
                await kit.Db.StringSetAsync(kit.N("k"), RedisValue.EmptyString, Life);
            }
        };

        var read = await kit.Client.ReadAsync("k", Ct);

        read.Kind.Should().Be(ReadOutcome.Absent);
        read.Value.Should().BeNull();
        (await kit.ExistsAsync(kit.L("k"))).Should().BeTrue("the publish was refused, so L stays");
    }

    // The inventory is told how to judge a cache's values by the binding in force, the supplied one as much as the bound one.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_inventory_judges_the_values_of_a_cache_by_the_binding_in_force(bool supplied)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var (provider, _, _) = await HostAsync(InPlace.Envelope, supplied: supplied);
        var values = ((Router)provider.GetRequiredService<IRouter>()).KeyMove!.Values!;

        values.IsDroppable("items", MoveSide.Legacy, 0).Should().BeTrue("N does not cache nulls, so an empty L value is dropped");
        values.IsDroppable("items", MoveSide.Legacy, 1).Should().BeFalse();
        values.IsDroppable("items", MoveSide.Legacy, new KeyMoveOptions().MaxValueBytes + 1).Should().BeTrue();
    }

    // A value above the size cap is served from L; with an origin to reload it, it is dropped, and without one it stays.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task A_value_above_the_size_cap_is_served_and_not_promoted_and_dropped_only_with_an_origin(bool noOrigin, bool stays)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var (_, _, client) = await HostAsync(InPlace.Envelope);
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: noOrigin, inPlace: client.Parts.InPlace, native: client.Parts.Native, nativePrefix: AppPrefix + AppKey(string.Empty));
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        var large = new Item(new string('x', new KeyMoveOptions().MaxValueBytes));
        var bytes = LegacyEnvelope.Write(large);
        await kit.Db.StringSetAsync(kit.L("k"), bytes, Life);

        var read = await kit.Client.ReadAsync("k", Ct);

        client.Parts.InPlace.Decode(bytes).Class.Should().Be(DecodeClass.TooLarge);
        read.Kind.Should().Be(ReadOutcome.Found);
        read.Value.Should().Be(large);
        (await kit.ExistsAsync(kit.L("k"))).Should().Be(stays);
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    // The inventory judges by stored length, so a value above the cap goes whether or not it can be read; at the cap, or without an origin, it stays.
    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, true)]
    public async Task A_value_above_the_cap_is_dropped_readable_or_not_only_with_an_origin(bool readable, bool aboveCap, bool noOrigin, bool stays)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var (_, _, client) = await HostAsync(InPlace.Envelope);
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: noOrigin, inPlace: client.Parts.InPlace, native: client.Parts.Native, nativePrefix: AppPrefix + AppKey(string.Empty));
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        var length = new KeyMoveOptions().MaxValueBytes + (aboveCap ? 1 : 0);
        var padding = length - LegacyEnvelope.Write(new Item(string.Empty)).Length;
        var bytes = readable ? LegacyEnvelope.Write(new Item(new string('x', padding))) : Encoding.UTF8.GetBytes("{" + new string('x', length - 1));
        bytes.Length.Should().Be(length);
        await kit.Db.StringSetAsync(kit.L("k"), bytes, Life);

        var read = await kit.Client.ReadAsync("k", Ct);

        read.Kind.Should().Be(readable ? ReadOutcome.Found : ReadOutcome.Unreadable);
        (await kit.ExistsAsync(kit.L("k"))).Should().Be(stays);
        (await kit.ExistsAsync(kit.N("k"))).Should().Be(readable && !aboveCap);
    }

    [Fact]
    public async Task A_value_of_exactly_the_size_cap_is_read_as_it_is()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var (_, _, client) = await HostAsync(InPlace.Envelope);
        var cap = new KeyMoveOptions().MaxValueBytes;
        var padding = cap - LegacyEnvelope.Write(new Item(string.Empty)).Length;
        var atCap = LegacyEnvelope.Write(new Item(new string('x', padding)));
        var over = LegacyEnvelope.Write(new Item(new string('x', padding + 1)));

        atCap.Length.Should().Be(cap);
        client.Parts.InPlace.Decode(atCap).Class.Should().Be(DecodeClass.Ok);
        client.Parts.InPlace.Decode(over).Class.Should().Be(DecodeClass.TooLarge);
    }

    [Fact]
    public async Task UseKeyMove_binds_every_registered_cache_and_a_binding_a_registration_carries_wins()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var (_, _, bound) = await HostAsync(InPlace.Envelope);
        var (_, _, supplied) = await HostAsync(InPlace.Envelope, supplied: true);

        bound.Parts.InPlace.Should().BeOfType<LibraryKeyMoveCodec<Item>>();
        bound.Parts.Native.Should().BeOfType<LibraryKeyMoveCodec<Item>>();
        bound.Parts.NoOrigin.Should().BeFalse();
        bound.Parts.InPlaceKey("k").Should().Be(L("k"));
        bound.Parts.NativeKey("k").Should().Be(N("k"));
        supplied.Parts.InPlace.Should().BeSameAs(ItemCodec.Instance);
        supplied.Parts.Native.Should().BeSameAs(ItemCodec.Instance);
    }

    // Without an envelope the router's library keeps its own format, the library's JSON.
    private static byte[] OldClientBytes(InPlace inPlace, Item value) => inPlace switch
    {
        InPlace.Envelope => LegacyEnvelope.Write(value),
        InPlace.MessagePack => MessagePackInPlaceProxy.Write(value),
        _ => JsonSerializer.SerializeToUtf8Bytes(value),
    };

    private static RouterBuilder WithEnvelope(RouterBuilder router, InPlace inPlace) => inPlace switch
    {
        InPlace.Envelope => router.UseEnvelope(new LegacyEnvelopeProxy()),
        InPlace.MessagePack => router.UseEnvelope(new MessagePackInPlaceProxy()),
        _ => router,
    };

    // What LibraryPathClient does for a set: the router stack's own cache writes the key verbatim with the default policy.
    private async Task WriteOnLibraryPathAsync(ServiceProvider provider, string key, Item value)
    {
        var routerCache = provider.GetRequiredKeyedService<ICacheFactory>(RouterServiceCollectionExtensions.StackName).CreateCache(KnownCacheProviderNames.Redis).AsMigrationCache();
        (await routerCache.WriteAsync(CacheKey.Verbatim(L(key)), value, DateTimeOffset.UtcNow + Life, null, Ct)).Should().Be(StoreOutcome.Committed);
    }

    private string L(string key) => InPlacePrefix + key;

    private string N(string key) => AppPrefix + AppKey(key);

    private string AppKey(string key) => $"{_unique}:N:{key}";

    private async Task<(ServiceProvider Provider, RoutedCache<Item> Cache, KeyMoveClient<Item> Client)> HostAsync(InPlace inPlace, bool messagePackDefault = false, bool supplied = false, bool cacheNulls = false)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Caching:AppShortName"] = "kmk",
            ["Caching:DefaultCache"] = KnownCacheProviderNames.Redis,
            ["Caching:Connections:Redis:ConnectionString"] = fixture.ConnectionString,
            ["Caching:Connections:Redis:WarmUpOnStart"] = "false",
            ["Caching:Connections:Redis:PlannedMaintenanceEnabled"] = "false",
            ["RouterCaching:AppShortName"] = "kcrouter",
            ["RouterCaching:Separator"] = "$",
            ["RouterCaching:SourceUri"] = "urn:km-codec",
            ["RouterCaching:Redis:FormatNegotiation:LivenessWindow"] = "00:00:30",
            ["RouterCaching:Redis:FormatNegotiation:AdvertiseInterval"] = "00:00:01",
        };
        if (cacheNulls)
        {
            settings["Caching:Redis:CacheNullValues"] = "true";
        }

        if (messagePackDefault)
        {
            settings["Caching:DefaultCachePolicy:Serializer:Write"] = MessagePackCachingBuilderExtensions.DefaultName;
            settings["Caching:Redis:FormatNegotiation:Mode"] = "Force";
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddCaching(configuration, builder =>
        {
            builder.AddRedisConnection();
            builder.AddRedis();
            builder.AddMessagePackSerializer(configure: o => o.SerializerOptions = MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolverAllowPrivate.Instance));
        });
        var registration = new RouterCacheRegistration<Item>
        {
            Name = "items",
            Group = Group,
            Source = new NoSource(),
            RedisKey = L,
        };
        if (supplied)
        {
            registration = new RouterCacheRegistration<Item>
            {
                Name = registration.Name,
                Group = registration.Group,
                Source = registration.Source,
                RedisKey = L,
                KeyMove = new KeyMoveBinding<Item>(ItemCodec.Instance, ItemCodec.Instance, L, N, false),
            };
        }

        services.AddCachingRouter(
            configuration,
            router => WithEnvelope(router.UseLibraryWhen(_ => true), inPlace)
                .UseNodeIdentity($"kc{_unique}{_providers.Count}")
                .UseSourceConnection(() => Db)
                .Add(registration)
                .UseKeyMove(new Stage(), moves => moves
                    .UseLibraryKeyPrefix(Library)
                    .AddGroup(Group)
                    .AddCache(new KeyMoveCacheRegistration
                    {
                        Name = "items",
                        Group = Group,
                        InPlacePattern = $"{InPlacePrefix}*",
                        ParseInPlaceKey = key => key.StartsWith(InPlacePrefix, StringComparison.Ordinal) ? key[InPlacePrefix.Length..] : null,
                        NativePattern = $"{AppPrefix}{_unique}:N:*",
                        FormatNativeKey = N,
                        ParseNativeKey = key => key.StartsWith($"{AppPrefix}{_unique}:N:", StringComparison.Ordinal) ? key[$"{AppPrefix}{_unique}:N:".Length..] : null,
                    })),
            "RouterCaching");
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        var router = (Router)provider.GetRequiredService<IRouter>();
        var cache = (RoutedCache<Item>)router.Get<Item>("items");
        var until = DateTime.UtcNow.AddSeconds(20);
        while (cache.Library is not KeyMoveClient<Item> && DateTime.UtcNow < until)
        {
            await Task.Delay(25, Ct);
        }

        var client = cache.Library as KeyMoveClient<Item> ?? throw new InvalidOperationException("The router never bound the key move client.");
        var cacheOptions = provider.GetRequiredKeyedService<IOptions<CacheOptions>>(RouterServiceCollectionExtensions.StackName).Value;
        var redisOptions = provider.GetRequiredKeyedService<IOptions<RedisCacheOptions>>(RouterServiceCollectionExtensions.StackName).Value;
        _entries.Add((RouterServiceCollectionExtensions.NodeListKey(cacheOptions, redisOptions), router.Advertiser!.Field));
        return (provider, cache, client);
    }

    private sealed class Stage : IMigrationStageSource
    {
        public ValueTask<int?> GetStageAsync(string group, CancellationToken cancellationToken) => ValueTask.FromResult<int?>(2);
    }

    private sealed class NoSource : ISourceCache<Item>
    {
        public ValueTask<SourceRead<Item>> GetAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<WriteResult> SetAsync(string key, Item value, CacheExpiry expiry, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<AddResult> TryAddAsync(string key, Item value, CacheExpiry expiry, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask ClearAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
