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
    private const string RedisFixtureReason = "Set RUN_REDIS_INTEGRATION_TESTS=1 to run.";
    private const string Library = "kmk:";
    private const string AppPrefix = Library + RedisKeyspaces.String + ":";

    private static readonly TimeSpan Life = TimeSpan.FromMinutes(5);
    private static readonly string RunTag = Guid.NewGuid().ToString("N")[..8];

    private readonly string _unique = Guid.NewGuid().ToString("N")[..10];
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<(RedisKey Node, string Field)> _entries = [];
    private ConnectionMultiplexer? _redis;
    private ManualTimeProvider _clock = null!;

    public enum InPlace
    {
        Envelope,
        MessagePack,
        NoEnvelope,
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDatabase Db => _redis!.GetDatabase();

    private string Group => $"kc{_unique}";

    private string StateKey => $"{Library}mg:{{{Group}}}:state";

    private string ReportKey => $"{Library}mg:{{{Group}}}:dry";

    private string InPlacePrefix => $"kcl{RunTag}{_unique}:";

    public async ValueTask InitializeAsync()
    {
        if (fixture.Enabled)
        {
            _redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString + ",allowAdmin=true");
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
        (await app.AsMigrationCache().ReadSnapshotAsync<Item>(CacheKey.Verbatim(AppKey("k")), null, Ct)).Entry!.Expiration.Should().BeCloseTo(_clock.ToDateTimeOffset(Life), TimeSpan.FromSeconds(15), "the expiry comes from the source's px");
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
    public async Task UseKeyMove_runs_the_mover_after_the_key_move_node_on_the_one_rate_of_the_process()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var (provider, _, _) = await HostAsync(InPlace.Envelope);
        var started = ((Router)provider.GetRequiredService<IRouter>()).Library!.Started.ToList();

        started.OfType<MoverHostedService>().Should().ContainSingle();
        started.FindIndex(s => s is MoverHostedService).Should().BeGreaterThan(started.FindIndex(s => s is KeyMoveHostedService), "the router stops what it started in reverse, so the movers stop before the node");
        provider.GetServices<IMoveRate>().Should().ContainSingle().Which.Should().BeOfType<AimdMoveRate>();
        provider.GetRequiredService<IScanUnits>().Should().BeOfType<MultiplexerScanUnits>();
        provider.GetServices<IValidateOptions<KeyMoveMoverOptions>>().Should().ContainSingle(v => v is KeyMoveMoverOptionsValidator);
    }

    [Fact]
    public async Task A_bound_cache_gives_the_mover_its_target_with_the_supersession_handler_of_its_client()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var (provider, _, client) = await HostAsync(InPlace.Envelope);
        var movers = ((Router)provider.GetRequiredService<IRouter>()).Library!.Movers!;

        var target = movers.Of(Group).Should().ContainKey("items").WhoseValue.Should().BeOfType<KeyMover<Item>>().Subject;
        target.Supersession.Should().BeSameAs(client.Supersession);
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

    [Fact]
    public async Task A_dry_run_stores_its_stamp_and_report_and_a_reverse_run_stores_only_its_report()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var (provider, _, _) = await HostAsync(InPlace.Envelope, rate: new Unpaced());
        var router = (Router)provider.GetRequiredService<IRouter>();
        (await router.KeyMove!.Runtime!.Commands.InitAsync(Group, ["items"], Ct)).Should().NotBeNull();
        foreach (var key in new[] { "k1", "k2", "k3" })
        {
            await Db.StringSetAsync(L(key), OldClientBytes(InPlace.Envelope, new Item(key)), TimeSpan.FromDays(1));
        }

        var started = router.Clock.GetUtcNow();
        var report = await router.DryRunAsync(Group, cancellationToken: Ct);
        var ended = router.Clock.GetUtcNow();

        var stamp = DryRunStamp.TryParse((await Db.HashGetAsync(StateKey, StateFields.DryRun)).ToString());
        stamp.Should().NotBeNull();
        stamp.EstimatedBytes.Should().Be(report.EstimatedNativeBytes).And.BeGreaterThan(0);
        stamp.Summary.Should().Be(report.Summary()).And.Contain("movable=3");
        stamp.At.Should().BeOnOrAfter(DateTimeOffset.FromUnixTimeMilliseconds(started.ToUnixTimeMilliseconds())).And.BeOnOrBefore(ended);
        var stored = await Db.HashGetAllAsync(ReportKey);
        stored.Select(e => e.Name.ToString()).Should().Contain(["units", "scanned", "estimated_native_bytes", "items:movable", "items:size:0", "items:sampled"]);
        stored.Single(e => e.Name == "items:movable").Value.ToString().Should().Be("3");
        (await Db.KeyTimeToLiveAsync(ReportKey)).Should().BeCloseTo(new StageDriverOptions().DryRunMaxAge, TimeSpan.FromMinutes(1));

        await Db.StringSetAsync(N("n1"), "x", Life);
        var reverse = await router.DryRunAsync(Group, "rev", Ct);

        reverse.Caches["items"].Movable.Should().Be(1);
        var reverseFields = (await Db.HashGetAllAsync(ReportKey + ":rev")).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        reverseFields.Should().Contain("items:movable", "1");
        reverseFields.Should().NotContainKeys("estimated_native_bytes", "estimated_duration_ms");
        (await Db.HashGetAsync(StateKey, StateFields.DryRun)).ToString().Should().Be(stamp.Encode());
    }

    [Fact]
    public async Task A_dry_run_of_a_group_without_state_or_of_an_unknown_group_is_refused()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var (provider, _, _) = await HostAsync(InPlace.Envelope, rate: new Unpaced());
        var router = (Router)provider.GetRequiredService<IRouter>();

        var noState = () => router.DryRunAsync(Group, cancellationToken: Ct);
        var unknown = () => router.DryRunAsync("nobody", cancellationToken: Ct);
        var direction = () => router.DryRunAsync(Group, "sideways", Ct);

        (await noState.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Init it first*");
        await unknown.Should().ThrowAsync<ArgumentException>();
        await direction.Should().ThrowAsync<ArgumentException>();
        (await Db.KeyExistsAsync(ReportKey)).Should().BeFalse();
    }

    [Fact]
    public async Task A_missing_store_fails_before_starting_the_scan()
    {
        Assert.SkipUnless(fixture.Enabled, RedisFixtureReason);
        var (provider, _, _) = await HostAsync(
            InPlace.Envelope,
            rate: new Unpaced(),
            configureServices: services => services.AddSingleton<IScanUnits, UnexpectedScan>());
        var router = (Router)provider.GetRequiredService<IRouter>();
        await router.KeyMove!.StopAsync();

        var run = () => router.DryRunAsync(Group, cancellationToken: Ct);

        (await run.Should().ThrowAsync<InvalidOperationException>()).WithMessage("The key move node is not running.");
    }

    [Fact]
    public async Task Stopping_the_router_cancels_a_dry_run_waiting_to_scan()
    {
        Assert.SkipUnless(fixture.Enabled, RedisFixtureReason);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rate = new Unpaced(async token =>
        {
            reached.TrySetResult();
            await new TaskCompletionSource().Task.WaitAsync(token);
            return null;
        });
        var router = await ReadyDryRunRouterAsync(rate);
        using var runStop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = router.DryRunAsync(Group, cancellationToken: runStop.Token);
        try
        {
            await reached.Task.WaitAsync(Ct);
            await router.StopAsync(Ct);

            var result = () => run.WaitAsync(Ct);
            await result.Should().ThrowAsync<OperationCanceledException>();
            (await Db.KeyExistsAsync(ReportKey)).Should().BeFalse();
        }
        finally
        {
            await runStop.CancelAsync();
        }
    }

    [Fact]
    public async Task A_state_id_changed_during_the_scan_refuses_the_report_and_stamp()
    {
        Assert.SkipUnless(fixture.Enabled, RedisFixtureReason);
        var replacement = MoveScripts.NewNonce();
        var router = await ReadyDryRunRouterAsync(new Unpaced(async _ =>
        {
            await Db.HashSetAsync(StateKey, StateFields.Id, replacement);
            return null;
        }));
        await Db.StringSetAsync(L("key"), OldClientBytes(InPlace.Envelope, new Item("value")), Life);

        var run = () => router.DryRunAsync(Group, cancellationToken: Ct);

        (await run.Should().ThrowAsync<InvalidOperationException>()).WithMessage("The group's key move state changed during the dry run.");
        (await Db.KeyExistsAsync(ReportKey)).Should().BeFalse();
        (await Db.HashExistsAsync(StateKey, StateFields.DryRun)).Should().BeFalse();
    }

    [Fact]
    public async Task An_unavailable_key_still_stores_a_report_without_a_stamp()
    {
        Assert.SkipUnless(fixture.Enabled, RedisFixtureReason);
        var router = await ReadyDryRunRouterAsync(new Unpaced());
        router.Library!.Movers!.Add(Group, new UnavailableTarget());
        await Db.StringSetAsync(L("key"), "value", Life);

        var report = await router.DryRunAsync(Group, cancellationToken: Ct);

        report.Caches["items"].Counts.Should().Contain(DryRunClasses.Unavailable, 1);
        (await Db.HashGetAsync(ReportKey, "items:unavailable")).ToString().Should().Be("1");
        (await Db.HashExistsAsync(StateKey, StateFields.DryRun)).Should().BeFalse();
    }

    [Fact]
    public async Task A_dry_run_uses_the_configured_starting_rate_in_its_estimate()
    {
        Assert.SkipUnless(fixture.Enabled, RedisFixtureReason);
        var router = await ReadyDryRunRouterAsync(new Unpaced(), services => services.Configure<KeyMoveMoverOptions>(options => options.StartKeysPerSecond = 2));
        await Db.StringSetAsync(L("key"), OldClientBytes(InPlace.Envelope, new Item("value")), TimeSpan.FromDays(1));

        var report = await router.DryRunAsync(Group, cancellationToken: Ct);

        report.EstimatedDuration.Should().Be(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task The_operations_of_UseKeyMove_initialize_hold_approve_and_dry_run_a_group_and_read_its_status()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var (provider, _, _) = await HostAsync(InPlace.Envelope, rate: new Unpaced());
        var operations = provider.GetRequiredService<IKeyMoveOperations>();

        (await operations.GetStatusAsync(Group, Ct)).Should().BeNull();
        (await operations.HoldAsync(Group, "maintenance", Ct)).Should().BeFalse("there is no state to hold");
        (await operations.ApproveAsync(Group, KeyMoveApprovalKinds.Start, "release floor attested", "op", Ct)).Should().Be(KeyMoveApproval.NoState);
        (await operations.InitializeAsync(Group, Ct)).Should().BeTrue();
        (await operations.InitializeAsync(Group, Ct)).Should().BeTrue("a state that has not started takes the registered caches again");

        (await operations.HoldAsync(Group, "maintenance", Ct)).Should().BeTrue();
        (await operations.ApproveAsync(Group, KeyMoveApprovalKinds.Start, "release floor attested", "op", Ct)).Should().Be(KeyMoveApproval.Stored);
        (await operations.ApproveAsync(Group, "invented", "x", "op", Ct)).Should().Be(KeyMoveApproval.UnknownKind);
        var held = await operations.GetStatusAsync(Group, Ct);
        held.Should().Match<KeyMoveStatus>(s => s.Group == Group && s.Stage == KeyMoveStage.NotStarted && !s.Reverse && s.OperatorHold == "maintenance");
        held.Approvals.Should().Equal(KeyMoveApprovalKinds.Start);
        (await Db.HashGetAsync($"{Library}mg:{{{Group}}}:state", StateFields.Caches)).ToString().Should().Be("items");

        await Db.StringSetAsync(L("k1"), OldClientBytes(InPlace.Envelope, new Item("k1")), TimeSpan.FromDays(1));
        var report = await operations.RunDryRunAsync(Group, cancellationToken: Ct);
        report.Scanned.Should().BeGreaterThanOrEqualTo(1);
        report.EstimatedNativeBytes.Should().BeGreaterThan(0);
        report.Caches["items"].Counts[DryRunClasses.Movable].Should().Be(1);
        report.Caches["items"].SizeBuckets.Should().HaveCount(KeyMoveDryRunCache.SizeBucketLimits.Count + 1).And.Contain(1);
        (await operations.RunDryRunAsync(Group, reverse: true, Ct)).Caches.Should().NotContainKey("items", "no native key is there to count");
        // The hold kept the group from starting while it was dry run; the resume lets it go on.
        (await operations.ResumeAsync(Group, Ct)).Should().BeTrue();
        (await operations.GetStatusAsync(Group, Ct))!.OperatorHold.Should().BeNull();
        var unknown = () => operations.GetStatusAsync("nobody", Ct);
        await unknown.Should().ThrowAsync<ArgumentException>();
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

    private async Task<Router> ReadyDryRunRouterAsync(IMoveRate rate, Action<IServiceCollection>? configureServices = null)
    {
        var (provider, _, _) = await HostAsync(InPlace.Envelope, rate: rate, configureServices: configureServices);
        var router = (Router)provider.GetRequiredService<IRouter>();
        (await router.KeyMove!.Runtime!.Commands.InitAsync(Group, ["items"], Ct)).Should().NotBeNull();
        return router;
    }

    // What LibraryPathClient does for a set: the router stack's own cache writes the key verbatim with the default policy.
    private async Task WriteOnLibraryPathAsync(ServiceProvider provider, string key, Item value)
    {
        var routerCache = provider.GetRequiredKeyedService<ICacheFactory>(RouterServiceCollectionExtensions.StackName).CreateCache(KnownCacheProviderNames.Redis).AsMigrationCache();
        (await routerCache.WriteAsync(CacheKey.Verbatim(L(key)), value, _clock.ToDateTimeOffset(Life), null, Ct)).Should().Be(StoreOutcome.Committed);
    }

    private string L(string key) => InPlacePrefix + key;

    private string N(string key) => AppPrefix + AppKey(key);

    private string AppKey(string key) => $"{_unique}:N:{key}";

    private async Task<(ServiceProvider Provider, RoutedCache<Item> Cache, KeyMoveClient<Item> Client)> HostAsync(InPlace inPlace, bool messagePackDefault = false, bool supplied = false, bool cacheNulls = false, IMoveRate? rate = null, Action<IServiceCollection>? configureServices = null)
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
        var services = new ServiceCollection().AddLogging().AddSingleton<TimeProvider>(_clock);
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

        if (rate is not null)
        {
            services.AddSingleton(rate);
        }

        var nativePrefix = N(string.Empty);
        var moveRegistration = new KeyMoveCacheRegistration
        {
            Name = registration.Name,
            Group = Group,
            InPlacePattern = $"{InPlacePrefix}*",
            ParseInPlaceKey = key => key.StartsWith(InPlacePrefix, StringComparison.Ordinal) ? key[InPlacePrefix.Length..] : null,
            NativePattern = $"{nativePrefix}*",
            FormatNativeKey = N,
            ParseNativeKey = key => key.StartsWith(nativePrefix, StringComparison.Ordinal) ? key[nativePrefix.Length..] : null,
        };
        configureServices?.Invoke(services);
        services.AddCachingRouter(
            configuration,
            router =>
            {
                router.MoverOptions = new MoverOptions { MinScanInterval = TimeSpan.Zero };
                WithEnvelope(router.UseLibraryWhen(_ => true), inPlace)
                    .UseNodeIdentity($"kc{_unique}{_providers.Count}")
                    .UseSourceConnection(() => Db)
                    .Add(registration)
                    .UseKeyMove(new Stage(), moves => moves
                        .UseLibraryKeyPrefix(Library)
                        .AddGroup(Group)
                        .AddCache(moveRegistration));
            },
            "RouterCaching");
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        var router = (Router)provider.GetRequiredService<IRouter>();
        var cache = (RoutedCache<Item>)router.Get<Item>("items");
        if (cache.Library is not KeyMoveClient<Item>)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnMode(RouterMode mode, RouterModeReason _)
            {
                if (mode == RouterMode.Library)
                {
                    ready.TrySetResult();
                }
            }


            router.ModeChanged += OnMode;
            try
            {
                if (cache.Library is not KeyMoveClient<Item>)
                {
                    await ready.Task.WaitAsync(Ct);
                }
            }
            finally
            {
                router.ModeChanged -= OnMode;
            }
        }

        var client = cache.Library as KeyMoveClient<Item> ?? throw new InvalidOperationException("The router never bound the key move client.");
        var cacheOptions = provider.GetRequiredKeyedService<IOptions<CacheOptions>>(RouterServiceCollectionExtensions.StackName).Value;
        var redisOptions = provider.GetRequiredKeyedService<IOptions<RedisCacheOptions>>(RouterServiceCollectionExtensions.StackName).Value;
        _entries.Add((RouterServiceCollectionExtensions.NodeListKey(cacheOptions, redisOptions), router.Advertiser!.Field));
        return (provider, cache, client);
    }

    // The real rate pauses on the health of the test server; a dry run only needs it to let the scan through.
    private sealed class UnexpectedScan : IScanUnits
    {
        public Task<IReadOnlyList<ScanUnit>> ListAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The dry run tried to list the scan units.");
    }

    private sealed class UnavailableTarget : IMoveTarget
    {
        public string CacheId => "items";

        public bool NoOrigin => false;

        public ValueTask<DryRunProbe> ProbeAsync(string key, TimeSpan minTtl, bool decode, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new DryRunProbe(DryRunClasses.Unavailable, null, null));

        public ValueTask<MoveResult> MoveAsync(string key, TimeSpan minTtl, string identity, IMoveOwnership ownership, Action<long, long>? observeSourceTtl, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class Unpaced(Func<CancellationToken, ValueTask<string?>>? checkPause = null) : IMoveRate
    {
        public ValueTask<string?> PauseReasonAsync(string unit, CancellationToken cancellationToken) =>
            checkPause is null ? ValueTask.FromResult<string?>(null) : checkPause(cancellationToken);

        public TimeSpan Reserve(string unit, int keys, long bytes) => TimeSpan.Zero;

        public void Owe(string unit, long bytes)
        {
        }

        public long TakeDebt(string unit) => 0;

        public void Observed(string unit, MoveResult result, TimeSpan elapsed)
        {
        }
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
