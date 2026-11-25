using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Migration.Tests.Support;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Migration.Tests;

/// <summary>The per-group write format over real routers: the stored bytes, the downgrade and a switched group that never reaches the source.</summary>
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class WriteFormatRedisTests(RedisFixture fixture) : IAsyncLifetime
{
    private const string Skip = "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly string _cacheName = $"Wf{Guid.NewGuid():N}"[..12];
    private readonly string _app = $"wf{Guid.NewGuid():N}"[..10];
    private readonly List<ServiceProvider> _nodes = [];
    private ConnectionMultiplexer? _redis;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDatabase Db => _redis!.GetDatabase();

    public async ValueTask InitializeAsync()
    {
        if (fixture.Enabled)
        {
            _redis = await ConnectionMultiplexer.ConnectAsync($"{fixture.ConnectionString},allowAdmin=true");
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
            foreach (var key in server.Keys(pattern: $"n:{_cacheName},c:*").Concat(server.Keys(pattern: $"mg:{{{ScopeTokens.For(_app)}:*")))
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
    public async Task Stored_bytes_stay_the_envelope_until_the_switch_then_carry_the_header_and_a_writer_back_to_json_downgrades_at_once()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var format = "messagepack";
        var cache = Cache(NewNode("a", () => true, () => format));
        var value = new List<long> { 1, 2, 3 };

        await cache.SetAsync("early", value, Lifetime, Ct);

        (await Bytes("early")).Should().Equal(LegacyEnvelope.Write(value), "no node has acknowledged the pin yet, so the gate is closed and the group writes the envelope");
        (await UntilAsync(async () =>
        {
            await cache.SetAsync("k", value, Lifetime, Ct);
            return (await Bytes("k")) is { Length: > 0 } bytes && bytes[0] == TestFormatSerializer.Id;
        })).Should().BeTrue("the group is switched once every node reads the format");
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

    // A pinned node asks for MessagePack while its group's keys move: it writes the envelope until the move is over, then switches on its own.
    [Fact]
    public async Task A_pinned_node_that_asks_for_messagepack_during_a_move_switches_once_the_move_is_over_without_a_restart()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var node = NewNode("a", () => true, () => "messagepack");
        var core = ((Router)node.GetRequiredService<IRouter>()).Core;
        var moving = true;
        var alerts = new List<UiPath.Caching.KeyMove.KeyMoveAlert>();
        var moveRules = new UiPath.Caching.KeyMove.GroupRules(true, UiPath.Caching.KeyMove.ReadRule.Follow, UiPath.Caching.KeyMove.WriteRule.Follow, new UiPath.Caching.KeyMove.ActedState("S3", "fwd", 1, "id1", 1, false), null, false, true, []);
        // The caches took this selector when they were built; the test gives it the rules of a group whose keys move.
        core.WriteFormats!.MoveHold = _ => WriteFormatSelector.Moving(moving ? moveRules : UiPath.Caching.KeyMove.GroupRules.Unaffected);
        core.WriteFormats.Alert = alerts.Add;
        var cache = Cache(node);
        var value = new List<long> { 1, 2, 3 };

        // Long past the time the switch takes when nothing holds it: every write is still the envelope, and the point of no return is not recorded.
        var until = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < until)
        {
            await cache.SetAsync("k", value, Lifetime, Ct);
            (await Bytes("k")).Should().Equal(LegacyEnvelope.Write(value), "the group's keys move, so it keeps its format");
            await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
        }

        (await Db.KeyExistsAsync($"mg:{{{ScopeTokens.For(_app)}:g}}:sw")).Should().BeFalse("the switch was never asked for");
        alerts.Should().ContainSingle().Which.Code.Should().Be("format_switch_held");

        moving = false;
        (await UntilAsync(async () =>
        {
            await cache.SetAsync("k", value, Lifetime, Ct);
            return (await Bytes("k")) is { Length: > 0 } bytes && bytes[0] == TestFormatSerializer.Id;
        })).Should().BeTrue("the move is over, so the same node switches");
        (await cache.GetAsync("k", Ct)).Should().Equal(value);
    }

    [Fact]
    public async Task A_group_that_asks_for_json_never_writes_the_header_format_even_when_it_is_pinned()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var cache = Cache(NewNode("a", () => true, () => null));
        var value = new List<long> { 4 };

        for (var i = 0; i < 6; i++)
        {
            await cache.SetAsync($"k{i}", value, Lifetime, Ct);
            await Task.Delay(500, Ct);
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
        (await UntilAsync(async () =>
        {
            await cache.SetAsync("k", value, Lifetime, Ct);
            return (await Bytes("k")) is { Length: > 0 } bytes && bytes[0] == TestFormatSerializer.Id;
        })).Should().BeTrue();
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

    private static async Task<bool> UntilAsync(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < until)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(250, Ct);
        }

        return false;
    }

    private static ICache<List<long>> Cache(ServiceProvider node) => node.GetRequiredService<IRouter>().Get<List<long>>("lists");

    private async Task<byte[]?> Bytes(string key) => (byte[]?)await Db.StringGetAsync(RedisKeyOf(key));

    private string RedisKeyOf(string key) => $"n:{_cacheName},c:{key}";

    private ServiceProvider NewNode(string name, Func<bool> pinned, Func<string?> format, Func<bool>? useLibrary = null, bool deadLibrary = false, ISourceCache<List<long>>? source = null)
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
            ["Caching:Redis:FormatNegotiation:ViewInterval"] = "00:00:01",
            ["Caching:Redis:FormatNegotiation:LivenessWindow"] = "00:00:02",
            ["Caching:Redis:FormatNegotiation:UpgradeHold"] = "00:00:04",
            ["Caching:Redis:FormatNegotiation:MaxViewAge"] = "00:00:20",
        }).Build();
        var legacy = source ?? new LegacySource(_redis!, _cacheName);
        var redisKey = new LegacySource(_redis!, _cacheName).RedisKey;
        var services = new ServiceCollection().AddLogging();
        services.AddCaching(configuration, builder => builder.AddRedisConnection());
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
                if (deadLibrary)
                {
                    router.UseSourceConnection(() => Db);
                }
            });
        var provider = services.BuildServiceProvider();
        _nodes.Add(provider);
        _ = provider.GetRequiredService<IRouter>();
        return provider;
    }
}
