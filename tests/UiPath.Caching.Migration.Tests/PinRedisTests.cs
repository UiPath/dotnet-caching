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

        store.StateKey("orders").ToString().Should().Be("svc:mg:{app1:orders}:sw");
        store.AckKey("orders").ToString().Should().Be("svc:mg:{app1:orders}:ack", "both keys carry one hash tag, so a script reaches them in one slot");
        store.StateKey("a{b}").ToString().Should().Be("svc:mg:{app1:a%7Bb%7D}:sw");
        Pins(null, prefix: string.Empty).StateKey("g").ToString().Should().Be("mg:{_:g}:sw");
    }

    [Fact]
    public async Task A_state_is_absent_until_marked_then_switched_and_a_foreign_value_is_unreadable()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var store = Pins("app1");

        (await store.ReadStateAsync("g", Ct)).Should().Be(SwitchRead.Unswitched);
        await store.RestoreSwitchAsync("g", Ct);
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

        await one.RestoreSwitchAsync("g", Ct);

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
        await second.Pins.RefreshAsync(Ct);

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
        var second = await NodeAsync("app1", "urn:two", pin: true);
        first.Pins.Hold();
        await second.Pins.RefreshAsync(Ct);
        await first.Pins.RefreshAsync(Ct);
        first.Pins.Hold().Should().BeNull();

        await NodeAsync("app1", "urn:late", pin: false);
        await first.Pins.RefreshAsync(Ct);
        first.Pins.Hold().Should().Be("node_without_pin");

        await Db.KeyDeleteAsync(Pins("app1").StateKey("g"));
        await first.Pins.RefreshAsync(Ct);
        (await Pins("app1").ReadStateAsync("g", Ct)).Should().Be(SwitchRead.Switched);
    }

    [Fact]
    public async Task A_switch_recorded_against_an_acknowledgement_that_was_withdrawn_in_between_is_refused()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var store = Pins("app1");
        await store.PublishAckAsync("g", "a#router", "inc-a", 1, Ct);
        await store.PublishAckAsync("g", "b#router", "inc-b", 1, Ct);
        var observed = (await store.ReadAcksAsync("g", Ct)).ToList();

        var withdrawal = await store.PublishAckAsync("g", "a#router", "inc-a", 2, Ct);
        var committed = await store.CommitSwitchAsync("g", observed, Ct);

        withdrawal.Should().Be(new AckPublish(true, SwitchRead.Unswitched));
        committed.Should().BeFalse();
        (await store.ReadStateAsync("g", Ct)).Should().Be(SwitchRead.Unswitched, "the withdrawal and the read that confirms it are one step, and nothing can be recorded after it");
        await store.PublishAckAsync("g", "a#router", "inc-a", 3, Ct);
        (await store.CommitSwitchAsync("g", [.. (await store.ReadAcksAsync("g", Ct))], Ct)).Should().BeTrue();
        (await store.ReadStateAsync("g", Ct)).Should().Be(SwitchRead.Switched);
    }

    [Fact]
    public async Task A_commit_is_refused_for_a_missing_record_an_empty_list_and_a_changed_value_and_a_late_acknowledgement_does_not_replace_a_newer_one()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var store = Pins("app1");
        await store.PublishAckAsync("g", "a#router", "inc", 3, Ct);

        (await store.CommitSwitchAsync("g", [new("b#router", "inc:1")], Ct)).Should().BeFalse("no record for that node");
        (await store.CommitSwitchAsync("g", [], Ct)).Should().BeFalse("a switch needs an acknowledgement to rest on");
        (await store.CommitSwitchAsync("g", [new("a#router", "inc:1")], Ct)).Should().BeFalse("the record is not the one that was read");
        var late = await store.PublishAckAsync("g", "a#router", "inc", 1, Ct);

        late.Applied.Should().BeFalse();
        (await store.ReadAcksAsync("g", Ct))["a#router"].Should().Be("inc:3");
        (await store.PublishAckAsync("g", "a#router", "restarted", 1, Ct)).Applied.Should().BeTrue("a new incarnation of the node starts again");
        (await store.ReadStateAsync("g", Ct)).Should().Be(SwitchRead.Unswitched);
        (await Db.KeyTimeToLiveAsync(store.AckKey("g"))).Should().BeLessThanOrEqualTo(TimeSpan.FromDays(1)).And.NotBeNull();
    }

    [Fact]
    public async Task Publishing_an_acknowledgement_reports_a_recorded_switch()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var store = Pins("app1");
        await store.RestoreSwitchAsync("g", Ct);

        var published = await store.PublishAckAsync("g", "a#router", "inc", 2, Ct);

        published.Should().Be(new AckPublish(true, SwitchRead.Switched));
    }

    [Fact]
    public async Task A_withdrawal_through_the_coordinator_reaches_redis_and_a_commit_made_from_the_old_acknowledgement_is_refused()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var pinned = true;
        var core = new RouterCore(TimeProvider.System, _ => false, NullLogger.Instance);
        var nodes = new RouterAdvertisementStore(_ => ValueTask.FromResult(Db), NodeHash);
        var store = new RedisPinStore(nodes, _run + ":", "app1");
        var pins = new PinCoordinator(core, _ => pinned, store, "app1", TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(2), NullLogger.Instance) { OwnField = "urn:a#router" };
        core.Pins = pins;
        var pin = pins.GroupFor("g");
        pins.EvaluatePins();
        await pins.RefreshAsync(Ct);
        var observed = (await store.ReadAcksAsync("g", Ct)).ToList();

        pinned = false;
        pins.EvaluatePins();
        await pins.RefreshAsync(Ct);

        pin.StateKnown(TimeProvider.System, pins.StateValidity).Should().BeTrue();
        (await store.CommitSwitchAsync("g", observed, Ct)).Should().BeFalse();
        (await store.ReadStateAsync("g", Ct)).Should().Be(SwitchRead.Unswitched);
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
