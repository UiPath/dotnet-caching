using System.Buffers;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Policies;
using UiPath.Caching.Redis;
using UiPath.Caching.Serialization;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Redis;

// A hash is read as a whole or by field, and each field it carries is decided on its own: the fields in another readable format are rewritten, the ones in the
// write format are left, and _version_ (a counter) and _metadata_ (kept in the format the hash was last written in) are never part of a rewrite.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class MigrateOnReadHashIntegrationTests(RedisContainerFixture fixture)
{
    private static readonly byte[] JsonX = "\"x\""u8.ToArray();
    private static readonly byte[] JsonY = "\"y\""u8.ToArray();
    private static readonly byte[] FastX = [0x10, (byte)'x'];
    private static readonly byte[] FastY = [0x10, (byte)'y'];
    private static readonly byte[] OtherZ = [0x12, (byte)'z'];
    private static readonly byte[] FastZ = [0x10, (byte)'z'];
    private static readonly byte[] JsonMeta = "{\"m\":\"v\"}"u8.ToArray();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string, string> States()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var layer in new[] { "redis", "layered" })
        {
            foreach (var operation in new[] { "all", "entry", "getoradd" })
            {
                data.Add(layer, "force", operation);
                data.Add(layer, "switched", operation);
            }
        }

        return data;
    }

    // policy, mode and state, what a field is stored as, what the read answers for it, and what is stored after
    public static TheoryData<string, string, string, string?, string?> FieldCases() => new()
    {
        { "force", "mig", "json", "x", "fast" },
        { "force", "mig", "other", "x", "fast" },
        { "force", "mig", "fast", "x", "fast" },
        { "force", "mig", "unknown", null, "unknown" },
        { "force", "narrow", "other", null, "other" },
        { "force", "narrow", "json", "x", "fast" },
        { "force", "plain", "json", "x", "json" },
        { "force", "unset", "json", "x", "json" },
        { "force", "tojson", "json", "x", "json" },
        { "force", "tojson", "fast", "x", "fast" },
        { "held", "mig", "json", "x", "json" },
        { "held", "narrow", "other", null, "other" },
        { "held", "narrow", "json", "x", "json" },
        { "held", "mig", "unknown", null, "unknown" },
        { "held", "open", "unknown", null, "unknown" },
        { "held", "tojson", "fast", "x", "fast" },
        { "held", "mig", "other", "x", "other" },
        { "held", "open", "json", "x", "json" },
        { "switched", "open", "json", "x", "fast" },
        { "switched", "mig", "other", "x", "fast" },
        { "switched", "plain", "json", "x", "json" },
    };

    [Theory]
    [MemberData(nameof(States))]
    public async Task Each_field_is_decided_on_its_own_and_version_and_metadata_are_left_as_they_are(string layer, string state, string operation)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var layered = layer == "layered";
        using var harness = Build(state == "force" ? FormatNegotiationMode.Force : FormatNegotiationMode.Auto, layered);
        if (state == "switched")
        {
            harness.Switch();
        }

        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(harness, raw, ("a", JsonX), ("b", FastY), ("c", OtherZ));
        await raw.GetDatabase().HashSetAsync(redisKey, [new("_version_", 7), new("_metadata_", JsonMeta)]);
        var policy = harness.Resolve(MigrateOnReadHarness.Migrating);

        var read = await ReadAsync(harness, layered, operation, key, policy);
        await harness.Migrator.WhenIdleAsync(Ct);

        read.Should().BeEquivalentTo(new Dictionary<string, string?> { ["a"] = "x", ["b"] = "y", ["c"] = "z" });
        var hash = (await raw.GetDatabase().HashGetAllAsync(redisKey)).ToDictionary(e => e.Name.ToString(), e => (byte[]?)e.Value);
        hash["a"].Should().Equal(FastX, "JSON is rewritten");
        hash["b"].Should().Equal(FastY, "a field already in the write format is left alone");
        hash["c"].Should().Equal(FastZ, "another readable format is rewritten");
        hash["_version_"].Should().Equal("7"u8.ToArray());
        hash["_metadata_"].Should().Equal(JsonMeta, "metadata stays in the format the hash was written in");
        harness.Outcomes("done").Should().Be(2, "one per field that was rewritten");
        harness.Outcomes("changed").Should().Be(0);
        harness.Outcomes("lossy").Should().Be(0, "the field in the write format is not even offered");
        harness.Outcomes("failed").Should().Be(0);
        (await raw.GetDatabase().KeyTimeToLiveAsync(redisKey))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(4));
    }

    [Theory]
    [MemberData(nameof(FieldCases))]
    public async Task A_field_is_rewritten_upward_only_where_the_format_is_effective_and_a_field_that_cannot_be_read_keeps_the_whole_hash_as_it_is(
        string state, string policyName, string stored, string? answer, string? after)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(state == "force" ? FormatNegotiationMode.Force : FormatNegotiationMode.Auto);
        if (state == "switched")
        {
            harness.Switch();
        }

        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var bytes = Stored(stored);
        // The second field is readable by every policy, so a miss on the first is the policy's, not the hash's.
        var (key, redisKey) = await SeedAsync(harness, raw, ("a", bytes), ("b", JsonY));
        var policy = harness.Resolve(policyName);

        var read = await harness.Hashes().GetAsync<string>(key, policy, Ct);
        await harness.Migrator.WhenIdleAsync(Ct);

        var hash = (await raw.GetDatabase().HashGetAllAsync(redisKey)).ToDictionary(e => e.Name.ToString(), e => (byte[]?)e.Value);
        hash["a"].Should().Equal(Stored(after!));
        if (answer is null)
        {
            read.Should().BeEmpty("a hash with a field the policy cannot read misses whole");
            hash["b"].Should().Equal(JsonY, "and nothing in it is rewritten");
            harness.Outcomes("done").Should().Be(0);
        }
        else
        {
            read["a"].Should().Be(answer);
            read["b"].Should().Be("y");
        }
    }

    [Theory]
    [InlineData("all")]
    [InlineData("entry")]
    [InlineData("fields")]
    [InlineData("getoradd")]
    public async Task A_hash_with_a_field_the_policy_cannot_read_offers_nothing_on_any_read_path(string operation)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Force);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(harness, raw, ("a", [0x12, (byte)'x']), ("b", JsonY));
        var policy = harness.Resolve(MigrateOnReadHarness.Narrow);

        var generated = new Dictionary<string, string?> { ["g"] = "generated" };
        var read = operation switch
        {
            "fields" => await harness.Hashes().GetAsync<string>(key, ["a", "b"], policy, Ct),
            "getoradd" => await harness.Hashes().GetOrAddAsync<string>(key, _ => Task.FromResult<IDictionary<string, string?>>(generated), TimeSpan.FromMinutes(5), policy, Ct),
            _ => await ReadAsync(harness, layered: false, operation, key, policy),
        };
        await harness.Migrator.WhenIdleAsync(Ct);

        read.Should().NotContainKey("a");
        harness.Telemetry.Metrics.Where(m => m.Name == Metrics.SerializerRewrite).Should().BeEmpty();
        if (operation != "getoradd")
        {
            (await StoredAsync(raw, redisKey, "b")).Single().Should().Equal(JsonY);
        }
    }

    [Fact]
    public async Task A_hash_rewrite_goes_in_commands_of_the_field_batch_size()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var app = App();
        using var seeder = Build(FormatNegotiationMode.Force, app: app);
        var writes = new CountingWrites();
        var sizes = new List<int>();
        using var harness = Build(
            FormatNegotiationMode.Force,
            scripts: true,
            app: app,
            hashBatch: 10,
            configure: b =>
            {
                b.Services.AddSingleton<IResiliencePipelineProvider>(writes);
                b.Services.AddSingleton<IRedisConnector>(sp => new InterceptingConnector(
                    ActivatorUtilities.CreateInstance<RedisConnector>(sp),
                    before: (method, args) =>
                    {
                        if (method.Name == nameof(IDatabase.ScriptEvaluateAsync) && args is [_, _, RedisValue[] values, ..])
                        {
                            lock (sizes)
                            {
                                sizes.Add(values.Length);
                            }
                        }

                        return (false, null);
                    }));
            });
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var fields = Enumerable.Range(0, 25).Select(i => ($"f{i}", JsonX)).ToArray();
        var (key, redisKey) = await SeedAsync(seeder, raw, fields);

        (await harness.Hashes().GetAsync<string>(key, harness.Resolve(MigrateOnReadHarness.Migrating), Ct)).Should().HaveCount(25);
        await harness.Migrator.WhenIdleAsync(Ct);

        writes.Count.Should().Be(3, "25 fields in commands of 10");
        sizes.Where(size => size > 0).Order().Should().Equal(15, 30, 30);
        (await StoredAsync(raw, redisKey, fields.Select(f => f.Item1).ToArray())).Should().AllSatisfy(v => v.Should().Equal(FastX));
        harness.Outcomes("done").Should().Be(25);
    }

    [Theory]
    [InlineData("redis", "all")]
    [InlineData("redis", "entry")]
    [InlineData("redis", "getoradd")]
    [InlineData("layered", "all")]
    [InlineData("layered", "entry")]
    [InlineData("layered", "getoradd")]
    public async Task Nothing_in_a_hash_is_rewritten_while_the_node_list_holds_the_format_back_on_any_read_path(string layer, string operation)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var layered = layer == "layered";
        using var harness = Build(FormatNegotiationMode.Auto, layered);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(harness, raw, ("a", JsonX), ("b", [0x12, (byte)'x']), ("c", FastY));

        var read = await ReadAsync(harness, layered, operation, key, harness.Resolve(MigrateOnReadHarness.Migrating));
        await harness.Migrator.WhenIdleAsync(Ct);

        read.Should().HaveCount(3);
        (await StoredAsync(raw, redisKey, "a", "b", "c")).Should().SatisfyRespectively(
            a => a.Should().Equal(JsonX),
            b => b.Should().Equal([0x12, (byte)'x']),
            c => c.Should().Equal(FastY));
        harness.Telemetry.Metrics.Where(m => m.Name == Metrics.SerializerRewrite).Should().BeEmpty();
    }

    [Fact]
    public async Task A_hash_rewrite_that_waited_asks_the_node_list_again_before_each_batch()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var app = App();
        using var seeder = Build(FormatNegotiationMode.Auto, app: app);
        MigrateOnReadHarness? harness = null;
        // A node that cannot read the format joins after the first command of the rewrite has gone.
        var writes = new CountingWrites { AfterExecute = count => { if (count == 1) { harness!.Switch(on: false); } } };
        harness = Build(FormatNegotiationMode.Auto, app: app, hashBatch: 10, configure: b => b.Services.AddSingleton<IResiliencePipelineProvider>(writes));
        using (harness)
        {
            harness.Switch();
            await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
            var fields = Enumerable.Range(0, 25).Select(i => ($"f{i}", JsonX)).ToArray();
            var (key, redisKey) = await SeedAsync(seeder, raw, fields);

            (await harness.Hashes().GetAsync<string>(key, harness.Resolve(MigrateOnReadHarness.Migrating), Ct)).Should().HaveCount(25);
            await harness.Migrator.WhenIdleAsync(Ct);

            var stored = await StoredAsync(raw, redisKey, fields.Select(f => f.Item1).ToArray());
            stored.Count(v => v![0] == 0x10).Should().Be(10, "the first batch went before the node list changed");
            stored.Count(v => v!.SequenceEqual(JsonX)).Should().Be(15);
            writes.Count.Should().Be(1);
            harness.Outcomes("done").Should().Be(10);
            harness.Outcomes("held").Should().Be(15);
            harness.Outcomes("changed").Should().Be(0);
        }
    }

    [Fact]
    public async Task A_hash_rewrite_queued_behind_another_asks_the_node_list_before_it_does_any_work()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = new BlockingSerializer(0x10, entered, gate);
        using var harness = Build(FormatNegotiationMode.Auto, fast: target);
        harness.Switch();
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (hashKey, redisKey) = await SeedAsync(harness, raw, ("a", JsonX), ("b", JsonY));
        var stringKey = Unique();
        await harness.Cache().SetAsync(stringKey, "seed", policy: null, Ct);
        var rawString = raw.GetServer(raw.GetEndPoints()[0]).Keys(pattern: $"*{stringKey}*").Single();
        await raw.GetDatabase().StringSetAsync(rawString, JsonX, TimeSpan.FromMinutes(5));
        var policy = harness.Resolve(MigrateOnReadHarness.Migrating);
        await harness.Cache().GetAsync<string>(stringKey, policy, Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await harness.Hashes().GetAsync<string>(hashKey, policy, Ct);

        harness.Switch(on: false);
        gate.SetResult();
        await harness.Migrator.WhenIdleAsync(Ct);

        (await StoredAsync(raw, redisKey, "a", "b")).Should().SatisfyRespectively(
            a => a.Should().Equal(JsonX),
            b => b.Should().Equal(JsonY));
        harness.Outcomes("held").Should().Be(3, "the string in flight and the two fields queued");
        target.Calls.Should().Be(1, "the hash asked before it did any work");
    }

    [Fact]
    public async Task Field_bytes_a_peer_sent_are_decoded_but_neither_counted_as_a_read_nor_offered()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Force);
        var hashes = (RedisHashCache)harness.Hashes();

        var decoded = hashes.TryDecodeFields<string>(new Dictionary<string, byte[]?> { ["a"] = JsonX, ["b"] = OtherZ }, harness.Resolve(MigrateOnReadHarness.Migrating), out var values);
        await harness.Migrator.WhenIdleAsync(Ct);

        decoded.Should().BeTrue();
        values.Should().BeEquivalentTo(new Dictionary<string, string?> { ["a"] = "x", ["b"] = "z" });
        harness.Telemetry.Metrics.Where(m => m.Name == Metrics.SerializerRead || m.Name == Metrics.SerializerRewrite).Should().BeEmpty("those bytes were not read from Redis");
    }

    [Fact]
    public async Task Fields_that_cannot_migrate_do_not_use_up_the_budget_of_a_rewrite_so_a_big_hash_keeps_making_progress()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Force);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        // Five fields a type reads with a member missing, each almost as big as a field may be: four of them fill the megabyte one rewrite carries.
        var lossy = Enumerable.Range(0, 5).Select(i => ($"l{i}", Encoding.UTF8.GetBytes($"{{\"Id\":{i},\"Name\":\"{new string('n', 261_900)}\",\"Extra\":true}}"))).ToArray();
        var eligible = Encoding.UTF8.GetBytes($"{{\"Id\":9,\"Name\":\"{new string('e', 1_900)}\"}}");
        var small = Encoding.UTF8.GetBytes($"{{\"Id\":8,\"Name\":\"{new string('s', 250)}\"}}");
        var (key, redisKey) = await SeedAsync(harness, raw, [.. lossy, ("y", small), ("z", eligible)]);
        var policy = harness.Resolve(MigrateOnReadHarness.Migrating);
        string[] order = ["l0", "l1", "l2", "l3", "l4", "y", "z"];

        await harness.Hashes().GetAsync<TestDto>(key, order, policy, Ct);
        await harness.Migrator.WhenIdleAsync(Ct);
        (await StoredAsync(raw, redisKey, "y", "z")).Should().SatisfyRespectively(
            y => y![0].Should().Be(0x10, "a field that fits is taken past one that does not"),
            z => z.Should().Equal(eligible, "the first rewrite was full of fields that came out lossy"));
        await harness.Hashes().GetAsync<TestDto>(key, order, policy, Ct);
        await harness.Migrator.WhenIdleAsync(Ct);

        (await StoredAsync(raw, redisKey, "z")).Single()![0].Should().Be(0x10, "the lossy ones wait, and the next read moves on to the field behind them");
        harness.Outcomes("done").Should().Be(2);
        harness.Outcomes("lossy").Should().Be(5);
    }

    [Fact]
    public async Task A_hash_rewrite_of_a_hundred_fields_that_fails_counts_a_hundred_failed()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var app = App();
        using var seeder = Build(FormatNegotiationMode.Force, app: app);
        using var harness = Build(FormatNegotiationMode.Force, app: app, configure: b => b.Services.AddSingleton<IResiliencePipelineProvider>(new GivesUpOnWrites()));
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, _) = await SeedAsync(seeder, raw, [.. Enumerable.Range(0, 100).Select(i => ($"f{i}", JsonX))]);

        await harness.Hashes().GetAsync<string>(key, harness.Resolve(MigrateOnReadHarness.Migrating), Ct);
        await harness.Migrator.WhenIdleAsync(Ct);

        harness.Outcomes("failed").Should().Be(100);
        harness.Outcomes("changed").Should().Be(0);
    }

    [Fact]
    public async Task A_hash_read_with_an_empty_bucket_stops_asking_at_the_first_refusal()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Force);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, _) = await SeedAsync(harness, raw, [.. Enumerable.Range(0, 1000).Select(i => ($"f{i}", JsonX))]);
        var policy = harness.Resolve(MigrateOnReadHarness.Migrating);

        await harness.Hashes().GetAsync<string>(key, policy, Ct);
        await harness.Migrator.WhenIdleAsync(Ct);
        harness.Migrator.Flush();

        harness.Outcomes("done").Should().BeGreaterThanOrEqualTo(FormatMigrator.DefaultRatePerSecond);
        harness.Migrator.GatedClaims.Should().BeLessThan(FormatMigrator.DefaultRatePerSecond + 50, "the fields after the first refusal never reach the lock");
        harness.Outcomes("ratelimited").Should().Be(1, "one refusal for the read, not one for each of the nine hundred fields left");
    }

    [Fact]
    public async Task A_target_serializer_that_throws_for_the_fields_counts_them_lossy_not_failed_and_leaves_them()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Force, fast: new HookedSerializer(0x10, static () => throw new NotSupportedException("cannot carry the type")));
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(harness, raw, ("a", JsonX), ("b", JsonY));

        await harness.Hashes().GetAsync<string>(key, harness.Resolve(MigrateOnReadHarness.Migrating), Ct);
        await harness.Migrator.WhenIdleAsync(Ct);

        (await StoredAsync(raw, redisKey, "a", "b")).Should().SatisfyRespectively(a => a.Should().Equal(JsonX), b => b.Should().Equal(JsonY));
        harness.Outcomes("lossy").Should().Be(2);
        harness.Outcomes("failed").Should().Be(0);
    }

    [Fact]
    public async Task A_hash_read_a_field_at_a_time_migrates_a_field_at_a_time_not_one_a_window()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Force);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(harness, raw, ("a", JsonX), ("b", JsonY), ("c", JsonX));
        var policy = harness.Resolve(MigrateOnReadHarness.Migrating);

        await harness.Hashes().GetItemAsync<string>(key, "a", policy, Ct);
        await harness.Hashes().GetItemAsync<string>(key, "b", policy, Ct);
        await harness.Migrator.WhenIdleAsync(Ct);
        await harness.Hashes().GetItemAsync<string>(key, "a", policy, Ct);

        (await StoredAsync(raw, redisKey, "a", "b", "c")).Should().SatisfyRespectively(
            a => a.Should().Equal(FastX),
            b => b.Should().Equal(FastY),
            c => c.Should().Equal(JsonX, "it was never read"));
        harness.Outcomes("done").Should().Be(2);
    }

    [Fact]
    public async Task A_legacy_field_named_like_a_system_one_is_left_and_does_not_stop_the_others()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Force);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(harness, raw, ("a", JsonX), ("_expiration_", JsonY), ("b", JsonY));

        await harness.Hashes().GetAsync<string>(key, harness.Resolve(MigrateOnReadHarness.Migrating), Ct);
        await harness.Migrator.WhenIdleAsync(Ct);

        (await StoredAsync(raw, redisKey, "a", "_expiration_", "b")).Should().SatisfyRespectively(
            a => a.Should().Equal(FastX),
            legacy => legacy.Should().Equal(JsonY),
            b => b.Should().Equal(FastY));
        harness.Outcomes("done").Should().Be(2);
        harness.Outcomes("failed").Should().Be(0);
    }

    [Fact]
    public async Task A_write_pipeline_that_gives_up_counts_the_hash_rewrite_failed_and_leaves_the_fields()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var app = App();
        using var seeder = Build(FormatNegotiationMode.Force, app: app);
        using var harness = Build(FormatNegotiationMode.Force, app: app, configure: b => b.Services.AddSingleton<IResiliencePipelineProvider>(new GivesUpOnWrites()));
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(seeder, raw, ("a", JsonX));

        (await harness.Hashes().GetAsync<string>(key, harness.Resolve(MigrateOnReadHarness.Migrating), Ct)).Should().ContainKey("a");
        await harness.Migrator.WhenIdleAsync(Ct);

        (await StoredAsync(raw, redisKey, "a")).Single().Should().Equal(JsonX);
        harness.Outcomes("failed").Should().Be(1);
        harness.Outcomes("changed").Should().Be(0);
    }

    [Fact]
    public async Task With_scripts_off_a_hash_rewrite_sends_no_script_and_with_them_on_a_refusal_is_not_repeated()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.SupportsAcl, "Needs ACL SETUSER, which this server does not allow.");
        await using var admin = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var server = admin.GetServer(admin.GetEndPoints()[0]);
        var user = $"it-hashscript-{Guid.NewGuid():N}";
        await server.ExecuteAsync("ACL", "SETUSER", user, "on", ">pw", "~*", "&*", "+@all", "-@scripting");
        try
        {
            var connection = $"{fixture.ConnectionString},user={user},password=pw";
            var app = App();
            using var seeder = Build(FormatNegotiationMode.Force, app: app);

            await server.ExecuteAsync("ACL", "LOG", "RESET");
            using (var off = MigrateOnReadHarness.Build(connection, FormatNegotiationMode.Force, app, scripts: false))
            {
                var (key, redisKey) = await SeedAsync(seeder, admin, ("a", JsonX));
                await off.Hashes().GetAsync<string>(key, off.Resolve(MigrateOnReadHarness.Migrating), Ct);
                await off.Migrator.WhenIdleAsync(Ct);
                (await StoredAsync(admin, redisKey, "a")).Single().Should().Equal(FastX);
                (await ScriptRefusalsAsync(server, user)).Should().Be(0, "scripts are off, so none is sent");
            }

            using var on = MigrateOnReadHarness.Build(connection, FormatNegotiationMode.Force, app, scripts: true);
            var (first, firstKey) = await SeedAsync(seeder, admin, ("a", JsonX));
            var (second, secondKey) = await SeedAsync(seeder, admin, ("a", JsonX));
            var policy = on.Resolve(MigrateOnReadHarness.Migrating);

            await on.Hashes().GetAsync<string>(first, policy, Ct);
            await on.Migrator.WhenIdleAsync(Ct);
            var refusals = await ScriptRefusalsAsync(server, user);
            await on.Hashes().GetAsync<string>(second, policy, Ct);
            await on.Migrator.WhenIdleAsync(Ct);

            (await StoredAsync(admin, firstKey, "a")).Single().Should().Equal(FastX, "the call that was refused still rewrote, by transaction");
            (await StoredAsync(admin, secondKey, "a")).Single().Should().Equal(FastX);
            refusals.Should().BeGreaterThan(0);
            (await ScriptRefusalsAsync(server, user)).Should().Be(refusals, "scripts stay off for this node once the server has refused one");
        }
        finally
        {
            await server.ExecuteAsync("ACL", "DELUSER", user);
        }
    }

    [Fact]
    public async Task A_single_field_read_rewrites_that_field_and_a_read_of_some_fields_rewrites_those()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Force);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(harness, raw, ("a", JsonX), ("b", JsonY), ("c", JsonX), ("d", JsonY));
        var hashes = harness.Hashes();
        var policy = harness.Resolve(MigrateOnReadHarness.Migrating);

        (await hashes.GetItemAsync<string>(key, "a", policy, Ct)).Should().Be("x");
        await harness.Migrator.WhenIdleAsync(Ct);
        (await StoredAsync(raw, redisKey, "a", "b", "c", "d")).Should().SatisfyRespectively(
            a => a.Should().Equal(FastX),
            b => b.Should().Equal(JsonY),
            c => c.Should().Equal(JsonX),
            d => d.Should().Equal(JsonY));
        harness.Outcomes("done").Should().Be(1);
    }

    [Fact]
    public async Task A_read_of_some_fields_rewrites_the_fields_it_read()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Force);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(harness, raw, ("a", JsonX), ("b", JsonY), ("c", JsonX), ("d", JsonY));

        var read = await harness.Hashes().GetAsync<string>(key, ["a", "c"], harness.Resolve(MigrateOnReadHarness.Migrating), Ct);
        await harness.Migrator.WhenIdleAsync(Ct);

        read.Should().BeEquivalentTo(new Dictionary<string, string?> { ["a"] = "x", ["c"] = "x" });
        (await StoredAsync(raw, redisKey, "a", "b", "c", "d")).Should().SatisfyRespectively(
            a => a.Should().Equal(FastX),
            b => b.Should().Equal(JsonY),
            c => c.Should().Equal(FastX),
            d => d.Should().Equal(JsonY));
        harness.Outcomes("done").Should().Be(2);
    }

    [Fact]
    public async Task A_field_that_changed_while_the_rewrite_waited_is_skipped_and_the_others_are_rewritten()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        RedisKey redisKey = default;
        var newer = "\"newer\""u8.ToArray();
        var serializer = new HookedSerializer(0x10, () => raw.GetDatabase().HashSet(redisKey, "b", newer));
        using var harness = Build(FormatNegotiationMode.Force, fast: serializer);
        string key;
        (key, redisKey) = await SeedAsync(harness, raw, ("a", JsonX), ("b", JsonY));

        await harness.Hashes().GetAsync<string>(key, harness.Resolve(MigrateOnReadHarness.Migrating), Ct);
        await harness.Migrator.WhenIdleAsync(Ct);

        (await StoredAsync(raw, redisKey, "a", "b")).Should().SatisfyRespectively(
            a => a.Should().Equal(FastX),
            b => b.Should().Equal(newer));
        harness.Outcomes("done").Should().Be(1);
        harness.Outcomes("changed").Should().Be(1);
    }

    [Fact]
    public async Task A_field_that_would_change_is_left_alone_and_counted_lossy_while_the_others_are_rewritten()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Force);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var subset = Encoding.UTF8.GetBytes("{\"Id\":7,\"Name\":\"n\",\"AddedByANewerVersion\":true}");
        var whole = Encoding.UTF8.GetBytes("{\"Id\":8,\"Name\":\"m\"}");
        var (key, redisKey) = await SeedAsync(harness, raw, ("a", subset), ("b", whole));

        var read = await harness.Hashes().GetAsync<TestDto>(key, harness.Resolve(MigrateOnReadHarness.Migrating), Ct);
        await harness.Migrator.WhenIdleAsync(Ct);

        read["a"]!.Id.Should().Be(7);
        (await StoredAsync(raw, redisKey, "a", "b")).Should().SatisfyRespectively(
            a => a.Should().Equal(subset),
            b => b![0].Should().Be(0x10));
        harness.Outcomes("lossy").Should().Be(1);
        harness.Outcomes("done").Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scripts_on_or_off_every_field_lands_and_a_big_hash_goes_in_batches(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Force, scripts: scripts, hashBatch: 10);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var fields = Enumerable.Range(0, 25).Select(i => ($"f{i}", JsonX)).ToArray();
        var (key, redisKey) = await SeedAsync(harness, raw, fields);

        (await harness.Hashes().GetAsync<string>(key, harness.Resolve(MigrateOnReadHarness.Migrating), Ct)).Should().HaveCount(25);
        await harness.Migrator.WhenIdleAsync(Ct);

        (await StoredAsync(raw, redisKey, fields.Select(f => f.Item1).ToArray())).Should().AllSatisfy(v => v.Should().Equal(FastX));
        harness.Outcomes("done").Should().Be(25);
    }

    [Fact]
    public async Task What_one_rewrite_carries_is_bounded_so_a_hash_of_large_fields_migrates_over_several_reads()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Force);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var big = Encoding.UTF8.GetBytes($"\"{new string('v', 200_000)}\"");
        var fields = Enumerable.Range(0, 8).Select(i => ($"f{i}", big)).ToArray();
        var (key, redisKey) = await SeedAsync(harness, raw, fields);

        (await harness.Hashes().GetAsync<string>(key, harness.Resolve(MigrateOnReadHarness.Migrating), Ct)).Should().HaveCount(8);
        await harness.Migrator.WhenIdleAsync(Ct);

        var stored = await StoredAsync(raw, redisKey, fields.Select(f => f.Item1).ToArray());
        stored.Count(v => v![0] == 0x10).Should().Be(5, "a megabyte of fields in one rewrite, then the rest on a later read");
        harness.Outcomes("done").Should().Be(5);
    }

    [Fact]
    public async Task The_empty_marker_in_a_field_and_in_the_metadata_is_never_rewritten()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Force, cacheNulls: true);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(harness, raw, ("a", JsonX), ("b", []));
        await raw.GetDatabase().HashSetAsync(redisKey, "_metadata_", RedisValue.EmptyString);

        (await harness.Hashes().GetAsync<string>(key, harness.Resolve(MigrateOnReadHarness.Migrating), Ct)).Should().ContainKey("a");
        await harness.Migrator.WhenIdleAsync(Ct);

        var hash = (await raw.GetDatabase().HashGetAllAsync(redisKey)).ToDictionary(e => e.Name.ToString(), e => (byte[]?)e.Value);
        hash["a"].Should().Equal(FastX);
        hash["b"].Should().BeEmpty();
        hash["_metadata_"].Should().BeEmpty();
        harness.Outcomes("done").Should().Be(1);
    }

    [Fact]
    public async Task A_hash_deleted_while_the_rewrite_waited_is_not_created_again()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        RedisKey redisKey = default;
        var serializer = new HookedSerializer(0x10, () => raw.GetDatabase().KeyDelete(redisKey));
        using var harness = Build(FormatNegotiationMode.Force, fast: serializer);
        string key;
        (key, redisKey) = await SeedAsync(harness, raw, ("a", JsonX));

        await harness.Hashes().GetAsync<string>(key, harness.Resolve(MigrateOnReadHarness.Migrating), Ct);
        await harness.Migrator.WhenIdleAsync(Ct);

        (await raw.GetDatabase().KeyExistsAsync(redisKey)).Should().BeFalse();
        harness.Outcomes("changed").Should().Be(1);
    }

    [Fact]
    public async Task Every_field_read_is_counted_by_its_format_and_the_metadata_is_not()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Force);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(harness, raw, ("a", JsonX), ("b", FastY));
        await raw.GetDatabase().HashSetAsync(redisKey, "_metadata_", JsonMeta);

        await harness.Hashes().GetCacheEntryAsync<string>(key, harness.Resolve(MigrateOnReadHarness.NotMigrating), Ct);
        (await harness.Hashes().GetMetadataAsync<string>(key, Ct)).Should().ContainKey("m");

        harness.Telemetry.Metrics.Where(m => m.Name == Metrics.SerializerRead).Select(m => m.Properties!["format"]).Order().Should().Equal("fast", "json");
        harness.Telemetry.Metrics.Where(m => m.Name == Metrics.SerializerRead).Select(m => m.Properties!["cache"]).Distinct().Should().Equal("RedisHash");
    }

    [Fact]
    public async Task Every_field_written_is_counted_by_its_format()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Force);
        var policy = harness.Resolve(MigrateOnReadHarness.Migrating);

        await harness.Hashes().SetAsync(Unique(), new Dictionary<string, string?> { ["a"] = "x", ["b"] = "y" }, new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(5)), policy, Ct);

        harness.Telemetry.Metrics.Where(m => m.Name == Metrics.SerializerWrite).Select(m => m.Properties!["format"]).Should().Equal("fast", "fast");
        harness.Telemetry.Metrics.Where(m => m.Name == Metrics.SerializerWrite).Select(m => m.Properties!["cache"]).Distinct().Should().Equal("RedisHash");
    }

    private static string Unique() => $"it-{Guid.NewGuid():N}";

    private static string App() => $"morh{Guid.NewGuid():N}"[..20];

    private static byte[] Stored(string name) => name switch
    {
        "json" => JsonX,
        "fast" => FastX,
        "other" => [0x12, (byte)'x'],
        "unknown" => [0x14, (byte)'x'],
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No such stored form."),
    };

    private static async Task<List<byte[]?>> StoredAsync(ConnectionMultiplexer raw, RedisKey redisKey, params string[] fields)
    {
        var values = await raw.GetDatabase().HashGetAsync(redisKey, [.. fields.Select(f => (RedisValue)f)]);
        return [.. values.Select(v => (byte[]?)v)];
    }

    private static async Task<IDictionary<string, string?>> ReadAsync(MigrateOnReadHarness harness, bool layered, string operation, string key, CachePolicy policy)
    {
        var hashes = harness.Hashes(layered);
        switch (operation)
        {
            case "all":
                return await hashes.GetAsync<string>(key, policy, Ct);
            case "entry":
                return (await hashes.GetCacheEntryAsync<string>(key, policy, Ct)).Value!;
            default:
                return await hashes.GetOrAddAsync<string>(key, _ => throw new InvalidOperationException("a hit ran its generator"), TimeSpan.FromMinutes(5), policy, Ct);
        }
    }

    private static async Task<long> ScriptRefusalsAsync(IServer server, string user)
    {
        var log = (RedisResult[])(await server.ExecuteAsync("ACL", "LOG"))!;
        long count = 0;
        foreach (var entry in log)
        {
            var fields = (RedisResult[])entry!;
            var map = new Dictionary<string, RedisResult>();
            for (var i = 0; i + 1 < fields.Length; i += 2)
            {
                map[fields[i].ToString()!] = fields[i + 1];
            }

            if (map["username"].ToString() == user && map["object"].ToString()!.StartsWith("eval", StringComparison.OrdinalIgnoreCase))
            {
                count += (long)map["count"];
            }
        }

        return count;
    }

    private async Task<(string Key, RedisKey RedisKey)> SeedAsync(MigrateOnReadHarness harness, ConnectionMultiplexer raw, params (string Field, byte[] Value)[] fields)
    {
        var key = Unique();
        await harness.Hashes().SetAsync(key, new Dictionary<string, string?> { ["seed"] = "seed" }, new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(5)), policy: null, Ct);
        var redisKey = raw.GetServer(raw.GetEndPoints()[0]).Keys(pattern: $"*{key}*").Single();
        var db = raw.GetDatabase();
        await db.HashSetAsync(redisKey, [.. fields.Select(f => new HashEntry(f.Field, f.Value))]);
        await db.HashDeleteAsync(redisKey, "seed");
        return (key, redisKey);
    }

    private MigrateOnReadHarness Build(
        FormatNegotiationMode mode,
        bool layered = false,
        bool scripts = false,
        bool cacheNulls = false,
        int hashBatch = 1000,
        ICacheValueSerializer? fast = null,
        string? app = null,
        Action<ICachingBuilder>? configure = null) =>
        MigrateOnReadHarness.Build(fixture.ConnectionString, mode, app ?? App(), layered, scripts, cacheNulls, configure, other: null, fast: fast, hashBatch: hashBatch);

    private sealed class CountingWrites : IResiliencePipelineProvider
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Action<int>? AfterExecute { get; set; }

        public IResiliencePipeline Get(string? name) =>
            name == ResiliencePipelineNames.Write ? new Counting(this) : new PassThrough();

        private sealed class Counting(CountingWrites owner) : IResiliencePipeline
        {
            public ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default)
            {
                var count = Interlocked.Increment(ref owner._count);
                return Run(callback, count, cancellationToken);
            }

            private async ValueTask<TResult> Run<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, int count, CancellationToken cancellationToken)
            {
                var result = await callback(cancellationToken).ConfigureAwait(false);
                owner.AfterExecute?.Invoke(count);
                return result;
            }
        }
    }

    private sealed class GivesUpOnWrites : IResiliencePipelineProvider
    {
        public IResiliencePipeline Get(string? name) =>
            name == ResiliencePipelineNames.Write ? new GivesUp() : new PassThrough();

        private sealed class GivesUp : IResiliencePipeline
        {
            public ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default) =>
                ValueTask.FromResult(defaultValue);
        }
    }

    private sealed class PassThrough : IResiliencePipeline
    {
        public ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default) =>
            callback(cancellationToken);
    }

    private sealed class BlockingSerializer(int id, TaskCompletionSource entered, TaskCompletionSource gate) : ICacheValueSerializer
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public IReadOnlyList<int> FormatIds { get; } = [id];

        public int Serialize<T>(T value, IBufferWriter<byte> target)
        {
            Interlocked.Increment(ref _calls);
            entered.TrySetResult();
            gate.Task.GetAwaiter().GetResult();
            target.Write(Encoding.UTF8.GetBytes(value?.ToString() ?? string.Empty));
            return id;
        }

        public T? Deserialize<T>(int formatId, ReadOnlySequence<byte> source) =>
            (T)(object)Encoding.UTF8.GetString(source.ToArray());
    }

    private sealed class HookedSerializer(int id, Action beforeSerialize) : ICacheValueSerializer
    {
        public IReadOnlyList<int> FormatIds { get; } = [id];

        public int Serialize<T>(T value, IBufferWriter<byte> target)
        {
            beforeSerialize();
            target.Write(Encoding.UTF8.GetBytes(value?.ToString() ?? string.Empty));
            return id;
        }

        public T? Deserialize<T>(int formatId, ReadOnlySequence<byte> source) =>
            (T)(object)Encoding.UTF8.GetString(source.ToArray());
    }
}
