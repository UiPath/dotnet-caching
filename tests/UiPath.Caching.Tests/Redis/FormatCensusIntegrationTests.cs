using System.Net;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;
using UiPath.Caching.Config;
using UiPath.Caching.Policies;
using UiPath.Caching.Redis;
using UiPath.Caching.Serialization;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Redis;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class FormatCensusIntegrationTests(RedisContainerFixture fixture)
{
    private static readonly byte[] Json = "\"x\""u8.ToArray();
    private static readonly byte[] Fast = [0x10, (byte)'x'];
    private static readonly byte[] Other = [0x12, (byte)'x'];
    private static readonly byte[] Extended = [0x1F, 0x01, (byte)'x'];
    private static readonly byte[] Unknown = [0x14, (byte)'x'];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Counts_keys_per_format()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = mux.GetDatabase();
        var run = Run();
        await SetAsync(db, run, "json", 3, Json);
        await SetAsync(db, run, "fast", 2, Fast);
        await SetAsync(db, run, "unknown", 1, Unknown);
        using var connector = new Connector(mux);
        var sut = Provider(connector);

        var counts = await sut.CountFormatsAsync($"{run}:*", Ct);

        counts.Should().BeEquivalentTo(new Dictionary<string, long> { ["json"] = 3, ["fast"] = 2, ["unknown"] = 1 });
    }

    [Fact]
    public async Task A_registered_id_counts_under_its_serializers_name_and_an_extended_id_is_read_in_full()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = mux.GetDatabase();
        var run = Run();
        await SetAsync(db, run, "other", 2, Other);
        await SetAsync(db, run, "ext", 1, Extended);
        await SetAsync(db, run, "cut", 1, [0x1F]);
        await SetAsync(db, run, "extunknown", 1, [0x1F, 0x7F, (byte)'x']);
        using var connector = new Connector(mux);
        var sut = Provider(connector, extended: true);

        var counts = await sut.CountFormatsAsync($"{run}:*", Ct);

        counts.Should().BeEquivalentTo(new Dictionary<string, long> { ["other"] = 2, ["wide"] = 1, ["unknown"] = 2 });
    }

    [Fact]
    public async Task A_hash_counts_by_its_first_user_field_whatever_the_system_fields_hold()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = mux.GetDatabase();
        var run = Run();
        await db.HashSetAsync($"census:h:{run}:h-json", [new("_version_", 3), new("_metadata_", Fast), new("a", Json)]);
        await db.HashSetAsync($"census:h:{run}:h-fast", [new("_metadata_", Json), new("_version_", 1), new("a", Fast), new("b", Fast)]);
        await db.HashSetAsync($"census:h:{run}:h-unknown", [new("a", Unknown)]);
        await db.HashSetAsync($"census:h:{run}:h-only-system", [new("_version_", 3), new("_metadata_", Json)]);
        await db.HashSetAsync($"census:h:{run}:h-marker", [new("_metadata_", RedisValue.EmptyString)]);
        using var connector = new Connector(mux);
        var sut = Provider(connector);

        var counts = await sut.CountFormatsAsync($"{run}:*", Ct);

        counts.Should().BeEquivalentTo(new Dictionary<string, long> { ["json"] = 1, ["fast"] = 1, ["unknown"] = 1 });
    }

    [Fact]
    public async Task A_hash_with_fields_in_two_formats_is_counted_under_both_so_no_json_means_none_left()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = mux.GetDatabase();
        var run = Run();
        // Whichever field a scan meets first, the hash still has JSON in it.
        await db.HashSetAsync($"census:h:{run}:a-first", [new("a", Fast), new("b", Json)]);
        await db.HashSetAsync($"census:h:{run}:a-last", [new("a", Json), new("b", Fast)]);
        await db.HashSetAsync($"census:h:{run}:three", [new("a", Fast), new("b", Other), new("c", Unknown), new("_version_", 2)]);
        using var connector = new Connector(mux);

        var counts = await Provider(connector).CountFormatsAsync($"{run}:*", Ct);

        counts.Should().BeEquivalentTo(new Dictionary<string, long> { ["json"] = 2, ["fast"] = 3, ["other"] = 1, ["unknown"] = 1 });
    }

    [Fact]
    public async Task A_hash_is_looked_at_for_no_more_than_the_first_fields()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = mux.GetDatabase();
        var run = Run();
        var entries = Enumerable.Range(0, FormatCensus.MaxHashFields * 3).Select(i => new HashEntry($"f{i:D4}", Fast)).ToArray();
        await db.HashSetAsync($"census:h:{run}:huge", entries);
        using var connector = new Connector(mux);
        var before = await HScansAsync();

        var counts = await Provider(connector).CountFormatsAsync($"{run}:*", Ct);

        counts.Should().BeEquivalentTo(new Dictionary<string, long> { ["fast"] = 1 });
        // A page is a hundred fields: the first two hundred and fifty-six take three, where all of them would take eight or more.
        (await HScansAsync() - before).Should().BeInRange(1, 4);
    }

    [Fact]
    public async Task The_example_in_the_docs_counts_what_the_caches_wrote_under_the_default_key_layout()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = MigrateOnReadHarness.Build(fixture.ConnectionString, FormatNegotiationMode.Force, $"cen{Guid.NewGuid():N}"[..16]);
        var policy = harness.Resolve(MigrateOnReadHarness.Migrating);
        await harness.Cache().SetAsync("Orders:1", "x", policy, Ct);
        await harness.Cache().SetAsync("orders:2", "y", policy: null, Ct);
        await harness.Cache().SetAsync("invoices:1", "z", policy, Ct);
        await harness.Hashes().SetAsync("orders:h1", new Dictionary<string, string?> { ["a"] = "x" }, new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(5)), policy, Ct);
        var redis = harness.Services.GetServices<ICacheProvider>().OfType<RedisCacheProvider>().Single();

        var counts = await redis.CountFormatsAsync("orders:*", Ct);

        counts.Should().BeEquivalentTo(new Dictionary<string, long> { ["fast"] = 2, ["json"] = 1 });
        (await redis.CountFormatsAsync("ORDERS:*", Ct)).Should().BeEquivalentTo(counts, "names are cased as the caches case them");
    }

    [Fact]
    public async Task A_decorated_connector_whose_prefix_has_glob_characters_is_looked_for_under_that_prefix()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var run = Run();
        var prefix = $"census-[{Guid.NewGuid():N}]*?:";
        using var prefixed = new Connector(mux, prefix);
        await SetAsync(prefixed.Database, run, "k", 3, Json);
        await SetAsync(mux.GetDatabase(), run, "bare", 4, Fast);
        // Not StackExchange.Redis's own database, so the prefix is not read off its type: it is asked of the server, and nothing is configured.
        using var opaque = new Connector(mux, database: Forwarding<IDatabase>.Wrap(prefixed.Database));

        var counts = await Provider(opaque).CountFormatsAsync($"{run}:*", Ct);

        counts.Should().BeEquivalentTo(new Dictionary<string, long> { ["json"] = 3 });
    }

    [Fact]
    public async Task Glob_characters_in_what_the_key_strategy_generates_are_literal_and_those_of_the_pattern_are_not()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = mux.GetDatabase();
        var run = Run();
        // The application is called app[1]; a neighbour is called app1, which a glob would read app[1] as.
        await db.StringSetAsync($"app[1]:s:{run}:k1", Json);
        await db.StringSetAsync($"app[1]:s:{run}:k2", Json);
        await db.StringSetAsync($"app[1]:s:{run}:other", Fast);
        await db.StringSetAsync($"app1:s:{run}:k1", Fast);
        await db.HashSetAsync($"app[1]:h:{run}:k1", [new("a", Json)]);
        await db.HashSetAsync($"app1:h:{run}:k1", [new("a", Fast)]);
        using var connector = new Connector(mux);

        var counts = await Provider(connector, app: "app[1]").CountFormatsAsync($"{run}:k*", Ct);

        counts.Should().BeEquivalentTo(new Dictionary<string, long> { ["json"] = 3 }, "the pattern's star still matches, the application's bracket does not");
    }

    [Fact]
    public async Task What_a_custom_strategy_puts_after_the_name_is_literal_too()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = mux.GetDatabase();
        var run = Run();
        await db.StringSetAsync($"v[1]:{run}:k1:[tail]", Json);
        await db.StringSetAsync($"v[1]:{run}:k2:[tail]", Json);
        await db.StringSetAsync($"v1:{run}:k1:t", Fast);
        await db.HashSetAsync($"v[1]:h:{run}:k1:[tail]", [new("a", Json)]);
        using var connector = new Connector(mux);

        var counts = await Provider(connector, strategies: new BracketedStrategies()).CountFormatsAsync($"{run}:k*", Ct);

        counts.Should().BeEquivalentTo(new Dictionary<string, long> { ["json"] = 3 });
    }

    [Fact]
    public async Task A_page_has_a_bounded_number_of_commands_in_flight()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var run = Run();
        // One page of three hundred keys, as a server answers a dense keyspace; the keys need not exist, the commands are what is counted.
        var page = RedisResult.Create(
        [
            RedisResult.Create((RedisValue)"0", ResultType.BulkString),
            RedisResult.Create([.. Enumerable.Range(0, 300).Select(i => RedisResult.Create((RedisValue)$"census:s:{run}:k{i}", ResultType.BulkString))]),
        ]);
        var empty = RedisResult.Create([RedisResult.Create((RedisValue)"0", ResultType.BulkString), RedisResult.Create(Array.Empty<RedisResult>())]);
        var server = Forwarding<IServer>.Wrap(
            mux.GetServer(mux.GetEndPoints()[0]),
            before: (method, args) => method.Name == nameof(IServer.ExecuteAsync) && args is [_, "SCAN", ICollection<object> scan, ..]
                ? (true, Task.FromResult(scan.Contains("string") ? page : empty))
                : (false, null));
        var started = 0;
        var inFlight = 0;
        var most = 0;
        IDatabase Counting(int index) => Forwarding<IDatabase>.Wrap(
            mux.GetDatabase(index),
            before: (method, args) =>
            {
                if (method.Name == nameof(IDatabase.StringGetRangeAsync))
                {
                    var now = Interlocked.Increment(ref inFlight);
                    Interlocked.Increment(ref started);
                    lock (this)
                    {
                        most = Math.Max(most, now);
                    }
                }

                return (false, null);
            },
            after: (method, args, result) => method.Name == nameof(IDatabase.StringGetRangeAsync) && result is Task<RedisValue> task
                ? task.ContinueWith(
                    done =>
                    {
                        Interlocked.Decrement(ref inFlight);
                        return done.Result;
                    },
                    TaskScheduler.Default)
                : result);
        var multiplexer = Forwarding<IConnectionMultiplexer>.Wrap(mux, before: (method, args) => method.Name == nameof(IConnectionMultiplexer.GetDatabase) ? (true, Counting((int)args[0]!)) : (false, null));
        var database = Forwarding<IDatabase>.Wrap(mux.GetDatabase(), before: (method, args) => method.Name == "get_Multiplexer" ? (true, multiplexer) : (false, null));
        using var connector = new Connector(mux, database: database, primaries: [server]);

        await Provider(connector).CountFormatsAsync($"{run}:*", Ct);

        started.Should().Be(300);
        most.Should().BeInRange(2, 32);
    }

    [Fact]
    public async Task A_prefix_with_glob_characters_is_looked_for_as_it_is_not_as_a_pattern()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var run = Run();
        var prefix = $"census-[{Guid.NewGuid():N}]*?:";
        using var connector = new Connector(mux, prefix);
        await SetAsync(connector.Database, run, "k", 3, Json);
        // A key that the prefix would match if its characters were read as a glob.
        await SetAsync(mux.GetDatabase(), run, "decoy", 4, Fast);
        await mux.GetDatabase().StringSetAsync($"census-1{Guid.NewGuid():N}x:census:s:{run}:decoy", Fast);

        var counts = await Provider(connector, keyPrefix: prefix).CountFormatsAsync($"{run}:*", Ct);

        counts.Should().BeEquivalentTo(new Dictionary<string, long> { ["json"] = 3 });
    }

    [Fact]
    public async Task A_big_hash_is_read_by_pages_until_a_user_field_turns_up()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = mux.GetDatabase();
        var run = Run();
        var entries = Enumerable.Range(0, 600).Select(i => new HashEntry($"f{i:D4}", Fast)).Append(new("_version_", 9)).Append(new("_metadata_", Json)).ToArray();
        await db.HashSetAsync($"census:h:{run}:big", entries);
        using var connector = new Connector(mux);
        var sut = Provider(connector);

        var counts = await sut.CountFormatsAsync($"{run}:*", Ct);

        counts.Should().BeEquivalentTo(new Dictionary<string, long> { ["fast"] = 1 });
    }

    [Fact]
    public async Task Other_types_empty_values_and_keys_outside_the_pattern_are_not_counted()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = mux.GetDatabase();
        var run = Run();
        await db.StringSetAsync($"census:s:{run}:one", Json);
        await db.StringSetAsync($"census:s:{run}:empty", RedisValue.EmptyString);
        await db.SetAddAsync($"census:s:{run}:set", "member");
        await db.SetAddAsync($"census:h:{run}:set", "member");
        await db.ListRightPushAsync($"census:s:{run}:list", "item");
        await db.SortedSetAddAsync($"census:s:{run}:zset", "m", 1);
        await db.StreamAddAsync($"census:s:{run}:stream", "k", "v");
        await db.StringSetAsync($"census:s:{Run()}:elsewhere", Fast);
        using var connector = new Connector(mux);
        var sut = Provider(connector);

        var counts = await sut.CountFormatsAsync($"{run}:*", Ct);

        counts.Should().BeEquivalentTo(new Dictionary<string, long> { ["json"] = 1 });
    }

    [Fact]
    public async Task A_pattern_that_matches_nothing_counts_nothing()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        using var connector = new Connector(mux);

        var counts = await Provider(connector).CountFormatsAsync($"{Run()}:*", Ct);

        counts.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_pattern_is_looked_for_under_the_connectors_key_prefix_and_keys_without_it_are_not_counted(bool exposePrimaries)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var run = Run();
        var prefix = $"census-{Guid.NewGuid():N}:";
        using var connector = new Connector(mux, prefix, exposePrimaries);
        await SetAsync(connector.Database, run, "k", 2, Json);
        await SetAsync(mux.GetDatabase(), run, "bare", 5, Fast);
        var sut = Provider(connector, keyPrefix: prefix);

        var counts = await sut.CountFormatsAsync($"{run}:*", Ct);

        counts.Should().BeEquivalentTo(new Dictionary<string, long> { ["json"] = 2 });
        (await mux.GetDatabase().KeyExistsAsync($"{prefix}census:s:{run}:k0")).Should().BeTrue("the keys live under the prefix");
    }

    [Fact]
    public async Task Every_primary_the_connector_names_is_scanned()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = mux.GetDatabase();
        var run = Run();
        await SetAsync(db, run, "k", 4, Json);
        var server = mux.GetServer(mux.GetEndPoints()[0]);
        using var connector = new Connector(mux, primaries: [server, server]);

        var counts = await Provider(connector).CountFormatsAsync($"{run}:*", Ct);

        counts["json"].Should().Be(8, "each primary is asked, as each shard of a cluster would be");
    }

    [Fact]
    public async Task A_token_cancelled_during_the_scan_stops_it_after_the_page_in_hand()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = mux.GetDatabase();
        var run = Run();
        await SetAsync(db, run, "k", 2_500, Json);
        using var connector = new Connector(mux);
        using var cancel = new CancellationTokenSource();
        var pages = 0;
        var census = Census(connector, onPage: () =>
        {
            pages++;
            cancel.Cancel();
        });

        var act = () => census.CountAsync($"{run}:*", cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        pages.Should().Be(1);
    }

    [Fact]
    public async Task A_scan_page_looks_at_many_keys_so_a_big_keyspace_is_not_walked_ten_at_a_time()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = mux.GetDatabase();
        var run = Run();
        await SetAsync(db, run, "k", 2_000, Json);
        using var connector = new Connector(mux);
        var pages = 0;
        var census = Census(connector, onPage: () => pages++);
        var keys = await mux.GetServer(mux.GetEndPoints()[0]).DatabaseSizeAsync();

        var counts = await census.CountAsync($"{run}:*", Ct);

        counts["json"].Should().Be(2_000);
        // Two passes, one per type; the default page of ten keys would take a hundred times as many as this.
        pages.Should().BeLessThanOrEqualTo(2 * ((int)(keys / 100) + 5));
    }

    [Fact]
    public async Task Each_page_is_asked_for_one_type_and_a_cancelled_scan_asks_for_no_more()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = mux.GetDatabase();
        var run = Run();
        await SetAsync(db, run, "k", 2_500, Json);
        var scans = new List<object?[]>();
        var server = Forwarding<IServer>.Wrap(
            mux.GetServer(mux.GetEndPoints()[0]),
            before: (method, args) =>
            {
                if (method.Name == nameof(IServer.ExecuteAsync) && args is [_, "SCAN", ..])
                {
                    lock (scans)
                    {
                        scans.Add(args);
                    }
                }

                return (false, null);
            });
        using var connector = new Connector(mux, primaries: [server]);

        await Provider(connector).CountFormatsAsync($"{run}:*", Ct);

        var arguments = scans.Select(s => ((IEnumerable<object>)s[2]!).Select(a => a.ToString()!).ToList()).ToList();
        arguments.Should().OnlyContain(a => a.Contains("TYPE") && a.Contains("COUNT") && a.Contains("MATCH"));
        arguments.Select(a => a[a.IndexOf("TYPE") + 1]).Distinct().Should().BeEquivalentTo(["string", "hash"]);
        arguments.Select(a => a[a.IndexOf("COUNT") + 1]).Distinct().Should().Equal(FormatCensus.ScanCount.ToString());

        scans.Clear();
        using var cancel = new CancellationTokenSource();
        var census = Census(connector, onPage: cancel.Cancel);
        var act = () => census.CountAsync($"{run}:*", cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        scans.Should().HaveCount(1, "the page in hand is finished and no other is asked for");
    }

    [Fact]
    public async Task The_configured_key_prefix_stands_in_when_the_connectors_cannot_be_read()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var run = Run();
        var prefix = $"census-{Guid.NewGuid():N}:";
        using var prefixed = new Connector(mux, prefix);
        await SetAsync(prefixed.Database, run, "k", 3, Json);
        // A database that is not StackExchange.Redis's own, and that does not answer the probe, says nothing of its prefix.
        var database = Forwarding<IDatabase>.Wrap(
            prefixed.Database,
            before: (method, args) => method.Name == nameof(IDatabase.ExecuteAsync) ? throw new NotSupportedException("no probe") : (false, null));
        using var opaque = new Connector(mux, database: database);

        var counts = await Provider(opaque, keyPrefix: prefix).CountFormatsAsync($"{run}:*", Ct);

        counts.Should().BeEquivalentTo(new Dictionary<string, long> { ["json"] = 3 });
    }

    [Fact]
    public async Task A_token_cancelled_before_the_scan_throws_before_asking_the_server()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        using var connector = new Connector(mux);
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        var act = () => Provider(connector).CountFormatsAsync("*", cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_blank_pattern_is_refused()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        using var connector = new Connector(mux);
        var sut = Provider(connector);

        await sut.Invoking(s => s.CountFormatsAsync(" ", Ct)).Should().ThrowAsync<ArgumentException>();
        await sut.Invoking(s => s.CountFormatsAsync(null!, Ct)).Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(new byte[0], null)]
    [InlineData(new byte[] { (byte)'"' }, "json")]
    [InlineData(new byte[] { (byte)'{', (byte)'}' }, "json")]
    [InlineData(new byte[] { 0x10 }, "fast")]
    [InlineData(new byte[] { 0x10, 1 }, "fast")]
    [InlineData(new byte[] { 0x12, 1 }, "other")]
    [InlineData(new byte[] { 0x14, 1 }, "unknown")]
    [InlineData(new byte[] { 0x02, 1 }, "unknown")]
    [InlineData(new byte[] { 0x1F }, "unknown")]
    [InlineData(new byte[] { 0x1F, 0x01 }, "wide")]
    [InlineData(new byte[] { 0x1F, 0x02 }, "unknown")]
    public void A_value_is_classified_by_its_first_bytes(byte[] head, string? expected)
    {
        var registry = new ValueSerializerRegistry([("fast", new PassthroughValueSerializer(0x10), false), ("other", new PassthroughValueSerializer(0x12), false), ("wide", new PassthroughValueSerializer(0x101), false)]);

        FormatCensus.Classify(head, registry).Should().Be(expected);
    }

    private static FormatCensus Census(IRedisConnector connector, Action? onPage = null)
    {
        var factory = new DefaultRedisKeyStrategyFactory();
        var options = new CacheOptions { AppShortName = "census" };
        return new FormatCensus(
            connector,
            string.Empty,
            new ValueSerializerRegistry([]),
            factory.Create(options, typeof(RedisCache)),
            factory.Create(options, typeof(RedisHashCache)),
            options.KeyCasing,
            onPage);
    }

    private static string Run() => $"census{Guid.NewGuid():N}"[..24];

    private static async Task SetAsync(IDatabase db, string run, string name, int count, byte[] value)
    {
        for (var i = 0; i < count; i++)
        {
            await db.StringSetAsync($"census:s:{run}:{name}{i}", value, TimeSpan.FromMinutes(5));
        }
    }

    private static RedisCacheProvider Provider(IRedisConnector connector, string keyPrefix = "", bool extended = false, string app = "census", IRedisKeyStrategyFactory? strategies = null)
    {
        var registry = new ValueSerializerRegistry(
            [
                ("fast", new PassthroughValueSerializer(0x10), false),
                ("other", new PassthroughValueSerializer(0x12), false),
                .. extended ? new[] { ("wide", (ICacheValueSerializer)new PassthroughValueSerializer(0x101), false) } : [],
            ]);
        var options = new RedisCacheOptions { KeyPrefix = keyPrefix, RedisKeyStrategyFactory = strategies };
        return new RedisCacheProvider(
            Options.Create(options),
            Options.Create(new CacheOptions { AppShortName = app }),
            connector,
            new SystemJsonByteSerializerProxy(),
            EmptyResiliencePipelineProvider.Instance,
            NullTelemetryProvider.Instance,
            NullLoggerFactory.Instance,
            new DefaultCachePolicyFactory(null, null, valueSerializers: registry),
            TimeProvider.System,
            keyMaskingPolicy: null,
            registry);
    }

    private async Task<long> HScansAsync()
    {
        await using var admin = await ConnectionMultiplexer.ConnectAsync($"{fixture.ConnectionString},allowAdmin=true");
        var stats = (await admin.GetServer(admin.GetEndPoints()[0]).InfoAsync("commandstats")).SelectMany(group => group).ToList();
        var line = stats.FirstOrDefault(pair => pair.Key == "cmdstat_hscan").Value;
        return line is null ? 0 : long.Parse(line.Split(',')[0].Split('=')[1], System.Globalization.CultureInfo.InvariantCulture);
    }

    // v[1]:{name}:[tail] for a string and v[1]:h:{name}:[tail] for a hash: glob characters before and after the name.
    private sealed class BracketedStrategies : IRedisKeyStrategyFactory
    {
        public IRedisKeyStrategy Create(CacheOptions options, Type cacheType) =>
            new Bracketed(typeof(ICache).IsAssignableFrom(cacheType) ? "v[1]:" : "v[1]:h:");

        public IRedisKeyStrategy Create(CacheOptions options, string differentiator) => new Bracketed("v[1]:");

        private sealed class Bracketed(string prefix) : IRedisKeyStrategy
        {
            public RedisKey GetRedisKey(CacheKey key) => $"{prefix}{key.Name}:[tail]";
        }
    }

    private sealed class Connector(IConnectionMultiplexer mux, string? prefix = null, bool exposePrimaries = true, IServer[]? primaries = null, IDatabase? database = null) : IRedisConnector
    {
        public event EventHandler? OnConnectionFailed
        {
            add { }
            remove { }
        }

        public event EventHandler? OnConnectionRestored
        {
            add { }
            remove { }
        }

        public event EventHandler? OnReconnected
        {
            add { }
            remove { }
        }

        public bool IsConnected => mux.IsConnected;

        public Version Version => mux.GetServer(mux.GetEndPoints()[0]).Version;

        public IDatabase Database => database ?? (prefix is null ? mux.GetDatabase() : mux.GetDatabase().WithKeyPrefix(prefix));

        public ISubscriber Subscriber => mux.GetSubscriber();

        public void ForceReconnect()
        {
        }

        public EndPoint[] GetEndPoints(bool configuredOnly = false) => mux.GetEndPoints(configuredOnly);

        public IEnumerable<IServer> GetPrimaries() =>
            primaries ?? (exposePrimaries ? mux.GetServers().Where(s => !s.IsReplica && s.IsConnected).ToArray() : []);

        public void Dispose()
        {
        }
    }
}
