using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Redis;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Broadcast;

/// <summary>A server that refuses Streams and Pub/Sub both: the cache keeps working and broadcast is off.</summary>
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class StreamsFallbackOffIntegrationTests(RedisContainerFixture fixture) : IAsyncLifetime
{
    private readonly string _app = $"off{Guid.NewGuid():N}"[..10];
    private readonly ManualTimeProvider _clock = new(DateTimeOffset.UnixEpoch);
    private readonly RecordingTelemetryProvider _telemetry = new();
    private readonly TaskCompletionSource _off = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ConnectionMultiplexer? _admin;
    private ServiceProvider? _node;
    private string? _user;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _telemetry.EventRecorded += record =>
        {
            if (record.Name == Metrics.TransportChanged && record.Properties?[Metrics.TransportTo] == Metrics.TransportOff)
            {
                _off.TrySetResult();
            }
        };
        if (fixture.Enabled)
        {
            _admin = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // The node first: its topics and probe stop before the user they connect as goes.
        _node?.Dispose();
        if (_admin is not null)
        {
            if (_user is not null)
            {
                await RedisTestKeys.OnPrimariesAsync(_admin, "ACL", "DELUSER", _user);
            }

            await _admin.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_layered_write_succeeds_and_the_topic_is_off_when_the_server_refuses_Streams_and_Pub_Sub()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.SupportsAcl && fixture.ServerVersion.Major >= 7, "Needs ACL SETUSER and sharded Pub/Sub commands (Redis 7+).");
        var user = $"it-nobroadcast-{Guid.NewGuid():N}"[..20];
        await RedisTestKeys.OnPrimariesAsync(_admin!, "ACL", "SETUSER", user, "on", ">pw", "~*", "&*", "+@all", "-@stream", "-xgroup", "-xadd", "-xreadgroup", "-publish", "-subscribe", "-spublish", "-ssubscribe");
        _user = user;
        _node = Node($"{fixture.ConnectionString},user={user},password=pw");
        var cache = _node.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.InMemoryRedis);
        var capability = BroadcastCapability.For(_node.GetRequiredService<IRedisConnector>());
        var key = $"it-{Guid.NewGuid():N}";

        (await cache.SetAsync(key, "value", Ct)).Should().BeTrue("Off publishes true, so the write is not reported as unbroadcast");

        await _off.Task.WaitAsync(Ct);
        capability.PubSubIsRefused.Should().BeTrue("the refused PUBLISH or SUBSCRIBE latched");
        var topics = _node.GetRequiredService<RedisStreamsTopicProvider>().CreatedTopics().OfType<StreamsFallbackTopic>().ToList();
        topics.Should().NotBeEmpty();
        topics.Should().OnlyContain(t => !t.OnStreams && !t.IsSubscribed, "the topic is Off");
        (await cache.GetAsync<string>(key, Ct)).Should().Be("value", "the local tier serves it");
        (await cache.SetAsync(key, "second", Ct)).Should().BeTrue();
        (await cache.GetAsync<string>(key, Ct)).Should().Be("second");
    }

    private ServiceProvider Node(string connectionString) =>
        new ServiceCollection()
            .AddSingleton<TimeProvider>(_clock)
            .AddSingleton<ICachingTelemetryProvider>(_telemetry)
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = connectionString);
                    b.AddRedis();
                    b.AddRedisStreams(o => o.MaintainerEnabled = false);
                    b.AddBroadcast();
                    b.AddInMemoryRedis(o => o.LocalMaxExpiration = TimeSpan.FromMinutes(1));
                },
                o =>
                {
                    o.AppShortName = _app;
                    o.SourceUri = new Uri($"urn:a-{_app}");
                    o.DefaultTopic = KnownTopicNames.RedisStreams;
                })
            .BuildServiceProvider();
}
