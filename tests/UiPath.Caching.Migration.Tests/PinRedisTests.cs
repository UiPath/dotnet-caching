using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class PinRedisTests(RedisFixture fixture) : IAsyncLifetime
{
    private const string Skip = "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.";

    private readonly string _run = $"pin-test:{Guid.NewGuid():N}";
    private ConnectionMultiplexer? _redis;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDatabase Db => _redis!.GetDatabase();

    private RedisKey NodeHash => _run + ":nodes";

    public async ValueTask InitializeAsync()
    {
        if (fixture.Enabled)
        {
            _redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_redis is not null)
        {
            var server = _redis.GetServer(_redis.GetEndPoints()[0]);
            foreach (var key in server.Keys(Db.Database, _run + "*"))
            {
                await Db.KeyDeleteAsync(key);
            }

            await _redis.DisposeAsync();
        }
    }

    [Fact]
    public void The_state_key_carries_the_prefix_and_the_application_and_holds_no_braces()
    {
        var store = Pins("app1", prefix: "svc:");

        store.StateKey("orders").ToString().Should().Be("svc:mg:sw:app1:orders");
        store.StateKey("a{b}").ToString().Should().Be("svc:mg:sw:app1:a%7Bb%7D").And.NotContain("{").And.NotContain("}");
        Pins(null, prefix: string.Empty).StateKey("g").ToString().Should().Be("mg:sw:_:g");
    }

    [Fact]
    public async Task A_state_is_absent_until_marked_then_switched_and_a_foreign_value_is_unreadable()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var store = Pins("app1");

        (await store.ReadStateAsync("g", Ct)).Should().Be(SwitchRead.Unswitched);
        await store.MarkSwitchedAsync("g", Ct);
        (await store.ReadStateAsync("g", Ct)).Should().Be(SwitchRead.Switched);
        (await Db.KeyTimeToLiveAsync(store.StateKey("g"))).Should().BeNull("the switch has no expiry");
        await Db.StringSetAsync(store.StateKey("odd"), "maybe");
        (await store.ReadStateAsync("odd", Ct)).Should().Be(SwitchRead.Unreadable);
    }

    [Fact]
    public async Task Two_applications_and_two_prefixes_keep_separate_state_for_the_same_group_name()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var one = Pins("app1");

        await one.MarkSwitchedAsync("g", Ct);

        (await Pins("app2").ReadStateAsync("g", Ct)).Should().Be(SwitchRead.Unswitched);
        (await Pins("app1", prefix: _run + ":other:").ReadStateAsync("g", Ct)).Should().Be(SwitchRead.Unswitched);
    }

    [Fact]
    public async Task The_node_list_read_returns_library_advertisements_and_router_entries_with_their_ages()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var nodes = new RouterAdvertisementStore(_ => ValueTask.FromResult(Db), NodeHash);
        await nodes.WriteAsync("urn:a#router", new RouterAdvertisement("urn:a", "b", ["pin=g"]), Ct);
        await Db.HashSetAsync(NodeHash, "urn:a", $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}|lib=3;inst=x;vr=json");
        await Db.HashSetAsync(NodeHash, "garbage", "no stamp here");

        var entries = await new RedisPinStore(nodes, string.Empty, "app1").ReadNodesAsync(Ct);

        entries.Select(entry => entry.Field).Should().BeEquivalentTo("urn:a#router", "urn:a");
        entries.Should().OnlyContain(entry => entry.Age < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Redis_down_fails_the_state_read_and_the_group_closed_without_throwing_to_a_caller()
    {
        var nodes = new RouterAdvertisementStore(_ => throw new RedisException("down"), NodeHash);
        var core = new RouterCore(TimeProvider.System, _ => false, NullLogger.Instance);
        var pins = new PinCoordinator(core, _ => false, new RedisPinStore(nodes, string.Empty, "app1"), "app1", TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(2), NullLogger.Instance);
        core.Pins = pins;
        pins.GroupFor("g");
        pins.EvaluatePins();

        (await pins.RefreshAsync(Ct)).Should().BeFalse();

        pins.Hold().Should().NotBeNull();
        core.PinFor("g")!.StateKnown(TimeProvider.System, pins.StateValidity).Should().BeFalse();
    }

    [Fact]
    public async Task The_gate_opens_over_real_entries_records_the_switch_and_ignores_another_application_on_the_same_hash()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var first = await NodeAsync("app1", "urn:one", pin: true);
        var second = await NodeAsync("app1", "urn:two", pin: true);
        await NodeAsync("app2", "urn:foreign", pin: false);
        first.Pins.Hold().Should().NotBeNull();

        await first.Pins.RefreshAsync(Ct);

        first.Pins.Hold().Should().BeNull();
        (await Pins("app1").ReadStateAsync("g", Ct)).Should().Be(SwitchRead.Switched);
        (await Pins("app2").ReadStateAsync("g", Ct)).Should().Be(SwitchRead.Unswitched);
        second.Pins.Hold();
        await second.Pins.RefreshAsync(Ct);
        second.Pins.Hold().Should().BeNull();
    }

    [Fact]
    public async Task A_node_of_the_same_application_without_the_pin_closes_the_gate_and_a_lost_key_is_written_again()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var first = await NodeAsync("app1", "urn:one", pin: true);
        await NodeAsync("app1", "urn:two", pin: true);
        first.Pins.Hold();
        await first.Pins.RefreshAsync(Ct);
        first.Pins.Hold().Should().BeNull();

        await NodeAsync("app1", "urn:late", pin: false);
        await first.Pins.RefreshAsync(Ct);
        first.Pins.Hold().Should().Be("node_without_pin");

        await Db.KeyDeleteAsync(Pins("app1").StateKey("g"));
        await first.Pins.RefreshAsync(Ct);
        (await Pins("app1").ReadStateAsync("g", Ct)).Should().Be(SwitchRead.Switched);
    }

    private RedisPinStore Pins(string? scope, string? prefix = null) =>
        new(new RouterAdvertisementStore(_ => ValueTask.FromResult(Db), NodeHash), prefix ?? _run + ":", scope);

    private async Task<(PinCoordinator Pins, RouterAdvertiser Advertiser)> NodeAsync(string app, string node, bool pin)
    {
        var core = new RouterCore(TimeProvider.System, _ => false, NullLogger.Instance);
        var nodes = new RouterAdvertisementStore(_ => ValueTask.FromResult(Db), NodeHash);
        var pins = new PinCoordinator(core, _ => pin, new RedisPinStore(nodes, _run + ":", app), app, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(2), NullLogger.Instance) { OwnField = node + RouterAdvertisement.FieldSuffix };
        core.Pins = pins;
        pins.GroupFor("g");
        pins.EvaluatePins();
        var advertiser = new RouterAdvertiser(nodes, node, "b", [new BypassTokens(core), new ScopeTokens(app), pins], TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(120), TimeProvider.System, NullLogger.Instance);
        (await advertiser.WriteAsync(Ct)).Should().BeTrue();
        await Db.HashSetAsync(NodeHash, node, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}|lib=3;inst=x;vr=json");
        return (pins, advertiser);
    }
}
