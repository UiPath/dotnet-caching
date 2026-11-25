using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.MessagePack;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// The byte codec seam of the Redis cache against the cache's own write and read, on the bytes a raw GET sees.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class ByteCodecConformanceTests(RedisContainerFixture fixture) : IDisposable
{
    private static readonly string RunTag = Guid.NewGuid().ToString("N")[..8];
    private static readonly TimeSpan Life = TimeSpan.FromMinutes(5);

    private readonly string _app = $"bc{Guid.NewGuid():N}"[..10];
    private readonly List<ServiceProvider> _providers = [];
    private ConnectionMultiplexer? _raw;
    private ManualTimeProvider? _time;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ManualTimeProvider Time => _time ??= new(new DateTimeOffset(DateTime.SpecifyKind(Db.Multiplexer.GetServer(Db.Multiplexer.GetEndPoints()[0]).Time(), DateTimeKind.Utc)));

    private IDatabase Db => (_raw ??= ConnectionMultiplexer.Connect(fixture.ConnectionString)).GetDatabase();

    public static TheoryData<string, bool, bool, bool> Formats() => new()
    {
        { "json with no policy", false, false, false },
        { "messagepack by the call's policy", false, true, false },
        { "messagepack with compression by the call's policy", false, true, true },
        { "messagepack by the default policy and no call policy", true, false, false },
        { "messagepack with compression by the default policy and no call policy", true, false, true },
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Encode_returns_the_bytes_the_write_path_stores_and_Decode_reads_them_as_the_read_path_does(string format, bool defaultIsMessagePack, bool passPolicy, bool large)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Open(defaultIsMessagePack);
        var policy = passPolicy ? MessagePackPolicy() : null;
        var value = new Doc(large ? new string('x', 50_000) : "name", 7, ["a", "b"]);
        var written = Key("w");
        var set = Key("s");

        (await cache.AsMigrationCache().WriteAsync(written, value, Time.ToDateTimeOffset(Life), policy, Ct)).Should().Be(StoreOutcome.Committed);
        (await cache.SetAsync(set, value, Life, policy, Ct)).Should().BeTrue();

        var stored = (byte[]?)await Db.StringGetAsync(Find(written)) ?? throw new InvalidOperationException(format);
        var codec = (IByteCodecCache)cache;
        codec.Encode(value, policy).Should().Equal(stored, format);
        ((byte[]?)await Db.StringGetAsync(Find(set))).Should().Equal(stored, "both write entry points store the same bytes");
        stored[0].Should().Be(ExpectedMarker(passPolicy || defaultIsMessagePack, large));

        var viaLibrary = await cache.AsMigrationCache().ReadSnapshotAsync<Doc>(written, policy, Ct);
        var viaCodec = codec.Decode<Doc>(stored, policy);
        viaLibrary.Outcome.Should().Be(SnapshotOutcome.Found);
        viaCodec.Should().Be((SnapshotOutcome.Found, value));
        viaLibrary.Entry!.Value.Should().Be(value);
        viaLibrary.Entry.Expiration.Should().BeCloseTo(Time.ToDateTimeOffset(Life), TimeSpan.FromSeconds(15), "the lifetime is the key's, and the bytes carry none");
        (await Db.KeyTimeToLiveAsync(Find(written)))!.Value.Should().BeCloseTo(Life, TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task A_value_in_a_format_the_policys_read_list_leaves_out_is_unreadable_to_both()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Open(false);
        var value = new Doc("n", 1, []);
        var key = Key("r");
        await cache.SetAsync(key, value, Life, MessagePackPolicy(), Ct);
        var stored = (byte[]?)await Db.StringGetAsync(Find(key)) ?? [];
        var jsonOnly = new CachePolicy { Serializer = new SerializerPolicy { Read = ["json"] } };

        var viaLibrary = await cache.AsMigrationCache().ReadSnapshotAsync<Doc>(key, jsonOnly, Ct);
        var viaCodec = ((IByteCodecCache)cache).Decode<Doc>(stored, jsonOnly);

        viaLibrary.Outcome.Should().Be(SnapshotOutcome.Unreadable);
        viaCodec.Should().Be((SnapshotOutcome.Unreadable, default(Doc)));
    }

    [Theory]
    [InlineData("a header no serializer owns", new byte[] { 0x1E, 1, 2, 3 })]
    [InlineData("a header cut short", new byte[] { 0x10 })]
    [InlineData("text that is not JSON", new byte[] { (byte)'{', (byte)'x', (byte)'!' })]
    public async Task Bytes_that_no_format_reads_are_unreadable_to_both_and_never_throw_or_change_the_key(string what, byte[] foreign)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Open(false);
        var key = Key("u");
        await cache.SetAsync(key, new Doc("n", 1, []), Life, Ct);
        await Db.StringSetAsync(Find(key), foreign, Life);

        var viaLibrary = await cache.AsMigrationCache().ReadSnapshotAsync<Doc>(key, null, Ct);
        var (codecOutcome, _) = ((IByteCodecCache)cache).Decode<Doc>(foreign, null);

        viaLibrary.Outcome.Should().Be(SnapshotOutcome.Unreadable, what);
        codecOutcome.Should().Be(SnapshotOutcome.Unreadable, what);
        ((byte[]?)await Db.StringGetAsync(Find(key))).Should().Equal(foreign);
    }

    [Theory]
    [InlineData(false, SnapshotOutcome.Absent)]
    [InlineData(true, SnapshotOutcome.Found)]
    public async Task An_empty_value_reads_as_a_cached_null_or_a_miss_as_the_cache_is_set_to_store_nulls(bool cacheNulls, SnapshotOutcome expected)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Open(false, cacheNulls);
        var key = Key("n");
        await cache.SetAsync(key, new Doc("n", 1, []), Life, Ct);
        await Db.StringSetAsync(Find(key), RedisValue.EmptyString, Life);

        var viaLibrary = await cache.AsMigrationCache().ReadSnapshotAsync<Doc>(key, null, Ct);
        var viaCodec = ((IByteCodecCache)cache).Decode<Doc>([], null);

        viaLibrary.Outcome.Should().Be(expected);
        viaCodec.Should().Be((expected, default(Doc)));
    }

    // A payload that fills its rented array exactly is held by the RedisValue conversion as that array, which must not go back to the pool while the caller holds it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Encode_of_a_payload_that_fills_its_pooled_buffer_is_not_a_buffer_the_pool_hands_out_again(bool compress)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = (IByteCodecCache)Open(true, compress: compress);

        var (exact, reused) = SweepPoolBuckets(cache);

        exact.Should().BePositive("the sweep reaches payloads that are exactly one pool bucket");
        reused.Should().BeEmpty("the pool's next renter must not change what was encoded");
    }

    [Fact]
    public async Task Encode_of_a_null_value_is_refused_because_the_write_path_stores_it_as_a_delete_or_an_empty_value()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = (IByteCodecCache)Open(false);

        var act = () => cache.Encode<Doc>(null!, null);

        act.Should().Throw<ArgumentNullException>();
    }

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }

        _raw?.Dispose();
    }

    // Synchronous, so the rent after each encode is on the thread whose pool cache the buffer went back to.
    private static (int Exact, List<int> Reused) SweepPoolBuckets(IByteCodecCache cache)
    {
        var random = new Random(7);
        var text = new string(Enumerable.Range(0, 70_000).Select(_ => (char)random.Next('a', 'z' + 1)).ToArray());
        var exact = 0;
        var reused = new List<int>();
        for (var length = 8_000; length <= 66_000; length += 1)
        {
            var bytes = cache.Encode(new Doc(text[..Math.Min(length, text.Length)] + new string('y', Math.Max(0, length - text.Length)), 1, []), null);
            if (bytes.Length < 16_384 || (bytes.Length & (bytes.Length - 1)) != 0)
            {
                continue;
            }

            exact++;
            var expected = bytes.ToArray();
            var rented = ArrayPool<byte>.Shared.Rent(bytes.Length);
            rented.AsSpan().Fill(0xAB);
            if (ReferenceEquals(bytes, rented) || !bytes.AsSpan().SequenceEqual(expected))
            {
                reused.Add(length);
            }

            ArrayPool<byte>.Shared.Return(rented);
        }

        return (exact, reused);
    }

    private static byte ExpectedMarker(bool messagePack, bool large)
    {
        if (!messagePack)
        {
            return (byte)'{';
        }

        return large ? (byte)MessagePackFormats.ValueLz4 : (byte)MessagePackFormats.Value;
    }

    private static CachePolicy MessagePackPolicy() => new() { DistributedExpiration = Life, Serializer = new SerializerPolicy { Write = MessagePackCachingBuilderExtensions.DefaultName } };

    private string Key(string part) => $"bc:{RunTag}:{_app}:{part}";

    private RedisKey Find(string key)
    {
        _ = Db;
        return _raw!.GetServer(_raw.GetEndPoints()[0]).Keys(pattern: $"*{key}*").Single();
    }

    private ICache Open(bool defaultIsMessagePack, bool cacheNulls = false, bool compress = true)
    {
        var provider = new ServiceCollection()
            .AddSingleton<TimeProvider>(Time)
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = fixture.ConnectionString);
                    b.AddRedis(o =>
                    {
                        o.FormatNegotiation.Mode = FormatNegotiationMode.Force;
                        o.CacheNullValues = cacheNulls;
                    });
                    b.AddMessagePackSerializer(configure: o => o.CompressionThresholdBytes = compress ? 1024 : int.MaxValue);
                },
                o =>
                {
                    o.AppShortName = _app;
                    if (defaultIsMessagePack)
                    {
                        o.DefaultCachePolicy = MessagePackPolicy();
                    }
                })
            .BuildServiceProvider();
        _providers.Add(provider);
        return provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
    }

    public sealed record Doc(string Name, int Version, string[] Tags)
    {
        public bool Equals(Doc? other) => other is not null && Name == other.Name && Version == other.Version && Tags.SequenceEqual(other.Tags);

        public override int GetHashCode() => HashCode.Combine(Name, Version);
    }
}
