using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Negotiation;
using UiPath.Caching.Serialization;
using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Redis;

// A sweep and a census over a fake keyspace: what a pass counts, how it is paced and bounded, and where it resumes. The conversion itself is a fake delegate here.
public sealed class FormatSweeperTests : IDisposable
{
    private static readonly byte[] Json = "\"x\""u8.ToArray();
    private static readonly byte[] Fast = [0x10, (byte)'x'];
    private static readonly byte[] Other = [0x12, (byte)'x'];
    private static readonly byte[] Unknown = [0x14, (byte)'x'];
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);

    private readonly ManualTimeProvider _clock = new(Start);
    private readonly RecordingTelemetryProvider _telemetry = new();
    private readonly FormatMigrator _migrator;
    private readonly PassthroughValueSerializer _fast = new(0x10);
    private readonly ValueSerializerRegistry _registry;
    private readonly AllowedFormat _effective = new();
    private readonly List<string> _rewritten = [];
    private readonly Dictionary<string, Func<RewriteTally>> _outcomes = [];
    private Func<string, bool> _isDefault = _ => false;

    public FormatSweeperTests()
    {
        _registry = new ValueSerializerRegistry([RegistryEntry.Of("fast", _fast), RegistryEntry.Of("other", new PassthroughValueSerializer(0x12))]);
        _migrator = new FormatMigrator(_telemetry, _clock, NullLogger.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _migrator.Dispose();

    [Fact]
    public async Task Each_string_is_counted_under_what_became_of_it()
    {
        var keys = new Keyspace();
        keys.Add(0, "json-done", Json);
        keys.Add(0, "json-changed", Json);
        keys.Add(0, "json-lossy", Json);
        keys.Add(0, "json-default", "\"d\""u8.ToArray());
        keys.Add(0, "json-fails", Json);
        keys.Add(0, "other-done", Other);
        keys.Add(0, "already-fast", Fast);
        keys.Add(0, "unknown-id", Unknown);
        keys.Add(0, "empty", []);
        keys.Add(0, "oversize", [.. "\""u8, .. new byte[300 * 1024], .. "\""u8]);
        keys.Add(0, "gone", null);
        _outcomes["json-changed"] = () => RewriteTally.Of(RewriteOutcome.Changed);
        _outcomes["json-lossy"] = () => RewriteTally.Of(RewriteOutcome.Lossy);
        _outcomes["json-default"] = () => RewriteTally.Of(RewriteOutcome.Lossy);
        _outcomes["json-fails"] = () => throw new InvalidOperationException("the pipeline gave up");
        _isDefault = key => key == "json-default";

        var report = await Sweep(keys, new FormatSweepOptions { RatePerSecond = 1000 });

        report.Converted.Should().Be(2);
        report.Changed.Should().Be(1);
        report.Lossy.Should().Be(1);
        report.DefaultOrEmpty.Should().Be(2, "a value equal to default(T) and an empty one");
        report.Oversize.Should().Be(1);
        report.Failed.Should().Be(1);
        report.Current.Should().Be(1);
        report.Unreadable.Should().Be(1);
        report.Held.Should().Be(0);
        report.Scanned.Should().Be(10, "a key that went away is not counted");
        report.Completed.Should().BeTrue();
        report.NextCursor.Should().BeNull();
        _rewritten.Should().BeEquivalentTo("json-done", "json-changed", "json-lossy", "json-default", "json-fails", "other-done");
        Tracked("converted").Should().Be(2);
        Tracked("oversize").Should().Be(1);
        Tracked("default_or_empty").Should().Be(2);
    }

    [Fact]
    public async Task A_key_in_the_target_format_costs_no_more_than_the_two_byte_read()
    {
        var keys = new Keyspace();
        keys.Add(0, "a", Fast);
        keys.Add(0, "b", Fast);

        var report = await Sweep(keys, new FormatSweepOptions());

        report.Current.Should().Be(2);
        await keys.Database.DidNotReceiveWithAnyArgs().StringLengthAsync(default, default);
        await keys.Database.DidNotReceiveWithAnyArgs().StringGetAsync(default(RedisKey), default);
    }

    [Fact]
    public async Task The_rate_paces_the_keys_that_need_work_and_never_bursts()
    {
        var keys = new Keyspace();
        for (var i = 0; i < 5; i++)
        {
            keys.Add(0, $"k{i}", Json);
        }

        var run = Begin(keys, new FormatSweepOptions { RatePerSecond = 2 }, Ct);
        await Drive(run);

        (await run).Converted.Should().Be(5);
        (_clock.GetUtcNow() - Start).Should().Be(TimeSpan.FromSeconds(2), "five keys at two a second are four intervals of half a second");
    }

    [Fact]
    public async Task A_budget_ends_the_pass_between_pages_with_a_cursor_that_a_later_pass_continues_from()
    {
        var keys = new Keyspace();
        for (var i = 0; i < 6; i++)
        {
            keys.Add(0, $"k{i}", Json);
        }

        var firstReport = await Sweep(keys, new FormatSweepOptions { RatePerSecond = 1, PageSize = 2, Budget = TimeSpan.FromSeconds(1) });

        firstReport.Completed.Should().BeFalse();
        firstReport.NextCursor.Should().NotBeNullOrEmpty();
        firstReport.Converted.Should().Be(2, "the page that was under way is finished, no more");

        var secondReport = await Sweep(keys, new FormatSweepOptions { RatePerSecond = 1000, PageSize = 2, Cursor = firstReport.NextCursor });

        secondReport.Completed.Should().BeTrue();
        secondReport.NextCursor.Should().BeNull();
        secondReport.Converted.Should().Be(4);
        _rewritten.Should().BeEquivalentTo("k0", "k1", "k2", "k3", "k4", "k5").And.OnlyHaveUniqueItems("a resumed pass starts where the first stopped");
    }

    [Fact]
    public async Task A_cursor_names_a_primary_and_a_pass_goes_through_them_in_order()
    {
        var keys = new Keyspace();
        keys.Add(0, "a0", Json);
        keys.Add(0, "a1", Json);
        keys.Add(1, "b0", Json);
        keys.Add(1, "b1", Json);

        var firstReport = await Sweep(keys, new FormatSweepOptions { RatePerSecond = 1, PageSize = 2, Budget = TimeSpan.FromSeconds(1) });
        var rewrittenFirst = _rewritten.ToArray();
        var secondReport = await Sweep(keys, new FormatSweepOptions { RatePerSecond = 1000, PageSize = 2, Cursor = firstReport.NextCursor });

        firstReport.NextCursor.Should().Be("10.0.0.2:6379|0");
        rewrittenFirst.Should().BeEquivalentTo("a0", "a1");
        _rewritten.Skip(2).Should().BeEquivalentTo("b0", "b1");
        secondReport.Completed.Should().BeTrue();
    }

    [Fact]
    public async Task A_cursor_of_a_primary_that_is_gone_starts_the_pass_again_and_a_bad_cursor_is_refused()
    {
        var keys = new Keyspace();
        keys.Add(0, "a0", Json);
        keys.Add(1, "b0", Json);

        var report = await Sweep(keys, new FormatSweepOptions { Cursor = "10.0.0.1:7000|7" });
        var bad = () => Sweep(keys, new FormatSweepOptions { Cursor = "nonsense" });

        _rewritten.Should().Equal("a0", "b0");
        report.Completed.Should().BeTrue();
        await bad.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("10.0.0.2:6379|bbb|0", "bbb", new[] { "b0" })]
    [InlineData("10.0.0.2:6379|bbb|0", "ccc", new[] { "a0", "b0" })]
    [InlineData("10.0.0.2:6379|0", "ccc", new[] { "a0", "b0" })]
    [InlineData("10.0.0.2:6379|bbb|0", null, new[] { "a0", "b0" })]
    [InlineData("10.0.0.2:6379|0", null, new[] { "b0" })]
    public async Task A_cursor_goes_on_only_on_the_server_it_named(string cursor, string? runId, string[] expected)
    {
        var keys = new Keyspace();
        keys.Add(0, "a0", Json);
        keys.Add(1, "b0", Json);
        keys.RunIds[0] = "aaa";
        keys.RunIds[1] = runId;

        var report = await Sweep(keys, new FormatSweepOptions { Cursor = cursor });

        _rewritten.Should().Equal(expected);
        report.Completed.Should().BeTrue();
    }

    [Fact]
    public async Task A_cursor_carries_the_run_id_of_the_server_it_stopped_on()
    {
        var keys = new Keyspace();
        keys.Add(0, "a0", Json);
        keys.Add(0, "a1", Json);
        keys.Add(1, "b0", Json);
        keys.Add(1, "b1", Json);
        keys.RunIds[0] = "aaa";
        keys.RunIds[1] = "bbb";

        var report = await Sweep(keys, new FormatSweepOptions { RatePerSecond = 1, PageSize = 2, Budget = TimeSpan.FromSeconds(1) });

        report.NextCursor.Should().Be("10.0.0.2:6379|bbb|0");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_key_that_changed_type_meanwhile_is_counted_as_changed_and_the_pass_goes_on(bool atRead)
    {
        var keys = new Keyspace { Claimed = () => _migrator.ClaimedBytes };
        keys.Add(0, "a", Json);
        keys.Add(0, "b", Json);
        keys.Add(0, "c", Json);
        (atRead ? keys.WrongTypeAtRead : keys.WrongTypeAtLength).Add("b");

        var report = await Sweep(keys, new FormatSweepOptions { RatePerSecond = 1000 });

        report.Changed.Should().Be(1);
        report.Failed.Should().Be(0);
        report.Converted.Should().Be(2);
        report.Completed.Should().BeTrue();
        report.NextCursor.Should().BeNull();
        _rewritten.Should().Equal("a", "c");
        _migrator.ClaimedBytes.Should().Be(0, "the claim of the key that changed type was given back");
    }

    [Theory]
    [InlineData(100, 10_000_000, true)]
    [InlineData(100, 101, true)]
    [InlineData(100, 100, false)]
    public async Task A_value_replaced_by_a_larger_one_after_its_length_was_read_is_never_fetched_past_the_limit(int limit, int replacement, bool oversize)
    {
        var keys = new Keyspace { Claimed = () => _migrator.ClaimedBytes };
        keys.Add(0, "k", Json);
        keys.AfterLength = key => keys.Replace(key, [.. "\""u8, .. new byte[replacement - 1]]);

        var report = await Sweep(keys, new FormatSweepOptions { MaxValueBytes = limit });

        keys.MaxFetched.Should().BeLessThanOrEqualTo(limit + 1, "the read asks for the limit and one byte more, not the value");
        keys.ClaimedAtRead.Should().ContainSingle().Which.Should().Be(limit + 1L, "the bound of the read is what the budget holds, not the length that was seen");
        keys.ClaimedAtRead.Max().Should().BeLessThanOrEqualTo(FormatMigrator.DefaultMaxQueuedBytes);
        report.Oversize.Should().Be(oversize ? 1 : 0);
        report.Converted.Should().Be(oversize ? 0 : 1);
        _rewritten.Should().HaveCount(oversize ? 0 : 1);
        _migrator.ClaimedBytes.Should().Be(0);
    }

    [Fact]
    public async Task Cancelling_stops_at_the_next_key_and_gives_back_the_claim()
    {
        var keys = new Keyspace();
        for (var i = 0; i < 4; i++)
        {
            keys.Add(0, $"k{i}", Json);
        }

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _outcomes["k1"] = () =>
        {
            cancel.Cancel();
            return RewriteTally.Of(RewriteOutcome.Done);
        };

        var act = () => SweepWith(keys, new FormatSweepOptions(), cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _rewritten.Should().Equal("k0", "k1");
        _migrator.TryClaimDirect(this, "k2", 1, out _).Should().Be(FormatMigrator.ClaimResult.Claimed, "nothing was held for a key the pass never reached");
    }

    [Fact]
    public async Task The_node_list_is_asked_for_each_key_and_a_no_stops_the_pass_with_a_cursor()
    {
        var keys = new Keyspace();
        for (var i = 0; i < 5; i++)
        {
            keys.Add(0, $"k{i}", Json);
        }

        _outcomes["k1"] = () =>
        {
            _effective.Allowed = false;
            return RewriteTally.Of(RewriteOutcome.Done);
        };

        var report = await Sweep(keys, new FormatSweepOptions { PageSize = 10 });

        _rewritten.Should().Equal("k0", "k1");
        report.Converted.Should().Be(2);
        report.Held.Should().Be(1);
        report.Completed.Should().BeFalse();
        report.NextCursor.Should().NotBeNull();
    }

    [Fact]
    public async Task A_rewrite_that_finds_the_format_held_back_by_then_stops_the_pass_too()
    {
        var keys = new Keyspace();
        keys.Add(0, "k0", Json);
        keys.Add(0, "k1", Json);
        _outcomes["k0"] = () => RewriteTally.Of(RewriteOutcome.Held);

        var report = await Sweep(keys, new FormatSweepOptions());

        _rewritten.Should().Equal("k0");
        report.Held.Should().Be(1);
        report.Completed.Should().BeFalse();
    }

    [Fact]
    public async Task A_key_rewritten_lately_is_skipped_and_a_lossy_one_stays_held_back()
    {
        var keys = new Keyspace();
        keys.Add(0, "done", Json);
        keys.Add(0, "lossy", Json);
        _outcomes["lossy"] = () => RewriteTally.Of(RewriteOutcome.Lossy);
        await Sweep(keys, new FormatSweepOptions());

        var again = await Sweep(keys, new FormatSweepOptions());

        _rewritten.Should().Equal("done", "lossy");
        again.Recent.Should().Be(1);
        again.Lossy.Should().Be(1);
        again.Converted.Should().Be(0);
    }

    [Fact]
    public async Task A_read_and_a_sweep_share_one_claim_per_key()
    {
        var keys = new Keyspace();
        keys.Add(0, "k0", Json);
        var owner = new object();
        _migrator.TryClaim(owner, "k0", "Redis", 1).Should().Be(FormatMigrator.ClaimResult.Claimed, "a read offered the key a moment ago");

        var report = await Sweep(keys, new FormatSweepOptions(), owner: owner);

        report.Recent.Should().Be(1);
        _rewritten.Should().BeEmpty();
    }

    [Fact]
    public async Task A_target_that_nobody_may_write_yet_converts_nothing()
    {
        var keys = new Keyspace();
        keys.Add(0, "k0", Json);
        _effective.Allowed = false;

        var report = await Sweep(keys, new FormatSweepOptions());

        report.Held.Should().Be(1);
        report.Converted.Should().Be(0);
        _rewritten.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, 100, 10)]
    [InlineData(10, 0, 10)]
    [InlineData(10, 100, 0)]
    public async Task A_limit_that_is_not_positive_is_refused(int rate, int bytes, int page)
    {
        var act = () => Sweep(new Keyspace(), new FormatSweepOptions { RatePerSecond = rate, MaxValueBytes = bytes, PageSize = page });

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("head", "connection")]
    [InlineData("length", "timeout")]
    [InlineData("scan", "busy")]
    [InlineData("length", "loading")]
    public async Task A_redis_error_outside_the_rewrite_ends_the_pass_with_a_cursor_that_a_later_pass_continues_from(string where, string kind)
    {
        var keys = new Keyspace();
        for (var i = 0; i < 4; i++)
        {
            keys.Add(0, $"k{i}", Json);
        }

        keys.Fault = (op, at) => op == where && at is "k2" or "2" ? Transient(kind) : null;

        var report = await Sweep(keys, new FormatSweepOptions { RatePerSecond = 1000, PageSize = 2 });

        report.Completed.Should().BeFalse();
        report.NextCursor.Should().Be("10.0.0.1:6379|2", "the page that failed starts again, the one before it is done");
        report.Converted.Should().Be(2);
        report.Failed.Should().Be(0);
        _migrator.ClaimedBytes.Should().Be(0);

        keys.Fault = null;
        var resumed = await Sweep(keys, new FormatSweepOptions { RatePerSecond = 1000, PageSize = 2, Cursor = report.NextCursor });

        resumed.Completed.Should().BeTrue();
        resumed.Converted.Should().Be(2);
        _rewritten.Should().BeEquivalentTo("k0", "k1", "k2", "k3").And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_redis_error_on_the_first_page_gives_back_the_cursor_the_pass_was_given()
    {
        var keys = new Keyspace();
        keys.Add(0, "k0", Json);
        keys.Fault = (op, _) => op == "scan" ? Transient("timeout") : null;

        var report = await Sweep(keys, new FormatSweepOptions { Cursor = "10.0.0.1:6379|0" });

        report.Completed.Should().BeFalse();
        report.NextCursor.Should().Be("10.0.0.1:6379|0");
        report.Scanned.Should().Be(0);
    }

    [Fact]
    public async Task Cancelling_inside_a_redis_call_throws_and_gives_back_the_claim()
    {
        var keys = new Keyspace { Claimed = () => _migrator.ClaimedBytes };
        for (var i = 0; i < 3; i++)
        {
            keys.Add(0, $"k{i}", Json);
        }

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        keys.Fault = (op, at) =>
        {
            if (op != "read" || at != "k1")
            {
                return null;
            }

            cancel.Cancel();
            return new OperationCanceledException(cancel.Token);
        };

        var act = () => SweepWith(keys, new FormatSweepOptions { RatePerSecond = 1000 }, cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _rewritten.Should().Equal("k0");
        _migrator.ClaimedBytes.Should().Be(0, "the claim of the key being read was given back");
    }

    [Fact]
    public async Task A_budget_ends_the_pass_between_the_keys_of_a_page_and_the_cursor_starts_that_page_again()
    {
        var keys = new Keyspace();
        for (var i = 0; i < 6; i++)
        {
            keys.Add(0, $"k{i}", Json);
        }

        var first = await Sweep(keys, new FormatSweepOptions { RatePerSecond = 1, PageSize = 6, Budget = TimeSpan.FromSeconds(2.5) });

        first.Completed.Should().BeFalse();
        first.Converted.Should().Be(4, "keys are taken at 0, 1, 2 and 3 seconds, and the budget has passed at the fifth");
        first.NextCursor.Should().Be("10.0.0.1:6379|0", "a page is only ever resumed from its start");
        (_clock.GetUtcNow() - Start).Should().Be(TimeSpan.FromSeconds(3));

        var second = await Sweep(keys, new FormatSweepOptions { RatePerSecond = 1000, PageSize = 6, Cursor = first.NextCursor });

        second.Completed.Should().BeTrue();
        second.Converted.Should().Be(2);
        second.Recent.Should().Be(4, "the keys the first pass did are skipped, not done twice");
        _rewritten.Should().BeEquivalentTo("k0", "k1", "k2", "k3", "k4", "k5").And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_pass_whose_budget_is_gone_before_it_starts_still_takes_one_key_so_each_pass_moves_on()
    {
        var keys = new Keyspace();
        for (var i = 0; i < 3; i++)
        {
            keys.Add(0, $"k{i}", Json);
        }

        _outcomes["k0"] = () =>
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            return RewriteTally.Of(RewriteOutcome.Done);
        };

        var report = await Sweep(keys, new FormatSweepOptions { RatePerSecond = 1, PageSize = 3, Budget = TimeSpan.FromSeconds(1) });

        report.Converted.Should().Be(1);
        report.Completed.Should().BeFalse();
    }

    [Fact]
    public async Task A_key_that_is_recent_or_oversize_is_not_paced()
    {
        var keys = new Keyspace();
        keys.Add(0, "recent", Json);
        keys.Add(0, "oversize", [.. "\""u8, .. new byte[300 * 1024], .. "\""u8]);
        keys.Add(0, "work", Json);
        _migrator.TryClaimDirect(this, "recent", 1, out _);

        var report = await Sweep(keys, new FormatSweepOptions { RatePerSecond = 1 });

        report.Recent.Should().Be(1);
        report.Oversize.Should().Be(1);
        report.Converted.Should().Be(1);
        (_clock.GetUtcNow() - Start).Should().Be(TimeSpan.Zero, "only the key taken in full has a turn, and the first turn is at once");
    }

    [Fact]
    public async Task A_byte_budget_that_is_full_counts_each_key_as_held_and_the_pass_goes_on()
    {
        var keys = new Keyspace();
        keys.Add(0, "a", Json);
        keys.Add(0, "b", Json);
        _migrator.TryClaimDirect(new object(), "reads", (int)FormatMigrator.DefaultMaxQueuedBytes, out _).Should().Be(FormatMigrator.ClaimResult.Claimed);

        var report = await Sweep(keys, new FormatSweepOptions { RatePerSecond = 1000 });

        report.Held.Should().Be(2);
        report.Converted.Should().Be(0);
        report.Completed.Should().BeTrue("only the node list ends a pass");
    }

    [Fact]
    public async Task A_key_converted_meanwhile_is_current_and_not_held_back_as_lossy()
    {
        var keys = new Keyspace();
        keys.Add(0, "k", Json);
        keys.AfterLength = key => keys.Replace(key, Fast);

        var report = await Sweep(keys, new FormatSweepOptions { RatePerSecond = 1000 });

        report.Current.Should().Be(1);
        report.Lossy.Should().Be(0);
        _rewritten.Should().BeEmpty();
        _migrator.LossyKeys.Should().Be(0);
    }

    [Fact]
    public async Task The_census_gives_each_string_its_reasons_and_a_gone_key_none()
    {
        var keys = new Keyspace();
        keys.Add(0, "json", Json, TimeSpan.FromMinutes(1));
        keys.Add(0, "json-forever", Json);
        keys.Add(0, "fast", Fast, TimeSpan.FromMinutes(1));
        keys.Add(0, "unknown", Unknown, TimeSpan.FromMinutes(1));
        keys.Add(0, "empty", [], TimeSpan.FromMinutes(1));
        keys.Add(0, "oversize", [.. "\""u8, .. new byte[300 * 1024], .. "\""u8], TimeSpan.FromMinutes(1));
        keys.Add(0, "gone", null);

        var report = await Census(keys).CountByReasonAsync(new CacheKey("*"), ValueMigration.MaxValueBytes, Ct);

        report.ByFormat.Should().BeEquivalentTo(new Dictionary<string, long> { ["json"] = 3, ["fast"] = 1, ["unknown"] = 1 });
        report.ByReason.Should().BeEquivalentTo(new Dictionary<string, long>
        {
            ["format_json"] = 3,
            ["format_fast"] = 1,
            ["format_unknown"] = 1,
            ["empty"] = 1,
            ["oversize"] = 1,
            ["no_ttl"] = 1,
        });
        report.Scanned.Should().Be(6);
        await keys.Database.Received(3).StringLengthAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    private static Exception Transient(string kind) => kind switch
    {
        "connection" => new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "the connection dropped"),
        "timeout" => new RedisTimeoutException(CommandFlags.None, "the command timed out", CommandStatus.WaitingToBeSent),
        "busy" => new RedisServerException(RedisErrorKind.None, CommandFlags.None, "BUSY Redis is busy running a script"),
        _ => new RedisServerException(RedisErrorKind.None, CommandFlags.None, "LOADING Redis is loading the dataset in memory"),
    };

    // The fake delegate is told the bytes, so the key they belong to is looked up.
    private static string Name(Keyspace keys, byte[] original) => keys.NameOf(original);

    // Time moves only while the pass waits for its turn, so what it waited is the rate and nothing else.
    private async Task Drive(Task run)
    {
        while (!run.IsCompleted)
        {
            if (_clock.HasTimers)
            {
                _clock.Advance(TimeSpan.FromMilliseconds(100));
            }
            else
            {
                await Task.Delay(1, Ct);
            }
        }
    }

    private long Tracked(string outcome) =>
        (long)_telemetry.Metrics.Where(m => m.Name == Metrics.FormatSweep && m.Properties!["outcome"] == outcome && m.Properties["group"] == "default").Sum(m => m.Value);

    private FormatCensus Census(Keyspace keys)
    {
        var factory = new DefaultRedisKeyStrategyFactory();
        var options = new CacheOptions { AppShortName = "sweep" };
        return new FormatCensus(keys.Connector, string.Empty, _registry, factory.Create(options, typeof(RedisCache)), factory.Create(options, typeof(RedisHashCache)), options.KeyCasing);
    }

    private async Task<FormatSweepReport> Sweep(Keyspace keys, FormatSweepOptions options, object? owner = null)
    {
        var run = Begin(keys, options, Ct, owner);
        await Drive(run);
        return await run;
    }

    private async Task<FormatSweepReport> SweepWith(Keyspace keys, FormatSweepOptions options, CancellationToken token)
    {
        var run = Begin(keys, options, token);
        await Drive(run);
        return await run;
    }

    private Task<FormatSweepReport> Begin(Keyspace keys, FormatSweepOptions options, CancellationToken token, object? owner = null)
    {
        var migration = new ValueMigration(_migrator, new ValueRewriter(new SystemJsonByteSerializerProxy(), _registry), _registry, _effective);
        var sweeper = new FormatSweeper(
            Census(keys),
            keys.Connector,
            migration,
            _registry,
            _fast,
            owner ?? this,
            (key, original, _) =>
            {
                var name = key.ToString();
                _rewritten.Add(name);
                return ValueTask.FromResult(_outcomes.TryGetValue(name, out var outcome) ? outcome() : RewriteTally.Of(RewriteOutcome.Done));
            },
            original => _isDefault(Name(keys, original)),
            _telemetry,
            _clock);
        return sweeper.SweepAsync(new CacheKey("*"), options, token);
    }

    private sealed class AllowedFormat : IEffectiveFormat
    {
        public bool Allowed { get; set; } = true;

        public bool Allows(ICacheValueSerializer writer) => Allowed;
    }

    // A keyspace of strings spread over primaries; SCAN pages through a primary's keys in order, as many as COUNT says.
    private sealed class Keyspace
    {
        private readonly List<(int Primary, string Key, byte[]? Value, TimeSpan? Ttl)> _keys = [];
        private readonly IServer[] _primaries = [Primary("10.0.0.1:6379"), Primary("10.0.0.2:6379")];

        public Keyspace()
        {
            Database = Substitute.For<IDatabase>();
            var multiplexer = Substitute.For<IConnectionMultiplexer>();
            multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(Database);
            Database.Multiplexer.Returns(multiplexer);
            Database.StringGetRangeAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>()).Returns(ci => Range(ci.Arg<RedisKey>().ToString(), ci.ArgAt<long>(2)));
            Database.StringLengthAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(ci => Length(ci.Arg<RedisKey>().ToString()));
            Database.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(ci => Task.FromResult(Find(ci.Arg<RedisKey>().ToString()) is not null));
            Database.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(ci => Task.FromResult(_keys.FirstOrDefault(k => k.Key == ci.Arg<RedisKey>().ToString()).Ttl));
            Connector = Substitute.For<IRedisConnector>();
            Connector.Database.Returns(Database);
            Connector.GetPrimaries().Returns(_primaries);
            for (var i = 0; i < _primaries.Length; i++)
            {
                var index = i;
                _primaries[i].ExecuteAsync(Arg.Any<int>(), "INFO", Arg.Any<ICollection<object>>(), Arg.Any<CommandFlags>()).Returns(_ => Task.FromResult(Info(index)));
                _primaries[i].ExecuteAsync(Arg.Any<int>(), "SCAN", Arg.Any<ICollection<object>>(), Arg.Any<CommandFlags>()).Returns(ci => Task.FromResult(Page(index, ci.ArgAt<ICollection<object>>(2).ToArray())));
            }
        }

        public IDatabase Database { get; }

        public IRedisConnector Connector { get; }

        /// <summary>The run id each primary answers to INFO server; null answers none.</summary>
        public string?[] RunIds { get; } = new string?[2];

        /// <summary>The largest answer to a read past the two-byte one.</summary>
        public int MaxFetched { get; private set; }

        /// <summary>Asked for the bytes the migrator holds claimed at each read past the two-byte one.</summary>
        public Func<long>? Claimed { get; set; }

        public List<long> ClaimedAtRead { get; } = [];

        /// <summary>The keys whose STRLEN, or whose read past the two-byte one, answers WRONGTYPE.</summary>
        public HashSet<string> WrongTypeAtLength { get; } = [];

        public HashSet<string> WrongTypeAtRead { get; } = [];

        /// <summary>Told the key once its STRLEN has been answered, so a writer can come in between.</summary>
        public Action<string>? AfterLength { get; set; }

        /// <summary>Asked for each command, by its kind (<c>scan</c> with the cursor, <c>head</c>, <c>length</c> and <c>read</c> with the key); an exception it gives is what the command fails with.</summary>
        public Func<string, string, Exception?>? Fault { get; set; }

        public void Replace(string key, byte[] value)
        {
            var at = _keys.FindIndex(k => k.Key == key);
            _keys[at] = (_keys[at].Primary, key, value, _keys[at].Ttl);
        }

        public void Add(int primary, string key, byte[]? value, TimeSpan? ttl = null) => _keys.Add((primary, key, value, ttl));

        public string NameOf(byte[] value) => _keys.First(k => ReferenceEquals(k.Value, value) || (k.Value is not null && k.Value.AsSpan().SequenceEqual(value))).Key;

        private static IServer Primary(string endpoint)
        {
            var server = Substitute.For<IServer>();
            server.EndPoint.Returns(IPEndPoint.Parse(endpoint));
            server.IsReplica.Returns(false);
            return server;
        }

        private byte[]? Find(string key) => _keys.FirstOrDefault(k => k.Key == key).Value;

        private RedisResult Info(int primary) =>
            RedisResult.Create((RedisValue)("# Server\r\nredis_version:7.2.0\r\n" + (RunIds[primary] is { } id ? $"run_id:{id}\r\n" : string.Empty)), ResultType.BulkString);

        private Task<long> Length(string key)
        {
            if (WrongTypeAtLength.Contains(key))
            {
                return Task.FromException<long>(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "WRONGTYPE Operation against a key holding the wrong kind of value"));
            }

            if (Fault?.Invoke("length", key) is { } fault)
            {
                return Task.FromException<long>(fault);
            }

            var length = (long)(Find(key)?.Length ?? 0);
            AfterLength?.Invoke(key);
            return Task.FromResult(length);
        }

        // As GETRANGE: the bytes from the start to the end given, both ends counted, never more than the value has.
        private Task<RedisValue> Range(string key, long end)
        {
            if (Fault?.Invoke(end > 1 ? "read" : "head", key) is { } fault)
            {
                return Task.FromException<RedisValue>(fault);
            }

            if (end > 1)
            {
                if (WrongTypeAtRead.Contains(key))
                {
                    return Task.FromException<RedisValue>(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "WRONGTYPE Operation against a key holding the wrong kind of value"));
                }

                ClaimedAtRead.Add(Claimed?.Invoke() ?? 0);
            }

            var answer = Find(key) is { } value ? (RedisValue)value[..(int)Math.Min(end + 1, value.Length)] : RedisValue.Null;
            if (end > 1)
            {
                MaxFetched = Math.Max(MaxFetched, answer.IsNull ? 0 : ((byte[])answer!).Length);
            }

            return Task.FromResult(answer);
        }

        private RedisResult Page(int primary, object[] arguments)
        {
            var cursor = (int)(ulong)arguments[0];
            if (Fault?.Invoke("scan", cursor.ToString(System.Globalization.CultureInfo.InvariantCulture)) is { } fault)
            {
                throw fault;
            }

            var count = (int)arguments[4];
            var type = (string)arguments[6];
            var mine = type == "string" ? _keys.Where(k => k.Primary == primary).Select(k => k.Key).ToArray() : [];
            var page = mine.Skip(cursor).Take(count).Select(key => RedisResult.Create((RedisValue)System.Text.Encoding.UTF8.GetBytes(key), ResultType.BulkString)).ToArray();
            var next = cursor + count >= mine.Length ? 0 : cursor + count;
            return RedisResult.Create([RedisResult.Create((RedisValue)next.ToString(System.Globalization.CultureInfo.InvariantCulture), ResultType.BulkString), RedisResult.Create(page)]);
        }
    }
}
