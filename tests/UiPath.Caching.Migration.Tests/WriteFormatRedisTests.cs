using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;
using UiPath.Caching.Negotiation;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Migration.Tests;

/// <summary>The per-group write format over real routers: the stored bytes, the downgrade and a switched group that never reaches the source.</summary>
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class WriteFormatRedisTests(RedisFixture fixture) : IAsyncLifetime
{
    private const string Skip = "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.";
    private static readonly TimeSpan ViewInterval = TimeSpan.FromMilliseconds(411);
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly string _cacheName = $"Wf{Guid.NewGuid():N}"[..12];
    private readonly string _app = $"wf{Guid.NewGuid():N}"[..10];
    private readonly List<ServiceProvider> _nodes = [];
    private readonly HashSet<ServiceProvider> _formatViewsArmed = [];
    private ConnectionMultiplexer? _redis;
    private DateTimeOffset _serverTime;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDatabase Db => _redis!.GetDatabase();

    public async ValueTask InitializeAsync()
    {
        if (fixture.Enabled)
        {
            _redis = await ConnectionMultiplexer.ConnectAsync($"{fixture.ConnectionString},allowAdmin=true");
            _serverTime = await _redis.GetServer(_redis.GetEndPoints()[0]).TimeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var node in _nodes)
        {
            await node.DisposeAsync();
        }

        if (_redis is not null)
        {
            var server = _redis.GetServer(_redis.GetEndPoints()[0]);
            var keys = server.Keys(pattern: $"n:{_cacheName},c:*")
                .Concat(server.Keys(pattern: $"h:{_cacheName},c:*"))
                .Concat(server.Keys(pattern: $"mg:{{{ScopeTokens.For(_app)}:*"))
                .Concat(server.Keys(pattern: $"{_app}:mg:*"));
            foreach (var key in keys)
            {
                await Db.KeyDeleteAsync(key);
            }

            // Only this test's nodes: the hash is shared with the other tests of the run.
            var hash = RouterServiceCollectionExtensions.NodeListKey(new CacheOptions(), new RedisCacheOptions { RedisKeyStrategyFactory = PassThroughKeyStrategyFactory.Instance });
            foreach (var field in await Db.HashKeysAsync(hash))
            {
                if (field.ToString().Contains(_app, StringComparison.Ordinal))
                {
                    await Db.HashDeleteAsync(hash, field);
                }
            }

            await _redis.DisposeAsync();
        }
    }

    [Fact]
    public async Task Plain_hash_writes_fills_and_metadata_refreshes_use_the_selected_group_format()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var format = "messagepack";
        var clock = new ManualTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var node = NewNode("hash", () => true, () => format, clock: clock, includeHash: true);
        var router = (Router)node.GetRequiredService<IRouter>();
        var hash = router.GetHash<List<long>>("hash");
        var value = new List<long> { 1, 2, 3 };
        var fields = new Dictionary<string, List<long>?> { ["field"] = value };
        var source = new LegacyHashSource(_redis!, _cacheName);
        var early = source.RedisKey("early");
        (await hash.SetAsync("early", fields, Lifetime, Ct)).Should().BeTrue();
        ((byte[]?)await Db.HashGetAsync(early, "field")).Should().Equal(LegacyEnvelope.Write(value));

        await UseSwitchedFormatAsync(router, clock);

        var written = source.RedisKey("written");
        (await hash.SetAsync("written", fields, Lifetime, Ct)).Should().BeTrue();
        ((byte[]?)await Db.HashGetAsync(written, "field"))![0].Should().Be(TestFormatSerializer.Id);
        (await hash.GetOrAddAsync("fill", _ => Task.FromResult<IDictionary<string, List<long>?>>(fields), Lifetime, Ct)).Should().BeEquivalentTo(fields);
        ((byte[]?)await Db.HashGetAsync(source.RedisKey("fill"), "field"))![0].Should().Be(TestFormatSerializer.Id);
        var metadata = new Dictionary<string, string?> { ["name"] = "value" };
        (await hash.RefreshAsync("written", new HashCacheEntryOptions(TimeToLive: Lifetime, Metadata: metadata), Ct)).Should().BeTrue();
        ((byte[]?)await Db.HashGetAsync(written, KnownFieldNames.MetadataKey))![0].Should().Be(TestFormatSerializer.Id);

        format = "json";
        (await hash.SetAsync("written", fields, Lifetime, Ct)).Should().BeTrue();
        ((byte[]?)await Db.HashGetAsync(written, "field")).Should().Equal(LegacyEnvelope.Write(value));
    }

    [Fact]
    public async Task Stored_bytes_stay_the_envelope_until_the_switch_then_carry_the_header_and_a_writer_back_to_json_downgrades_at_once()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var format = "messagepack";
        var node = NewNode("a", () => true, () => format);
        var cache = Cache(node);
        var value = new List<long> { 1, 2, 3 };

        await cache.SetAsync("early", value, Lifetime, Ct);

        (await Bytes("early")).Should().Equal(LegacyEnvelope.Write(value), "no node has acknowledged the pin yet, so the gate is closed and the group writes the envelope");
        await RefreshFormatsAsync(node, TimeSpan.FromSeconds(4));
        await cache.SetAsync("k", value, Lifetime, Ct);
        (await Bytes("k"))![0].Should().Be(TestFormatSerializer.Id, "the group is switched once every node reads the format");
        var stored = await Bytes("k");
        stored!.Skip(1).Should().Equal(JsonSerializer.SerializeToUtf8Bytes(value), "the header byte is followed by the serializer's payload");
        (await Db.KeyTimeToLiveAsync(RedisKeyOf("k")))!.Value.Should().BeCloseTo(Lifetime, TimeSpan.FromSeconds(10));
        (await Db.StringGetAsync($"mg:{{{ScopeTokens.For(_app)}:g}}:sw")).ToString().Should().Be("1", "the point of no return is recorded");
        (await cache.GetAsync("k", Ct)).Should().Equal(value, "the header format reads back through the router");
        (await Bytes("early")).Should().Equal(LegacyEnvelope.Write(value), "a value written before the switch is not touched");

        format = "json";
        await cache.SetAsync("down", value, Lifetime, Ct);

        (await Bytes("down")).Should().Equal(LegacyEnvelope.Write(value), "a writer set back to json writes the envelope on the next write");
        (await cache.GetAsync("k", Ct)).Should().Equal(value, "a value already converted still reads");
        (await cache.GetAsync("early", Ct)).Should().Equal(value);
    }

    [Fact]
    public async Task The_key_move_writes_the_in_place_keys_in_the_format_the_group_writes_now()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var clock = new ManualTimeProvider(_serverTime);
        var node = NewNode("a", () => true, () => "messagepack", moveAlert: _ => { }, clock: clock);
        var router = (Router)node.GetRequiredService<IRouter>();
        var parts = router.Core.StartedParts!;
        var binding = parts.Binder!.Bind<List<long>>("lists")!;
        var value = new List<long> { 1, 2, 3 };

        var early = binding.InPlace.Encode(value, out var earlyLease);
        using (earlyLease)
        {
            await Db.StringSetAsync(binding.InPlaceKey("early"), early);
        }

        (await Bytes("early")).Should().Equal(LegacyEnvelope.Write(value));

        await UseSwitchedFormatAsync(router, clock);
        var switched = binding.InPlace.Encode(value, out var switchedLease);
        using (switchedLease)
        {
            await Db.StringSetAsync(binding.InPlaceKey("switched"), switched);
        }

        var stored = await Bytes("switched");
        stored![0].Should().Be(TestFormatSerializer.Id);
        binding.InPlace.Decode(stored).Value.Should().Equal(value);
        binding.InPlace.Decode(early).Value.Should().Equal(value);
    }

    [Fact]
    public async Task A_reverse_after_the_switch_writes_the_in_place_keys_in_the_header_format()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero));
        var node = NewNode("a", () => true, () => "messagepack", moveAlert: _ => { }, clock: clock);
        var router = (Router)node.GetRequiredService<IRouter>();
        var inPlace = router.Core.StartedParts!.Binder!.Bind<Item>("lists")!.InPlace;
        await UseSwitchedFormatAsync(router, clock);

        await using var kit = await KeyMove.KeyMoveKit.CreateAsync(fixture, clock: clock, inPlace: inPlace);
        await kit.PrepareAsync("promoted", "written");
        await kit.SetStateAsync("S3", direction: "rev");
        await kit.Db.StringSetAsync(kit.N("promoted"), "v1");
        await kit.Db.StringSetAsync(kit.N("written"), "old");

        (await kit.Client.ReadAsync("promoted", Ct)).Value!.Name.Should().Be("v1");
        (await kit.Client.SetAsync("written", new Item("v2"), null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        foreach (var (key, expected) in new[] { ("promoted", "v1"), ("written", "v2") })
        {
            var stored = (byte[]?)await kit.Db.StringGetAsync(kit.L(key));
            stored.Should().NotBeNull(key);
            stored[0].Should().Be(TestFormatSerializer.Id, $"the reverse writes {key} in the format the group switched to");
            inPlace.Decode(stored).Value!.Name.Should().Be(expected, $"{key} decodes through the router-bound codec");
            (await kit.ExistsAsync(kit.N(key))).Should().BeFalse(key);
        }
    }

    [Fact]
    public async Task A_pinned_node_that_asks_for_messagepack_during_a_move_switches_once_the_move_is_over_without_a_restart()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var alerts = new ConcurrentQueue<KeyMoveAlert>();
        var node = NewNode("a", () => true, () => "messagepack", moveAlert: alerts.Enqueue);
        var router = (Router)node.GetRequiredService<IRouter>();
        var cache = Cache(node);
        var routed = (RoutedCache<List<long>>)cache;
        await RouterTestBase.WaitForLibraryAsync(router, Ct);
        routed.Library.Should().BeOfType<KeyMoveClient<List<long>>>();
        var move = router.KeyMove!;
        var reader = new MoveStateReader();
        var deferred = (DeferredStateReader)typeof(KeyMoveNode).GetField("_reader", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(move)!;
        deferred.Attach(reader);
        await move.Monitor.StepAsync("g", Ct);
        move.Monitor.RulesFor("g").Acting!.State.Should().Be("S3");

        var pin = router.Core.PinFor("g")!;
        router.Core.Pins!.Want(pin, true);
        await router.Core.Pins.RefreshAsync(Ct);
        pin.Wanted.Should().BeFalse();

        // Managed writes use their move codecs. The router's bound plain path exercises the write format selector and stores real bytes.
        var plain = ((KeyMoveClient<List<long>>)routed.Library).Parts.Plain;
        var value = new List<long> { 1, 2, 3 };

        var clock = (SignalingTimeProvider)router.Clock;
        for (var pass = 0; pass < 3; pass++)
        {
            (await plain.SetAsync("k", value, clock.ToDateTimeOffset(Lifetime), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
            (await Bytes("k")).Should().Equal(LegacyEnvelope.Write(value), "the group's keys move, so it keeps its format");
            await RefreshFormatsAsync(node, TimeSpan.FromSeconds(4));
        }

        (await Db.KeyExistsAsync($"mg:{{{ScopeTokens.For(_app)}:g}}:sw")).Should().BeFalse("the switch was never asked for");
        alerts.Where(alert => alert.Code == "format_switch_held").Should().ContainSingle().Which.Group.Should().Be("g");

        reader.EndMove();
        await move.Monitor.StepAsync("g", Ct);
        move.Monitor.RulesFor("g").Acting!.State.Should().Be("S0");
        clock.Advance(TimeSpan.FromMilliseconds(100));
        (await cache.SetAsync("k", value, Lifetime, Ct)).Should().BeTrue();
        await RefreshFormatsAsync(node, TimeSpan.FromSeconds(4));
        (await cache.SetAsync("k", value, Lifetime, Ct)).Should().BeTrue();
        (await Bytes("k"))![0].Should().Be(TestFormatSerializer.Id, "the move is over, so the same node switches");
        (await cache.GetAsync("k", Ct)).Should().Equal(value);
    }

    [Fact]
    public async Task A_group_that_asks_for_json_never_writes_the_header_format_even_when_it_is_pinned()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var node = NewNode("a", () => true, () => null);
        var cache = Cache(node);
        var value = new List<long> { 4 };

        for (var i = 0; i < 6; i++)
        {
            await cache.SetAsync($"k{i}", value, Lifetime, Ct);
            await RefreshFormatsAsync(node, TimeSpan.FromMilliseconds(500));
        }

        for (var i = 0; i < 6; i++)
        {
            (await Bytes($"k{i}")).Should().Equal(LegacyEnvelope.Write(value));
        }

        (await Db.KeyExistsAsync($"mg:{{{ScopeTokens.For(_app)}:g}}:sw")).Should().BeFalse("nothing asked for MessagePack, so no switch was recorded");
    }

    [Fact]
    public async Task After_the_switch_a_restart_a_throwing_setting_and_a_library_that_cannot_start_never_reach_the_source_and_never_miss()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var first = NewNode("a", () => true, () => "messagepack");
        var cache = Cache(first);
        var value = new List<long> { 7, 8 };
        await cache.SetAsync("k", value, Lifetime, Ct);
        await RefreshFormatsAsync(first, TimeSpan.FromSeconds(4));
        await cache.SetAsync("k", value, Lifetime, Ct);
        (await Bytes("k"))![0].Should().Be(TestFormatSerializer.Id);
        _nodes.Remove(first);
        await first.DisposeAsync();

        var restarted = new CountingSource(new LegacySource(_redis!, _cacheName));
        var afterRestart = Cache(NewNode("b", () => false, () => null, () => false, source: restarted));
        (await afterRestart.GetAsync("k", Ct)).Should().Equal(value, "the switch is durable: the pin setting is gone and the group predicate says source, and the library still answers");

        var throwing = new CountingSource(new LegacySource(_redis!, _cacheName));
        var afterThrow = Cache(NewNode("c", () => throw new InvalidOperationException("no setting"), () => throw new InvalidOperationException("no setting"), () => throw new InvalidOperationException("no setting"), source: throwing));
        (await afterThrow.GetAsync("k", Ct)).Should().Equal(value);
        await afterThrow.SetAsync("written", value, Lifetime, Ct);
        (await Bytes("written")).Should().Equal(LegacyEnvelope.Write(value), "a write setting that throws writes the envelope");

        var down = new CountingSource(new LegacySource(_redis!, _cacheName));
        var noLibrary = Cache(NewNode("d", () => false, () => null, () => false, deadLibrary: true, source: down));
        var factoryRuns = 0;
        Task<List<long>?> Factory(CancellationToken token)
        {
            factoryRuns++;
            return Task.FromResult<List<long>?>([]);
        }

        var read = async () => await noLibrary.GetOrAddAsync("k", Factory, Ct);

        await read.Should().ThrowAsync<CacheUnavailableException>();
        (await noLibrary.SetAsync("lost", value, Lifetime, Ct)).Should().BeFalse();
        factoryRuns.Should().Be(0, "a switched group never runs the origin");
        (await Db.KeyExistsAsync(RedisKeyOf("lost"))).Should().BeFalse();
        restarted.Calls.Should().Be(0);
        throwing.Calls.Should().Be(0);
        down.Calls.Should().Be(0);
    }

    private static ICache<List<long>> Cache(ServiceProvider node) => node.GetRequiredService<IRouter>().Get<List<long>>("lists");

    private async Task RefreshFormatsAsync(ServiceProvider node, TimeSpan by)
    {
        var router = (Router)node.GetRequiredService<IRouter>();
        await RouterTestBase.WaitForLibraryAsync(router, Ct);
        var clock = (SignalingTimeProvider)router.Clock;
        var earliest = ViewInterval * 0.8;
        var latest = ViewInterval * 1.2;
        bool IsViewTimer(TimeSpan delay) => delay >= earliest && delay <= latest;

        (await router.Advertiser!.WriteAsync(Ct)).Should().BeTrue();
        await router.Core.Pins!.RefreshAsync(Ct);
        if (_formatViewsArmed.Add(node))
        {
            await clock.WaitForTimerAsync(IsViewTimer, Ct);
        }

        clock.Advance(latest);
        await clock.WaitForTimerAsync(IsViewTimer, Ct);
        clock.Advance(by);
        await clock.WaitForTimerAsync(IsViewTimer, Ct);
    }

    private async Task UseSwitchedFormatAsync(Router router, ManualTimeProvider clock)
    {
        var pin = router.Core.PinFor("g")!;
        var pins = router.Core.Pins!;
        await Db.StringSetAsync($"mg:{{{ScopeTokens.For(_app)}:g}}:sw", "1");
        pins.Want(pin, true);
        (await router.Advertiser!.WriteAsync(Ct)).Should().BeTrue();
        await pins.RefreshAsync(Ct);
        pins.HoldFor(pin).Should().BeNull();

        var negotiation = router.Core.StartedParts!.Started.OfType<FormatNegotiationService>().Single();
        await negotiation.TickAsync(Ct);
        clock.Advance(TimeSpan.FromSeconds(4));
        await negotiation.TickAsync(Ct);
    }

    private async Task<byte[]?> Bytes(string key) => (byte[]?)await Db.StringGetAsync(RedisKeyOf(key));

    private string RedisKeyOf(string key) => $"n:{_cacheName},c:{key}";

    private ServiceProvider NewNode(string name, Func<bool> pinned, Func<string?> format, Func<bool>? useLibrary = null, bool deadLibrary = false, ISourceCache<List<long>>? source = null, Action<KeyMoveAlert>? moveAlert = null, TimeProvider? clock = null, bool includeHash = false)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Caching:AppShortName"] = _app,
            ["Caching:SourceUri"] = $"urn:router-{name}-{_app}",
            ["Caching:DefaultCache"] = KnownCacheProviderNames.Redis,
            ["Caching:Connections:Redis:ConnectionString"] = deadLibrary ? "127.0.0.1:1" : $"{fixture.ConnectionString},name=wf{name}",
            ["Caching:Connections:Redis:WarmUpOnStart"] = "false",
            ["Caching:Connections:Redis:PlannedMaintenanceEnabled"] = "false",
            ["Caching:Redis:FormatNegotiation:AdvertiseInterval"] = "00:00:01",
            ["Caching:Redis:FormatNegotiation:ViewInterval"] = ViewInterval.ToString("c"),
            ["Caching:Redis:FormatNegotiation:LivenessWindow"] = "00:00:02",
            ["Caching:Redis:FormatNegotiation:UpgradeHold"] = "00:00:04",
            ["Caching:Redis:FormatNegotiation:MaxViewAge"] = "00:00:20",
        }).Build();
        var legacy = source ?? new LegacySource(_redis!, _cacheName);
        var redisKey = new LegacySource(_redis!, _cacheName).RedisKey;
        var services = new ServiceCollection().AddLogging().AddSingleton<TimeProvider>(clock ?? new SignalingTimeProvider(_serverTime));

        services.AddCaching(configuration, builder =>
        {
            builder.AddRedisConnection();
            if (moveAlert is not null)
            {
                builder.AddRedis();
            }
        });
        services.AddCachingRouter(
            configuration,
            router =>
            {
                router
                    .Add(new RouterCacheRegistration<List<long>>
                    {
                        Name = "lists",
                        Group = "g",
                        Source = legacy,
                        RedisKey = redisKey,
                        LocalCap = TimeSpan.Zero,
                        DefaultExpiry = Lifetime,
                    })
                    .UseLibraryWhen(_ => (useLibrary ?? (() => true))())
                    .UsePinWhen(_ => pinned())
                    .UseEnvelope(new LegacyEnvelopeProxy())
                    .UseValueSerializers(stack => stack.AddValueSerializer("messagepack", [TestFormatSerializer.Id], _ => new TestFormatSerializer()))
                    .UseWriteFormatWhen(_ => format());
                if (includeHash)
                {
                    var hash = new LegacyHashSource(_redis!, _cacheName);
                    router.AddHash(new RouterHashCacheRegistration<List<long>> { Name = "hash", Group = "g", Source = hash, RedisKey = hash.RedisKey });
                }

                if (deadLibrary)
                {
                    router.UseSourceConnection(() => Db);
                }

                if (moveAlert is not null)
                {
                    RegisterMove(router, moveAlert);
                }
            });
        var provider = services.BuildServiceProvider();
        _nodes.Add(provider);
        _ = provider.GetRequiredService<IRouter>();
        return provider;
    }

    private void RegisterMove(RouterBuilder router, Action<KeyMoveAlert> alert)
    {
        var inPlace = RedisKeyOf(string.Empty);
        var native = $"{_app}:native:";
        router.UseKeyMove(new FixedStage(), moves => moves.UseLibraryKeyPrefix($"{_app}:").AddGroup("g", listed: true).OnAlert(alert)
            .AddCache(new KeyMoveCacheRegistration
            {
                Name = "lists",
                Group = "g",
                InPlacePattern = inPlace + "*",
                ParseInPlaceKey = key => key.StartsWith(inPlace, StringComparison.Ordinal) ? key[inPlace.Length..] : null,
                NativePattern = native + "*",
                FormatNativeKey = key => native + key,
                ParseNativeKey = key => key.StartsWith(native, StringComparison.Ordinal) ? key[native.Length..] : null,
            }));
    }

    private sealed class FixedStage : IMigrationStageSource
    {
        public ValueTask<int?> GetStageAsync(string group, CancellationToken cancellationToken) => ValueTask.FromResult<int?>(2);
    }

    private sealed class MoveStateReader : IStateReader
    {
        private StateRecord _state = new("S3", "fwd", 1, "moving", true, 1, string.Empty, 7, "lists", string.Empty, "moving", string.Empty, string.Empty, string.Empty);

        public void EndMove() => Volatile.Write(ref _state, _state with { State = "S0", Ep = 2, Id = "ended", AllAck = "ended" });

        public Task<StateRecord?> ReadAsync(string group, CancellationToken cancellationToken) => Task.FromResult<StateRecord?>(Volatile.Read(ref _state));

        public Task<ControlSample?> ProbeAsync(string group, CancellationToken cancellationToken) => Task.FromResult<ControlSample?>(null);

        public Task<ControlSample?> SampleAsync(string group, CancellationToken cancellationToken) => Task.FromResult<ControlSample?>(null);
    }
}
