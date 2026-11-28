using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class MoverWrittenAfterS3Tests(RedisFixture fixture)
{
    private const string Counter = "cache.keymove.written_after_moving";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(7_999, false)]
    [InlineData(8_000, false)]
    [InlineData(8_001, true)]
    public void The_canary_requires_the_TTL_plus_elapsed_time_to_exceed_the_source_limit(int ttlMs, bool expected)
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var s3At = time.GetUtcNow().ToUnixTimeMilliseconds();
        time.Advance(TimeSpan.FromSeconds(2));

        S3Canary.WrittenAfterS3(ttlMs, time.GetUtcNow().ToUnixTimeMilliseconds(), s3At, TimeSpan.FromSeconds(10)).Should().Be(expected);
    }

    [Fact]
    public async Task One_pass_counts_each_key_whose_life_says_it_was_written_after_S3_once_with_the_cache_and_group()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        await using var kit = await MoverKit.CreateAsync(fixture, clock: time, maxSourceTtl: TimeSpan.FromMinutes(10));
        using var counted = new Counted(kit.Kit.Group);
        await kit.SeedAsync(new Dictionary<string, (string Value, TimeSpan? Ttl)>
        {
            ["long"] = ("v", TimeSpan.FromMinutes(9)),
            ["short"] = ("v", TimeSpan.FromSeconds(30)),
            ["never"] = ("v", null),
        });
        var s3At = time.GetUtcNow().ToUnixTimeMilliseconds();
        time.Advance(TimeSpan.FromMinutes(2));

        await FinishPassAsync(kit.Host(state: () => kit.State(s3At: s3At)), time);

        counted.Tags.Should().ContainSingle().Which.Should().BeEquivalentTo(new Dictionary<string, string?> { ["cache"] = kit.Kit.Cid, ["group"] = kit.Kit.Group });
    }

    [Fact]
    public async Task A_state_without_the_time_of_S3_counts_nothing()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        await using var kit = await MoverKit.CreateAsync(fixture, clock: time, maxSourceTtl: TimeSpan.FromMinutes(10));
        using var counted = new Counted(kit.Kit.Group);
        await kit.SeedAsync(new Dictionary<string, (string Value, TimeSpan? Ttl)> { ["long"] = ("v", TimeSpan.FromHours(1)) });

        await FinishPassAsync(kit.Host(), time);

        counted.Tags.Should().BeEmpty("no S3 time, no evidence");
    }

    [Fact]
    public async Task A_key_observed_before_and_after_an_OOM_retry_counts_once()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        PublishFaultingDatabase? fault = null;
        await using var kit = await MoverKit.CreateAsync(
            fixture,
            clock: time,
            maxSourceTtl: TimeSpan.FromMinutes(10),
            interceptDb: db => PublishFaultingDatabase.Over(db, out fault));
        using var counted = new Counted(kit.Kit.Group);
        await kit.SeedAsync(new Dictionary<string, (string Value, TimeSpan? Ttl)> { ["long"] = ("v", TimeSpan.FromHours(1)) });
        fault!.Fail(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "OOM command not allowed when used memory > 'maxmemory'."), 1);
        var s3At = time.GetUtcNow().ToUnixTimeMilliseconds();

        await FinishPassAsync(kit.Host(state: () => kit.State(s3At: s3At)), time);

        fault.Faulted.Should().Be(1);
        (await kit.ProgressAsync())["moved"].Should().Be(1);
        counted.Tags.Should().ContainSingle();
    }

    [Fact]
    public async Task The_mover_judges_the_TTL_at_request_time_even_when_the_reply_arrives_later()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var maxTtl = TimeSpan.FromHours(1);
        await using var kit = await MoverKit.CreateAsync(
            fixture,
            clock: time,
            maxSourceTtl: maxTtl,
            source: k => new RecordingMoveSide(
                new InPlaceMoveSide<Item>(k.Kit.Plane, k.Kit.Group, k.Kit.L, ItemCodec.Instance, null, time),
                snap =>
                {
                    time.Advance(TimeSpan.FromMilliseconds(1));
                    return snap with { TtlMs = (long)maxTtl.TotalMilliseconds };
                }));
        using var counted = new Counted(kit.Kit.Group);
        await kit.SeedAsync(new Dictionary<string, (string Value, TimeSpan? Ttl)> { ["long"] = ("v", maxTtl) });
        var s3At = time.GetUtcNow().ToUnixTimeMilliseconds();

        await FinishPassAsync(kit.Host(state: () => kit.State(s3At: s3At)), time);

        (await kit.ProgressAsync())["moved"].Should().Be(1);
        counted.Tags.Should().BeEmpty();
    }

    [Fact]
    public async Task A_reverse_pass_does_not_count_writes_after_S3()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        await using var kit = await MoverKit.CreateAsync(fixture, clock: time, maxSourceTtl: TimeSpan.FromMinutes(10));
        using var counted = new Counted(kit.Kit.Group);
        await kit.SeedNativeAsync(new Dictionary<string, (string Value, TimeSpan? Ttl)> { ["long"] = ("v", TimeSpan.FromHours(1)) });
        var s3At = time.GetUtcNow().ToUnixTimeMilliseconds();

        await FinishPassAsync(kit.Host(state: () => kit.State(direction: "rev", s3At: s3At)), time);

        (await kit.Kit.GetAsync(kit.Kit.L("long"))).Should().Be("v");
        counted.Tags.Should().BeEmpty();
    }

    [Fact]
    public async Task A_read_through_promotion_does_not_count()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        await using var kit = await MoverKit.CreateAsync(fixture, clock: time, maxSourceTtl: TimeSpan.FromMinutes(10));
        using var counted = new Counted(kit.Kit.Group);
        await kit.SeedAsync(new Dictionary<string, (string Value, TimeSpan? Ttl)> { ["long"] = ("v", TimeSpan.FromHours(1)) });

        var read = await kit.Kit.Client.ReadAsync("long", Ct);

        read.Kind.Should().Be(ReadOutcome.Found);
        counted.Tags.Should().BeEmpty();
    }

    private static async Task FinishPassAsync(MoverHost host, ManualTimeProvider time)
    {
        host.Start();
        try
        {
            var first = await time.NextTimerAsync(Ct);
            var second = await time.NextTimerAsync(Ct);
            new[] { first, second }.Should().BeEquivalentTo(new[] { MoverKit.Quick.PollInterval, MoverKit.Quick.PassPause });
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private sealed class Counted : IDisposable
    {
        private readonly MeterListener _listener = new();

        public Counted(string group)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == KeyMoveTelemetry.MeterName && instrument.Name == Counter)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                var map = tags.ToArray().ToDictionary(t => t.Key, t => t.Value?.ToString());
                if (map.GetValueOrDefault("group") == group)
                {
                    Tags.Enqueue(map);
                }
            });
            _listener.Start();
        }

        public ConcurrentQueue<Dictionary<string, string?>> Tags { get; } = [];

        public void Dispose() => _listener.Dispose();
    }
}
