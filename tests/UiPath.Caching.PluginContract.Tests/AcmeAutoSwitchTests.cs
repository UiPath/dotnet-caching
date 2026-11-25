using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Broadcast;
using UiPath.Caching.Config;
using UiPath.Caching.PluginContract.Tests.Acme;
using UiPath.Caching.PluginContract.Tests.Support;
using UiPath.Caching.Redis;

namespace UiPath.Caching.PluginContract.Tests;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class AcmeAutoSwitchTests(RedisFixture fixture) : AcmeNodeTests(fixture)
{
    [Fact]
    public async Task A_lone_node_in_auto_writes_json_until_its_first_view_and_then_the_plugin_for_values_and_events()
    {
        Assert.SkipUnless(Fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var node = Node(new("a") { Mode = FormatNegotiationMode.Auto, Streams = true });
        var cache = Cache(node);
        var events = node.GetRequiredService<IEventFormatterProxy<ICacheEvent>>();
        var before = Unique();
        await cache.SetAsync(before, new Profile("x", 1), PolicyOf(node, Policy), Ct);
        (await Raw(before))![0].Should().Be((byte)'{', "no node list has said every node reads the format");
        events.Encode(Event()).Span[0].Should().Be((byte)'{');
        await Start(node);

        events.Encode(Event()).Span[0].Should().Be((byte)AcmeFormats.Event, "events switch once every node lists 11 in er");
        var after = Unique();
        await cache.SetAsync(after, new Profile("y", 2), PolicyOf(node, Policy), Ct);
        (await Raw(after))!.Take(2).Should().Equal(0x1F, 0x00);
    }

    [Fact]
    public async Task A_node_that_cannot_read_the_plugin_holds_it_back_until_that_node_leaves()
    {
        Assert.SkipUnless(Fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var telemetry = new RecordingTelemetry();
        var node = Node(new("a") { Mode = FormatNegotiationMode.Auto, Streams = true, Telemetry = telemetry });
        var other = Node(new("b") { Mode = FormatNegotiationMode.Auto, Streams = true, Acme = false, WithBeta = true });
        var cache = Cache(node);
        var events = node.GetRequiredService<IEventFormatterProxy<ICacheEvent>>();
        await Start(node);
        await Start(other);
        (await Advertisements()).Should().HaveCount(2);
        await AdvanceViewAsync(node, telemetry.WaitForMetricAsync(Metrics.FormatNodes, "state", "live", Ct, sample: 2));

        var held = Unique();
        await cache.SetAsync(held, new Profile("x", 1), PolicyOf(node, Policy), Ct);
        (await Raw(held))![0].Should().Be((byte)'{', "the other node's ad lists no 100 or 101");
        events.Encode(Event()).Span[0].Should().Be((byte)'{', "and no 11");

        await Stop(other);

        await AdvanceViewAsync(node, telemetry.WaitForMetricAsync(Metrics.FormatNodes, "state", "live", Ct, sample: 1));
        events.Encode(Event()).Span[0].Should().Be((byte)AcmeFormats.Event, "a node that left stops blocking events");
        var key = Unique();
        await cache.SetAsync(key, new Profile("y", 2), PolicyOf(node, Policy), Ct);
        (await Raw(key))![0].Should().Be(0x1F, "a node that left stops blocking values");
    }

    private static CacheEvent Event() => new()
    {
        Id = Guid.NewGuid().ToString(),
        Type = KnownEventTypes.CacheSet,
        Source = new Uri("urn:node-a"),
        Data = new CacheEventData("k"),
    };

    public sealed record Profile(string Name, int Version);
}
