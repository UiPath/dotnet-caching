using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

[Collection("RedisIntegration")]
public sealed class InfoHealthTests(RedisFixture fixture)
{
    private const string LinkReason = "link";
    private const string RedPause = "red";
    private const string CategoryTrait = "Category";
    private const string IntegrationCategory = "Integration";

    private const string IntegrationDisabled = "Set RUN_REDIS_INTEGRATION_TESTS=1 to run.";
    private const string NoEvictionPolicy = "noeviction";
    private const string ReplicaOffsetField = "slave_repl_offset";

    private static readonly DateTimeOffset Start = new(2026, 3, 4, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string PrimaryText => "# Server\r\nredis_version:7.2.4\r\n# Memory\r\nused_memory:1048576\r\nmaxmemory:0\r\nmaxmemory_policy:allkeys-lru\r\n# Stats\r\nevicted_keys:0\r\n# Replication\r\nrole:master\r\nmaster_repl_offset:900\r\n# CPU\r\nused_cpu_sys:1.50\r\nused_cpu_user:2.25\r\n";

    private static string ReplicaText => "# Server\r\nredis_version:7.2.4\r\n# Memory\r\nused_memory:1048576\r\nmaxmemory:2097152\r\nmaxmemory_policy:noeviction\r\n# Stats\r\nevicted_keys:12\r\n# Replication\r\nrole:slave\r\nmaster_link_status:down\r\nslave_repl_offset:400\r\n# CPU\r\nused_cpu_sys:1.50\r\nused_cpu_user:2.25\r\n";

    [Fact]
    [Trait(CategoryTrait, IntegrationCategory)]
    public async Task INFO_of_a_real_primary_and_replica_is_parsed_and_the_replication_lag_is_computed()
    {
        Assert.SkipUnless(fixture.Enabled, IntegrationDisabled);
        var replica = Environment.GetEnvironmentVariable("CLAIM_REPLICA_CONNECTION_STRING");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(replica), "Set CLAIM_REPLICA_CONNECTION_STRING to run.");
        await using var both = await ConnectionMultiplexer.ConnectAsync($"{fixture.ConnectionString},{replica},allowAdmin=true");
        var unit = (await new MultiplexerScanUnits(() => both).ListAsync(Ct)).Single();

        var primary = NodeInfo.Parse(unit.Id, false, (await unit.Primary.ExecuteAsync("INFO").WaitAsync(Ct)).ToString());
        var follower = NodeInfo.Parse(unit.Id, true, (await unit.Replica!.ExecuteAsync("INFO").WaitAsync(Ct)).ToString());

        primary.UsedMemory.Should().BeGreaterThan(0);
        primary.Policy.Should().NotBeNullOrEmpty();
        primary.CpuSeconds.Should().BeGreaterThan(0);
        primary.ReplOffset.Should().BeGreaterThan(0);
        primary.LinkUp.Should().BeNull();
        follower.IsReplica.Should().BeTrue();
        follower.LinkUp.Should().BeTrue();
        follower.ReplOffset.Should().BeGreaterThan(0);

        var watch = RedisInfoWatch.Over(new MultiplexerScanUnits(() => both), new StaticOptionsMonitor<KeyMoveMoverOptions>(new KeyMoveMoverOptions()), TimeProvider.System);
        var reading = await watch.ReadAsync(Ct);

        reading.Loads.Should().HaveCount(2);
        var load = reading.Loads.Single(l => l.IsReplica);
        load.LinkDown.Should().BeFalse();
        load.LagBytes.Should().NotBeNull().And.BeGreaterThanOrEqualTo(0);
        reading.Loads.Single(l => !l.IsReplica).LagBytes.Should().BeNull();
    }

    [Fact]
    public void INFO_text_is_read_field_by_field()
    {
        var info = NodeInfo.Parse("u", true, ReplicaText);

        info.Should().Be(new NodeInfo("u", true, 1048576, 2097152, NoEvictionPolicy, 12, 3.75, 400, false));
        NodeInfo.Parse("u", false, PrimaryText).Should().Be(new NodeInfo("u", false, 1048576, 0, "allkeys-lru", 0, 3.75, 900, null));
    }

    [Fact]
    public void A_maxmemory_of_zero_is_no_ceiling_only_when_the_field_is_there()
    {
        var present = NodeInfo.Parse("u", false, PrimaryText);

        present.MaxMemory.Should().Be(0);
        new MemoryRules().Evaluate([present with { UsedMemory = long.MaxValue / 2 }], Start, new KeyMoveMoverOptions()).Should().BeNull();
        var absent = () => NodeInfo.Parse("u", false, Without(PrimaryText, "maxmemory"));
        absent.Should().Throw<FormatException>();
    }

    [Theory]
    [InlineData("used_memory")]
    [InlineData("maxmemory")]
    [InlineData("maxmemory_policy")]
    [InlineData("evicted_keys")]
    [InlineData("used_cpu_sys")]
    [InlineData("used_cpu_user")]
    public void A_primary_INFO_without_a_required_field_throws_and_so_does_one_where_it_cannot_be_read(string field)
    {
        var missing = () => NodeInfo.Parse("u", false, Without(PrimaryText, field));
        var garbled = () => NodeInfo.Parse("u", false, Garble(PrimaryText, field));
        var empty = () => NodeInfo.Parse("u", false, PrimaryText.Replace(field + ":", field + ":\r\n#", StringComparison.Ordinal));

        missing.Should().Throw<FormatException>().WithMessage("*" + field + "*");
        garbled.Should().Throw<FormatException>().WithMessage("*" + field + "*");
        empty.Should().Throw<FormatException>();
    }

    [Fact]
    public void A_replica_INFO_without_the_link_status_throws_and_an_empty_or_junk_reply_does_too()
    {
        var noLink = () => NodeInfo.Parse("u", true, Without(ReplicaText, "master_link_status"));
        var empty = () => NodeInfo.Parse("u", false, string.Empty);
        var junk = () => NodeInfo.Parse("u", false, "garbage\r\n");
        var negative = () => NodeInfo.Parse("u", false, Garble(PrimaryText, "used_memory", "-5"));

        noLink.Should().Throw<FormatException>().WithMessage("*master_link_status*");
        empty.Should().Throw<FormatException>();
        junk.Should().Throw<FormatException>();
        negative.Should().Throw<FormatException>();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_missing_replication_offset_on_either_side_reads_red_while_the_link_is_up(bool primaryMissing, bool replicaMissing)
    {
        var rig = new Rig();
        var primary = primaryMissing ? Without(PrimaryText, "master_repl_offset") : PrimaryText;
        var replica = replicaMissing ? Without(UpReplicaText(100), ReplicaOffsetField) : UpReplicaText(100);
        rig.Read = () => [NodeInfo.Parse("u", false, primary), NodeInfo.Parse("u", true, replica, "r1")];
        var rate = new AimdMoveRate(rig.Health, rig.Watch, new StaticOptionsMonitor<KeyMoveMoverOptions>(new KeyMoveMoverOptions()), rig.Time, rig.Latency);

        (await rate.PauseReasonAsync("u", Ct)).Should().Be(RedPause, "a lag that cannot be judged is not green");
    }

    [Theory]
    [InlineData("master_repl_offset", false)]
    [InlineData(ReplicaOffsetField, true)]
    public void A_malformed_replication_offset_throws_and_a_down_replica_may_lack_its_own(string field, bool replica)
    {
        var text = replica ? UpReplicaText(100) : PrimaryText;
        var garbled = () => NodeInfo.Parse("u", replica, Garble(text, field), "r1");

        garbled.Should().Throw<FormatException>().WithMessage("*" + field + "*");
        NodeInfo.Parse("u", true, Without(ReplicaText, ReplicaOffsetField), "r1").ReplOffset.Should().BeNull("its link is down, which reads red on its own");
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(900, 900, 0)]
    [InlineData(900, 400, 500)]
    public async Task A_valid_offset_of_zero_is_a_lag_of_zero_and_a_positive_lag_is_its_difference(long primary, long replica, long lag)
    {
        var rig = new Rig
        {
            Read = () => [NodeInfo.Parse("u", false, PrimaryText.Replace("master_repl_offset:900", "master_repl_offset:" + primary, StringComparison.Ordinal)), NodeInfo.Parse("u", true, UpReplicaText(replica), "r1")],
        };

        var reading = await rig.Watch.ReadAsync(Ct);

        reading.Loads.Single(l => l.IsReplica).LagBytes.Should().Be(lag);
    }

    [Fact]
    public async Task A_reading_that_throws_reads_red_through_the_rate_and_never_green()
    {
        var rig = new Rig { Read = () => throw new FormatException("INFO of u has no used_memory.") };
        var rate = new AimdMoveRate(rig.Health, rig.Watch, new StaticOptionsMonitor<KeyMoveMoverOptions>(new KeyMoveMoverOptions()), rig.Time, rig.Latency);

        (await rate.PauseReasonAsync("u", Ct)).Should().Be(RedPause);
    }

    [Fact]
    public async Task A_failed_read_is_kept_for_the_interval_so_INFO_runs_once_even_when_it_fails()
    {
        var rig = new Rig { Read = () => throw new FormatException("INFO of u has no used_memory.") };
        var rate = new AimdMoveRate(rig.Health, rig.Watch, new StaticOptionsMonitor<KeyMoveMoverOptions>(new KeyMoveMoverOptions()), rig.Time, rig.Latency);

        for (var i = 0; i < 5; i++)
        {
            (await rate.PauseReasonAsync("u" + i, Ct)).Should().Be(RedPause);
        }

        var again = async () => await rig.Watch.ReadAsync(Ct);
        await again.Should().ThrowAsync<InvalidOperationException>();
        rig.Reads.Should().Be(1, "the health and the memory rules share the one failed read");
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        await rate.PauseReasonAsync("u", Ct);
        rig.Reads.Should().Be(2);
    }

    [Fact]
    public async Task A_replica_that_is_named_but_not_connected_makes_the_unit_red_whichever_replica_it_is()
    {
        var rig = new Rig();
        var up = Node("u1", true, offset: 1_000);
        rig.Nodes = [Node("u1", false, offset: 1_000), up with { Node = "r1" }, NodeInfo.Down("u1", "r2")];

        var health = await rig.Health.GetHealthAsync(Ct);

        health.State.Should().Be(KeyMoveHealthState.Red);
        health.Reason.Should().Contain(LinkReason).And.Contain("u1");
        (await rig.Watch.ReadAsync(Ct)).Loads.Where(l => l.IsReplica).Should().HaveCount(2).And.Contain(l => l.LinkDown);
    }

    [Fact]
    [Trait(CategoryTrait, IntegrationCategory)]
    public async Task INFO_through_the_call_the_source_uses_is_refused_without_allowAdmin_and_the_rate_reads_red()
    {
        Assert.SkipUnless(fixture.Enabled, IntegrationDisabled);
        await using var plain = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var units = new MultiplexerScanUnits(() => plain);
        var options = new StaticOptionsMonitor<KeyMoveMoverOptions>(new KeyMoveMoverOptions());
        var watch = RedisInfoWatch.Over(units, options, TimeProvider.System);
        var rate = new AimdMoveRate(new RedisInfoHealthSource(watch, new MoverLatency(TimeProvider.System), options), watch, options, TimeProvider.System);

        var read = async () => await watch.ReadAsync(Ct);

        (await read.Should().ThrowAsync<RedisCommandException>()).WithMessage("*admin*INFO*");
        (await rate.PauseReasonAsync("u", Ct)).Should().Be(RedPause, "memory is never read as safe when INFO is refused");
    }

    [Fact]
    [Trait(CategoryTrait, IntegrationCategory)]
    public async Task A_unit_lists_every_replica_it_knows_and_one_that_is_down_makes_the_real_source_red()
    {
        Assert.SkipUnless(fixture.Enabled, IntegrationDisabled);
        var replica = Environment.GetEnvironmentVariable("CLAIM_REPLICA_CONNECTION_STRING");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(replica), "Set CLAIM_REPLICA_CONNECTION_STRING to run.");
        await using var both = await ConnectionMultiplexer.ConnectAsync($"{fixture.ConnectionString},{replica},allowAdmin=true");
        var unit = (await new MultiplexerScanUnits(() => both).ListAsync(Ct)).Single();

        unit.Replicas.Should().ContainSingle().Which.Server.Should().NotBeNull();
        var withGhost = unit with { Replicas = [.. unit.Replicas, new ScanReplica("ghost:1", null)] };
        var watch = RedisInfoWatch.Over(new Listed(withGhost), new StaticOptionsMonitor<KeyMoveMoverOptions>(new KeyMoveMoverOptions()), TimeProvider.System);
        var source = new RedisInfoHealthSource(watch, new MoverLatency(TimeProvider.System), new StaticOptionsMonitor<KeyMoveMoverOptions>(new KeyMoveMoverOptions()));

        var health = await source.GetHealthAsync(Ct);

        health.State.Should().Be(KeyMoveHealthState.Red);
        health.Reason.Should().Contain(LinkReason);
    }

    [Fact]
    public async Task The_lag_is_the_primary_offset_less_the_replica_offset_and_never_negative()
    {
        var rig = new Rig { Nodes = [Node("u1", false, offset: 1_000), Node("u1", true, offset: 400)] };
        var first = await rig.Watch.ReadAsync(Ct);
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        rig.Nodes = [Node("u1", false, offset: 1_000), Node("u1", true, offset: 1_200)];
        var second = await rig.Watch.ReadAsync(Ct);

        first.Loads.Single(l => l.IsReplica).LagBytes.Should().Be(600);
        second.Loads.Single(l => l.IsReplica).LagBytes.Should().Be(0);
    }

    [Fact]
    public async Task A_quiet_node_with_a_replica_close_behind_is_green()
    {
        var rig = new Rig();

        (await rig.HealthAsync([Node("u1", false, cpu: 0, offset: 1_000), Node("u1", true, cpu: 0, offset: 990)], [Node("u1", false, cpu: 1, offset: 2_000), Node("u1", true, cpu: 1, offset: 1_990)]))
            .State.Should().Be(KeyMoveHealthState.Green);
    }

    [Theory]
    [InlineData(5.0, (int)KeyMoveHealthState.Green)]
    [InlineData(7.0, (int)KeyMoveHealthState.Amber)]
    [InlineData(8.9, (int)KeyMoveHealthState.Amber)]
    [InlineData(9.0, (int)KeyMoveHealthState.Red)]
    public async Task Cpu_is_judged_by_the_change_between_two_readings_over_the_time_between_them(double seconds, int expected)
    {
        var rig = new Rig();

        // Seconds of cpu in the ten seconds between the readings: 7 is 70 percent, 9 is 90.
        var health = await rig.HealthAsync([Node("u1", false, cpu: 100)], [Node("u1", false, cpu: 100 + seconds)]);

        health.State.Should().Be((KeyMoveHealthState)expected);
        if ((KeyMoveHealthState)expected != KeyMoveHealthState.Green)
        {
            health.Reason.Should().Contain("cpu").And.Contain("u1");
        }
    }

    [Theory]
    [InlineData(100L, (int)KeyMoveHealthState.Green)]
    [InlineData(1024L * 1024, (int)KeyMoveHealthState.Amber)]
    [InlineData(32L * 1024 * 1024, (int)KeyMoveHealthState.Red)]
    public async Task Replication_lag_in_bytes_makes_amber_and_red(long lag, int expected)
    {
        var rig = new Rig();
        var nodes = new[] { Node("u1", false, offset: 100_000_000), Node("u1", true, offset: 100_000_000 - lag) };

        (await rig.HealthAsync(nodes, nodes)).State.Should().Be((KeyMoveHealthState)expected);
    }

    [Fact]
    public async Task A_replica_whose_link_is_down_is_red()
    {
        var rig = new Rig();
        var nodes = new[] { Node("u1", false), Node("u1", true, linkUp: false) };

        var health = await rig.HealthAsync(nodes, nodes);

        health.State.Should().Be(KeyMoveHealthState.Red);
        health.Reason.Should().Contain(LinkReason);
    }

    [Theory]
    [InlineData("evicted")]
    [InlineData("memory_ceiling")]
    [InlineData("memory_growth")]
    [InlineData(NoEvictionPolicy)]
    public async Task A_memory_rule_makes_the_health_red_with_its_reason(string rule)
    {
        var rig = new Rig();
        var calm = Node("u1", false, used: 100 * 1024 * 1024);
        NodeInfo hot = rule switch
        {
            "evicted" => Node("u1", false, evicted: 9, used: 100 * 1024 * 1024),
            "memory_growth" => Node("u1", false, used: 2000L * 1024 * 1024),
            "memory_ceiling" => Node("u1", false, used: 96, max: 100),
            _ => Node("u1", false, used: 96, max: 100, policy: NoEvictionPolicy),
        };

        var health = await rig.HealthAsync([calm], [hot]);

        health.State.Should().Be(KeyMoveHealthState.Red);
        health.Reason.Should().Contain(rule);
    }

    [Theory]
    [InlineData(100, (int)KeyMoveHealthState.Green)]
    [InlineData(300, (int)KeyMoveHealthState.Amber)]
    [InlineData(1500, (int)KeyMoveHealthState.Red)]
    public async Task The_movers_own_p99_makes_amber_and_red_but_only_from_enough_samples(int millis, int expected)
    {
        var rig = new Rig();
        for (var i = 0; i < MoverLatency.MinSamples - 1; i++)
        {
            rig.Latency.Record(TimeSpan.FromMilliseconds(millis));
        }

        (await rig.Health.GetHealthAsync(Ct)).State.Should().Be(KeyMoveHealthState.Green, "too few samples say nothing");
        rig.Latency.Record(TimeSpan.FromMilliseconds(millis));
        rig.Time.Advance(TimeSpan.FromSeconds(10));

        (await rig.Health.GetHealthAsync(Ct)).State.Should().Be((KeyMoveHealthState)expected);
    }

    [Fact]
    public async Task A_p99_that_turned_red_ends_when_its_samples_age_out_even_though_a_paused_mover_records_none()
    {
        var rig = new Rig();
        for (var i = 0; i < MoverLatency.MinSamples; i++)
        {
            rig.Latency.Record(TimeSpan.FromSeconds(3));
        }

        (await rig.Health.GetHealthAsync(Ct)).State.Should().Be(KeyMoveHealthState.Red);

        // Paused: nothing is recorded. The clock passes the window.
        rig.Time.Advance(TimeSpan.FromSeconds(59));
        (await rig.Health.GetHealthAsync(Ct)).State.Should().Be(KeyMoveHealthState.Red, "still inside the window");
        rig.Time.Advance(TimeSpan.FromSeconds(2));

        (await rig.Health.GetHealthAsync(Ct)).State.Should().Be(KeyMoveHealthState.Green);
    }

    [Theory]
    [InlineData(0, (int)KeyMoveHealthState.Green)]
    [InlineData(1, (int)KeyMoveHealthState.Amber)]
    [InlineData(4, (int)KeyMoveHealthState.Amber)]
    [InlineData(5, (int)KeyMoveHealthState.Red)]
    public async Task Timeouts_since_the_last_reading_make_amber_and_red(int timeouts, int expected)
    {
        var rig = new Rig();
        for (var i = 0; i < timeouts; i++)
        {
            rig.Latency.RecordTimeout();
        }

        (await rig.Health.GetHealthAsync(Ct)).State.Should().Be((KeyMoveHealthState)expected);
    }

    private static string UpReplicaText(long offset) =>
        ReplicaText.Replace("master_link_status:down", "master_link_status:up", StringComparison.Ordinal).Replace("slave_repl_offset:400", "slave_repl_offset:" + offset, StringComparison.Ordinal);

    private static string Without(string text, string field) =>
        string.Join("\n", text.Split('\n').Where(l => !l.StartsWith(field + ":", StringComparison.Ordinal)));

    private static string Garble(string text, string field, string value = "not-a-number")
    {
        var replacement = field == "maxmemory_policy" ? string.Empty : value;
        return string.Join("\n", text.Split('\n').Select(l => l.StartsWith(field + ":", StringComparison.Ordinal) ? field + ":" + replacement : l));
    }

    private static NodeInfo Node(string unit, bool replica, double cpu = 0, long? offset = null, bool? linkUp = null, long evicted = 0, long used = 1024, long max = 0, string policy = "allkeys-lru") =>
        new(unit, replica, used, max, policy, evicted, cpu, offset, replica ? linkUp ?? true : null);

    private sealed class Listed(ScanUnit unit) : IScanUnits
    {
        public Task<IReadOnlyList<ScanUnit>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ScanUnit>>([unit]);
    }

    private sealed class Rig
    {
        public Rig()
        {
            Time = new ManualTimeProvider(Start);
            Latency = new MoverLatency(Time);
            var options = new StaticOptionsMonitor<KeyMoveMoverOptions>(new KeyMoveMoverOptions());
            Watch = new RedisInfoWatch(
                _ =>
                {
                    Reads++;
                    return Task.FromResult<IReadOnlyList<NodeInfo>>(Read is { } read ? read() : Nodes);
                },
                options,
                Time);
            Health = new RedisInfoHealthSource(Watch, Latency, options);
        }

        public ManualTimeProvider Time { get; }

        public IReadOnlyList<NodeInfo> Nodes { get; set; } = [];

        public Func<IReadOnlyList<NodeInfo>>? Read { get; set; }

        public int Reads { get; private set; }

        public MoverLatency Latency { get; }

        public RedisInfoWatch Watch { get; }

        public RedisInfoHealthSource Health { get; }

        // Reads the nodes once, ten seconds pass, reads them again, and judges the second reading.
        public async Task<KeyMoveHealth> HealthAsync(IReadOnlyList<NodeInfo> before, IReadOnlyList<NodeInfo> after)
        {
            Nodes = before;
            await Watch.ReadAsync(TestContext.Current.CancellationToken);
            Time.Advance(TimeSpan.FromSeconds(10));
            Nodes = after;
            return await Health.GetHealthAsync(TestContext.Current.CancellationToken);
        }
    }
}
