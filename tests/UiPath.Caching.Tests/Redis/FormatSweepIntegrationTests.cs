using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Logging;
using UiPath.Caching.Negotiation;
using UiPath.Caching.Policies;
using UiPath.Caching.Serialization;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Redis;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class FormatSweepIntegrationTests(RedisContainerFixture fixture)
{
    private static readonly byte[] Header = [0x10];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_sweep_converts_json_and_envelope_values_in_place_keeping_the_exact_expiry_and_leaves_the_residue_with_its_reason(bool envelope)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.ServerVersion >= new Version(7, 0), "PEXPIRETIME needs Redis 7.");
        using var rig = Rig.Build(fixture.ConnectionString, envelope);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = raw.GetDatabase();
        var ttl = TimeSpan.FromMinutes(5);
        var plain = (string name) => envelope ? new HeaderFreeProxy().Serialize(new Item(name))! : Json(new Item(name));
        await rig.SetAsync(db, "json-ttl", plain("a"), ttl);
        await rig.SetAsync(db, "json-forever", plain("b"), null);
        await rig.SetAsync(db, "other", [0x12, .. Json(new Item("c"))], ttl);
        await rig.SetAsync(db, "already", [.. Header, .. Json(new Item("d"))], ttl);
        await rig.SetAsync(db, "empty", [], ttl);
        await rig.SetAsync(db, "default", envelope ? "env:null"u8.ToArray() : "null"u8.ToArray(), ttl);
        await rig.SetAsync(db, "oversize", [.. plain("e"), .. new byte[300 * 1024]], ttl);
        await rig.SetAsync(db, "lossy", envelope ? "env:{\"A\":\"f\",\"B\":1}"u8.ToArray() : "{\"A\":\"f\",\"B\":1}"u8.ToArray(), ttl);
        var expiries = new Dictionary<string, long>();
        foreach (var name in new[] { "json-ttl", "other", "already" })
        {
            expiries[name] = await rig.ExpiryAsync(db, name);
        }

        var report = await rig.Provider.SweepFormatsAsync<Item>(rig.Pattern, new FormatSweepOptions { RatePerSecond = 1000 }, Ct);

        report.Converted.Should().Be(3, "json with a ttl, json without one, and the other registered format");
        report.Current.Should().Be(1);
        report.DefaultOrEmpty.Should().Be(2);
        report.Oversize.Should().Be(1);
        report.Lossy.Should().Be(1);
        report.Failed.Should().Be(0);
        report.Completed.Should().BeTrue();
        (await rig.GetAsync(db, "json-ttl")).Should().Equal([.. Header, .. Json(new Item("a"))]);
        (await rig.GetAsync(db, "json-forever")).Should().Equal([.. Header, .. Json(new Item("b"))]);
        (await rig.GetAsync(db, "other")).Should().Equal([.. Header, .. Json(new Item("c"))]);
        (await rig.GetAsync(db, "empty")).Should().BeEmpty();
        (await rig.GetAsync(db, "default")).Should().Equal(envelope ? "env:null"u8.ToArray() : "null"u8.ToArray());
        (await rig.GetAsync(db, "lossy")).Should().Equal(envelope ? "env:{\"A\":\"f\",\"B\":1}"u8.ToArray() : "{\"A\":\"f\",\"B\":1}"u8.ToArray());
        (await rig.ExpiryAsync(db, "json-ttl")).Should().Be(expiries["json-ttl"], "the exact expiry is kept");
        (await rig.ExpiryAsync(db, "other")).Should().Be(expiries["other"]);
        (await rig.ExpiryAsync(db, "already")).Should().Be(expiries["already"]);
        (await db.KeyTimeToLiveAsync(rig.Key("json-forever"))).Should().BeNull("a key with no ttl stays without one");

        var census = await rig.Provider.CountFormatsByReasonAsync(rig.Pattern, Ct);

        census.ByReason.Should().BeEquivalentTo(
            new Dictionary<string, long>
            {
                ["format_fast"] = 4,
                ["format_json"] = 3,
                ["empty"] = 1,
                ["oversize"] = 1,
                ["no_ttl"] = 1,
            },
            "what is left in json is the residue: a default, a value over the size and a lossy one");
        census.ByFormat.Should().BeEquivalentTo(new Dictionary<string, long> { ["fast"] = 4, ["json"] = 3 });
        census.Scanned.Should().Be(8);
        rig.Tracked(Metrics.FormatCensus, "reason", "format_json").Should().Be(3);
        rig.Tracked(Metrics.FormatSweep, "outcome", "converted").Should().Be(3);
    }

    [Fact]
    public async Task A_hash_is_reported_as_sampled_and_never_swept()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var rig = Rig.Build(fixture.ConnectionString, envelope: false);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = raw.GetDatabase();
        var hash = $"{rig.App}:h:{rig.Run}:h1";
        await db.HashSetAsync(hash, "a", Json(new Item("a")));

        var report = await rig.Provider.SweepFormatsAsync<Item>(rig.Pattern, new FormatSweepOptions(), Ct);
        var census = await rig.Provider.CountFormatsByReasonAsync(rig.Pattern, Ct);

        report.Scanned.Should().Be(0);
        ((byte[]?)await db.HashGetAsync(hash, "a")).Should().Equal(Json(new Item("a")));
        census.ByReason.Should().BeEquivalentTo(new Dictionary<string, long> { ["hash_sampled"] = 1 });
        census.ByFormat.Should().BeEquivalentTo(new Dictionary<string, long> { ["json"] = 1 });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_write_that_lands_after_the_read_wins_over_the_rewrite(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = raw.GetDatabase();
        using var rig = Rig.Build(fixture.ConnectionString, envelope: false, scripts: scripts);
        var newer = Json(new Item("newer"));
        using var racing = rig.Race(key => db.StringSetAsync(key, newer, TimeSpan.FromMinutes(1)));
        await rig.SetAsync(db, "k", Json(new Item("older")), TimeSpan.FromMinutes(5));

        var report = await racing.SweepAsync(Ct);

        report.Changed.Should().Be(1);
        report.Converted.Should().Be(0);
        (await rig.GetAsync(db, "k")).Should().Equal(newer, "the writer's value stands, unconverted");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_key_deleted_after_the_read_is_never_recreated(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = raw.GetDatabase();
        using var rig = Rig.Build(fixture.ConnectionString, envelope: false, scripts: scripts);
        using var racing = rig.Race(key => db.KeyDeleteAsync(key));
        await rig.SetAsync(db, "k", Json(new Item("older")), TimeSpan.FromMinutes(5));

        var report = await racing.SweepAsync(Ct);

        report.Changed.Should().Be(1);
        (await db.KeyExistsAsync(rig.Key("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_writer_loop_running_beside_the_sweep_loses_no_write()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var rig = Rig.Build(fixture.ConnectionString, envelope: false, scripts: true);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = raw.GetDatabase();
        const int keys = 20;
        var last = new int[keys];
        for (var i = 0; i < keys; i++)
        {
            await rig.SetAsync(db, $"k{i}", Json(new Item("0")), null);
        }

        var writer = Task.Run(
            async () =>
            {
                for (var n = 1; n <= 3000; n++)
                {
                    var key = n % keys;
                    await rig.SetAsync(db, $"k{key}", Json(new Item(n.ToString())), null);
                    last[key] = n;
                }
            },
            Ct);
        long attempted = 0;
        while (!writer.IsCompleted)
        {
            var pass = await rig.Provider.SweepFormatsAsync<Item>(rig.Pattern, new FormatSweepOptions { RatePerSecond = 100_000, PageSize = 10 }, Ct);
            attempted += pass.Converted + pass.Changed;
        }

        await writer;
        for (var i = 0; i < keys; i++)
        {
            var stored = await rig.GetAsync(db, $"k{i}");
            var body = stored.AsSpan().StartsWith(Header) ? stored[1..] : stored;
            JsonSerializer.Deserialize<Item>(body)!.A.Should().Be(last[i].ToString(), $"the last write to k{i} is the one that stands");
        }

        attempted.Should().BeGreaterThan(0, "the sweep did convert while the writer ran");
    }

    [Fact]
    public async Task A_server_that_refuses_scripts_makes_the_sweep_use_the_transaction_and_stop_asking()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.SupportsAcl, "Needs ACL SETUSER, which this server does not allow.");
        Assert.SkipUnless(fixture.ServerVersion >= new Version(7, 0), "PEXPIRETIME needs Redis 7.");
        await using var admin = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var server = admin.GetServer(admin.GetEndPoints()[0]);
        var user = $"it-sweepscript-{Guid.NewGuid():N}";
        await server.ExecuteAsync("ACL", "SETUSER", user, "on", ">pw", "~*", "&*", "+@all", "-eval", "-evalsha", "-eval_ro", "-evalsha_ro", "-@scripting");
        try
        {
            using var rig = Rig.Build($"{fixture.ConnectionString},user={user},password=pw", envelope: false, scripts: true);
            var db = admin.GetDatabase();
            await rig.SetAsync(db, "a", Json(new Item("a")), TimeSpan.FromMinutes(5));
            await rig.SetAsync(db, "b", Json(new Item("b")), TimeSpan.FromMinutes(5));
            var expiry = await rig.ExpiryAsync(db, "a");
            await server.ExecuteAsync("ACL", "LOG", "RESET");

            var first = await rig.Provider.SweepFormatsAsync<Item>(rig.Pattern, new FormatSweepOptions { RatePerSecond = 1000 }, Ct);
            var refusals = await ScriptRefusalsAsync(server, user);
            await rig.SetAsync(db, "c", Json(new Item("c")), TimeSpan.FromMinutes(5));
            var second = await rig.Provider.SweepFormatsAsync<Item>(rig.Pattern, new FormatSweepOptions { RatePerSecond = 1000 }, Ct);

            first.Converted.Should().Be(2);
            first.Failed.Should().Be(0);
            second.Converted.Should().Be(1);
            refusals.Should().BeGreaterThan(0, "the script was tried and refused once");
            (await ScriptRefusalsAsync(server, user)).Should().Be(refusals, "scripts stay off once the server has refused one");
            (await rig.GetAsync(db, "a")).Should().Equal([.. Header, .. Json(new Item("a"))]);
            (await rig.GetAsync(db, "c")).Should().Equal([.. Header, .. Json(new Item("c"))]);
            (await rig.ExpiryAsync(db, "a")).Should().Be(expiry, "the transaction keeps the expiry too");
        }
        finally
        {
            await server.ExecuteAsync("ACL", "DELUSER", user);
        }
    }

    [Fact]
    public async Task A_sweep_with_nothing_registered_or_a_json_default_says_so()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var rig = Rig.Build(fixture.ConnectionString, envelope: false, writes: "json");

        var act = () => rig.Provider.SweepFormatsAsync<Item>(rig.Pattern, new FormatSweepOptions(), Ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*JSON*");
    }

    [Fact]
    public async Task A_value_replaced_by_a_huge_one_after_its_length_was_read_is_not_fetched_past_the_limit()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = raw.GetDatabase();
        using var rig = Rig.Build(fixture.ConnectionString, envelope: false);
        await rig.SetAsync(db, "k", Json(new Item("small")), TimeSpan.FromMinutes(5));
        var huge = new byte[8 * 1024 * 1024];
        huge[0] = (byte)'"';
        var fetched = new List<int>();
        var connector = new InterceptingConnector(
            rig.ConnectorOf(),
            after: (method, args, result) => (method.Name, result, args) switch
            {
                (nameof(IDatabase.StringLengthAsync), Task<long> length, _) => ReplaceAfter(length, () => rig.SetAsync(db, "k", huge, TimeSpan.FromMinutes(5))),
                (nameof(IDatabase.StringGetRangeAsync), Task<RedisValue> read, [_, _, long end, _]) when end > 1 => Measure(read, fetched),
                _ => result,
            });

        var report = await rig.ProviderOver(connector).SweepFormatsAsync<Item>(rig.Pattern, new FormatSweepOptions { RatePerSecond = 1000, MaxValueBytes = 1024 }, Ct);

        fetched.Should().ContainSingle().Which.Should().Be(1025, "the read asked for the limit and one byte, and got exactly that of an eight megabyte value");
        report.Oversize.Should().Be(1);
        report.Converted.Should().Be(0);
        (await rig.GetAsync(db, "k")).Should().HaveCount(huge.Length, "the writer's value stands");
    }

    [Fact]
    public async Task A_census_publishes_every_known_reason_so_a_count_can_drain_to_zero()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.ServerVersion >= new Version(7, 0), "PEXPIRETIME needs Redis 7.");
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = raw.GetDatabase();
        using var rig = Rig.Build(fixture.ConnectionString, envelope: false);
        await rig.SetAsync(db, "a", Json(new Item("a")), TimeSpan.FromMinutes(5));
        await rig.SetAsync(db, "b", Json(new Item("b")), TimeSpan.FromMinutes(5));

        await rig.Provider.CountFormatsByReasonAsync(rig.Pattern, Ct);
        var first = rig.Telemetry.Metrics.Where(m => m.Name == Metrics.FormatCensus).ToList();
        await rig.Provider.SweepFormatsAsync<Item>(rig.Pattern, new FormatSweepOptions { RatePerSecond = 1000 }, Ct);
        await rig.Provider.CountFormatsByReasonAsync(rig.Pattern, Ct);
        var second = rig.Telemetry.Metrics.Where(m => m.Name == Metrics.FormatCensus).Skip(first.Count).ToList();

        first.Single(m => m.Properties!["reason"] == "format_json").Value.Should().Be(2);
        second.Single(m => m.Properties!["reason"] == "format_json").Value.Should().Be(0, "nothing is left in json, and the gauge says so");
        second.Single(m => m.Properties!["reason"] == "format_fast").Value.Should().Be(2);
        second.Select(m => m.Properties!["reason"]).Should().BeEquivalentTo(
            "empty", "oversize", "no_ttl", "hash_sampled", "format_json", "format_unknown", "format_fast", "format_other");
        second.Where(m => m.Properties!["reason"] is not "format_fast").Should().OnlyContain(m => m.Value == 0);
    }

    private static byte[] Json(Item item) => JsonSerializer.SerializeToUtf8Bytes(item);

    private static async Task<long> ReplaceAfter(Task<long> length, Func<Task> replace)
    {
        var seen = await length.ConfigureAwait(false);
        await replace().ConfigureAwait(false);
        return seen;
    }

    private static async Task<RedisValue> Measure(Task<RedisValue> read, List<int> fetched)
    {
        var value = await read.ConfigureAwait(false);
        fetched.Add(((byte[]?)value)?.Length ?? 0);
        return value;
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

    private sealed record Item(string A);

    // An app of its own under a run of its own, so keys of one test are never another's; the sweep goes through the provider the container builds.
    private sealed class Rig : IDisposable
    {
        private readonly ServiceProvider _services;

        private Rig(ServiceProvider services, RecordingTelemetryProvider telemetry, string app)
        {
            _services = services;
            Telemetry = telemetry;
            App = app;
            Run = $"sweep{Guid.NewGuid():N}"[..24];
            Provider = services.GetServices<ICacheProvider>().OfType<RedisCacheProvider>().Single();
        }

        public RecordingTelemetryProvider Telemetry { get; }

        public RedisCacheProvider Provider { get; }

        public string App { get; }

        public string Run { get; }

        public CacheKey Pattern => new($"{Run}:*");

        public static Rig Build(string connection, bool envelope, bool scripts = false, string writes = "fast")
        {
            var telemetry = new RecordingTelemetryProvider();
            var app = $"sw{Guid.NewGuid():N}"[..12];
            var services = new ServiceCollection().AddLogging().AddSingleton<ICachingTelemetryProvider>(telemetry);
            var fast = new PassthroughValueSerializer(0x10);
            var other = new PassthroughValueSerializer(0x12);
            services.AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = connection);
                    b.AddRedis(o =>
                    {
                        o.FormatNegotiation.Mode = FormatNegotiationMode.Force;
                        o.ReadScriptsEnabled = scripts;
                    });
                    b.AddValueSerializer("fast", fast.FormatIds, _ => fast);
                    b.AddValueSerializer("other", other.FormatIds, _ => other);
                    if (envelope)
                    {
                        b.Services.AddSingleton<ISerializerProxy<byte[]>>(new HeaderFreeProxy());
                    }
                },
                o =>
                {
                    o.AppShortName = app;
                    o.DefaultCachePolicy = new CachePolicy
                    {
                        DistributedExpiration = TimeSpan.FromMinutes(5),
                        Serializer = new SerializerPolicy { Write = writes, Read = ["json", "fast", "other"] },
                    };
                });
            return new Rig(services.BuildServiceProvider(), telemetry, app);
        }

        public RedisKey Key(string name) => $"{App}:s:{Run}:{name}";

        public Task SetAsync(IDatabase db, string name, byte[] value, TimeSpan? ttl) => db.StringSetAsync(Key(name), value, ttl is { } span ? span : Expiration.Default);

        public async Task<byte[]> GetAsync(IDatabase db, string name) => (byte[]?)await db.StringGetAsync(Key(name)) ?? [];

        public async Task<long> ExpiryAsync(IDatabase db, string name) => (long)await db.ExecuteAsync("PEXPIRETIME", Key(name));

        public double Tracked(string metric, string tag, string value) =>
            Telemetry.Metrics.Where(m => m.Name == metric && m.Properties![tag] == value).Sum(m => m.Value);

        // A sweep whose connector runs the step once, right after the sweep has read a string and before it rewrites it.
        public RaceStep Race(Func<RedisKey, Task> step) => new(this, step);

        public IRedisConnector ConnectorOf() => _services.GetRequiredService<IRedisConnector>();

        public RedisCacheProvider ProviderOver(IRedisConnector connector) =>
            new(
                _services.GetRequiredService<IOptions<RedisCacheOptions>>(),
                _services.GetRequiredService<IOptions<CacheOptions>>(),
                connector,
                _services.GetRequiredService<ISerializerProxy<byte[]>>(),
                _services.GetRequiredService<IResiliencePipelineProvider>(),
                _services.GetRequiredService<ICachingTelemetryProvider>(),
                _services.GetRequiredService<ILoggerFactory>(),
                _services.GetRequiredService<ICachePolicyFactory>(),
                _services.GetRequiredService<TimeProvider>(),
                _services.GetService<IKeyMaskingPolicy>(),
                _services.GetRequiredService<ValueSerializerRegistry>(),
                _services.GetService<FormatNegotiation>()?.Negotiator,
                _services.GetService<ValueMigration>());

        public void Dispose() => _services.Dispose();
    }

    private sealed class RaceStep : IDisposable
    {
        private readonly Rig _rig;
        private readonly Func<RedisKey, Task> _step;
        private int _done;

        public RaceStep(Rig rig, Func<RedisKey, Task> step)
        {
            _rig = rig;
            _step = step;
        }

        public Task<FormatSweepReport> SweepAsync(CancellationToken token)
        {
            var inner = _rig.ConnectorOf();
            var connector = new InterceptingConnector(inner, after: After);
            return _rig.ProviderOver(connector).SweepFormatsAsync<Item>(_rig.Pattern, new FormatSweepOptions { RatePerSecond = 1000 }, token);
        }

        public void Dispose()
        {
        }

        private object? After(MethodInfo method, object?[] args, object? result) =>
            method.Name == nameof(IDatabase.StringGetRangeAsync) && args is [RedisKey key, _, long end, _] && end > 1 && result is Task<RedisValue> read && Interlocked.Exchange(ref _done, 1) == 0
                ? Then(key, read)
                : result;

        private async Task<RedisValue> Then(RedisKey key, Task<RedisValue> read)
        {
            var value = await read.ConfigureAwait(false);
            await _step(key).ConfigureAwait(false);
            return value;
        }
    }
}
