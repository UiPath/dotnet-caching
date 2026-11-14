using System.Buffers;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.MessagePack;
using UiPath.Caching.Policies;
using UiPath.Caching.Redis;
using UiPath.Caching.Serialization;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Redis;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class MigrateOnReadIntegrationTests(RedisContainerFixture fixture)
{
    private static readonly byte[] JsonX = "\"x\""u8.ToArray();
    private static readonly byte[] FastX = [0x10, (byte)'x'];
    private static readonly byte[] OtherX = [0x12, (byte)'x'];
    private static readonly byte[] UnknownX = [0x14, (byte)'x'];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Each row: where the value is read (Redis or InMemoryRedis), the mode and what the node list says, the policy, what is stored, what the read answers, and what is stored after.
    // After is null for a key that is gone, or the bytes it holds.
    public static TheoryData<string, string, string, string, string?, string?> Cases()
    {
        var data = new TheoryData<string, string, string, string, string?, string?>();
        foreach (var layer in new[] { "redis", "layered" })
        {
            // Force: the configured format is the effective one.
            data.Add(layer, "force", "mig", "json", "x", "fast");
            data.Add(layer, "force", "mig", "other", "x", "fast");
            data.Add(layer, "force", "mig", "fast", "x", "fast");
            data.Add(layer, "force", "mig", "unknown", null, null);
            data.Add(layer, "force", "narrow", "json", "x", "fast");
            data.Add(layer, "force", "narrow", "other", null, "other");
            data.Add(layer, "force", "open", "json", "x", "fast");
            data.Add(layer, "force", "open", "other", "x", "fast");
            data.Add(layer, "force", "open", "unknown", null, null);
            data.Add(layer, "force", "plain", "json", "x", "json");
            data.Add(layer, "force", "plain", "other", "x", "other");
            data.Add(layer, "force", "unset", "json", "x", "json");
            data.Add(layer, "force", "tojson", "json", "x", "json");
            data.Add(layer, "force", "tojson", "fast", "x", "fast");
            data.Add(layer, "force", "tojson", "other", "x", "other");

            // Auto, the format held back at JSON: nothing is rewritten, whatever is stored.
            data.Add(layer, "held", "mig", "json", "x", "json");
            data.Add(layer, "held", "mig", "other", "x", "other");
            data.Add(layer, "held", "mig", "fast", "x", "fast");
            data.Add(layer, "held", "narrow", "other", null, "other");
            data.Add(layer, "held", "open", "other", "x", "other");
            data.Add(layer, "held", "tojson", "fast", "x", "fast");

            // Auto, switched: the same as Force.
            data.Add(layer, "switched", "mig", "json", "x", "fast");
            data.Add(layer, "switched", "mig", "other", "x", "fast");
            data.Add(layer, "switched", "mig", "fast", "x", "fast");
            data.Add(layer, "switched", "narrow", "other", null, "other");
            data.Add(layer, "switched", "open", "json", "x", "fast");
            data.Add(layer, "switched", "plain", "json", "x", "json");
            data.Add(layer, "switched", "tojson", "json", "x", "json");
            data.Add(layer, "switched", "tojson", "fast", "x", "fast");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_value_read_in_another_readable_format_is_rewritten_upward_only_and_only_where_the_format_is_effective(
        string layer, string state, string policyName, string stored, string? answer, string? after)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var layered = layer == "layered";
        using var harness = Build(state == "force" ? FormatNegotiationMode.Force : FormatNegotiationMode.Auto, layered);
        if (state == "switched")
        {
            harness.Switch();
        }

        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(harness, raw, Bytes(stored));
        var policy = harness.Resolve(policyName);

        var read = await harness.Cache(layered).GetAsync<string>(key, policy, Ct);
        await harness.Migrator.WhenIdleAsync(Ct);

        read.Should().Be(answer);
        var now = (byte[]?)await raw.GetDatabase().StringGetAsync(redisKey);
        if (after is null)
        {
            now.Should().BeNull();
        }
        else
        {
            now.Should().Equal(Bytes(after), $"a {stored} value read under {policyName} while {state}");
        }

        harness.Outcomes("failed").Should().Be(0);
        harness.Outcomes("lossy").Should().Be(0);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("entry")]
    [InlineData("batch")]
    [InlineData("entries")]
    [InlineData("getoradd")]
    [InlineData("getoradd-expiring")]
    public async Task Every_read_path_that_hits_offers_the_value(string operation)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Force);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(harness, raw, JsonX);
        var cache = harness.Cache();
        var policy = harness.Resolve(MigrateOnReadHarness.Migrating);

        string? read = operation switch
        {
            "get" => await cache.GetAsync<string>(key, policy, Ct),
            "entry" => (await cache.GetCacheEntryAsync<string>(key, policy, Ct)).Value,
            "batch" => (await cache.GetAsync<string>([key], policy, Ct)).Single().Value,
            "entries" => (await cache.GetCacheEntriesAsync<string>([key], policy, Ct)).Single().Value.Value,
            "getoradd" => await cache.GetOrAddAsync<string>(key, _ => throw new InvalidOperationException("a hit ran its generator"), policy, Ct),
            _ => await ((RedisCache)cache).GetOrAddWithExpirationAsync<string>(key, _ => throw new InvalidOperationException("a hit ran its generator"), policy, Ct),
        };
        await harness.Migrator.WhenIdleAsync(Ct);

        read.Should().Be("x");
        ((byte[]?)await raw.GetDatabase().StringGetAsync(redisKey)).Should().Equal(FastX);
        harness.Outcomes("done").Should().Be(1);
    }

    [Fact]
    public async Task A_read_does_not_wait_for_the_rewrite()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var harness = Build(FormatNegotiationMode.Force, fast: new BlockingSerializer(0x10, entered, gate));
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(harness, raw, JsonX);

        var read = await harness.Cache().GetAsync<string>(key, harness.Resolve(MigrateOnReadHarness.Migrating), Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        read.Should().Be("x");
        ((byte[]?)await raw.GetDatabase().StringGetAsync(redisKey)).Should().Equal(JsonX, "the rewrite is still waiting on its serializer");
        gate.SetResult();
        await harness.Migrator.WhenIdleAsync(Ct);
        ((byte[]?)await raw.GetDatabase().StringGetAsync(redisKey)).Should().Equal(FastX);
    }

    [Fact]
    public async Task A_rewrite_keeps_the_ttl()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Force);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(harness, raw, JsonX, ttl: TimeSpan.FromSeconds(90));

        await harness.Cache().GetAsync<string>(key, harness.Resolve(MigrateOnReadHarness.Migrating), Ct);
        await harness.Migrator.WhenIdleAsync(Ct);

        ((byte[]?)await raw.GetDatabase().StringGetAsync(redisKey)).Should().Equal(FastX);
        var ttl = (await raw.GetDatabase().KeyTimeToLiveAsync(redisKey))!.Value;
        ttl.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(90)).And.BeGreaterThan(TimeSpan.FromSeconds(60));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scripts_on_or_off_the_rewrite_lands_the_same(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Force, scripts: scripts);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(harness, raw, JsonX);

        await harness.Cache().GetAsync<string>(key, harness.Resolve(MigrateOnReadHarness.Migrating), Ct);
        await harness.Migrator.WhenIdleAsync(Ct);

        ((byte[]?)await raw.GetDatabase().StringGetAsync(redisKey)).Should().Equal(FastX);
        harness.Outcomes("done").Should().Be(1);
    }

    [Fact]
    public async Task A_script_the_server_refuses_falls_back_to_the_transaction_and_is_not_sent_again()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.SupportsAcl, "Needs ACL SETUSER, which this server does not allow.");
        await using var admin = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var server = admin.GetServer(admin.GetEndPoints()[0]);
        var user = $"it-norewrite-{Guid.NewGuid():N}";
        await server.ExecuteAsync("ACL", "SETUSER", user, "on", ">pw", "~*", "&*", "+@all", "-@scripting");
        try
        {
            await server.ExecuteAsync("ACL", "LOG", "RESET");
            using var harness = MigrateOnReadHarness.Build($"{fixture.ConnectionString},user={user},password=pw", FormatNegotiationMode.Force, App(), scripts: true);
            var (first, firstKey) = await SeedAsync(harness, admin, JsonX);
            var (second, secondKey) = await SeedAsync(harness, admin, JsonX);
            var cache = harness.Cache();
            var policy = harness.Resolve(MigrateOnReadHarness.Migrating);

            await cache.GetAsync<string>(first, policy, Ct);
            await harness.Migrator.WhenIdleAsync(Ct);
            var refusals = await ScriptRefusalsAsync(server, user);
            await cache.GetAsync<string>(second, policy, Ct);
            await harness.Migrator.WhenIdleAsync(Ct);

            ((byte[]?)await admin.GetDatabase().StringGetAsync(firstKey)).Should().Equal(FastX, "the call that was refused still rewrote, by transaction");
            ((byte[]?)await admin.GetDatabase().StringGetAsync(secondKey)).Should().Equal(FastX);
            refusals.Should().BeGreaterThan(0, "the first rewrite did try the script");
            (await ScriptRefusalsAsync(server, user)).Should().Be(refusals, "scripts stay off for this node once the server has refused one");
            harness.Outcomes("done").Should().Be(2);
            harness.Outcomes("failed").Should().Be(0);
        }
        finally
        {
            await server.ExecuteAsync("ACL", "DELUSER", user);
        }
    }

    [Fact]
    public async Task The_rewrite_works_from_a_copy_of_the_bytes_the_read_returned()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        byte[]? returned = null;
        // Whoever owns the buffer a read returned may reuse it while the job waits: here, the serializer the job asks to check the value does.
        var source = new HookedSerializer(0x12, () => returned![1] = (byte)'y');
        using var harness = Build(
            FormatNegotiationMode.Force,
            configure: b => b.Services.AddSingleton<IRedisConnector>(sp => new InterceptingConnector(
                ActivatorUtilities.CreateInstance<RedisConnector>(sp),
                after: (method, _, result) =>
                {
                    if (method.Name == nameof(IDatabase.StringGetAsync) && result is Task<RedisValue> read)
                    {
                        return read.ContinueWith(
                            done =>
                            {
                                // Handed back as one array the test keeps hold of, which is what a cache that pools its buffers hands out.
                                returned = (byte[])done.Result!;
                                return (RedisValue)returned;
                            },
                            TaskScheduler.Default);
                    }

                    return result;
                })),
            other: source);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(harness, raw, OtherX);

        (await harness.Cache().GetAsync<string>(key, harness.Resolve(MigrateOnReadHarness.Migrating), Ct)).Should().Be("x");
        await harness.Migrator.WhenIdleAsync(Ct);

        ((byte[]?)await raw.GetDatabase().StringGetAsync(redisKey)).Should().Equal(FastX);
        returned.Should().Equal([0x12, (byte)'y'], "the buffer the read returned was reused");
        harness.Outcomes("done").Should().Be(1, "the job compared and replaced the bytes it had been given, not the reused buffer");
        harness.Outcomes("lossy").Should().Be(0);
    }

    [Fact]
    public async Task A_queued_rewrite_holds_a_copy_and_not_the_buffer_its_read_returned()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WeakReference? watched = null;
        RedisKey? watching = null;
        using var harness = Build(
            FormatNegotiationMode.Force,
            configure: b => b.Services.AddSingleton<IRedisConnector>(sp => new InterceptingConnector(
                ActivatorUtilities.CreateInstance<RedisConnector>(sp),
                after: (method, args, result) =>
                {
                    if (watching is { } wanted && args[0] is RedisKey asked && asked == wanted && method.Name == nameof(IDatabase.StringGetAsync) && result is Task<RedisValue> read)
                    {
                        return read.ContinueWith(
                            done =>
                            {
                                var returned = (byte[])done.Result!;
                                watched = new WeakReference(returned);
                                return (RedisValue)returned;
                            },
                            TaskScheduler.Default);
                    }

                    return result;
                })),
            fast: new BlockingSerializer(0x10, entered, gate));
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (first, _) = await SeedAsync(harness, raw, JsonX);
        var (second, secondKey) = await SeedAsync(harness, raw, JsonX);
        var cache = harness.Cache();
        var policy = harness.Resolve(MigrateOnReadHarness.Migrating);
        await cache.GetAsync<string>(first, policy, Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        watching = secondKey;
        await cache.GetAsync<string>(second, policy, Ct);

        // The first rewrite is running, so the second sits in the queue with what it holds.
        for (var attempt = 0; attempt < 10 && watched!.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(20, Ct);
        }

        var alive = watched!.IsAlive;
        gate.SetResult();
        await harness.Migrator.WhenIdleAsync(Ct);
        alive.Should().BeFalse("only the copy the job compares against is queued, so the queue's memory is what its budget counts");
        harness.Outcomes("done").Should().Be(2);
    }

    [Fact]
    public async Task A_target_serializer_that_throws_for_the_value_is_counted_lossy_not_failed_and_the_value_is_left()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Force, fast: new HookedSerializer(0x10, static () => throw new NotSupportedException("cannot carry the type")));
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(harness, raw, JsonX);

        (await harness.Cache().GetAsync<string>(key, harness.Resolve(MigrateOnReadHarness.Migrating), Ct)).Should().Be("x");
        await harness.Migrator.WhenIdleAsync(Ct);

        ((byte[]?)await raw.GetDatabase().StringGetAsync(redisKey)).Should().Equal(JsonX);
        harness.Outcomes("lossy").Should().Be(1);
        harness.Outcomes("failed").Should().Be(0, "failed is a refusal or a timeout from Redis");
    }

    [Fact]
    public async Task A_json_write_by_get_or_add_is_counted_while_the_format_is_held_back_like_one_by_set()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Auto);
        var cache = harness.Cache();
        var policy = harness.Resolve(MigrateOnReadHarness.Migrating);

        await cache.SetAsync(Unique(), "x", policy, Ct);
        await cache.GetOrAddAsync<string>(Unique(), _ => Task.FromResult<string?>("x"), policy, Ct);

        harness.Telemetry.Metrics.Where(m => m.Name == Metrics.SerializerWrite && m.Properties!["format"] == "json").Sum(m => m.Value).Should().Be(2);
    }

    [Fact]
    public async Task An_app_with_only_binary_events_registered_has_no_migration_to_run()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = fixture.ConnectionString);
                    b.AddRedis(_ => { });
                    b.AddMessagePackEvents(write: false);
                },
                o => o.AppShortName = App())
            .BuildServiceProvider();

        provider.GetService<ValueSerializerRegistry>()!.IsEmpty.Should().BeTrue();
        provider.GetService<ValueMigration>().Should().BeNull();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_rewrite_that_waited_in_the_queue_is_not_written_once_the_node_list_holds_the_format_back()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = new BlockingSerializer(0x10, entered, gate);
        using var harness = Build(FormatNegotiationMode.Auto, fast: target);
        harness.Switch();
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (first, firstKey) = await SeedAsync(harness, raw, JsonX);
        var (second, secondKey) = await SeedAsync(harness, raw, JsonX);
        var cache = harness.Cache();
        var policy = harness.Resolve(MigrateOnReadHarness.Migrating);
        await cache.GetAsync<string>(first, policy, Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cache.GetAsync<string>(second, policy, Ct);

        // A node that cannot read the format has joined, while one rewrite is in flight and another is queued behind it.
        harness.Switch(on: false);
        gate.SetResult();
        await harness.Migrator.WhenIdleAsync(Ct);

        ((byte[]?)await raw.GetDatabase().StringGetAsync(firstKey)).Should().Equal(JsonX, "the one in flight asks again before it writes");
        ((byte[]?)await raw.GetDatabase().StringGetAsync(secondKey)).Should().Equal(JsonX);
        harness.Outcomes("held").Should().Be(2);
        harness.Outcomes("done").Should().Be(0);
        target.Calls.Should().Be(1, "the queued one asked before it did any work");
    }

    [Fact]
    public async Task What_the_caller_does_to_the_value_it_was_returned_is_not_what_is_stored()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var harness = Build(FormatNegotiationMode.Force, fast: new JsonBlockingSerializer(0x10, entered, gate));
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(harness, raw, "[\"a\",\"b\"]"u8.ToArray());

        var returned = (await harness.Cache().GetAsync<List<string>>(key, harness.Resolve(MigrateOnReadHarness.Migrating), Ct))!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        returned.Add("added by the caller after the read");
        gate.SetResult();
        await harness.Migrator.WhenIdleAsync(Ct);

        ((byte[]?)await raw.GetDatabase().StringGetAsync(redisKey)).Should().Equal([0x10, .. "[\"a\",\"b\"]"u8]);
        harness.Outcomes("done").Should().Be(1);
        harness.Outcomes("failed").Should().Be(0);
    }

    [Fact]
    public async Task A_target_that_cannot_carry_the_value_leaves_it_and_counts_it_lossy()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Force, fast: new JsonBlockingSerializer(0x10, new TaskCompletionSource(), new TaskCompletionSource(), forgetful: true));
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var stored = Encoding.UTF8.GetBytes("{\"Id\":7,\"Name\":\"n\"}");
        var (key, redisKey) = await SeedAsync(harness, raw, stored);

        await harness.Cache().GetAsync<TestDto>(key, harness.Resolve(MigrateOnReadHarness.Migrating), Ct);
        await harness.Migrator.WhenIdleAsync(Ct);

        ((byte[]?)await raw.GetDatabase().StringGetAsync(redisKey)).Should().Equal(stored);
        harness.Outcomes("lossy").Should().Be(1);
        harness.Outcomes("done").Should().Be(0);
    }

    [Fact]
    public async Task A_batch_of_json_writes_is_counted_by_value()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Auto);

        await harness.Cache().SetAsync<string>([new(Unique(), "a"), new(Unique(), "b"), new(Unique(), "c")], harness.Resolve(MigrateOnReadHarness.Migrating), Ct);

        harness.Telemetry.Metrics.Where(m => m.Name == Metrics.SerializerWrite && m.Properties!["format"] == "json").Sum(m => m.Value).Should().Be(3);
        harness.Telemetry.Metrics.Where(m => m.Name == Metrics.SerializerWrite).Select(m => m.Properties!["cache"]).Distinct().Should().Equal("Redis");
    }

    [Fact]
    public async Task A_stored_null_literal_is_a_value_nobody_wrote_and_is_not_offered()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Force, cacheNulls: true);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(harness, raw, "null"u8.ToArray());

        (await harness.Cache().GetAsync<string>(key, harness.Resolve(MigrateOnReadHarness.Migrating), Ct)).Should().BeNull();
        await harness.Migrator.WhenIdleAsync(Ct);

        ((byte[]?)await raw.GetDatabase().StringGetAsync(redisKey)).Should().Equal("null"u8.ToArray());
        harness.Telemetry.Metrics.Where(m => m.Name == Metrics.SerializerRewrite).Should().BeEmpty();
    }

    [Fact]
    public async Task With_scripts_off_no_script_is_sent_even_to_a_server_that_would_refuse_it()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.SupportsAcl, "Needs ACL SETUSER, which this server does not allow.");
        await using var admin = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var server = admin.GetServer(admin.GetEndPoints()[0]);
        var user = $"it-noscript-{Guid.NewGuid():N}";
        await server.ExecuteAsync("ACL", "SETUSER", user, "on", ">pw", "~*", "&*", "+@all", "-@scripting");
        try
        {
            await server.ExecuteAsync("ACL", "LOG", "RESET");
            using var harness = MigrateOnReadHarness.Build($"{fixture.ConnectionString},user={user},password=pw", FormatNegotiationMode.Force, App(), scripts: false);
            var (key, redisKey) = await SeedAsync(harness, admin, JsonX);

            await harness.Cache().GetAsync<string>(key, harness.Resolve(MigrateOnReadHarness.Migrating), Ct);
            await harness.Migrator.WhenIdleAsync(Ct);

            ((byte[]?)await admin.GetDatabase().StringGetAsync(redisKey)).Should().Equal(FastX);
            (await ScriptRefusalsAsync(server, user)).Should().Be(0);
        }
        finally
        {
            await server.ExecuteAsync("ACL", "DELUSER", user);
        }
    }

    [Fact]
    public async Task A_write_pipeline_that_gives_up_counts_the_rewrite_failed_and_leaves_the_value()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var app = App();
        using var seeder = Build(FormatNegotiationMode.Force, app: app);
        using var harness = Build(FormatNegotiationMode.Force, app: app, configure: b => b.Services.AddSingleton<IResiliencePipelineProvider>(new GivesUpOnWrites()));
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(seeder, raw, JsonX);

        (await harness.Cache().GetAsync<string>(key, harness.Resolve(MigrateOnReadHarness.Migrating), Ct)).Should().Be("x");
        await harness.Migrator.WhenIdleAsync(Ct);

        ((byte[]?)await raw.GetDatabase().StringGetAsync(redisKey)).Should().Equal(JsonX);
        harness.Outcomes("failed").Should().Be(1);
        harness.Outcomes("changed").Should().Be(0);
    }

    [Fact]
    public async Task A_value_that_cannot_be_read_back_as_it_was_is_left_alone_and_counted_lossy()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Force);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var stored = Encoding.UTF8.GetBytes("{\"Id\":7,\"Name\":\"n\",\"AddedByANewerVersion\":true}");
        var (key, redisKey) = await SeedAsync(harness, raw, stored);

        var read = await harness.Cache().GetAsync<TestDto>(key, harness.Resolve(MigrateOnReadHarness.Migrating), Ct);
        await harness.Migrator.WhenIdleAsync(Ct);

        read.Should().BeEquivalentTo(new TestDto { Id = 7, Name = "n" });
        ((byte[]?)await raw.GetDatabase().StringGetAsync(redisKey)).Should().Equal(stored);
        harness.Outcomes("lossy").Should().Be(1);
        harness.Outcomes("done").Should().Be(0);
    }

    [Fact]
    public async Task A_member_order_and_number_form_other_than_the_serializers_still_rewrite()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Force);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(harness, raw, Encoding.UTF8.GetBytes("{\"Name\":\"n\",\"Value\":1.0}"));

        (await harness.Cache().GetAsync<Measure>(key, harness.Resolve(MigrateOnReadHarness.Migrating), Ct))!.Value.Should().Be(1);
        await harness.Migrator.WhenIdleAsync(Ct);

        ((byte[]?)await raw.GetDatabase().StringGetAsync(redisKey))![0].Should().Be(0x10);
        harness.Outcomes("done").Should().Be(1);
    }

    [Fact]
    public async Task A_value_written_while_the_rewrite_waited_is_not_overwritten()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var newer = "\"newer\""u8.ToArray();
        RedisKey redisKey = default;
        var serializer = new HookedSerializer(0x10, () => raw.GetDatabase().StringSet(redisKey, newer));
        using var harness = Build(FormatNegotiationMode.Force, fast: serializer);
        string key;
        (key, redisKey) = await SeedAsync(harness, raw, JsonX);

        await harness.Cache().GetAsync<string>(key, harness.Resolve(MigrateOnReadHarness.Migrating), Ct);
        await harness.Migrator.WhenIdleAsync(Ct);

        ((byte[]?)await raw.GetDatabase().StringGetAsync(redisKey)).Should().Equal(newer);
        harness.Outcomes("changed").Should().Be(1);
        harness.Outcomes("done").Should().Be(0);
    }

    [Fact]
    public async Task A_key_deleted_while_the_rewrite_waited_stays_deleted()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        RedisKey redisKey = default;
        var serializer = new HookedSerializer(0x10, () => raw.GetDatabase().KeyDelete(redisKey));
        using var harness = Build(FormatNegotiationMode.Force, fast: serializer);
        string key;
        (key, redisKey) = await SeedAsync(harness, raw, JsonX);

        await harness.Cache().GetAsync<string>(key, harness.Resolve(MigrateOnReadHarness.Migrating), Ct);
        await harness.Migrator.WhenIdleAsync(Ct);

        (await raw.GetDatabase().KeyExistsAsync(redisKey)).Should().BeFalse();
        harness.Outcomes("changed").Should().Be(1);
    }

    [Fact]
    public async Task The_cached_null_marker_and_an_empty_value_are_never_rewritten()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Force, cacheNulls: true);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(harness, raw, []);

        var read = await harness.Cache().GetAsync<string>(key, harness.Resolve(MigrateOnReadHarness.Migrating), Ct);
        await harness.Migrator.WhenIdleAsync(Ct);

        read.Should().BeNull();
        ((byte[]?)await raw.GetDatabase().StringGetAsync(redisKey)).Should().BeEmpty();
        harness.Telemetry.Metrics.Where(m => m.Name == Metrics.SerializerRewrite).Should().BeEmpty();
    }

    [Fact]
    public async Task A_hot_key_is_rewritten_once_however_many_read_it()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Force);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(harness, raw, JsonX);
        var cache = harness.Cache();
        var policy = harness.Resolve(MigrateOnReadHarness.Migrating);

        var reads = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => cache.GetAsync<string>(key, policy, Ct).AsTask(), Ct)));
        await harness.Migrator.WhenIdleAsync(Ct);

        reads.Should().AllBe("x");
        ((byte[]?)await raw.GetDatabase().StringGetAsync(redisKey)).Should().Equal(FastX);
        harness.Telemetry.Metrics.Where(m => m.Name == Metrics.SerializerRewrite).Should().ContainSingle().Which.Properties!["outcome"].Should().Be("done");
    }

    [Fact]
    public async Task A_value_served_from_the_local_tier_is_not_offered()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Force, layered: true);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(harness, raw, FastX);
        var layered = harness.Cache(layered: true);
        var policy = harness.Resolve(MigrateOnReadHarness.Migrating);
        (await layered.GetAsync<string>(key, policy, Ct)).Should().Be("x");
        await harness.Migrator.WhenIdleAsync(Ct);
        await raw.GetDatabase().StringSetAsync(redisKey, JsonX, TimeSpan.FromMinutes(5));

        var again = await layered.GetAsync<string>(key, policy, Ct);
        await harness.Migrator.WhenIdleAsync(Ct);

        again.Should().Be("x");
        ((byte[]?)await raw.GetDatabase().StringGetAsync(redisKey)).Should().Equal(JsonX, "the second read never reached Redis");
        harness.Telemetry.Metrics.Where(m => m.Name == Metrics.SerializerRewrite).Should().BeEmpty();
    }

    [Fact]
    public async Task A_rewrite_adds_nothing_to_the_broadcast_streams()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var app = App();
        using var harness = Build(FormatNegotiationMode.Force, layered: true, app: app, configure: b =>
        {
            b.AddRedisStreams(o => { o.Enabled = true; o.MaintainerEnabled = false; });
            b.AddBroadcast();
        });
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var layered = harness.Cache(layered: true);
        var policy = harness.Resolve(MigrateOnReadHarness.Migrating);
        // One write through the layered cache makes the stream exist, so there is something to count.
        await layered.SetAsync($"warm-{Unique()}", "w", policy, Ct);
        var key = Unique();
        var redisKey = await RawKeyAsync(raw, await SeedKeyAsync(harness, key));
        await raw.GetDatabase().StringSetAsync(redisKey, JsonX, TimeSpan.FromMinutes(5));
        var before = await StreamLengthsAsync(raw, app);
        before.Should().NotBeEmpty();

        (await harness.Cache().GetAsync<string>(key, policy, Ct)).Should().Be("x");
        await harness.Migrator.WhenIdleAsync(Ct);

        ((byte[]?)await raw.GetDatabase().StringGetAsync(redisKey)).Should().Equal(FastX);
        (await StreamLengthsAsync(raw, app)).Should().BeEquivalentTo(before, "a rewrite changes no meaning, so it tells no node");
    }

    [Fact]
    public async Task Shutting_down_with_rewrites_queued_ends_cleanly_and_never_leaves_a_half_written_value()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var unobserved = new List<Exception>();
        // Process-wide, so what another test left behind is not ours.
        void Observe(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.ToString().Contains("FormatMigrator", StringComparison.Ordinal) || e.Exception.ToString().Contains("RewriteStored", StringComparison.Ordinal))
            {
                unobserved.Add(e.Exception);
            }
        }

        TaskScheduler.UnobservedTaskException += Observe;
        try
        {
            var harness = Build(FormatNegotiationMode.Force);
            await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
            var cache = harness.Cache();
            var policy = harness.Resolve(MigrateOnReadHarness.Migrating);
            var keys = new List<(string Key, RedisKey RedisKey)>();
            for (var i = 0; i < 50; i++)
            {
                keys.Add(await SeedAsync(harness, raw, JsonX));
            }

            foreach (var (key, _) in keys)
            {
                await cache.GetAsync<string>(key, policy, Ct);
            }

            var migrator = harness.Migrator;
            harness.Dispose();
            await migrator.Stopped.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            GC.Collect();
            GC.WaitForPendingFinalizers();

            unobserved.Should().BeEmpty();
            foreach (var (_, redisKey) in keys)
            {
                var stored = (byte[]?)await raw.GetDatabase().StringGetAsync(redisKey);
                (stored!.SequenceEqual(JsonX) || stored!.SequenceEqual(FastX)).Should().BeTrue("a value is the old bytes or the new ones, never a mix");
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Observe;
        }
    }

    [Fact]
    public async Task Every_value_read_is_counted_by_the_format_it_was_stored_in_and_a_rewrite_ends_the_json_count()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Force);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, _) = await SeedAsync(harness, raw, JsonX);
        var cache = harness.Cache();
        var policy = harness.Resolve(MigrateOnReadHarness.Migrating);

        await cache.GetAsync<string>(key, policy, Ct);
        await harness.Migrator.WhenIdleAsync(Ct);
        await cache.GetAsync<string>(key, policy, Ct);
        await cache.GetAsync<string>(key, policy, Ct);

        var reads = harness.Telemetry.Metrics.Where(m => m.Name == Metrics.SerializerRead).ToList();
        reads.Select(r => (r.Properties!["format"], r.Properties["cache"])).Should().Equal(("json", "Redis"), ("fast", "Redis"), ("fast", "Redis"));
        reads.Should().OnlyContain(r => r.Value == 1);
    }

    [Fact]
    public async Task Every_value_written_through_a_registry_is_counted_by_the_format_it_was_written_in()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = Build(FormatNegotiationMode.Auto);
        var cache = harness.Cache();
        var policy = harness.Resolve(MigrateOnReadHarness.Migrating);
        var key = Unique();

        await cache.SetAsync(key, "x", policy, Ct);
        harness.Switch();
        await cache.SetAsync(key, "x", policy, Ct);
        await cache.GetOrAddAsync<string>(Unique(), _ => Task.FromResult<string?>("x"), policy, Ct);

        harness.Telemetry.Metrics.Where(m => m.Name == Metrics.SerializerWrite).Select(w => w.Properties!["format"]).Should().Equal("json", "fast", "fast");
    }

    [Fact]
    public async Task An_app_with_no_serializer_registered_emits_none_of_the_serializer_metrics()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var telemetry = new RecordingTelemetryProvider();
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<ICachingTelemetryProvider>(telemetry)
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = fixture.ConnectionString);
                    b.AddRedis(_ => { });
                },
                o => o.AppShortName = App())
            .BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
        var key = Unique();

        await cache.SetAsync(key, "x", policy: null, Ct);
        await cache.GetAsync<string>(key, policy: null, Ct);

        telemetry.Metrics.Where(m => m.Name.StartsWith("cache.serializer.", StringComparison.Ordinal)).Should().BeEmpty();
        provider.GetService<ValueMigration>().Should().BeNull();
    }

    [Fact]
    public async Task Real_messagepack_values_migrate_from_json_and_a_compressed_one_is_already_in_the_format()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = fixture.ConnectionString);
                    b.AddRedis(o => o.FormatNegotiation.Mode = FormatNegotiationMode.Force);
                    b.AddMessagePackSerializer();
                },
                o =>
                {
                    o.AppShortName = App();
                    o.Policies["mp"] = MigrateOnReadHarness.Policy("messagepack", null, migrate: true);
                })
            .BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
        var policy = provider.GetRequiredService<ICachePolicyFactory>().Resolve("mp")!;
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var dto = new TestDto { Id = 5, Name = "five" };
        var jsonKey = Unique();
        var lz4Key = Unique();
        await cache.SetAsync(jsonKey, dto, policy: null, Ct);
        var big = new TestDto { Id = 6, Name = new string('z', 5000) };
        await cache.SetAsync(lz4Key, big, policy, Ct);
        var lz4Raw = (await RawValueAsync(raw, lz4Key))!;
        lz4Raw[0].Should().Be(0x03, "above the compression threshold the serializer writes the compressed id");
        var migrator = provider.GetRequiredService<ValueMigration>().Migrator;

        (await cache.GetAsync<TestDto>(jsonKey, policy, Ct)).Should().BeEquivalentTo(dto);
        (await cache.GetAsync<TestDto>(lz4Key, policy, Ct)).Should().BeEquivalentTo(big);
        await migrator.WhenIdleAsync(Ct);

        (await RawValueAsync(raw, jsonKey))![0].Should().Be(0x02, "the JSON value is now MessagePack");
        (await RawValueAsync(raw, lz4Key)).Should().Equal(lz4Raw, "a value the target's own serializer wrote is not touched");
        (await cache.GetAsync<TestDto>(jsonKey, policy, Ct)).Should().BeEquivalentTo(dto);
    }

    private static string App() => $"mor{Guid.NewGuid():N}"[..20];

    private static string Unique() => $"it-{Guid.NewGuid():N}";

    private static byte[] Bytes(string name) => name switch
    {
        "json" => JsonX,
        "fast" => FastX,
        "other" => OtherX,
        "unknown" => UnknownX,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No such stored form."),
    };

    private static async Task<RedisKey> RawKeyAsync(ConnectionMultiplexer raw, string key)
    {
        await Task.CompletedTask;
        return raw.GetServer(raw.GetEndPoints()[0]).Keys(pattern: $"*{key}*").Single();
    }

    private static async Task<byte[]?> RawValueAsync(ConnectionMultiplexer raw, string key) =>
        await raw.GetDatabase().StringGetAsync(await RawKeyAsync(raw, key));

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

    private static async Task<Dictionary<string, long>> StreamLengthsAsync(ConnectionMultiplexer raw, string app)
    {
        var db = raw.GetDatabase();
        var lengths = new Dictionary<string, long>();
        foreach (var key in raw.GetServer(raw.GetEndPoints()[0]).Keys(pattern: $"*{app}*"))
        {
            if (await db.KeyTypeAsync(key) == RedisType.Stream)
            {
                lengths[key.ToString()] = await db.StreamLengthAsync(key);
            }
        }

        return lengths;
    }

    private async Task<string> SeedKeyAsync(MigrateOnReadHarness harness, string key)
    {
        await harness.Cache().SetAsync(key, "seed", policy: null, Ct);
        return key;
    }

    private async Task<(string Key, RedisKey RedisKey)> SeedAsync(MigrateOnReadHarness harness, ConnectionMultiplexer raw, byte[] stored, TimeSpan? ttl = null)
    {
        var key = Unique();
        await harness.Cache().SetAsync(key, "seed", policy: null, Ct);
        var redisKey = await RawKeyAsync(raw, key);
        await raw.GetDatabase().StringSetAsync(redisKey, stored, ttl ?? TimeSpan.FromMinutes(5));
        return (key, redisKey);
    }

    private MigrateOnReadHarness Build(
        FormatNegotiationMode mode,
        bool layered = false,
        bool scripts = false,
        bool cacheNulls = false,
        string? app = null,
        Action<ICachingBuilder>? configure = null,
        ICacheValueSerializer? fast = null,
        ICacheValueSerializer? other = null) =>
        MigrateOnReadHarness.Build(fixture.ConnectionString, mode, app ?? App(), layered, scripts, cacheNulls, configure, other: other, fast: fast);

    private sealed class GivesUpOnWrites : IResiliencePipelineProvider
    {
        public IResiliencePipeline Get(string? name) =>
            name == ResiliencePipelineNames.Write ? new GivesUp() : new PassThrough();

        private sealed class GivesUp : IResiliencePipeline
        {
            public ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default) =>
                ValueTask.FromResult(defaultValue);
        }

        private sealed class PassThrough : IResiliencePipeline
        {
            public ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default) =>
                callback(cancellationToken);
        }
    }

    private sealed class Measure
    {
        public string? Name { get; set; }

        public double Value { get; set; }
    }

    private sealed class JsonBlockingSerializer(int id, TaskCompletionSource entered, TaskCompletionSource gate, bool forgetful = false) : ICacheValueSerializer
    {
        public IReadOnlyList<int> FormatIds { get; } = [id];

        public int Serialize<T>(T value, IBufferWriter<byte> target)
        {
            entered.TrySetResult();
            if (!forgetful)
            {
                gate.Task.GetAwaiter().GetResult();
            }

            target.Write(forgetful ? "{\"Id\":7}"u8 : System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value));
            return id;
        }

        public T? Deserialize<T>(int formatId, ReadOnlySequence<byte> source) => System.Text.Json.JsonSerializer.Deserialize<T>(source.ToArray());
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
