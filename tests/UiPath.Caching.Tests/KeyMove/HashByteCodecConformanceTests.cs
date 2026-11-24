using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.MessagePack;
using UiPath.Caching.Redis;
using UiPath.Caching.Tests.Redis;
using Doc = UiPath.Caching.Tests.KeyMove.ByteCodecConformanceTests.Doc;

namespace UiPath.Caching.Tests.KeyMove;

// The hash byte seam of the Redis hash cache against the cache's own write and read, on the fields a raw HGETALL sees.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class HashByteCodecConformanceTests(RedisContainerFixture fixture) : IDisposable
{
    private static readonly string RunTag = Guid.NewGuid().ToString("N")[..8];
    private static readonly TimeSpan Life = TimeSpan.FromMinutes(5);
    private static readonly byte[] Foreign = [0x1E, 1, 2, 3];

    private readonly string _app = $"hc{Guid.NewGuid():N}"[..10];
    private readonly List<ServiceProvider> _providers = [];
    private ConnectionMultiplexer? _raw;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDatabase Db => (_raw ??= ConnectionMultiplexer.Connect(fixture.ConnectionString)).GetDatabase();

    public static TheoryData<string, bool, bool, bool> Formats() => new()
    {
        { "json with no policy", false, false, false },
        { "messagepack by the call's policy", false, true, false },
        { "messagepack with compression by the call's policy", false, true, true },
        { "messagepack by the default policy and no call policy", true, false, false },
        { "messagepack with compression by the default policy and no call policy", true, false, true },
    };

    public static TheoryData<bool, string> MetadataCases() => new()
    {
        { false, "some" },
        { false, "empty" },
        { true, "some" },
        { true, "empty" },
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task EncodeFields_returns_the_fields_the_write_path_stores_and_DecodeFields_reads_them_as_the_read_path_does(string format, bool defaultIsMessagePack, bool passPolicy, bool large)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Open(defaultIsMessagePack);
        var policy = passPolicy ? MessagePackPolicy() : null;
        var values = new Dictionary<string, Doc?> { ["a"] = new(large ? new string('x', 50_000) : "name", 7, ["a", "b"]), ["b"] = new("other", 8, []) };
        var set = Key("s");
        var plain = Key("p");

        (await cache.SetAsync(set, values, new HashCacheEntryOptions(TimeToLive: Life), policy, Ct)).Should().BeTrue();
        (await cache.SetAsync(plain, values, Life, policy, Ct)).Should().BeTrue();

        var stored = await RawAsync(set);
        var codec = (IByteHashCodecCache)cache;
        Same(codec.EncodeFields(values, null, policy), stored, format);
        Same(await RawAsync(plain), stored, "both write entry points store the same fields");

        var viaLibrary = await cache.GetCacheEntryAsync<Doc>(set, policy, Ct);
        var viaCodec = codec.DecodeFields<Doc>(stored, policy);
        viaCodec.Outcome.Should().Be(SnapshotOutcome.Found);
        viaCodec.Values.Should().BeEquivalentTo(viaLibrary.Value);
        viaCodec.Values.Should().BeEquivalentTo(await cache.GetAsync<Doc>(set, policy, Ct));
        viaCodec.Length.Should().Be(2);
        viaCodec.Metadata.Should().BeNull();
        viaCodec.Version.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(MetadataCases))]
    public async Task The_metadata_field_is_what_the_write_path_stores_for_the_metadata_the_call_asked_for(bool messagePack, string metadata)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Open(false);
        var policy = messagePack ? MessagePackPolicy() : null;
        var values = new Dictionary<string, Doc?> { ["a"] = new("n", 1, []) };
        var asked = new Dictionary<string, string?>();
        if (metadata == "some")
        {
            asked["owner"] = "me";
            asked["gone"] = null;
        }

        var key = Key("m");

        await cache.SetAsync(key, values, new HashCacheEntryOptions(TimeToLive: Life, Metadata: asked), policy, Ct);

        var stored = await RawAsync(key);
        var codec = (IByteHashCodecCache)cache;
        Same(codec.EncodeFields(values, asked, policy), stored, metadata);
        var viaCodec = codec.DecodeFields<Doc>(stored, policy);
        viaCodec.Metadata.Should().BeEquivalentTo(await cache.GetMetadataAsync<Doc>(key, Ct));
        viaCodec.Metadata.Should().BeEquivalentTo((await cache.GetCacheEntryAsync<Doc>(key, policy, Ct)).Metadata);
        viaCodec.Values.Should().ContainKey("a").And.HaveCount(1, "the metadata field is not a user field");
        stored.ContainsKey("_metadata_").Should().Be(!(messagePack && metadata == "empty"), "a non-JSON writer leaves an empty metadata field out");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_null_field_is_stored_as_the_cache_stores_a_null_and_reads_back_as_the_read_path_reads_it(bool cacheNulls)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Open(false, cacheNulls);
        var values = new Dictionary<string, Doc?> { ["a"] = new("n", 1, []), ["gone"] = null };
        var key = Key("n");
        await cache.SetAsync(key, values, new HashCacheEntryOptions(TimeToLive: Life), null, Ct);

        var stored = await RawAsync(key);
        var codec = (IByteHashCodecCache)cache;
        Same(codec.EncodeFields(values, null, null), stored, "null field");
        (stored["gone"].Length == 0).Should().Be(cacheNulls, "a cached null is the empty value");
        codec.CachesNulls.Should().Be(cacheNulls);
        var viaCodec = codec.DecodeFields<Doc>(stored, null);
        viaCodec.Values.Should().BeEquivalentTo(await cache.GetAsync<Doc>(key, null, Ct));
        viaCodec.Values["gone"].Should().BeNull();
    }

    [Theory]
    [InlineData(false, SnapshotOutcome.Absent)]
    [InlineData(true, SnapshotOutcome.Found)]
    public async Task An_empty_hash_is_the_empty_marker_while_nulls_are_cached_and_no_key_otherwise(bool cacheNulls, SnapshotOutcome expected)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Open(false, cacheNulls);
        var empty = new Dictionary<string, Doc?>();
        var key = Key("e");
        await cache.SetAsync(key, new Dictionary<string, Doc?> { ["a"] = new("n", 1, []) }, Life, null, Ct);
        var redisKey = Find(key);
        await cache.SetAsync(key, empty, new HashCacheEntryOptions(TimeToLive: Life), null, Ct);

        var stored = await RawAsync(redisKey);
        var codec = (IByteHashCodecCache)cache;
        var encoded = codec.EncodeFields(empty, null, null);
        Same(encoded, stored, "empty hash");
        encoded.Should().HaveCount(cacheNulls ? 1 : 0);
        var viaCodec = codec.DecodeFields<Doc>(stored, null);
        viaCodec.Outcome.Should().Be(expected);
        viaCodec.EmptyMarker.Should().Be(cacheNulls);
        viaCodec.Values.Should().BeEmpty();
        (await cache.GetCacheEntryAsync<Doc>(key, null, Ct)).Found.Should().Be(expected == SnapshotOutcome.Found);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_user_fields_encoder_writes_no_system_field_even_for_an_empty_hash_that_caches_nulls(bool cacheNulls)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Open(false, cacheNulls);
        var codec = (IByteHashCodecCache)cache;
        var values = new Dictionary<string, Doc?> { ["a"] = new("n", 1, []), ["b"] = null };
        var key = Key("u");
        await cache.SetAsync(key, values, new HashCacheEntryOptions(TimeToLive: Life, Metadata: new Dictionary<string, string?> { ["m"] = "1" }), null, Ct);

        var encoded = codec.EncodeUserFields(values, null);

        encoded.Select(e => e.Key).Should().Equal("a", "b");
        encoded.Should().NotContain(e => KnownFieldNames.IsReserved(e.Key));
        Same(encoded, Without(await RawAsync(key), "_metadata_"), "user fields");
        codec.EncodeUserFields(new Dictionary<string, Doc?>(), null).Should().BeEmpty("no empty marker, whatever the null setting");
    }

    [Fact]
    public async Task The_write_count_a_peer_aware_write_leaves_is_reported_apart_from_the_user_fields_and_is_no_part_of_the_encoded_fields()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Open(false);
        var values = new Dictionary<string, Doc?> { ["a"] = new("n", 1, []) };
        var key = Key("v");
        var written = await ((RedisHashCache)cache).SetVersionedAsync(key, values, new HashCacheEntryOptions(TimeToLive: Life), null, new PeerFieldLimits(10, 1000), Ct);
        written.Written.Should().BeTrue();

        var stored = await RawAsync(key);
        var codec = (IByteHashCodecCache)cache;
        var viaCodec = codec.DecodeFields<Doc>(stored, null);

        stored.Should().ContainKey("_version_");
        viaCodec.Version.Should().Be(long.Parse(System.Text.Encoding.UTF8.GetString(stored["_version_"]), CultureInfo.InvariantCulture));
        viaCodec.Values.Should().BeEquivalentTo(await cache.GetAsync<Doc>(key, null, Ct)).And.NotContainKey("_version_");
        viaCodec.Length.Should().Be(2);
        Same(codec.EncodeFields(values, null, null), Without(stored, "_version_"), "the count is minted by a counted write, not by the encoding");
    }

    [Theory]
    [InlineData("a field with a header no serializer owns")]
    [InlineData("a field of text that is not JSON")]
    [InlineData("metadata that is not JSON")]
    public async Task Fields_that_no_format_reads_are_unreadable_to_both_and_never_throw_or_change_the_key(string what)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Open(false);
        var key = Key("x");
        await cache.SetAsync(key, new Dictionary<string, Doc?> { ["a"] = new("n", 1, []) }, Life, null, Ct);
        var (field, bytes) = what switch
        {
            "a field with a header no serializer owns" => ("bad", Foreign),
            "a field of text that is not JSON" => ("bad", "{x!"u8.ToArray()),
            _ => ("_metadata_", "{x!"u8.ToArray()),
        };
        await Db.HashSetAsync(Find(key), field, bytes);
        var before = await RawAsync(key);

        var viaCodec = ((IByteHashCodecCache)cache).DecodeFields<Doc>(before, null);
        var viaLibrary = await cache.GetCacheEntryAsync<Doc>(key, null, Ct);

        viaCodec.Outcome.Should().Be(SnapshotOutcome.Unreadable, what);
        viaCodec.Values.Should().BeEmpty();
        viaLibrary.Found.Should().BeFalse(what);
        if (field != "_metadata_")
        {
            (await cache.GetAsync<Doc>(key, null, Ct)).Should().BeEmpty(what);
        }

        Same(await RawAsync(key), before, "the hash is left in place");
    }

    [Fact]
    public async Task A_field_in_a_format_the_policys_read_list_leaves_out_is_unreadable_to_both_and_the_library_read_says_so()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Open(false);
        var key = Key("r");
        await cache.SetAsync(key, new Dictionary<string, Doc?> { ["a"] = new("n", 1, []), ["b"] = new("o", 2, []) }, Life, MessagePackPolicy(), Ct);
        var stored = await RawAsync(key);
        var jsonOnly = new CachePolicy { Serializer = new SerializerPolicy { Read = ["json"] } };

        var viaCodec = ((IByteHashCodecCache)cache).DecodeFields<Doc>(stored, jsonOnly);
        var viaLibrary = await cache.GetCacheEntryAsync<Doc>(key, jsonOnly, Ct);

        viaCodec.Outcome.Should().Be(SnapshotOutcome.Unreadable);
        viaLibrary.Should().BeOfType<UnreadableEntry<IDictionary<string, Doc?>>>();
        viaCodec.Length.Should().Be(2);
    }

    [Fact]
    public void Repeated_fields_and_a_null_or_uncacheable_argument_follow_the_string_seam()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var codec = (IByteHashCodecCache)Open(false);
        var twice = new[] { new KeyValuePair<string, byte[]>("a", "{}"u8.ToArray()), new KeyValuePair<string, byte[]>("a", "{}"u8.ToArray()) };

        codec.DecodeFields<Doc>(twice, null).Outcome.Should().Be(SnapshotOutcome.Unreadable, "bad data is an outcome, never an exception");
        var nothing = () => codec.DecodeFields<Doc>(null!, null);
        nothing.Should().Throw<ArgumentNullException>();
        var reserved = () => codec.EncodeFields(new Dictionary<string, Doc?> { ["_odd_"] = null }, null, null);
        reserved.Should().Throw<ArgumentException>("the write path refuses a field name that is reserved");
    }

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }

        _raw?.Dispose();
    }

    private static void Same(IEnumerable<KeyValuePair<string, byte[]>> actual, IReadOnlyDictionary<string, byte[]> expected, string because)
    {
        var fields = actual.ToDictionary(e => e.Key, e => e.Value);
        fields.Keys.Should().BeEquivalentTo(expected.Keys, because);
        foreach (var (field, bytes) in expected)
        {
            fields[field].Should().Equal(bytes, $"{because}: {field}");
        }
    }

    private static Dictionary<string, byte[]> Without(Dictionary<string, byte[]> stored, string field) =>
        stored.Where(e => e.Key != field).ToDictionary(e => e.Key, e => e.Value);

    private static CachePolicy MessagePackPolicy() => new() { DistributedExpiration = Life, Serializer = new SerializerPolicy { Write = MessagePackCachingBuilderExtensions.DefaultName } };

    private string Key(string part) => $"hc:{RunTag}:{_app}:{part}";

    private RedisKey Find(string key)
    {
        _ = Db;
        return _raw!.GetServer(_raw.GetEndPoints()[0]).Keys(pattern: $"*{key}*").Single();
    }

    private Task<Dictionary<string, byte[]>> RawAsync(string key) => RawAsync(Find(key));

    private async Task<Dictionary<string, byte[]>> RawAsync(RedisKey key)
    {
        var entries = await Db.HashGetAllAsync(key);
        return entries.ToDictionary(e => ((string?)e.Name)!, e => ((byte[]?)e.Value)!);
    }

    private IHashCache Open(bool defaultIsMessagePack, bool cacheNulls = false)
    {
        var provider = new ServiceCollection()
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = fixture.ConnectionString);
                    b.AddRedis(o =>
                    {
                        o.FormatNegotiation.Mode = FormatNegotiationMode.Force;
                        o.CacheNullValues = cacheNulls;
                    });
                    b.AddMessagePackSerializer(configure: o => o.CompressionThresholdBytes = 1024);
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
        return provider.GetRequiredService<ICacheFactory>().CreateHashCache(KnownCacheProviderNames.Redis);
    }
}
