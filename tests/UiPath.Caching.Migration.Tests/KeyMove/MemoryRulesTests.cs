namespace UiPath.Caching.Migration.Tests.KeyMove;

// The memory rules on readings of the primaries.
public sealed class MemoryRulesTests
{
    private const long Max = 1000 * 1024 * 1024;

    private static readonly DateTimeOffset Start = new(2026, 3, 4, 12, 0, 0, TimeSpan.Zero);

    private static readonly KeyMoveMoverOptions Options = new() { MaxMemoryGrowthBytesPerSecond = 1024 * 1024, MemoryGrowthWindow = TimeSpan.FromSeconds(60) };

    [Theory]
    [InlineData(949, null)]
    [InlineData(950, "memory_ceiling")]
    [InlineData(999, "memory_ceiling")]
    public void The_ceiling_is_95_percent_of_maxmemory(int permille, string? expected)
    {
        var rules = new MemoryRules();

        rules.Evaluate([Primary(Max / 1000 * permille)], Start, Options).Should().Be(expected);
    }

    [Fact]
    public void A_maxmemory_of_zero_that_the_server_reported_is_no_ceiling()
    {
        const string Text = "used_memory:4611686018427387903\r\nmaxmemory:0\r\nmaxmemory_policy:noeviction\r\nevicted_keys:0\r\nused_cpu_sys:1\r\nused_cpu_user:1\r\nmaster_repl_offset:1\r\n";

        new MemoryRules().Evaluate([NodeInfo.Parse("u1", false, Text)], Start, Options).Should().BeNull();
    }

    [Fact]
    public void Under_noeviction_above_the_ceiling_the_publish_is_refused_and_below_it_is_not()
    {
        var rules = new MemoryRules();

        rules.Evaluate([Primary(Max / 100 * 96, policy: "noeviction")], Start, Options).Should().Be("noeviction");
        rules.Evaluate([Primary(Max / 100 * 50, policy: "noeviction")], Start.AddSeconds(10), Options).Should().BeNull();
    }

    [Fact]
    public void Any_evicted_keys_since_the_last_reading_pause_and_the_pause_lifts_when_the_count_stops_rising()
    {
        var rules = new MemoryRules();

        rules.Evaluate([Primary(100, evicted: 7)], Start, Options).Should().BeNull("the first reading is a baseline");
        rules.Evaluate([Primary(100, evicted: 8)], Start.AddSeconds(10), Options).Should().Be("evicted");
        rules.Evaluate([Primary(100, evicted: 8)], Start.AddSeconds(20), Options).Should().BeNull();
    }

    [Fact]
    public void A_counter_that_went_back_after_a_restart_is_a_new_baseline_not_an_eviction()
    {
        var rules = new MemoryRules();
        rules.Evaluate([Primary(100, evicted: 500)], Start, Options);

        rules.Evaluate([Primary(100, evicted: 0)], Start.AddSeconds(10), Options).Should().BeNull();
        rules.Evaluate([Primary(100, evicted: 0)], Start.AddSeconds(20), Options).Should().BeNull();
        rules.Evaluate([Primary(100, evicted: 3)], Start.AddSeconds(30), Options).Should().Be("evicted");
    }

    [Fact]
    public void Memory_that_grows_faster_than_the_limit_over_the_window_pauses_and_slow_growth_does_not()
    {
        var rules = new MemoryRules();
        const long Mib = 1024 * 1024;

        rules.Evaluate([Primary(100 * Mib)], Start, Options).Should().BeNull();
        rules.Evaluate([Primary(105 * Mib)], Start.AddSeconds(10), Options).Should().BeNull("0.5 MiB per second");
        rules.Evaluate([Primary(140 * Mib)], Start.AddSeconds(20), Options).Should().Be("memory_growth", "40 MiB in 20 seconds");
    }

    [Fact]
    public void Growth_older_than_the_window_is_forgotten()
    {
        var rules = new MemoryRules();
        const long Mib = 1024 * 1024;
        rules.Evaluate([Primary(100 * Mib)], Start, Options);
        rules.Evaluate([Primary(300 * Mib)], Start.AddSeconds(10), Options).Should().Be("memory_growth");

        // The jump left the window; the memory stays level after it.
        rules.Evaluate([Primary(300 * Mib)], Start.AddSeconds(75), Options).Should().BeNull();
    }

    [Fact]
    public void A_growth_limit_of_zero_turns_the_rule_off()
    {
        var rules = new MemoryRules();
        var off = new KeyMoveMoverOptions { MaxMemoryGrowthBytesPerSecond = 0 };
        rules.Evaluate([Primary(0)], Start, off);

        rules.Evaluate([Primary(1L << 40, max: 0)], Start.AddSeconds(10), off).Should().BeNull();
    }

    [Fact]
    public void Replicas_are_not_judged_and_the_strongest_reason_over_the_primaries_wins()
    {
        var rules = new MemoryRules();
        rules.Evaluate([Primary(100, unit: "u1"), Primary(100, unit: "u2")], Start, Options);

        var reason = rules.Evaluate(
            [
                Primary(Max, unit: "u1", replica: true),
                Primary(Max / 100 * 96, unit: "u2", policy: "noeviction"),
                Primary(100, evicted: 5, unit: "u1"),
            ],
            Start.AddSeconds(10),
            Options);

        reason.Should().Be("noeviction");
        rules.Evaluate([Primary(Max, unit: "u1", replica: true), Primary(100, unit: "u1"), Primary(100, unit: "u2")], Start.AddSeconds(20), Options).Should().BeNull("a full replica says nothing about the primary");
    }

    private static NodeInfo Primary(long used, long evicted = 0, string policy = "allkeys-lru", long max = Max, string unit = "u1", bool replica = false) =>
        new(unit, replica, used, max, policy, evicted, 0, null, null);
}
