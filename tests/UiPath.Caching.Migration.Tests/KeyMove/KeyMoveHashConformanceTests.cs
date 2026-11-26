using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The conformance round trip with the real library: what the hash client promotes or writes into N is what the application's hash cache reads, and what the library writes is what the client reads.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveHashConformanceTests(RedisFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan Life = TimeSpan.FromMinutes(5);
    private static readonly string RunTag = Guid.NewGuid().ToString("N")[..8];

    private readonly string _unique = Guid.NewGuid().ToString("N")[..10];
    private readonly List<ServiceProvider> _providers = [];
    private ConnectionMultiplexer? _redis;
    private ManualTimeProvider _clock = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDatabase Db => _redis!.GetDatabase();

    public async ValueTask InitializeAsync()
    {
        if (fixture.Enabled)
        {
            _redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
            _clock = new ManualTimeProvider(await KeyMoveKit.ServerTimeAsync(Db));
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_promotion_from_L_to_N_is_read_by_the_applications_hash_cache_as_the_fields_it_would_have_written(bool cacheNulls)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var app = Hashes(cacheNulls);
        var (binding, nativePrefix) = await BindingAsync(app, cacheNulls);
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, inPlace: binding.InPlace, native: binding.Native, nativePrefix: nativePrefix, nativeNulls: cacheNulls);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        var fields = new Dictionary<string, Item?> { ["a"] = new("1"), ["b"] = new("2"), ["n"] = null };
        await kit.Db.HashSetAsync(kit.L("k"), [.. binding.InPlace.EncodeFields(fields, null).Select(e => new HashEntry(e.Key, e.Value))]);
        await kit.Db.KeyExpireAsync(kit.L("k"), Life);

        var read = await kit.Client.ReadAsync("k", null, Ct);

        read.Kind.Should().Be(ReadOutcome.Found);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        var stored = await app.GetCacheEntryAsync<Item>(CacheKey.Verbatim(AppKey("k")), Ct);
        stored.Found.Should().BeTrue();
        stored.Value!.Keys.Should().BeEquivalentTo(cacheNulls ? ["a", "b", "n"] : ["a", "b"], "a null field is left out where the library does not cache nulls");
        stored.Value["a"].Should().Be(new Item("1"));
        stored.Value["b"].Should().Be(new Item("2"));
        stored.Expiration.Should().BeCloseTo(_clock.ToDateTimeOffset(Life), TimeSpan.FromSeconds(30), "the expiry comes from the source's px");
        (await app.GetMetadataAsync<Item>(CacheKey.Verbatim(AppKey("k")), Ct)).Should().BeNull();
        (await Db.HashGetAllAsync(nativePrefix + "k")).Select(e => e.Name.ToString()).Should().NotContain("_version_", "no version is minted by a promotion");
        var raw = (await Db.HashGetAllAsync(nativePrefix + "k")).ToDictionary(e => e.Name.ToString(), e => ((byte[]?)e.Value)!);
        foreach (var (field, bytes) in binding.Native.EncodeFields(new Dictionary<string, Item?>(fields.Where(f => cacheNulls || f.Value is not null)), null))
        {
            raw[field].Should().Equal(bytes, field);
        }
    }

    [Fact]
    public async Task A_hash_the_application_wrote_with_metadata_and_a_version_is_read_by_the_client_from_N_and_a_merge_through_the_client_keeps_the_library_able_to_read_it()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var app = Hashes(false);
        var (binding, nativePrefix) = await BindingAsync(app, false);
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, inPlace: binding.InPlace, native: binding.Native, nativePrefix: nativePrefix);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S5", swept: true);
        var metadata = new Dictionary<string, string?> { ["owner"] = "me" };
        var counted = await ((ISharedHashTier)app).SetVersionedAsync(CacheKey.Verbatim(AppKey("k")), new Dictionary<string, Item?> { ["a"] = new("1") }, new HashCacheEntryOptions(TimeToLive: Life, Metadata: metadata), null, new PeerFieldLimits(0, 0), Ct);
        counted.Version.Should().BeGreaterThan(0);

        var read = await kit.Client.ReadAsync("k", null, Ct);
        var merged = await kit.Client.SetAsync("k", new Dictionary<string, Item?> { ["b"] = new("2") }, new HashWriteOptions(HashCacheSetOption.HashReplace, _clock.ToDateTimeOffset(Life)), Ct);

        read.Fields!.Keys.Should().BeEquivalentTo("a");
        read.Metadata.Should().ContainKey("owner");
        read.Version.Should().BeGreaterThan(0, "the decoder reports a version it finds on N");
        merged.Outcome.Should().Be(WriteOutcome.Committed);
        var after = await app.GetCacheEntryAsync<Item>(CacheKey.Verbatim(AppKey("k")), Ct);
        after.Value!.Keys.Should().BeEquivalentTo("a", "b");
        after.Metadata.Should().ContainKey("owner", "a merge keeps the metadata the hash holds");
        (await Db.HashGetAsync(nativePrefix + "k", "_version_")).IsNull.Should().BeFalse("the write counts, as the library's does");

        var replaced = await kit.Client.SetAsync("k", new Dictionary<string, Item?> { ["z"] = new("9") }, new HashWriteOptions(HashCacheSetOption.KeyReplace, _clock.ToDateTimeOffset(Life), new Dictionary<string, string?> { ["owner"] = "you" }), Ct);
        replaced.Outcome.Should().Be(WriteOutcome.Committed);
        (await app.GetAsync<Item>(CacheKey.Verbatim(AppKey("k")), Ct)).Keys.Should().BeEquivalentTo("z");
        (await app.GetMetadataAsync<Item>(CacheKey.Verbatim(AppKey("k")), Ct))!["owner"].Should().Be("you");
    }

    [Theory]
    [InlineData("S1", false)]
    [InlineData("S2", false)]
    [InlineData("S3", false)]
    [InlineData("S3", true)]
    [InlineData("S4", false)]
    [InlineData("S4", true)]
    [InlineData("S5", true)]
    public async Task Selected_hash_fields_are_read_independently_when_one_is_unreadable(string state, bool native)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 to run.");
        var app = Hashes(true);
        var (binding, nativePrefix) = await BindingAsync(app, true);
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, noOrigin: true, inPlace: binding.InPlace, native: binding.Native, nativePrefix: nativePrefix);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state, nPop: 0, swept: state == "S5");

        var values = KeyMoveHashKit.Items(("first", "one"), ("last", "two"), ("unselected", "three"), ("empty", null));
        var side = native ? kit.N("k") : kit.L("k");
        var codec = native ? binding.Native : binding.InPlace;
        await Db.HashSetAsync(side, CorruptHash(codec, values));
        await Db.KeyExpireAsync(side, Life);
        await Db.HashSetAsync(nativePrefix + "plain", CorruptHash(binding.Native, values));
        var plainKey = CacheKey.Verbatim(AppKey("plain"));
        var expected = new Dictionary<string, Item?>
        {
            ["first"] = await app.GetItemAsync<Item>(plainKey, "first", null, Ct),
            ["last"] = await app.GetItemAsync<Item>(plainKey, "last", null, Ct),
            ["empty"] = await app.GetItemAsync<Item>(plainKey, "empty", null, Ct),
        };
        expected["first"].Should().Be(new Item("one"));
        expected["last"].Should().Be(new Item("two"));
        (await app.GetItemAsync<Item>(plainKey, "broken", null, Ct)).Should().BeNull();

        var read = await kit.Client.ReadAsync("k", ["first", "broken", "last", "empty", "missing"], Ct);

        read.Kind.Should().Be(ReadOutcome.Found);
        read.Fields.Should().BeEquivalentTo(expected);
        read.Uncached.Should().BeTrue();
        read.Expiration.Should().BeCloseTo(_clock.ToDateTimeOffset(Life), TimeSpan.FromSeconds(30));
        (await kit.Client.ReadAsync("k", ["broken", "missing"], Ct)).Kind.Should().Be(ReadOutcome.Unreadable);
        (await kit.Client.ReadAsync("k", null, Ct)).Kind.Should().Be(ReadOutcome.Unreadable);
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().Be(native, "a partially decoded hash is never promoted");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Field_recovery_matches_the_stored_bytes_of_the_requested_name(bool includeReplacement)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 to run.");
        const string replacement = "\uFFFD";
        var app = Hashes(false);
        var (binding, nativePrefix) = await BindingAsync(app, false);
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, native: binding.Native, nativePrefix: nativePrefix);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S5", swept: true);
        var expected = KeyMoveHashKit.Items(("good", "value"));
        if (includeReplacement)
        {
            expected[replacement] = new Item("replacement");
        }

        HashEntry[] entries = [.. CorruptHash(binding.Native, expected), new(new byte[] { 0xFF }, binding.Native.EncodeFields(KeyMoveHashKit.Items(("raw", "invalid name")), null).Single().Value)];
        await Db.HashSetAsync(kit.N("k"), entries);

        var read = await kit.Client.ReadAsync("k", ["good", replacement, replacement], Ct);

        read.Kind.Should().Be(ReadOutcome.Found);
        read.Fields.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Missing_selected_fields_on_a_corrupt_hash_are_an_empty_hit()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 to run.");
        var app = Hashes(false);
        var (binding, nativePrefix) = await BindingAsync(app, false);
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, native: binding.Native, nativePrefix: nativePrefix);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S5", swept: true);
        await Db.HashSetAsync(kit.N("k"), CorruptHash(binding.Native, KeyMoveHashKit.Items(("good", "value"))));

        var read = await kit.Client.ReadAsync("k", ["missing", "also missing"], Ct);

        read.Kind.Should().Be(ReadOutcome.Found);
        read.Fields.Should().BeEmpty();
        read.Uncached.Should().BeTrue();
    }

    [Theory]
    [InlineData("good", false)]
    [InlineData("missing", false)]
    [InlineData("good", true)]
    public async Task Field_recovery_keeps_the_system_fields_that_decode(string field, bool corruptMetadata)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 to run.");
        var app = Hashes(false);
        var (binding, nativePrefix) = await BindingAsync(app, false);
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, native: binding.Native, nativePrefix: nativePrefix);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S5", swept: true);
        var metadata = new Dictionary<string, string?> { ["owner"] = "me" };
        var counted = await ((ISharedHashTier)app).SetVersionedAsync(CacheKey.Verbatim(AppKey("k")), KeyMoveHashKit.Items(("good", "value")), new HashCacheEntryOptions(TimeToLive: Life, Metadata: metadata), null, new PeerFieldLimits(0, 0), Ct);
        counted.Version.Should().BeGreaterThan(0);
        RedisValue corrupt = new byte[] { 0x1E, 1, 2, 3 };
        await Db.HashSetAsync(kit.N("k"), corruptMetadata ? "_metadata_" : "broken", corrupt);

        var read = await kit.Client.ReadAsync("k", [field], Ct);

        read.Version.Should().Be(counted.Version);
        read.Metadata.Should().BeEquivalentTo(corruptMetadata ? null : metadata);
        read.Kind.Should().Be(ReadOutcome.Found);
    }

    [Fact]
    public async Task The_plain_library_path_reads_what_it_writes_and_what_the_cache_reads_and_a_fill_never_replaces_a_hash()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var cache = Hashes(true);
        var store = (IMigrationHashCache)cache;
        var key = $"plain:{RunTag}:{_unique}";
        var client = new LibraryHashPathClient<Item>(store, new FakeConnection(), k => k, TimeSpan.FromSeconds(5)) { Cache = cache, Tier = (ISharedHashTier)cache };
        var fields = new Dictionary<string, Item?> { ["a"] = new("1"), ["b"] = new("2") };

        (await client.ReadAsync(key, null, Ct)).Kind.Should().Be(ReadOutcome.Absent);
        (await client.ContainsAsync(key, Ct)).Should().BeFalse();
        (await client.SetAsync(key, fields, new HashWriteOptions(HashCacheSetOption.KeyReplace, _clock.ToDateTimeOffset(Life)), Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        var read = await client.ReadAsync(key, null, Ct);
        read.Fields!.Keys.Should().BeEquivalentTo("a", "b");
        read.Expiration.Should().BeCloseTo(_clock.ToDateTimeOffset(Life), TimeSpan.FromSeconds(30));
        (await client.ReadAsync(key, ["b"], Ct)).Fields!.Keys.Should().BeEquivalentTo("b");
        (await client.ContainsAsync(key, Ct)).Should().BeTrue();
        (await client.ExpireTimeAsync(key, Ct)).Should().BeCloseTo(_clock.ToDateTimeOffset(Life), TimeSpan.FromSeconds(30));

        (await client.SetAsync(key, new Dictionary<string, Item?> { ["c"] = new("3") }, new HashWriteOptions(HashCacheSetOption.HashReplace, _clock.ToDateTimeOffset(Life)), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await client.RemoveFieldsAsync(key, ["a"], Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await client.ReadAsync(key, null, Ct)).Fields!.Keys.Should().BeEquivalentTo("b", "c");
        (await client.RefreshAsync(key, CacheExpiry.After(TimeSpan.FromHours(2)), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await client.ExpireTimeAsync(key, Ct)).Should().BeCloseTo(_clock.ToDateTimeOffset(TimeSpan.FromHours(2)), TimeSpan.FromSeconds(30));

        var box = new HashFillBox<Item>();
        (await client.FillAsync(key, fields, new HashWriteOptions(HashCacheSetOption.KeyReplace, null), box, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
        box.Lost.Should().BeTrue();
        box.Stored!.Keys.Should().BeEquivalentTo("b", "c");
        (await client.RemoveAsync(key, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await client.FillAsync(key, fields, new HashWriteOptions(HashCacheSetOption.KeyReplace, null), null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await client.ReadAsync(key, null, Ct)).Fields!.Keys.Should().BeEquivalentTo("a", "b");
        (await client.RefreshAsync(key + "none", CacheExpiry.After(TimeSpan.FromHours(2)), Ct)).Outcome.Should().Be(WriteOutcome.NotSent, "a hash that is not there is left as it is, and nothing was applied");
    }

    // After the keys have moved the hash is the library's own, so a refresh does what the library's overload of the same form does: the options form sets the metadata it is given and clears it when it is null, and the expiry-only form keeps it.
    [Theory]
    [InlineData("null", true)]
    [InlineData("empty", true)]
    [InlineData("populated", true)]
    [InlineData("null", false)]
    public async Task In_S5_a_refresh_leaves_the_hash_as_the_librarys_overload_of_the_same_form_does(string kind, bool withOptions)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var app = Hashes(false);
        var (binding, nativePrefix) = await BindingAsync(app, false);
        var path = new LibraryHashPathClient<Item>((IMigrationHashCache)app, new FakeConnection(), AppKey, TimeSpan.FromSeconds(5)) { Cache = app };
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, inPlace: binding.InPlace, native: binding.Native, nativePrefix: nativePrefix, plain: path);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S5", swept: true);
        Dictionary<string, string?>? metadata = null;
        if (kind == "empty")
        {
            metadata = [];
        }
        else if (kind != "null")
        {
            metadata = new Dictionary<string, string?> { ["owner"] = "you" };
        }

        var deadline = _clock.ToDateTimeOffset(TimeSpan.FromHours(2));
        foreach (var key in new[] { "k", "twin" })
        {
            await app.SetAsync(CacheKey.Verbatim(AppKey(key)), new Dictionary<string, Item?> { ["a"] = new("1") }, new HashCacheEntryOptions(TimeToLive: Life, Metadata: new Dictionary<string, string?> { ["owner"] = "me" }), null, Ct);
        }

        var refreshed = withOptions
            ? await kit.Client.RefreshAsync("k", CacheExpiry.At(deadline), metadata, Ct)
            : await kit.Client.RefreshAsync("k", CacheExpiry.At(deadline), Ct);
        if (withOptions)
        {
            await app.RefreshAsync<Item>(CacheKey.Verbatim(AppKey("twin")), new HashCacheEntryOptions(deadline, null, metadata), null, Ct);
        }
        else
        {
            await app.RefreshAsync<Item>(CacheKey.Verbatim(AppKey("twin")), deadline, null, Ct);
        }

        refreshed.Outcome.Should().Be(WriteOutcome.Committed);
        var mine = await app.GetCacheEntryAsync<Item>(CacheKey.Verbatim(AppKey("k")), Ct);
        var theirs = await app.GetCacheEntryAsync<Item>(CacheKey.Verbatim(AppKey("twin")), Ct);
        mine.Expiration.Should().BeCloseTo(deadline, TimeSpan.FromSeconds(2));
        (mine.Metadata ?? new Dictionary<string, string?>()).Should().BeEquivalentTo(theirs.Metadata ?? new Dictionary<string, string?>());
        mine.Value!.Keys.Should().BeEquivalentTo("a");
        if (kind == "populated")
        {
            mine.Metadata.Should().ContainKey("owner").WhoseValue.Should().Be("you");
        }

        if (kind == "null" && withOptions)
        {
            (mine.Metadata?.ContainsKey("owner") ?? false).Should().BeFalse("the options refresh with no metadata clears it, as the library's does");
        }

        if (!withOptions)
        {
            mine.Metadata.Should().ContainKey("owner").WhoseValue.Should().Be("me", "an expiry-only refresh keeps the metadata");
        }
    }

    // The options refresh with no metadata clears what the side holding the hash keeps, atomically with the expiry and as the library's own does, the empty marker included where nulls are cached. Each case compares the stored hash and its life with the library's options refresh of a twin.
    [Theory]
    [InlineData("S1", true, false, true)]
    [InlineData("S1", true, false, false)]
    [InlineData("S1", true, true, true)]
    [InlineData("S1", true, true, false)]
    [InlineData("S3", false, false, true)]
    [InlineData("S3", false, false, false)]
    [InlineData("S3", false, true, true)]
    [InlineData("S3", false, true, false)]
    [InlineData("S4", false, false, true)]
    [InlineData("S4", false, true, false)]
    public async Task An_options_refresh_with_no_metadata_clears_it_where_the_side_that_holds_the_hash_carries_it_as_the_librarys_does(string state, bool inPlaceCarries, bool cacheNulls, bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var app = Hashes(cacheNulls);
        var (binding, nativePrefix) = await BindingAsync(app, cacheNulls);
        IKeyMoveHashCodec<Item> inPlace = inPlaceCarries ? new LibraryKeyMoveHashCodec<Item>((IByteHashCodecCache)Hashes(cacheNulls), 4 * 1024 * 1024, 4096, systemFields: true) : binding.InPlace;
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts, inPlace: inPlace, native: binding.Native, nativePrefix: nativePrefix, nativeNulls: cacheNulls, inPlaceNulls: cacheNulls);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state, nPop: state == "S1" ? 0 : 1);
        var seed = new HashCacheEntryOptions(TimeToLive: Life, Metadata: new Dictionary<string, string?> { ["owner"] = "me" });
        foreach (var key in new[] { "k", "twin" })
        {
            await app.SetAsync(CacheKey.Verbatim(AppKey(key)), new Dictionary<string, Item?> { ["a"] = new("1") }, seed, null, Ct);
        }

        // In S1 the hash is on L, which holds the library's bytes under the key of L.
        var held = state == "S1" ? kit.L("k") : kit.N("k");
        if (state == "S1")
        {
            await Db.HashSetAsync(held, await Db.HashGetAllAsync(nativePrefix + "k"));
            await Db.KeyExpireAsync(held, Life);
            await Db.KeyDeleteAsync(nativePrefix + "k");
        }

        var deadline = _clock.ToDateTimeOffset(TimeSpan.FromHours(2));
        var refreshed = await kit.Client.RefreshAsync("k", CacheExpiry.At(deadline), (IDictionary<string, string?>?)null, Ct);
        await app.RefreshAsync<Item>(CacheKey.Verbatim(AppKey("twin")), new HashCacheEntryOptions(deadline, null, null), null, Ct);

        refreshed.Outcome.Should().Be(WriteOutcome.Committed);
        var mine = await Db.HashGetAllAsync(held);
        var theirs = await Db.HashGetAllAsync(nativePrefix + "twin");
        mine.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString()).Should().BeEquivalentTo(theirs.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString()), "the metadata is cleared, or replaced by the empty marker, as the library's refresh does");
        mine.Select(e => e.Name.ToString()).Should().Contain(cacheNulls ? ["a", "_metadata_"] : ["a"]).And.HaveCount(cacheNulls ? 2 : 1);
        (await Db.KeyTimeToLiveAsync(held))!.Value.Should().BeCloseTo((await Db.KeyTimeToLiveAsync(nativePrefix + "twin"))!.Value, TimeSpan.FromSeconds(5));
        (await Db.KeyTimeToLiveAsync(held))!.Value.Should().BeGreaterThan(TimeSpan.FromHours(1));
    }

    // The expiry-only form keeps the metadata in every state, where the options form with none clears it.
    [Theory]
    [InlineData("S1", true, true)]
    [InlineData("S1", true, false)]
    [InlineData("S3", false, true)]
    [InlineData("S3", false, false)]
    [InlineData("S4", false, true)]
    public async Task An_expiry_only_refresh_keeps_the_metadata_the_hash_holds_in_every_state(string state, bool inPlaceCarries, bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var app = Hashes(false);
        var (binding, nativePrefix) = await BindingAsync(app, false);
        IKeyMoveHashCodec<Item> inPlace = inPlaceCarries ? new LibraryKeyMoveHashCodec<Item>((IByteHashCodecCache)Hashes(false), 4 * 1024 * 1024, 4096, systemFields: true) : binding.InPlace;
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts, inPlace: inPlace, native: binding.Native, nativePrefix: nativePrefix);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state, nPop: state == "S1" ? 0 : 1);
        await app.SetAsync(CacheKey.Verbatim(AppKey("k")), new Dictionary<string, Item?> { ["a"] = new("1") }, new HashCacheEntryOptions(TimeToLive: Life, Metadata: new Dictionary<string, string?> { ["owner"] = "me" }), null, Ct);
        var held = state == "S1" ? kit.L("k") : kit.N("k");
        if (state == "S1")
        {
            await Db.HashSetAsync(held, await Db.HashGetAllAsync(nativePrefix + "k"));
            await Db.KeyExpireAsync(held, Life);
            await Db.KeyDeleteAsync(nativePrefix + "k");
        }

        var before = (await Db.HashGetAllAsync(held)).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        var refreshed = await kit.Client.RefreshAsync("k", CacheExpiry.At(_clock.ToDateTimeOffset(TimeSpan.FromHours(2))), Ct);

        refreshed.Outcome.Should().Be(WriteOutcome.Committed);
        before.Should().ContainKey("_metadata_");
        (await Db.HashGetAllAsync(held)).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString()).Should().BeEquivalentTo(before);
        (await Db.KeyTimeToLiveAsync(held))!.Value.Should().BeGreaterThan(TimeSpan.FromHours(1));
    }

    // An in-place foreign format has no field for metadata, so there is nothing to clear: the options refresh with no metadata is an expiry-only refresh of L.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_options_refresh_with_no_metadata_of_a_foreign_in_place_hash_changes_the_expiry_only(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var app = Hashes(true);
        var (binding, nativePrefix) = await BindingAsync(app, true);
        var foreign = new LibraryKeyMoveHashCodec<Item>((IByteHashCodecCache)Hashes(true), 4 * 1024 * 1024, 4096, systemFields: false);
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts, inPlace: foreign, native: binding.Native, nativePrefix: nativePrefix, nativeNulls: true, inPlaceNulls: true);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S1", nPop: 0);
        await Db.HashSetAsync(kit.L("k"), [new HashEntry("a", "x")]);
        await Db.KeyExpireAsync(kit.L("k"), Life);

        var refreshed = await kit.Client.RefreshAsync("k", CacheExpiry.At(_clock.ToDateTimeOffset(TimeSpan.FromHours(2))), (IDictionary<string, string?>?)null, Ct);

        refreshed.Outcome.Should().Be(WriteOutcome.Committed);
        (await Db.HashGetAllAsync(kit.L("k"))).Select(e => e.Name.ToString()).Should().BeEquivalentTo("a");
        (await Db.KeyTimeToLiveAsync(kit.L("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromHours(1));
    }

    // A metadata-bearing hash written in S1, promoted in S3 and read natively in S5 keeps its metadata, which the library of N reads back.
    [Fact]
    public async Task A_hash_with_metadata_is_written_in_S1_promoted_in_S3_and_read_natively_in_S5_with_its_metadata()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var app = Hashes(false);
        var (binding, nativePrefix) = await BindingAsync(app, false);
        var carrying = new LibraryKeyMoveHashCodec<Item>((IByteHashCodecCache)Hashes(false), 4 * 1024 * 1024, 4096, systemFields: true);
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, inPlace: carrying, native: binding.Native, nativePrefix: nativePrefix);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S1", nPop: 0);
        var metadata = new Dictionary<string, string?> { ["owner"] = "me" };

        var written = await kit.Client.SetAsync("k", new Dictionary<string, Item?> { ["a"] = new("1") }, new HashWriteOptions(HashCacheSetOption.KeyReplace, _clock.ToDateTimeOffset(Life), metadata), Ct);
        (await kit.AllFieldsAsync(kit.L("k"))).Keys.Should().Contain("_metadata_");
        await kit.Kit.SetStateAsync("S3");
        var promoted = await kit.Client.ReadAsync("k", null, Ct);
        await kit.Kit.SetStateAsync("S5", swept: true);
        var native = await kit.Client.ReadAsync("k", null, Ct);

        written.Outcome.Should().Be(WriteOutcome.Committed);
        promoted.Kind.Should().Be(ReadOutcome.Found);
        promoted.Metadata.Should().ContainKey("owner");
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.AllFieldsAsync(kit.N("k"))).Keys.Should().Contain("_metadata_", "the metadata is on N");
        native.Metadata.Should().ContainKey("owner").WhoseValue.Should().Be("me");
        var entry = await app.GetCacheEntryAsync<Item>(CacheKey.Verbatim(AppKey("k")), Ct);
        entry.Metadata.Should().ContainKey("owner").WhoseValue.Should().Be("me");
        entry.Value!.Keys.Should().BeEquivalentTo("a");
    }

    // The configuration decides what an S1 write that carries metadata does: with the older client's envelope L is that client's format and the write is refused with the cache named; without one it is a library format and the metadata is stored.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_S1_write_with_metadata_is_refused_with_an_envelope_and_stored_without_one(bool withEnvelope)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var app = Hashes(false);
        var (_, nativePrefix) = await BindingAsync(app, false);
        var router = new RouterBuilder();
        if (withEnvelope)
        {
            router.UseEnvelope(new SystemJsonByteSerializerProxy());
        }

        var strings = (IByteCodecCache)Provider(false).GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
        var cache = new KeyMoveCache("items", "g", false, false, false, null, "p*", k => k, k => k, "n*", k => k, k => k);
        var binding = new KeyMoveBinder(strings, strings, [cache], inPlaceHash: (IByteHashCodecCache)Hashes(false), nativeHash: (IByteHashCodecCache)app, inPlaceKeepsSystemFields: router.InPlaceKeepsSystemFields).BindHash<Item>("items")!;
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, inPlace: binding.InPlace, native: binding.Native, nativePrefix: nativePrefix);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S1", nPop: 0);
        var options = new HashWriteOptions(HashCacheSetOption.KeyReplace, null, new Dictionary<string, string?> { ["owner"] = "me" });

        var write = async () => await kit.Client.SetAsync("k", new Dictionary<string, Item?> { ["a"] = new("1") }, options, Ct);

        if (withEnvelope)
        {
            (await write.Should().ThrowAsync<NotSupportedException>()).Which.Message.Should().Contain(kit.Kit.Cid);
            (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        }
        else
        {
            (await write()).Outcome.Should().Be(WriteOutcome.Committed);
            (await kit.AllFieldsAsync(kit.L("k"))).Keys.Should().Contain("_metadata_");
        }
    }

    // With the older client's envelope its readers know no system field, so no write of S1 or S2 puts one on L: not a replace, a merge, a fill, a refresh with options or an expiry-only refresh, whether nulls are cached or not.
    [Theory]
    [InlineData("S1", false)]
    [InlineData("S1", true)]
    [InlineData("S2", false)]
    [InlineData("S2", true)]
    public async Task With_an_envelope_no_write_of_S1_or_S2_puts_a_system_field_on_L(string state, bool cacheNulls)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var app = Hashes(cacheNulls);
        var (binding, nativePrefix) = await BindingAsync(app, cacheNulls);
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, inPlace: binding.InPlace, native: binding.Native, nativePrefix: nativePrefix, nativeNulls: cacheNulls, inPlaceNulls: cacheNulls);
        await kit.Kit.PrepareAsync("k", "f", "e");
        await kit.Kit.SetStateAsync(state, nPop: 0);
        var replace = new HashWriteOptions(HashCacheSetOption.KeyReplace, _clock.ToDateTimeOffset(Life));
        var merge = new HashWriteOptions(HashCacheSetOption.HashReplace, _clock.ToDateTimeOffset(Life));
        async Task NoSystemFieldAsync(string key, string step)
        {
            (await kit.AllFieldsAsync(kit.L(key))).Keys.Where(f => f.StartsWith('_')).Should().BeEmpty(step);
        }

        (await kit.Client.SetAsync("k", new Dictionary<string, Item?> { ["a"] = new("1") }, replace, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        await NoSystemFieldAsync("k", "after a replace");
        (await kit.Client.SetAsync("k", new Dictionary<string, Item?> { ["b"] = new("2") }, merge, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        await NoSystemFieldAsync("k", "after a merge");
        (await kit.Client.FillAsync("f", new Dictionary<string, Item?> { ["a"] = new("1") }, replace, null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        await NoSystemFieldAsync("f", "after a fill");
        (await kit.Client.RefreshAsync("k", CacheExpiry.At(_clock.ToDateTimeOffset(TimeSpan.FromHours(2))), (IDictionary<string, string?>?)null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        await NoSystemFieldAsync("k", "after a refresh with options");
        (await kit.Client.RefreshAsync("k", CacheExpiry.At(_clock.ToDateTimeOffset(TimeSpan.FromHours(3))), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        await NoSystemFieldAsync("k", "after an expiry-only refresh");
        (await kit.Client.RemoveFieldsAsync("k", ["a"], Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        await NoSystemFieldAsync("k", "after a field delete");
        (await kit.Client.SetAsync("e", new Dictionary<string, Item?>(), replace, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Kit.ExistsAsync(kit.L("e"))).Should().BeFalse("an empty replace is a delete, with no empty marker on L");
    }

    private static HashEntry[] CorruptHash(IKeyMoveHashCodec<Item> codec, IDictionary<string, Item?> values) =>
        [.. codec.EncodeFields(values, null).Select(field => new HashEntry(field.Key, field.Value)), new("broken", new byte[] { 0x1E, 1, 2, 3 })];

    private static string AppKeyOf(string unique, string key) => $"{unique}:{key}";

    private string AppKey(string key) => AppKeyOf(_unique, key);

    // The binding of the registered cache over the real caches, and the logical text of the native keys the application's cache writes.
    private async Task<(KeyMoveHashBinding<Item> Binding, string NativePrefix)> BindingAsync(IHashCache app, bool cacheNulls)
    {
        var probe = $"probe{_unique}";
        await app.SetAsync(CacheKey.Verbatim(probe), new Dictionary<string, Item?> { ["p"] = new("1") }, Life, null, Ct);
        var physical = _redis!.GetServer(_redis.GetEndPoints()[0]).Keys(pattern: $"*{probe}*").Single().ToString();
        var prefix = physical[..physical.IndexOf(probe, StringComparison.Ordinal)];
        var strings = (IByteCodecCache)Provider(false).GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
        var cache = new KeyMoveCache("items", "g", false, false, false, null, "p*", k => k, k => k, "n*", k => k, k => k);
        var inPlace = (IByteHashCodecCache)Hashes(cacheNulls);
        var binder = new KeyMoveBinder(strings, strings, [cache], inPlaceHash: inPlace, nativeHash: (IByteHashCodecCache)app);
        return (binder.BindHash<Item>("items")!, prefix + _unique + ":");
    }

    private IHashCache Hashes(bool cacheNulls) =>
        Provider(cacheNulls).GetRequiredService<ICacheFactory>().CreateHashCache(KnownCacheProviderNames.Redis);

    private ServiceProvider Provider(bool cacheNulls)
    {
        var provider = new ServiceCollection().AddSingleton<TimeProvider>(_clock)
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = fixture.ConnectionString);
                    b.AddRedis(o =>
                    {
                        o.CacheNullValues = cacheNulls;
                        o.AwaitRefresh = true;
                    });
                },
                o => o.AppShortName = $"khf{_unique}"[..8])
            .BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }
}
