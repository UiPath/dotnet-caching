using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Negotiation;
using UiPath.Caching.Policies;
using UiPath.Caching.Serialization;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Negotiation;

[Collection(WriterPoolCollection.Name)]
public class EffectiveWriteTests(ITestContextAccessor testContextAccessor)
{
    private static readonly string[] Binary = ["10"];

    private readonly IDatabase _database = Substitute.For<IDatabase>();
    private readonly ITransaction _transaction = Substitute.For<ITransaction>();
    private readonly RecordingResiliencePipeline _write = new();
    private readonly PassthroughValueSerializer _fast = new(0x10);
    private readonly Gate _gate = new();

    private CancellationToken Token => testContextAccessor.Current.CancellationToken;

    [Fact]
    public async Task Write_follows_effective_format_for_the_simple_cache()
    {
        var sut = SimpleCache(out var registry);
        var policy = ResolvedPolicy(registry);
        var written = CaptureSimpleWrites();

        await sut.SetAsync("k", "hello", TimeSpan.FromMinutes(1), policy, Token);
        _gate.Open = true;
        await sut.SetAsync("k", "hello", TimeSpan.FromMinutes(1), policy, Token);
        await sut.GetOrAddAsync<string>("g", _ => Task.FromResult<string?>("hello"), TimeSpan.FromMinutes(1), policy, Token);
        _gate.Open = false;
        await sut.SetAsync("k", "hello", TimeSpan.FromMinutes(1), policy, Token);

        written.Select(w => w[0] == 0x10).Should().Equal(false, true, true, false);
        written[0].Should().Equal(Encoding.UTF8.GetBytes("\"hello\""), "a closed gate writes the JSON the default policy writes");
    }

    [Fact]
    public async Task Write_follows_effective_format_for_hash_fields_and_metadata()
    {
        var sut = HashCache(out var registry);
        var policy = ResolvedPolicy(registry);
        var written = CaptureHashWrites();
        var options = new HashCacheEntryOptions(DateTimeOffset.UtcNow.AddMinutes(5), Metadata: new Dictionary<string, string?> { ["m"] = "v" });

        await sut.SetAsync("k", new Dictionary<string, string?> { ["a"] = "x" }, options, policy, Token);
        _gate.Open = true;
        await sut.SetAsync("k", new Dictionary<string, string?> { ["a"] = "x" }, options, policy, Token);

        written.Should().HaveCount(2);
        Raw(written[0], "a").Should().Equal(Encoding.UTF8.GetBytes("\"x\""));
        Raw(written[0], "_metadata_").Should().Equal(Encoding.UTF8.GetBytes("{\"m\":\"v\"}"));
        Raw(written[1], "a")[0].Should().Be(0x10);
        Raw(written[1], "_metadata_")[0].Should().Be(0x10);
    }

    [Fact]
    public async Task Write_follows_effective_format_for_a_hash_replace_and_for_metadata_alone()
    {
        var sut = HashCache(out var registry, new CachePolicy { Serializer = new SerializerPolicy { Write = "fast" } });
        var policy = ResolvedPolicy(registry);
        var written = CaptureHashWrites();
        var replace = new HashCacheEntryOptions(DateTimeOffset.UtcNow.AddMinutes(5), Metadata: new Dictionary<string, string?> { ["m"] = "v" }, SetOption: HashCacheSetOption.HashReplace);
        var metadata = new Dictionary<string, string?> { ["m"] = "v" };
        _database.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Do<RedisValue>(v => written.Add([new HashEntry("_metadata_", v)])), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);

        await sut.SetAsync("k", new Dictionary<string, string?> { ["a"] = "x" }, replace, policy, Token);
        await sut.SetMetadataAsync<string>("k", metadata, Token);

        written.Should().NotBeEmpty();
        written.SelectMany(e => e).Should().OnlyContain(e => ((byte[])e.Value!)[0] != 0x10);
        written.Clear();

        _gate.Open = true;
        await sut.SetAsync("k", new Dictionary<string, string?> { ["a"] = "x" }, replace, policy, Token);
        await sut.SetMetadataAsync<string>("k", metadata, Token);

        written.Should().NotBeEmpty();
        written.SelectMany(e => e).Should().OnlyContain(e => ((byte[])e.Value!)[0] == 0x10);
    }

    [Fact]
    public async Task A_closed_gate_still_reads_a_headered_value()
    {
        var sut = SimpleCache(out var registry);
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)new byte[] { 0x10, (byte)'h', (byte)'i' });

        (await sut.GetAsync<string>("k", ResolvedPolicy(registry), Token)).Should().Be("hi");
    }

    [Fact]
    public async Task Hot_path_issues_no_extra_redis_call()
    {
        var nodeList = new FakeNodeListServer();
        var policy = ResolvedPolicy(new ValueSerializerRegistry([("fast", _fast, false)]));
        var negotiator = new FormatNegotiator([new DesiredWrite(_fast, Binary, ["default"], [])], new FormatNegotiationOptions { UpgradeHold = TimeSpan.Zero }, TimeProvider.System);
        negotiator.Observe(new NodeListView([new("me", new("3.2.0", "i", ["json", "10"], new Dictionary<string, IReadOnlyList<string>>()), TimeSpan.Zero)], [], Complete: true));
        negotiator.Allows(_fast).Should().BeTrue();
        var without = SimpleCache(out _, gate: false);
        var with = SimpleCache(out _, negotiator: negotiator);
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        _transaction.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);

        _database.ClearReceivedCalls();
        await Operations(without, policy);
        var baseline = _database.ReceivedCalls().Count() + _transaction.ReceivedCalls().Count();
        _database.ClearReceivedCalls();
        _transaction.ClearReceivedCalls();
        await Operations(with, policy);

        // A set, a get and a get-or-add miss: one write, one read, one read and one write.
        baseline.Should().Be(4);
        (_database.ReceivedCalls().Count() + _transaction.ReceivedCalls().Count()).Should().Be(4);
        nodeList.Database.ReceivedCalls().Should().BeEmpty("the node list is read and written by the service on its timer, never by a cache call");
    }

    [Fact]
    public async Task Force_mode_ignores_node_list_and_still_advertises_what_it_reads()
    {
        var options = new RedisCacheOptions { FormatNegotiation = { Mode = FormatNegotiationMode.Force } };
        var services = Build(options, new ValueSerializerRegistry([("fast", _fast, false)]), new CachePolicy { Serializer = new SerializerPolicy { Write = "fast" } });

        var forced = FormatNegotiation.Create(services);
        forced.Negotiator.Should().BeNull("Force writes exactly what is configured, so there is nothing to decide");
        forced.IsActive.Should().BeTrue("it reads every format, so the nodes that decide must be told");
        forced.Advertisement!.ValueReads.Should().Equal("json", "10");
        var sut = SimpleCache(out var registry, gate: false);
        var written = CaptureSimpleWrites();
        await sut.SetAsync("k", "hello", TimeSpan.FromMinutes(1), ResolvedPolicy(registry), Token);
        written.Should().ContainSingle().Which[0].Should().Be(0x10);

        options.FormatNegotiation.Mode = FormatNegotiationMode.Auto;
        FormatNegotiation.Create(services).Negotiator.Should().NotBeNull();
    }

    [Fact]
    public void A_reader_only_app_advertises_and_decides_for_a_policy_built_at_the_call_site_and_one_with_no_serializer_does_neither()
    {
        var reader = FormatNegotiation.Create(Build(new RedisCacheOptions(), new ValueSerializerRegistry([("fast", _fast, false)]), null));
        reader.IsActive.Should().BeTrue();
        reader.Negotiator.Should().NotBeNull("an ad-hoc policy may still name a registered serializer");
        reader.Advertisement!.ValueReads.Should().Equal("json", "10");

        FormatNegotiation.Create(Build(new RedisCacheOptions(), ValueSerializerRegistry.Empty, null)).IsActive.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_node_that_only_advertises_validates_its_options(int seconds)
    {
        var options = new RedisCacheOptions { FormatNegotiation = { Mode = FormatNegotiationMode.Force, AdvertiseInterval = TimeSpan.FromSeconds(seconds) } };

        FluentActions.Invoking(() => FormatNegotiation.Create(Build(options, new ValueSerializerRegistry([("fast", _fast, false)]), null)))
            .Should().Throw<InvalidOperationException>().WithMessage("*AdvertiseInterval*");
    }

    [Fact]
    public async Task A_policy_built_at_the_call_site_is_held_to_the_decision_of_the_serializer_it_names()
    {
        var other = new PassthroughValueSerializer(0x11);
        var negotiator = new FormatNegotiator(
            [new DesiredWrite(_fast, Binary, ["default"], []), new DesiredWrite(other, ["11"], [], ["default"])],
            new FormatNegotiationOptions { UpgradeHold = TimeSpan.Zero },
            TimeProvider.System);
        // Every node reads 11 and one node does not read 10.
        negotiator.Observe(new NodeListView([new("me", new("3.2.0", "i", ["json", "11"], new Dictionary<string, IReadOnlyList<string>>()), TimeSpan.Zero)], [], Complete: true));
        var registry = new ValueSerializerRegistry([("fast", _fast, false), ("other", other, false)]);
        var sut = SimpleCache(registry, negotiator);
        var written = CaptureSimpleWrites();

        await sut.SetAsync("k", "hello", TimeSpan.FromMinutes(1), new CachePolicy { Serializer = new SerializerPolicy { Write = "fast" } }, Token);
        await sut.SetAsync("k", "hello", TimeSpan.FromMinutes(1), new CachePolicy { Serializer = new SerializerPolicy { Write = "other" } }, Token);

        written[0][0].Should().NotBe(0x10, "no node on the node list is known to read 10");
        written[1][0].Should().Be(0x11, "the node list says every node reads 11");
    }

    [Fact]
    public void Reading_the_decision_through_the_resolver_allocates_nothing()
    {
        var negotiator = new FormatNegotiator([new DesiredWrite(_fast, Binary, ["default"], [])], new FormatNegotiationOptions { UpgradeHold = TimeSpan.Zero }, TimeProvider.System);
        negotiator.Observe(new NodeListView([new("me", new("3.2.0", "i", ["json", "10"], new Dictionary<string, IReadOnlyList<string>>()), TimeSpan.Zero)], [], Complete: true));
        var registry = new ValueSerializerRegistry([("fast", _fast, false)]);
        var resolver = new ValueFormatResolver(new SystemJsonByteSerializerProxy(), registry, new CachePolicy(), negotiator);
        var policy = ResolvedPolicy(registry);
        resolver.For(policy, forWrite: true).Writer.Should().BeSameAs(_fast);

        // Steady state: after a warm-up, so the JIT and tiering do not count, the best of a few passes allocates nothing.
        var best = long.MaxValue;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10_000; i++)
            {
                _ = resolver.For(policy, forWrite: true);
            }

            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        best.Should().Be(0);
    }

    [Fact]
    public void A_copy_of_the_options_does_not_share_the_negotiation_settings()
    {
        var original = new RedisCacheOptions();

        var copy = original.ShallowCopy();
        copy.FormatNegotiation.UpgradeHold = TimeSpan.Zero;
        copy.FormatNegotiation.Mode = FormatNegotiationMode.Force;

        original.FormatNegotiation.UpgradeHold.Should().Be(TimeSpan.FromHours(1));
        original.FormatNegotiation.Mode.Should().Be(FormatNegotiationMode.Auto);
    }

    [Fact]
    public void Every_registered_serializer_is_planned_whichever_policies_write_it()
    {
        var other = new PassthroughValueSerializer(0x11);
        var registry = new ValueSerializerRegistry([("fast", _fast, false), ("other", other, false)]);
        var policies = new DefaultCachePolicyFactory(
            new Dictionary<string, CachePolicy> { ["Orders"] = new() { Serializer = new SerializerPolicy { Write = "fast" } } },
            null,
            valueSerializers: registry);

        var desired = NegotiationPlan.Desired(registry, policies);

        desired.Should().HaveCount(2, "a policy built at the call site may name either");
        desired.Single(d => ReferenceEquals(d.Writer, _fast)).Policies.Should().Equal("orders");
        var unused = desired.Single(d => ReferenceEquals(d.Writer, other));
        unused.Policies.Should().BeEmpty();
        unused.Others.Should().Equal("orders");
        unused.Tokens.Should().Equal("11");
    }

    [Fact]
    public async Task A_policy_on_a_raw_byte_proxy_writes_its_own_format_whatever_the_gate_says_so_nothing_reads_back_differently()
    {
        var registry = new ValueSerializerRegistry([("fast", _fast, false)]);
        var sut = SimpleCache(registry, _gate, new RawByteSerializerProxy());
        var written = CaptureSimpleWrites();
        byte[] looksLikeAHeader = [0x02, 0xC4, 0x01, 0x41];
        var policy = ResolvedPolicy(registry);

        // Closed: a JSON fallback does not exist for raw bytes, so these would be stored as they are and read back as one byte.
        await sut.SetAsync("k", looksLikeAHeader, TimeSpan.FromMinutes(1), policy, Token);

        var stored = written.Should().ContainSingle().Subject;
        stored[0].Should().Be(0x10, "the policy's own format, which keeps the bytes whole behind a header");
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)stored);
        (await sut.GetAsync<byte[]>("k", policy, Token)).Should().Equal(looksLikeAHeader);
    }

    [Fact]
    public void A_node_that_does_not_sniff_advertises_json_only_for_the_policies_that_do_not()
    {
        var registry = new ValueSerializerRegistry([("fast", _fast, false)]);
        var policies = new DefaultCachePolicyFactory(
            new Dictionary<string, CachePolicy> { ["Orders"] = new() { Serializer = new SerializerPolicy { Write = "fast" } }, ["Plain"] = new() },
            null,
            valueSerializers: registry);

        var raw = NegotiationPlan.Advertisement(registry, policies, new ValueReader(new RawByteSerializerProxy(), registry), "3.2");
        var normal = NegotiationPlan.Advertisement(registry, policies, new ValueReader(new SystemJsonByteSerializerProxy(), registry), "3.2");

        raw.Strict.Should().NotContainKey("orders", "its own reads look for the header it writes");
        raw.Strict["plain"].Should().Equal("json", "nosniff");
        raw.Strict["default"].Should().Equal("json", "nosniff");
        normal.Strict.Should().BeEmpty("the default proxy reads a header whatever the policy writes");
    }

    [Fact]
    public void A_default_app_registers_nothing_to_negotiate()
    {
        var services = new ServiceCollection();
        var builder = new CachingBuilder(services);
        builder.Complete();

        services.Should().NotContain(d => d.ServiceType == typeof(FormatNegotiation));
        services.Should().NotContain(d => d.ImplementationType == typeof(FormatNegotiationService));
    }

    [Fact]
    public void An_app_with_a_serializer_and_no_json_only_policy_registers_the_service()
    {
        var services = new ServiceCollection();
        var builder = new CachingBuilder(services);
        builder.AddValueSerializer("fast", _ => _fast);
        builder.Complete();

        services.Should().Contain(d => d.ImplementationType == typeof(FormatNegotiationService));
    }

    private static byte[] Raw(HashEntry[] entries, string field) => (byte[])entries.Single(e => e.Name == field).Value!;

    private static async Task Operations(RedisCache cache, CachePolicy policy)
    {
        await cache.SetAsync("k", "hello", TimeSpan.FromMinutes(1), policy, TestContext.Current.CancellationToken);
        await cache.GetAsync<string>("k", policy, TestContext.Current.CancellationToken);
        await cache.GetOrAddAsync<string>("k", _ => Task.FromResult<string?>("hello"), TimeSpan.FromMinutes(1), policy, TestContext.Current.CancellationToken);
    }

    private ServiceProvider Build(RedisCacheOptions options, ValueSerializerRegistry registry, CachePolicy? defaultPolicy) =>
        new ServiceCollection()
            .AddSingleton(Options.Create(options))
            .AddSingleton(Options.Create(new CacheOptions { AppShortName = "t" }))
            .AddSingleton(Substitute.For<IRedisConnector>())
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton<ICachingTelemetryProvider>(NullTelemetryProvider.Instance)
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ISerializerProxy<byte[]>>(new SystemJsonByteSerializerProxy())
            .AddSingleton(registry)
            .AddSingleton<ICachePolicyFactory>(sp => new DefaultCachePolicyFactory(null, defaultPolicy, valueSerializers: registry))
            .BuildServiceProvider();

    private CachePolicy ResolvedPolicy(ValueSerializerRegistry registry) =>
        new DefaultCachePolicyFactory(null, new CachePolicy { Serializer = new SerializerPolicy { Write = "fast" } }, valueSerializers: registry).Default!;

    private List<byte[]> CaptureSimpleWrites()
    {
        var captured = new List<byte[]>();
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Do<RedisValue>(v => captured.Add(((byte[]?)v)!)), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);
        _transaction.StringSetAsync(Arg.Any<RedisKey>(), Arg.Do<RedisValue>(v => captured.Add(((byte[]?)v)!)), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        return captured;
    }

    private List<HashEntry[]> CaptureHashWrites()
    {
        var written = new List<HashEntry[]>();
        _transaction.HashSetAsync(Arg.Any<RedisKey>(), Arg.Do<HashEntry[]>(written.Add), Arg.Any<CommandFlags>()).Returns(Task.CompletedTask);
        _transaction.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<DateTime?>(), Arg.Any<CommandFlags>()).Returns(true);
        _database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        return written;
    }

    private IRedisConnector Connector()
    {
        var connector = Substitute.For<IRedisConnector>();
        connector.Database.Returns(_database);
        connector.Version.Returns(new Version(6, 0));
        connector.IsConnected.Returns(true);
        _database.Multiplexer.GetHashSlot(Arg.Any<RedisKey>()).Returns(0);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);
        _database.CreateTransaction(Arg.Any<object?>()).Returns(_transaction);
        return connector;
    }

    private (IResiliencePipelineProvider Pipelines, IRedisKeyStrategyFactory Strategies) Plumbing(string keyspace)
    {
        var pipelines = Substitute.For<IResiliencePipelineProvider>();
        pipelines.Get(ResiliencePipelineNames.Read).Returns(EmptyResiliencePipeline.Instance);
        pipelines.Get(ResiliencePipelineNames.Write).Returns(_write);
        var strategy = Substitute.For<IRedisKeyStrategy>();
        strategy.GetRedisKey(Arg.Any<CacheKey>()).Returns(call => (RedisKey)$"test:{keyspace}:{call.Arg<CacheKey>()}");
        var strategies = Substitute.For<IRedisKeyStrategyFactory>();
        strategies.Create(Arg.Any<CacheOptions>(), Arg.Any<Type>()).Returns(strategy);
        return (pipelines, strategies);
    }

    private RedisCache SimpleCache(out ValueSerializerRegistry registry, bool gate = true, IEffectiveFormat? negotiator = null)
    {
        registry = new ValueSerializerRegistry([("fast", _fast, false)]);
        return SimpleCache(registry, negotiator ?? (gate ? _gate : null));
    }

    private RedisCache SimpleCache(ValueSerializerRegistry registry, IEffectiveFormat? effective, ISerializerProxy<byte[]>? proxy = null)
    {
        var (pipelines, strategies) = Plumbing("str");
        return new RedisCache(
            Connector(),
            proxy ?? new SystemJsonByteSerializerProxy(),
            pipelines,
            new RecordingTelemetryProvider(),
            new RedisCacheOptions { RedisKeyStrategyFactory = strategies, ReadScriptsEnabled = false },
            new CacheOptions { AppShortName = "test", ConnectionMonitorEnabled = true },
            new DefaultCachePolicyFactory(null, null, valueSerializers: registry),
            TimeProvider.System,
            NullLogger<RedisCache>.Instance)
        {
            ValueSerializers = registry,
            EffectiveFormat = effective,
        };
    }

    private RedisHashCache HashCache(out ValueSerializerRegistry registry, CachePolicy? defaultPolicy = null)
    {
        registry = new ValueSerializerRegistry([("fast", _fast, false)]);
        var (pipelines, strategies) = Plumbing("hash");
        return new RedisHashCache(
            Connector(),
            new SystemJsonByteSerializerProxy(),
            pipelines,
            new RecordingTelemetryProvider(),
            new RedisCacheOptions { RedisKeyStrategyFactory = strategies, ReadScriptsEnabled = false },
            new CacheOptions { AppShortName = "test", ConnectionMonitorEnabled = true },
            new DefaultCachePolicyFactory(null, defaultPolicy, valueSerializers: registry),
            TimeProvider.System,
            NullLogger<RedisHashCache>.Instance)
        {
            ValueSerializers = registry,
            EffectiveFormat = _gate,
        };
    }

    private sealed class Gate : IEffectiveFormat
    {
        public bool Open { get; set; }

        public bool Allows(ICacheValueSerializer writer) => Open;
    }
}
