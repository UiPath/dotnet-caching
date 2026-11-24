using System.Text;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The hash codecs the binder makes: each side over its own hash cache's write and read code, the cap on fields and on bytes, and the in-place side that stores user fields only.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveHashCodecTests(RedisFixture fixture) : IAsyncLifetime
{
    private const int FieldCap = 5;
    private static readonly TimeSpan Life = TimeSpan.FromMinutes(5);
    private static readonly string RunTag = Guid.NewGuid().ToString("N")[..8];

    private readonly string _unique = Guid.NewGuid().ToString("N")[..10];
    private readonly List<ServiceProvider> _providers = [];
    private ConnectionMultiplexer? _redis;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDatabase Db => _redis!.GetDatabase();

    public async ValueTask InitializeAsync()
    {
        if (fixture.Enabled)
        {
            _redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
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
            foreach (var key in _redis.GetServer(_redis.GetEndPoints()[0]).Keys(pattern: $"*{_unique}*"))
            {
                await Db.KeyDeleteAsync(key);
            }

            await _redis.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(FieldCap - 1, nameof(DecodeClass.Ok))]
    [InlineData(FieldCap, nameof(DecodeClass.Ok))]
    [InlineData(FieldCap + 1, nameof(DecodeClass.TooLarge))]
    public async Task A_hash_is_promotable_up_to_the_field_cap_and_too_large_above_it_without_being_read(int fields, string expected)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var native = Binding(cacheNulls: false).Native;
        var stored = await StoredAsync(Codec(false), Fields(fields));

        var decoded = native.DecodeFields(stored);

        decoded.Class.ToString().Should().Be(expected);
        decoded.Length.Should().Be(fields);
        decoded.Fields.Count.Should().Be(expected == nameof(DecodeClass.Ok) ? fields : 0, "an over-cap hash is not read");
    }

    [Fact]
    public async Task An_unreadable_hash_above_the_field_cap_is_too_large_like_a_readable_one_and_at_the_cap_it_is_unreadable()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var native = Binding(cacheNulls: false).Native;
        var cache = Codec(false);
        var atCap = (await StoredAsync(cache, Fields(FieldCap - 1))).Append(new("bad", [0x1E, 1, 2, 3])).ToArray();
        var aboveCap = atCap.Append(new("worse", [0x1E, 1, 2, 3])).ToArray();

        native.DecodeFields(atCap).Class.Should().Be(DecodeClass.Unreadable);
        native.DecodeFields(aboveCap).Class.Should().Be(DecodeClass.TooLarge, "the inventory judges by length alone, and the read agrees");
        native.DecodeFields(aboveCap).Length.Should().Be(FieldCap + 1);
    }

    [Theory]
    [InlineData(-1, nameof(DecodeClass.Ok))]
    [InlineData(0, nameof(DecodeClass.Ok))]
    [InlineData(1, nameof(DecodeClass.TooLarge))]
    public async Task A_hash_read_is_too_large_when_its_fields_and_values_exceed_the_byte_cap_and_not_at_it(int overBy, string expected)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var stored = await StoredAsync(Codec(false), Fields(3));
        var bytes = stored.Sum(e => (long)Encoding.UTF8.GetByteCount(e.Key) + e.Value.Length);
        var binder = new KeyMoveBinder(Strings(), Strings(), [Cache()], new KeyMoveOptions { MaxValueBytes = (int)bytes - overBy }, inPlaceHash: Codec(false), nativeHash: Codec(false));

        var decoded = binder.BindHash<HashItem>("items")!.Native.DecodeFields(stored);

        decoded.Class.ToString().Should().Be(expected);
        decoded.Bytes.Should().Be(bytes);
        decoded.Fields.Should().HaveCount(3, "a hash above the byte cap only was read");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_hash_with_no_user_field_is_a_cached_null_whether_it_is_the_empty_marker_or_only_a_count(bool marker)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var native = Binding(cacheNulls: true).Native;
        IReadOnlyCollection<KeyValuePair<string, byte[]>> stored = marker
            ? [new("_metadata_", [])]
            : [new("_version_", "7"u8.ToArray())];

        var decoded = native.DecodeFields(stored);

        decoded.Class.Should().Be(DecodeClass.Null);
        decoded.Fields.Should().BeEmpty();
        decoded.Length.Should().Be(1);
    }

    [Fact]
    public async Task The_native_side_stores_what_the_applications_hash_cache_stores_and_the_in_place_side_stores_user_fields_only()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var binding = Binding(cacheNulls: true);
        var cache = Hashes(true);
        var fields = new Dictionary<string, HashItem?> { ["a"] = new("1"), ["b"] = null };
        var metadata = new Dictionary<string, string?> { ["owner"] = "me" };
        var key = Key("w");
        await cache.SetAsync(key, fields, new HashCacheEntryOptions(TimeToLive: Life, Metadata: metadata), null, Ct);
        var stored = (await Db.HashGetAllAsync(Find(key))).ToDictionary(e => ((string?)e.Name)!, e => ((byte[]?)e.Value)!);

        var native = binding.Native.EncodeFields(fields, metadata).ToDictionary(e => e.Key, e => e.Value);
        var inPlace = binding.InPlace.EncodeFields(fields, metadata);

        native.Keys.Should().BeEquivalentTo(stored.Keys);
        foreach (var (field, bytes) in stored)
        {
            native[field].Should().Equal(bytes, field);
        }

        inPlace.Select(e => e.Key).Should().Equal(["a", "b"], "the metadata is no part of an in-place hash");
        binding.InPlace.EncodeFields(new Dictionary<string, HashItem?>(), null).Should().BeEmpty("nor is the empty marker");
        binding.Native.EncodeFields(new Dictionary<string, HashItem?>(), null).Should().ContainSingle().Which.Key.Should().Be("_metadata_");
        binding.InPlace.CachesNulls.Should().BeTrue();
    }

    [Fact]
    public void The_binder_binds_a_hash_cache_with_the_cap_of_its_registration_or_of_the_options()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var binder = new KeyMoveBinder(Strings(), Strings(), [Cache()], new KeyMoveOptions { MaxHashFields = 7 }, inPlaceHash: Codec(false), nativeHash: Codec(false));

        binder.BindHash<HashItem>("items")!.MaxHashFields.Should().Be(7);
        binder.BindHash<HashItem>("items", 12)!.MaxHashFields.Should().Be(12);
        binder.BindHash<HashItem>("other").Should().BeNull("it is not a key move cache");
        var none = () => binder.BindHash<HashItem>("items", 0);
        none.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_hash_cache_with_no_hash_codec_cache_fails_the_binding_so_the_host_fails_at_startup()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var onlyStrings = new KeyMoveBinder(Strings(), Strings(), [Cache()]);
        var halfway = new KeyMoveBinder(Strings(), Strings(), [Cache()], inPlaceHash: Codec(false));

        var first = () => onlyStrings.BindHash<HashItem>("items");
        var second = () => halfway.BindHash<HashItem>("items");

        first.Should().Throw<InvalidOperationException>().WithMessage("*items*hash cache*");
        second.Should().Throw<InvalidOperationException>();
        onlyStrings.BindHash<HashItem>("other").Should().BeNull("a cache that is not moving needs no codec");
    }

    private static IEnumerable<KeyValuePair<string, HashItem?>> Fields(int count) =>
        Enumerable.Range(0, count).Select(i => new KeyValuePair<string, HashItem?>($"f{i}", new HashItem($"v{i}")));

    private static KeyMoveCache Cache() =>
        new("items", "g", false, false, false, null, "p*", k => k, k => k, "n*", k => k, k => k);

    private static async Task<KeyValuePair<string, byte[]>[]> StoredAsync(IByteHashCodecCache cache, IEnumerable<KeyValuePair<string, HashItem?>> fields)
    {
        await Task.CompletedTask;
        return [.. cache.EncodeFields(new Dictionary<string, HashItem?>(fields), null, null)];
    }

    private string Key(string part) => $"khc:{RunTag}:{_unique}:{part}";

    private RedisKey Find(string key) => _redis!.GetServer(_redis.GetEndPoints()[0]).Keys(pattern: $"*{key}*").Single();

    private KeyMoveHashBinding<HashItem> Binding(bool cacheNulls)
    {
        var binder = new KeyMoveBinder(Strings(), Strings(), [Cache()], new KeyMoveOptions { MaxHashFields = FieldCap }, inPlaceHash: Codec(cacheNulls), nativeHash: Codec(cacheNulls));
        return binder.BindHash<HashItem>("items")!;
    }

    private IHashCache Hashes(bool cacheNulls) =>
        Provider(cacheNulls).GetRequiredService<ICacheFactory>().CreateHashCache(KnownCacheProviderNames.Redis);

    private IByteHashCodecCache Codec(bool cacheNulls) => (IByteHashCodecCache)Hashes(cacheNulls);

    private IByteCodecCache Strings() =>
        (IByteCodecCache)Provider(false).GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);

    private ServiceProvider Provider(bool cacheNulls)
    {
        var provider = new ServiceCollection()
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = fixture.ConnectionString);
                    b.AddRedis(o => o.CacheNullValues = cacheNulls);
                },
                o => o.AppShortName = $"khc{_unique}"[..8])
            .BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    public sealed record HashItem(string Name);
}
