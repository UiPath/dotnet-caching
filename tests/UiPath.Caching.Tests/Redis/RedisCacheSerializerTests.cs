using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Policies;
using UiPath.Caching.Serialization;
using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Redis;

[Collection(WriterPoolCollection.Name)]
public class RedisCacheSerializerTests(ITestContextAccessor testContextAccessor)
{
    private const string UnreadableMetric = "cache.serializer.unreadable";

    private static readonly byte[] Unreadable = [0x1E, 1, 2];

    private readonly IDatabase _database = Substitute.For<IDatabase>();
    private readonly RecordingTelemetryProvider _telemetry = new();
    private readonly RecordingResiliencePipeline _write = new();
    private readonly PassthroughValueSerializer _fast = new(0x10);
    private readonly ITransaction _transaction = Substitute.For<ITransaction>();

    private CancellationToken Token => testContextAccessor.Current.CancellationToken;

    public static TheoryData<string> WritePaths() =>
        ["set", "set many", "try add", "get or add", "get or add with expiration", "get or add result", "get or add batch", "get or add batch with state"];

    public static TheoryData<string> Scenarios() => ["no registry", "registry, json write", "registry, fast write"];

    [Fact]
    public async Task Policy_serializer_writes_a_headered_value()
    {
        var sut = Create(out var registry);
        var captured = CaptureWrites();

        await sut.SetAsync("k", "hello", TimeSpan.FromMinutes(1), ResolvedPolicy(registry, "fast"), Token);

        captured.Should().ContainSingle().Which.Should().Equal(Header(0x10, "hello"));
        _fast.Serialized.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(WritePaths))]
    public async Task Every_write_path_writes_the_policy_serializer(string path)
    {
        var sut = Create(out var registry);
        var captured = CaptureWrites();

        await Write(sut, path, ResolvedPolicy(registry, "fast"));

        captured.Should().NotBeEmpty();
        captured.Should().OnlyContain(value => value[0] == 0x10);
    }

    [Fact]
    public async Task Default_policy_writes_byte_identical_json()
    {
        var sut = Create(out _);
        var captured = CaptureWrites();

        await sut.SetAsync("k", "hello", TimeSpan.FromMinutes(1), policy: null, Token);
        await sut.SetAsync("k", "hello", TimeSpan.FromMinutes(1), new CachePolicy(), Token);
        await sut.SetAsync("k", "hello", TimeSpan.FromMinutes(1), new CachePolicy { Serializer = new SerializerPolicy { Write = "json" } }, Token);

        var json = new SystemJsonByteSerializerProxy().Serialize("hello");
        captured.Should().HaveCount(3);
        captured.Should().OnlyContain(value => value.SequenceEqual(json!));
        _fast.Serialized.Should().Be(0);
    }

    [Fact]
    public async Task A_default_policy_that_names_a_serializer_applies_when_the_call_has_no_policy()
    {
        var sut = Create(FastRegistry(), defaultPolicy: new CachePolicy { Serializer = new SerializerPolicy { Write = "fast" } });
        var captured = CaptureWrites();

        await sut.SetAsync("k", "hello", TimeSpan.FromMinutes(1), policy: null, Token);

        captured.Should().ContainSingle().Which.Should().Equal(Header(0x10, "hello"));
    }

    [Fact]
    public async Task Headered_value_reads_under_a_default_policy()
    {
        var sut = Create(out _);
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)Header(0x10, "stored"));

        var value = await sut.GetAsync<string>("k", policy: null, Token);

        value.Should().Be("stored");
        _fast.LastFormatId.Should().Be(0x10);
    }

    [Fact]
    public async Task Json_still_reads_under_a_policy_that_names_a_serializer()
    {
        var sut = Create(out var registry);
        var proxy = new SystemJsonByteSerializerProxy();
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)proxy.Serialize("json value"));

        var value = await sut.GetAsync<string>("k", ResolvedPolicy(registry, "fast"), Token);

        value.Should().Be("json value");
        _fast.Deserialized.Should().Be(0);
    }

    [Fact]
    public async Task Unknown_format_is_a_miss_and_the_key_is_removed_if_it_still_holds_the_bytes_read()
    {
        var sut = Create(out _);
        var conditions = CaptureConditions();
        byte[] stored = [0x1E, (byte)'a', (byte)'b'];
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)stored);

        var value = await sut.GetAsync<string>("k", policy: null, Token);

        value.Should().BeNull();
        _ = _transaction.Received(1).KeyDeleteAsync((RedisKey)"test:str:k", CommandFlags.DemandMaster);
        _ = _database.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        conditions.Should().ContainSingle().Which.Should().Be(Condition.StringEqual("test:str:k", stored).ToString());
    }

    [Fact]
    public async Task A_key_that_changed_after_the_read_is_not_deleted_and_the_call_is_still_a_miss()
    {
        var sut = Create(out _);
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)new byte[] { 0x1E, 1, 2 });
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(false);

        var value = await sut.GetAsync<string>("k", policy: null, Token);

        value.Should().BeNull();
        _ = _database.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        _telemetry.Metrics.Should().ContainSingle(m => m.Name == UnreadableMetric);
    }

    [Fact]
    public async Task A_verbatim_key_is_read_under_its_exact_name()
    {
        var sut = Create(out _);
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);

        await sut.GetAsync<string>(CacheKey.Verbatim(" Mixed:Key "), policy: null, Token);

        _ = _database.Received(1).StringGetAsync((RedisKey)"test:str: Mixed:Key ", Arg.Any<CommandFlags>());
    }

    [Fact]
    public void Unreadable_values_are_removed_by_default() =>
        new RedisCacheOptions().RemoveUnreadable.Should().BeTrue();

    [Fact]
    public async Task With_RemoveUnreadable_off_an_unknown_format_is_a_counted_miss_and_the_key_stays()
    {
        var sut = Create(out _, removeUnreadable: false, keyReadTelemetry: true);
        byte[] stored = [0x1E, (byte)'a'];
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)stored);
        _database.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns([(RedisValue)stored, (RedisValue)new SystemJsonByteSerializerProxy().Serialize("ok")!]);
        _database.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(TimeSpan.FromMinutes(1));

        var single = await MetricNamesOf(async () => (await sut.GetAsync<string>("k", policy: null, Token)).Should().BeNull());
        var entry = await MetricNamesOf(async () => (await sut.GetCacheEntryAsync<string>("k", policy: null, Token)).Found.Should().BeFalse());
        var batch = await MetricNamesOf(async () => (await sut.GetCacheEntriesAsync<string>(["a", "b"], policy: null, Token)).Select(e => e.Value.Found).Should().Equal(false, true));

        single.Should().Contain(n => n.StartsWith("Caching.Stats.Misses.Redis.GetAsync", StringComparison.Ordinal)).And.NotContain(n => n.StartsWith("Caching.Stats.Hits", StringComparison.Ordinal));
        entry.Should().Contain(n => n.StartsWith("Caching.Stats.Misses.Redis.GetCacheEntry", StringComparison.Ordinal)).And.NotContain(n => n.StartsWith("Caching.Stats.Hits", StringComparison.Ordinal));
        batch.Should().Contain(n => n.StartsWith("Caching.Stats.Hits.Redis.GetCacheEntries", StringComparison.Ordinal));
        var reads = _telemetry.Dependencies.Where(d => d.Name.StartsWith("GetCacheEntriesInternalAsync", StringComparison.Ordinal)).ToDictionary(d => d.Data, d => d.ResultCode);
        reads.Should().Contain("test:str:a", "Miss").And.Contain("test:str:b", "Hit");
        _telemetry.Metrics.Should().NotContain(m => m.Name.Contains("RemoveAsync", StringComparison.Ordinal));
        _ = _database.DidNotReceive().CreateTransaction(Arg.Any<object?>());
        AssertNothingDeleted();
        var unreadable = _telemetry.Metrics.Where(m => m.Name == UnreadableMetric).ToList();
        unreadable.Should().HaveCount(3);
        unreadable.Should().OnlyContain(m => m.Properties!["format"] == "0x1E");
    }

    [Fact]
    public async Task With_RemoveUnreadable_off_and_no_registered_serializer_the_default_proxy_still_counts_an_unknown_format()
    {
        var sut = Create(ValueSerializerRegistry.Empty, removeUnreadable: false);
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)new byte[] { 0x1E, (byte)'a' });

        (await sut.GetAsync<string>("k", policy: null, Token)).Should().BeNull();

        _telemetry.Metrics.Should().ContainSingle(m => m.Name == UnreadableMetric);
        AssertNothingDeleted();
    }

    [Fact]
    public async Task With_RemoveUnreadable_off_and_no_registered_serializer_valid_headerless_json_is_a_hit()
    {
        var sut = Create(ValueSerializerRegistry.Empty, removeUnreadable: false);
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)new SystemJsonByteSerializerProxy().Serialize("ok")!);

        (await sut.GetAsync<string>("k", policy: null, Token)).Should().Be("ok");

        _telemetry.Metrics.Should().NotContain(m => m.Name == UnreadableMetric);
        AssertNothingDeleted();
    }

    [Fact]
    public async Task With_RemoveUnreadable_off_and_no_registered_serializer_a_custom_proxy_is_not_header_classified()
    {
        var sut = Create(ValueSerializerRegistry.Empty, new ByteProxy(), removeUnreadable: false);
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)Unreadable);

        (await sut.GetAsync<byte[]>("k", policy: null, Token)).Should().Equal(Unreadable);

        _telemetry.Metrics.Should().NotContain(m => m.Name == UnreadableMetric);
        AssertNothingDeleted();
    }

    [Fact]
    public async Task With_RemoveUnreadable_on_and_no_registered_serializer_nothing_is_classified_or_removed()
    {
        var sut = Create(ValueSerializerRegistry.Empty);
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)new byte[] { 0x1E, (byte)'a' });

        (await sut.GetAsync<string>("k", policy: null, Token)).Should().BeNull();

        _telemetry.Metrics.Should().NotContain(m => m.Name == UnreadableMetric);
        AssertNothingDeleted();
    }

    [Fact]
    public async Task With_RemoveUnreadable_off_a_get_or_add_runs_the_generator_and_the_write_replaces_the_key()
    {
        var sut = Create(out _, removeUnreadable: false);
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)new byte[] { 0x1E, 1, 2 });
        var (written, whens) = CaptureWritesWithConditions();
        var generated = 0;

        var value = await sut.GetOrAddAsync<string>("k", _ => Task.FromResult<string?>("fresh" + ++generated), TimeSpan.FromMinutes(1), policy: null, Token);

        value.Should().Be("fresh1");
        generated.Should().Be(1);
        written.Should().NotBeEmpty().And.OnlyContain(w => !w.SequenceEqual(Unreadable));
        whens.Should().NotBeEmpty().And.OnlyContain(w => w == When.Always);
        AssertNothingDeleted();
        _telemetry.Metrics.Should().ContainSingle(m => m.Name == UnreadableMetric);
    }

    [Fact]
    public async Task With_RemoveUnreadable_off_a_batch_get_or_add_runs_the_generator_and_the_write_replaces_the_key()
    {
        var sut = Create(out _, removeUnreadable: false);
        _database.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns([(RedisValue)Unreadable]);
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)Unreadable);
        var (written, whens) = CaptureWritesWithConditions();

        var results = await sut.GetOrAddAsync<string, string>([new KeyValuePair<CacheKey, string>("a", "a")], static (states, _) => Task.FromResult(states.Select(s => new KeyValuePair<string, string?>(s, "fresh")).ToArray()), TimeSpan.FromMinutes(1), policy: null, Token);

        results[0].Value.Should().Be("fresh");
        written.Should().NotBeEmpty().And.OnlyContain(w => !w.SequenceEqual(Unreadable));
        whens.Should().NotBeEmpty().And.OnlyContain(w => w == When.Always);
        AssertNothingDeleted();
    }

    [Fact]
    public async Task The_conditional_removal_is_reported_as_a_remove()
    {
        var sut = Create(out _);
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)new byte[] { 0x1E, 1, 2 });

        await sut.GetAsync<string>("k", policy: null, Token);

        _telemetry.Metrics.Should().Contain(m => m.Name == "Caching.Stats.Hits.Redis.RemoveAsync.String");
        var metric = _telemetry.Metrics.Should().ContainSingle(m => m.Name == UnreadableMetric).Subject;
        metric.Value.Should().Be(1);
        metric.Properties.Should().Contain("format", "0x1E").And.Contain("cache", "Redis");
    }

    [Fact]
    public async Task An_unreadable_key_that_cannot_be_removed_is_still_a_miss()
    {
        var sut = Create(out _);
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)new byte[] { 0x1E, 1, 2 });
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(Task.FromException<bool>(new RedisException("down")));

        var value = await sut.GetAsync<string>("k", policy: null, Token);

        value.Should().BeNull();
    }

    [Fact]
    public async Task An_unreadable_key_is_removed_before_the_generator_runs()
    {
        var sut = Create(out _);
        var order = new List<string>();
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)new byte[] { 0x1E, 1, 2 });
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(_ =>
        {
            order.Add("delete");
            return true;
        });
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);

        Task<string?> Generate(CancellationToken token)
        {
            order.Add("generate");
            return Task.FromResult<string?>("fresh");
        }

        var value = await sut.GetOrAddAsync<string>("k", Generate, TimeSpan.FromMinutes(1), policy: null, Token);

        value.Should().Be("fresh");
        order.Should().Equal("delete", "generate");
    }

    [Fact]
    public async Task A_headered_value_whose_serializer_throws_is_a_miss_and_the_key_stays()
    {
        var sut = Create(new ValueSerializerRegistry([RegistryEntry.Of("bad", new ThrowingValueSerializer())]));
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)new byte[] { 0x10, 1, 2 });

        var value = await sut.GetAsync<string>("k", policy: null, Token);

        value.Should().BeNull();
        AssertNothingDeleted();
        _telemetry.Metrics.Should().NotContain(m => m.Name == UnreadableMetric);
    }

    [Fact]
    public async Task An_out_of_memory_from_a_serializer_propagates_from_every_read_path_and_runs_no_generator()
    {
        var sut = Create(new ValueSerializerRegistry([RegistryEntry.Of("bad", new ThrowingValueSerializer { Failure = new OutOfMemoryException() })]));
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)new byte[] { 0x10, 1, 2 });
        _database.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns([(RedisValue)new byte[] { 0x10, 1, 2 }, RedisValue.Null]);
        var generated = 0;
        Task<string?> Generate(CancellationToken token)
        {
            generated++;
            return Task.FromResult<string?>("fresh");
        }

        await FluentActions.Awaiting(async () => await sut.GetAsync<string>("k", policy: null, Token)).Should().ThrowAsync<OutOfMemoryException>();
        await FluentActions.Awaiting(async () => await sut.GetAsync<string>(["a", "b"], policy: null, Token)).Should().ThrowAsync<OutOfMemoryException>();
        await FluentActions.Awaiting(async () => await sut.GetCacheEntryAsync<string>("k", policy: null, Token)).Should().ThrowAsync<OutOfMemoryException>();
        await FluentActions.Awaiting(async () => await sut.GetCacheEntriesAsync<string>(["a", "b"], policy: null, Token)).Should().ThrowAsync<OutOfMemoryException>();
        await FluentActions.Awaiting(async () => await sut.GetOrAddAsync<string>("k", Generate, TimeSpan.FromMinutes(1), policy: null, Token)).Should().ThrowAsync<OutOfMemoryException>();
        await FluentActions.Awaiting(async () => await sut.GetOrAddResultAsync<string>("k", Generate, TimeSpan.FromMinutes(1), policy: null, Token)).Should().ThrowAsync<OutOfMemoryException>();

        generated.Should().Be(0);
    }

    [Fact]
    public async Task A_headered_value_whose_serializer_throws_is_a_miss_that_keeps_the_key_on_every_read_path()
    {
        var sut = Create(new ValueSerializerRegistry([RegistryEntry.Of("bad", new ThrowingValueSerializer())]));
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)new byte[] { 0x10, 1, 2 });
        _database.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns([(RedisValue)new byte[] { 0x10, 1, 2 }, RedisValue.Null]);
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);

        (await sut.GetAsync<string>(["a", "b"], policy: null, Token)).Select(v => v.Value).Should().Equal(null, null);
        (await sut.GetCacheEntryAsync<string>("k", policy: null, Token)).Found.Should().BeFalse();
        (await sut.GetCacheEntriesAsync<string>(["a", "b"], policy: null, Token)).Select(v => v.Value.Found).Should().Equal(false, false);
        Task<string?> Generate(CancellationToken token) => Task.FromResult<string?>("fresh");
        (await sut.GetOrAddAsync<string>("k", Generate, TimeSpan.FromMinutes(1), policy: null, Token)).Should().Be("fresh");

        AssertNothingDeleted();
        _telemetry.Metrics.Should().NotContain(m => m.Name == UnreadableMetric);
    }

    [Fact]
    public async Task An_unknown_format_on_the_entry_reads_is_a_miss_and_removed_if_unchanged()
    {
        var sut = Create(out _);
        var conditions = CaptureConditions();
        byte[] stored = [0x1E, (byte)'a'];
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)stored);
        _database.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns([(RedisValue)stored, (RedisValue)new SystemJsonByteSerializerProxy().Serialize("ok")!]);
        _database.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(TimeSpan.FromMinutes(1));

        (await sut.GetCacheEntryAsync<string>("k", policy: null, Token)).Found.Should().BeFalse();
        var entries = await sut.GetCacheEntriesAsync<string>(["a", "b"], policy: null, Token);

        entries.Select(e => e.Value.Found).Should().Equal(false, true);
        conditions.Should().HaveCount(2).And.OnlyContain(c => c == Condition.StringEqual("test:str:k", stored).ToString() || c == Condition.StringEqual("test:str:a", stored).ToString());
        _ = _transaction.Received(1).KeyDeleteAsync((RedisKey)"test:str:k", CommandFlags.DemandMaster);
        _ = _transaction.Received(1).KeyDeleteAsync((RedisKey)"test:str:a", CommandFlags.DemandMaster);
        _ = _database.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        _telemetry.Metrics.Count(m => m.Name == UnreadableMetric).Should().Be(2);
    }

    [Fact]
    public async Task Json_that_does_not_parse_is_a_miss_and_the_key_stays()
    {
        var sut = Create(out _);
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)Encoding.UTF8.GetBytes("{not json"));

        var value = await sut.GetAsync<string>("k", policy: null, Token);

        value.Should().BeNull();
        await _database.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        _telemetry.Metrics.Should().NotContain(m => m.Name == UnreadableMetric);
    }

    [Fact]
    public async Task A_value_round_trips_through_the_serializer_the_policy_names()
    {
        var sut = Create(out var registry);
        var captured = CaptureWrites();
        var policy = ResolvedPolicy(registry, "fast");

        await sut.SetAsync("k", "round trip", TimeSpan.FromMinutes(1), policy, Token);
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)captured.Single());
        var read = await sut.GetAsync<string>("k", policy, Token);

        read.Should().Be("round trip");
        _fast.Serialized.Should().Be(1);
        _fast.Deserialized.Should().Be(1);
        _fast.LastFormatId.Should().Be(0x10);
    }

    [Fact]
    public async Task A_value_round_trips_through_a_two_byte_format_id()
    {
        var extended = new PassthroughValueSerializer(0x105);
        var registry = new ValueSerializerRegistry([RegistryEntry.Of("extended", extended)]);
        var sut = Create(registry);
        var captured = CaptureWrites();
        var policy = ResolvedPolicy(registry, "extended");

        await sut.SetAsync("k", "round trip", TimeSpan.FromMinutes(1), policy, Token);
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)captured.Single());
        var read = await sut.GetAsync<string>("k", policy: null, Token);

        captured.Single().Take(2).Should().Equal(0x1F, 0x05);
        read.Should().Be("round trip");
        extended.LastFormatId.Should().Be(0x105);
    }

    [Fact]
    public async Task A_binary_payload_that_is_not_utf8_round_trips()
    {
        var sut = Create(out var registry);
        var captured = CaptureWrites();
        var policy = ResolvedPolicy(registry, "fast");
        byte[] payload = [0xFF, 0xFE, 0x00, 0x80, 0xC3, 0x28];

        await sut.SetAsync("k", payload, TimeSpan.FromMinutes(1), policy, Token);
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)captured.Single());
        var read = await sut.GetAsync<byte[]>("k", policy: null, Token);

        captured.Single().Should().Equal([0x10, .. payload]);
        read.Should().Equal(payload);
    }

    [Fact]
    public async Task Raw_proxy_never_sniffs()
    {
        var messagePack = new PassthroughValueSerializer(0x02);
        var sut = Create(new ValueSerializerRegistry([RegistryEntry.Of("messagepack", messagePack)]), new RawByteSerializerProxy());
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)new byte[] { 0x02, 1, 2 });

        var value = await sut.GetAsync<byte[]>("k", policy: null, Token);

        value.Should().Equal(0x02, 1, 2);
        messagePack.Deserialized.Should().Be(0);
        await _database.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Raw_proxy_bytes_with_an_unregistered_header_byte_are_kept()
    {
        var sut = Create(new ValueSerializerRegistry([RegistryEntry.Of("messagepack", new PassthroughValueSerializer(0x02))]), new RawByteSerializerProxy());
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)new byte[] { 0x1B, 1, 2 });

        var value = await sut.GetAsync<byte[]>("k", policy: null, Token);

        value.Should().Equal(0x1B, 1, 2);
        await _database.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Custom_proxy_never_sniffs_without_a_policy_serializer()
    {
        var sut = Create(FastRegistry(), new ByteProxy());
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)Header(0x10, "x"));

        var value = await sut.GetAsync<byte[]>("k", policy: null, Token);

        value.Should().Equal(Header(0x10, "x"));
        _fast.Deserialized.Should().Be(0);
    }

    [Fact]
    public async Task Custom_proxy_sniffs_when_the_policy_names_a_serializer()
    {
        var registry = FastRegistry();
        var sut = Create(registry, new ByteProxy());
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)Header(0x10, "x"));

        var value = await sut.GetAsync<string>("k", ResolvedPolicy(registry, "fast"), Token);

        value.Should().Be("x");
    }

    [Fact]
    public async Task Batch_reads_mixed_formats_per_key()
    {
        var sut = Create(out _);
        var proxy = new SystemJsonByteSerializerProxy();
        RedisValue[] stored = [proxy.Serialize("json")!, Header(0x10, "headered"), RedisValue.Null];
        _database.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns(stored);

        var values = await sut.GetAsync<string>(["a", "b", "c"], policy: null, Token);

        values.Select(v => v.Value).Should().Equal("json", "headered", null);
    }

    [Fact]
    public async Task Batch_read_removes_only_the_unreadable_keys()
    {
        var sut = Create(out _);
        var proxy = new SystemJsonByteSerializerProxy();
        RedisValue[] stored = [proxy.Serialize("json")!, new byte[] { 0x1E, 1 }, Header(0x10, "headered")];
        _database.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns(stored);

        var values = await sut.GetAsync<string>(["a", "b", "c"], policy: null, Token);

        values.Select(v => v.Value).Should().Equal("json", null, "headered");
        _ = _transaction.Received(1).KeyDeleteAsync(Arg.Any<RedisKey>(), CommandFlags.DemandMaster);
        _ = _transaction.Received(1).KeyDeleteAsync((RedisKey)"test:str:b", CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task An_out_of_memory_from_headerless_json_is_a_miss_whatever_is_registered()
    {
        var sut = Create(new ValueSerializerRegistry([RegistryEntry.Of("fast", _fast)]), new OutOfMemoryProxy(), new CachePolicy { Serializer = new SerializerPolicy { Write = "fast" } });
        var json = new SystemJsonByteSerializerProxy().Serialize("a")!;
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)json);
        _database.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns([(RedisValue)json, RedisValue.Null]);

        (await sut.GetAsync<string>("k", policy: null, Token)).Should().BeNull();
        (await sut.GetAsync<string>(["a", "b"], policy: null, Token)).Select(v => v.Value).Should().Equal(null, null);
        (await sut.GetCacheEntryAsync<string>("k", policy: null, Token)).Found.Should().BeFalse();
        (await sut.GetCacheEntriesAsync<string>(["a", "b"], policy: null, Token)).Select(e => e.Value.Found).Should().Equal(false, false);
    }

    [Fact]
    public async Task A_batch_read_cancelled_after_the_reply_returns_misses_for_the_unreadable_keys()
    {
        var sut = Create(out _);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        _database.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns(_ =>
        {
            cts.Cancel();
            return new RedisValue[] { new byte[] { 0x1E, 1 }, Header(0x10, "headered") };
        });

        var values = await sut.GetAsync<string>(["a", "b"], policy: null, cts.Token);

        values.Select(v => v.Value).Should().Equal(null, "headered");
    }

    [Fact]
    public async Task A_batch_entries_read_cancelled_after_the_reply_returns_misses_for_the_unreadable_keys()
    {
        var sut = Create(out _);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        _database.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns(_ =>
        {
            cts.Cancel();
            return new RedisValue[] { new byte[] { 0x1E, 1 }, Header(0x10, "headered") };
        });

        var entries = await sut.GetCacheEntriesAsync<string>(["a", "b"], policy: null, cts.Token);

        entries.Select(e => e.Value.Found).Should().Equal(false, true);
    }

    [Fact]
    public async Task Cached_null_stays_a_cached_null()
    {
        var sut = Create(out _, cacheNullValues: true);
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.EmptyString);
        var generated = false;

        Task<string?> Generate(CancellationToken token)
        {
            generated = true;
            return Task.FromResult<string?>("x");
        }

        var value = await sut.GetOrAddAsync<string>("k", Generate, TimeSpan.FromMinutes(1), policy: null, Token);

        value.Should().BeNull();
        generated.Should().BeFalse();
        _telemetry.Metrics.Should().NotContain(m => m.Name == UnreadableMetric);
    }

    [Fact]
    public async Task Unknown_name_on_an_ad_hoc_policy_throws_at_call_time()
    {
        var sut = Create(out _);
        var policy = new CachePolicy { Serializer = new SerializerPolicy { Write = "nope" } };

        var act = async () => await sut.SetAsync("k", "hello", TimeSpan.FromMinutes(1), policy, Token);

        (await act.Should().ThrowAsync<OptionsValidationException>()).Which.Message.Should().Contain("nope");
        await _database.DidNotReceiveWithAnyArgs().StringSetAsync(default, default, default, default, default);
    }

    [Theory]
    [MemberData(nameof(WritePaths))]
    public async Task Unknown_name_on_an_ad_hoc_policy_throws_on_every_write_path(string path)
    {
        var sut = Create(out _);
        var policy = new CachePolicy { Serializer = new SerializerPolicy { Write = "nope" } };

        var act = async () => await Write(sut, path, policy);

        await act.Should().ThrowAsync<OptionsValidationException>();
        await _database.DidNotReceiveWithAnyArgs().StringSetAsync(default, default, default, default, default);
        _database.DidNotReceiveWithAnyArgs().CreateTransaction(default);
    }

    [Theory]
    [MemberData(nameof(WritePaths))]
    public async Task A_policy_that_names_a_registered_serializer_without_a_resolved_slot_still_writes_it(string path)
    {
        var sut = Create(out _);
        var captured = CaptureWrites();
        var adHoc = new CachePolicy { Serializer = new SerializerPolicy { Write = "fast" } };
        CachePolicyMerger.WriteSerializerOf(adHoc).Should().BeNull();

        await Write(sut, path, adHoc);

        captured.Should().NotBeEmpty();
        captured.Should().OnlyContain(value => value[0] == 0x10);
    }

    [Fact]
    public async Task A_merged_policy_with_an_empty_slot_still_writes_the_named_serializer()
    {
        var sut = Create(out _);
        var captured = CaptureWrites();
        var merged = CachePolicyMerger.Merge(
            new CachePolicy { DistributedExpiration = TimeSpan.FromMinutes(1) },
            new CachePolicy { Serializer = new SerializerPolicy { Write = "fast" } });

        await sut.SetAsync("k", "hello", merged, Token);

        captured.Should().ContainSingle().Which.Should().Equal(Header(0x10, "hello"));
    }

    [Fact]
    public async Task Large_value_round_trips()
    {
        var sut = Create(out var registry);
        var captured = CaptureWrites();
        var large = new string('x', 200 * 1024);

        await sut.SetAsync("k", large, TimeSpan.FromMinutes(1), ResolvedPolicy(registry, "fast"), Token);
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)captured.Single());
        var read = await sut.GetAsync<string>("k", policy: null, Token);

        read.Should().Be(large);
    }

    [Fact]
    public async Task A_headered_write_goes_through_the_write_pipeline()
    {
        var sut = Create(out var registry);
        CaptureWrites();

        await sut.SetAsync("k", "hello", TimeSpan.FromMinutes(1), ResolvedPolicy(registry, "fast"), Token);

        _write.ResultTypes.Should().Contain(typeof(bool));
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Every_operation_reports_the_method_name_it_reported_before_serializers(string scenario)
    {
        var registry = scenario == "no registry" ? ValueSerializerRegistry.Empty : FastRegistry();
        var sut = Create(registry);
        var policy = scenario.EndsWith("fast write", StringComparison.Ordinal) ? ResolvedPolicy(registry, "fast") : null;
        CaptureWrites();
        _database.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns([RedisValue.Null, RedisValue.Null]);
        var minute = TimeSpan.FromMinutes(1);
        Task<string?> Generate(CancellationToken token) => Task.FromResult<string?>("hello");
        Task<GeneratedValue<string>> GenerateExpiring(CancellationToken token) => Task.FromResult(new GeneratedValue<string>("hello"));
        Task<KeyValuePair<string, string?>[]> GenerateMany(string[] states, CancellationToken token) =>
            Task.FromResult(states.Select(s => new KeyValuePair<string, string?>(s, "hello")).ToArray());

        (await MethodsOf(async () => await sut.GetAsync<string>("k", policy, Token))).Should().Equal("GetAsync");
        (await MethodsOf(async () => await sut.GetAsync<string>(["a", "b"], policy, Token))).Should().Equal("GetAsyncInternal");
        (await MethodsOf(async () => await sut.GetCacheEntryAsync<string>("k", policy, Token))).Should().Equal("GetCacheEntryInternalAsync");
        (await MethodsOf(async () => await sut.GetCacheEntriesAsync<string>(["a", "b"], policy, Token))).Should().Equal("GetCacheEntriesInternalAsync");
        (await MethodsOf(async () => await sut.SetAsync("k", "hello", minute, policy, Token))).Should().Equal("SetAsync");
        (await MethodsOf(async () => await sut.SetAsync([new KeyValuePair<CacheKey, string?>("a", "x")], minute, policy, Token))).Should().Equal("SetAsync");
        (await MethodsOf(async () => await sut.TryAddAsync("k", "hello", minute, policy, Token))).Should().Equal("TryAddAsync");
        (await MethodsOf(async () => await sut.GetOrAddAsync<string>("k", Generate, minute, policy, Token))).Should().Equal("GetOrAddAsync", "SetAsync");
        (await MethodsOf(async () => await sut.GetOrAddWithExpirationAsync<string>("k", GenerateExpiring, policy, Token))).Should().Equal("GetOrAddAsync", "SetAsync");
        (await MethodsOf(async () => await sut.GetOrAddResultAsync<string>("k", Generate, minute, policy, Token))).Should().Equal("GetOrAddResultAsync", "SetAsync");
        (await MethodsOf(async () => await sut.GetOrAddAsync<string, string>([new KeyValuePair<CacheKey, string>("a", "a")], GenerateMany, minute, policy, Token)))
            .Should().Equal("GetCacheEntriesInternalAsync", "SetAsync");
        (await MethodsOf(async () => await sut.RemoveAsync<string>("k", Token))).Should().Equal("RemoveAsync");
    }

    [Fact]
    public async Task A_pooled_buffer_is_not_returned_while_its_write_is_in_flight()
    {
        using var pool = new RecordingPool();
        var sut = Create(out var registry);
        var inFlight = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(_ => inFlight.Task);

        var write = sut.SetAsync("k", new string('x', 100 * 1024), TimeSpan.FromMinutes(1), ResolvedPolicy(registry, "fast"), Token).AsTask();
        var payload = pool.RentedByThisThread.MaxBy(array => array.Length)!;

        pool.Returned.Should().NotContain(array => ReferenceEquals(array, payload));

        inFlight.SetResult(true);
        await write;

        pool.Returned.Should().Contain(array => ReferenceEquals(array, payload));
    }

    [Fact]
    public async Task A_snapshot_of_a_value_is_found_with_the_lifetime_of_the_same_transaction()
    {
        var sut = Create(out _);
        StubSnapshot(new SystemJsonByteSerializerProxy().Serialize("v"), TimeSpan.FromMinutes(3));

        var read = await sut.ReadSnapshotAsync<string>("k", null, Token);

        read.Outcome.Should().Be(SnapshotOutcome.Found);
        read.Entry!.Value.Should().Be("v");
        read.Entry.Expiration.Should().BeCloseTo(DateTimeOffset.UtcNow + TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_snapshot_of_a_missing_key_is_absent()
    {
        var sut = Create(out _);
        StubSnapshot(RedisValue.Null, null);

        (await sut.ReadSnapshotAsync<string>("k", null, Token)).Should().Be(new SnapshotRead<string>(SnapshotOutcome.Absent, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_snapshot_of_an_unknown_format_is_unreadable_and_the_key_stays_whatever_RemoveUnreadable_says(bool removeUnreadable)
    {
        var sut = Create(out _, removeUnreadable: removeUnreadable);
        StubSnapshot(new byte[] { 0x1E, 1, 2 }, TimeSpan.FromMinutes(3));

        var read = await sut.ReadSnapshotAsync<string>("k", null, Token);

        read.Should().Be(new SnapshotRead<string>(SnapshotOutcome.Unreadable, null));
        AssertNothingDeleted();
        _ = _transaction.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task A_snapshot_of_a_value_its_serializer_cannot_read_is_unreadable()
    {
        var sut = Create(new ValueSerializerRegistry([RegistryEntry.Of("bad", new ThrowingValueSerializer())]));
        StubSnapshot(new byte[] { 0x10, 1, 2 }, TimeSpan.FromMinutes(3));

        (await sut.ReadSnapshotAsync<string>("k", null, Token)).Outcome.Should().Be(SnapshotOutcome.Unreadable);
    }

    [Fact]
    public async Task A_snapshot_of_bytes_the_default_serializer_cannot_parse_is_unreadable()
    {
        var sut = Create(ValueSerializerRegistry.Empty, removeUnreadable: false);
        StubSnapshot(Encoding.UTF8.GetBytes("{not json"), TimeSpan.FromMinutes(3));

        (await sut.ReadSnapshotAsync<string>("k", null, Token)).Outcome.Should().Be(SnapshotOutcome.Unreadable);
    }

    [Fact]
    public async Task A_snapshot_the_store_cannot_answer_is_failed_and_does_not_throw()
    {
        var sut = Create(out _);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(Task.FromException<bool>(new RedisException("lost")));

        (await sut.ReadSnapshotAsync<string>("k", null, Token)).Should().Be(new SnapshotRead<string>(SnapshotOutcome.Failed, null));
    }

    [Fact]
    public async Task A_snapshot_of_a_cache_that_is_not_connected_is_failed()
    {
        var sut = Create(FastRegistry(), connected: false);

        (await sut.ReadSnapshotAsync<string>("k", null, Token)).Outcome.Should().Be(SnapshotOutcome.Failed);
        _ = _database.DidNotReceive().CreateTransaction(Arg.Any<object?>());
    }

    [Fact]
    public async Task A_snapshot_with_a_cancelled_token_throws()
    {
        var sut = Create(out _);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await FluentActions.Awaiting(async () => await sut.ReadSnapshotAsync<string>("k", null, cts.Token)).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task An_acknowledged_write_and_delete_are_committed()
    {
        var sut = Create(out _);
        StubStore(set: true, delete: true);

        (await sut.WriteAsync("k", "v", DateTimeOffset.UtcNow.AddMinutes(1), null, Token)).Should().Be(StoreOutcome.Committed);
        (await sut.DeleteAsync<string>("k", Token)).Should().Be(StoreOutcome.Committed);
    }

    [Fact]
    public async Task A_write_and_delete_cancelled_before_sending_are_not_sent()
    {
        var sut = Create(out _);
        StubStore(set: true, delete: true);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        (await sut.WriteAsync("k", "v", DateTimeOffset.UtcNow.AddMinutes(1), null, cts.Token)).Should().Be(StoreOutcome.NotSent);
        (await sut.DeleteAsync<string>("k", cts.Token)).Should().Be(StoreOutcome.NotSent);
        _ = _database.DidNotReceiveWithAnyArgs().StringSetAsync(default, default, default, default, default);
        _ = _database.DidNotReceiveWithAnyArgs().KeyDeleteAsync(default(RedisKey), default);
    }

    [Fact]
    public async Task A_write_and_delete_on_a_cache_that_is_not_connected_are_not_sent()
    {
        var sut = Create(FastRegistry(), connected: false);
        StubStore(set: true, delete: true);

        (await sut.WriteAsync("k", "v", DateTimeOffset.UtcNow.AddMinutes(1), null, Token)).Should().Be(StoreOutcome.NotSent);
        (await sut.DeleteAsync<string>("k", Token)).Should().Be(StoreOutcome.NotSent);
        _ = _database.DidNotReceiveWithAnyArgs().StringSetAsync(default, default, default, default, default);
        _ = _database.DidNotReceiveWithAnyArgs().KeyDeleteAsync(default(RedisKey), default);
    }

    [Fact]
    public async Task A_value_that_fails_to_serialize_is_not_sent()
    {
        var registry = new ValueSerializerRegistry([RegistryEntry.Of("bad", new ThrowingValueSerializer())]);
        var sut = Create(registry);
        StubStore(set: true, delete: true);

        var write = await sut.WriteAsync("k", "v", DateTimeOffset.UtcNow.AddMinutes(1), ResolvedPolicy(registry, "bad"), Token);

        write.Should().Be(StoreOutcome.NotSent);
        _ = _database.DidNotReceiveWithAnyArgs().StringSetAsync(default, default, default, default, default);
    }

    [Fact]
    public async Task A_write_and_delete_that_time_out_after_sending_are_unknown()
    {
        var sut = Create(FastRegistry(), write: new GivingUpPipeline(TimeSpan.FromMilliseconds(100)));
        var never = new TaskCompletionSource<bool>();
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(never.Task);
        _database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(never.Task);

        (await sut.WriteAsync("k", "v", DateTimeOffset.UtcNow.AddMinutes(1), null, Token)).Should().Be(StoreOutcome.Unknown);
        (await sut.DeleteAsync<string>("k", Token)).Should().Be(StoreOutcome.Unknown);
        _ = _database.Received(1).StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task A_write_the_store_answers_with_a_failure_is_failed()
    {
        var sut = Create(out _);
        StubStore(set: false, delete: true);

        (await sut.WriteAsync("k", "v", DateTimeOffset.UtcNow.AddMinutes(1), null, Token)).Should().Be(StoreOutcome.Failed);
    }

    [Fact]
    public async Task A_write_that_landed_late_after_a_timeout_is_committed_whatever_the_retry_did()
    {
        var late = new TaskCompletionSource<bool>();
        var sut = Create(FastRegistry(), write: new RetryOncePipeline(TimeSpan.FromMilliseconds(100), async () =>
        {
            late.SetResult(true);
            await Task.Delay(100, Token);
        }));
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(late.Task, Task.FromException<bool>(new RedisException("lost")));

        (await sut.WriteAsync("k", "v", DateTimeOffset.UtcNow.AddMinutes(1), null, Token)).Should().Be(StoreOutcome.Committed);
    }

    [Fact]
    public async Task An_unrelated_write_a_hook_starts_during_a_timed_out_command_does_not_make_it_committed()
    {
        RedisCache? sut = null;
        var hooked = new HookedPipeline(new GivingUpPipeline(TimeSpan.FromMilliseconds(200)), async () => await sut!.SetAsync("other", "x", TimeSpan.FromMinutes(1), policy: null, Token));
        sut = Create(FastRegistry(), write: hooked);
        var never = new TaskCompletionSource<bool>();
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(never.Task);
        _database.StringSetAsync((RedisKey)"test:str:other", Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);

        (await sut.WriteAsync("k", "v", DateTimeOffset.UtcNow.AddMinutes(1), null, Token)).Should().Be(StoreOutcome.Unknown);
        hooked.HookRan.Should().BeTrue();
    }

    [Fact]
    public async Task A_refused_write_retried_into_a_timeout_is_unknown()
    {
        var sut = Create(FastRegistry(), write: new RetryOncePipeline(TimeSpan.FromMilliseconds(100), () => Task.CompletedTask));
        var never = new TaskCompletionSource<bool>();
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(false), never.Task);

        (await sut.WriteAsync("k", "v", DateTimeOffset.UtcNow.AddMinutes(1), null, Token)).Should().Be(StoreOutcome.Unknown);
    }

    [Fact]
    public async Task A_create_whose_reply_is_lost_is_unknown()
    {
        var sut = Create(FastRegistry(), write: new GivingUpPipeline(TimeSpan.FromMilliseconds(100)));
        var never = new TaskCompletionSource<bool>();
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists, Arg.Any<CommandFlags>()).Returns(never.Task);

        (await sut.TryCreateAsync("k", "v", DateTimeOffset.UtcNow.AddMinutes(1), null, Token)).Should().Be(AddOutcome.Unknown);
    }

    [Fact]
    public async Task A_create_retried_after_a_lost_reply_is_unknown_even_when_the_retry_finds_the_key()
    {
        var sut = Create(FastRegistry(), write: new RetryOncePipeline(TimeSpan.FromMilliseconds(100), () => Task.CompletedTask));
        var never = new TaskCompletionSource<bool>();
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists, Arg.Any<CommandFlags>())
            .Returns(never.Task, Task.FromResult(false));

        (await sut.TryCreateAsync("k", "v", DateTimeOffset.UtcNow.AddMinutes(1), null, Token)).Should().Be(AddOutcome.Unknown, "the first send may be the one that added it");
    }

    [Theory]
    [InlineData(true, AddOutcome.Added)]
    [InlineData(false, AddOutcome.Exists)]
    public async Task A_create_answered_once_reports_that_answer(bool answer, AddOutcome expected)
    {
        var sut = Create(out _);
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists, Arg.Any<CommandFlags>()).Returns(answer);

        (await sut.TryCreateAsync("k", "v", DateTimeOffset.UtcNow.AddMinutes(1), null, Token)).Should().Be(expected);
    }

    [Fact]
    public async Task A_write_and_delete_cancelled_in_flight_are_unknown()
    {
        var sut = Create(FastRegistry(), write: new GivingUpPipeline(TimeSpan.FromSeconds(30)));
        var never = new TaskCompletionSource<bool>();
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(never.Task);
        _database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(never.Task);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        (await sut.WriteAsync("k", "v", DateTimeOffset.UtcNow.AddMinutes(1), null, cts.Token)).Should().Be(StoreOutcome.Unknown);
        using var again = CancellationTokenSource.CreateLinkedTokenSource(Token);
        again.CancelAfter(TimeSpan.FromMilliseconds(100));
        (await sut.DeleteAsync<string>("k", again.Token)).Should().Be(StoreOutcome.Unknown);
    }

    [Fact]
    public async Task A_null_value_written_as_a_delete_that_times_out_is_unknown()
    {
        var sut = Create(FastRegistry(), write: new GivingUpPipeline(TimeSpan.FromMilliseconds(100)));
        var never = new TaskCompletionSource<bool>();
        _database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(never.Task);

        (await sut.WriteAsync<string>("k", null, DateTimeOffset.UtcNow.AddMinutes(1), null, Token)).Should().Be(StoreOutcome.Unknown);
        _ = _database.DidNotReceiveWithAnyArgs().StringSetAsync(default, default, default, default, default);
    }

    [Fact]
    public async Task A_write_or_delete_that_cannot_be_prepared_is_not_sent_and_does_not_throw()
    {
        var sut = Create(FastRegistry());
        StubStore(set: true, delete: true);
        var unregistered = new CachePolicy { Serializer = new SerializerPolicy { Write = "nobody" } };

        (await sut.WriteAsync("k", 1, DateTimeOffset.UtcNow.AddMinutes(1), null, Token)).Should().Be(StoreOutcome.NotSent);
        (await sut.DeleteAsync<int>("k", Token)).Should().Be(StoreOutcome.NotSent);
        (await sut.WriteAsync("k", "v", DateTimeOffset.UtcNow.AddMinutes(1), unregistered, Token)).Should().Be(StoreOutcome.NotSent);
        _ = _database.DidNotReceiveWithAnyArgs().StringSetAsync(default, default, default, default, default);
    }

    [Fact]
    public async Task A_snapshot_cancelled_while_its_key_is_converted_throws_and_a_write_is_not_sent()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var sut = Create(FastRegistry(), keyOf: _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        StubStore(set: true, delete: true);

        await FluentActions.Awaiting(async () => await sut.ReadSnapshotAsync<string>("k", null, cts.Token)).Should().ThrowAsync<OperationCanceledException>();
        using var writing = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var other = Create(FastRegistry(), keyOf: _ =>
        {
            writing.Cancel();
            throw new OperationCanceledException(writing.Token);
        });
        (await other.WriteAsync("k", "v", DateTimeOffset.UtcNow.AddMinutes(1), null, writing.Token)).Should().Be(StoreOutcome.NotSent);
    }

    [Fact]
    public async Task A_snapshot_of_bytes_outside_the_policys_readable_formats_is_unreadable()
    {
        var registry = FastRegistry();
        var sut = Create(registry);
        var policy = new DefaultCachePolicyFactory(null, new CachePolicy { Serializer = new SerializerPolicy { Read = ["json"] } }, valueSerializers: registry).Default!;
        StubSnapshot(Header(0x10, "v"), TimeSpan.FromMinutes(3));

        (await sut.ReadSnapshotAsync<string>("k", policy, Token)).Should().Be(new SnapshotRead<string>(SnapshotOutcome.Unreadable, null));
    }

    [Fact]
    public async Task A_snapshot_of_stored_bytes_that_decode_to_nothing_is_unreadable()
    {
        var sut = Create(ValueSerializerRegistry.Empty);
        StubSnapshot(Encoding.UTF8.GetBytes("null"), TimeSpan.FromMinutes(3));

        (await sut.ReadSnapshotAsync<string>("k", null, Token)).Should().Be(new SnapshotRead<string>(SnapshotOutcome.Unreadable, null));
    }
    private static byte[] Header(byte id, string body) => [id, .. Encoding.UTF8.GetBytes(body)];

    private static CachePolicy ResolvedPolicy(ValueSerializerRegistry registry, string write) =>
        new DefaultCachePolicyFactory(null, new CachePolicy { Serializer = new SerializerPolicy { Write = write } }, valueSerializers: registry).Default!;

    private static ValueTask<bool> Write(RedisCache sut, string path, CachePolicy policy) => path switch
    {
        "set" => sut.SetAsync("k", "hello", TimeSpan.FromMinutes(1), policy),
        "set many" => sut.SetAsync([new KeyValuePair<CacheKey, string?>("a", "hello"), new KeyValuePair<CacheKey, string?>("b", "world")], TimeSpan.FromMinutes(1), policy),
        "try add" => sut.TryAddAsync("k", "hello", TimeSpan.FromMinutes(1), policy),
        "get or add with expiration" => Written(sut.GetOrAddWithExpirationAsync<string>("k", _ => Task.FromResult(new GeneratedValue<string>("hello")), policy)),
        "get or add result" => Written(sut.GetOrAddResultAsync<string>("k", _ => Task.FromResult<string?>("hello"), TimeSpan.FromMinutes(1), policy), static r => r.Value),
        "get or add batch" => Written(sut.GetOrAddAsync<string, string>([new KeyValuePair<CacheKey, string>("a", "a")], static (states, _) => Task.FromResult(states.Select(s => new KeyValuePair<string, string?>(s, "hello")).ToArray()), TimeSpan.FromMinutes(1), policy), static r => r[0].Value),
        "get or add batch with state" => Written(sut.GetOrAddAsync<string, string, string>([new KeyValuePair<CacheKey, string>("a", "a")], "hello", static (value, states, _) => Task.FromResult(states.Select(s => new KeyValuePair<string, string?>(s, value)).ToArray()), TimeSpan.FromMinutes(1), policy), static r => r[0].Value),
        _ => GetOrAdd(sut, policy),
    };

    private static async ValueTask<bool> Written(ValueTask<string?> generated) => await generated is not null;

    private static async ValueTask<bool> Written<TResult>(ValueTask<TResult> generated, Func<TResult, string?> value) => value(await generated) is not null;

    private static async ValueTask<bool> GetOrAdd(RedisCache sut, CachePolicy policy) =>
        await sut.GetOrAddAsync<string>("k", _ => Task.FromResult<string?>("hello"), TimeSpan.FromMinutes(1), policy) is not null;

    private List<string> CaptureConditions()
    {
        var conditions = new List<string>();
        _transaction.AddCondition(Arg.Do<Condition>(c => conditions.Add(c.ToString()!)));
        return conditions;
    }

    private void AssertNothingDeleted()
    {
        _ = _database.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        _ = _database.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
        _ = _transaction.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    private async Task<string[]> MetricNamesOf(Func<Task> operation)
    {
        var before = _telemetry.Metrics.Count;
        await operation();
        return [.. _telemetry.Metrics.Skip(before).Select(m => m.Name).Distinct()];
    }

    private ValueSerializerRegistry FastRegistry() => new([RegistryEntry.Of("fast", _fast)]);

    private async Task<string[]> MethodsOf(Func<Task> operation)
    {
        var (metrics, dependencies) = (_telemetry.Metrics.Count, _telemetry.Dependencies.Count);
        await operation();
        return
        [
            .. _telemetry.Metrics.Skip(metrics).Select(m => m.Name).Where(n => n.StartsWith("Caching.Stats.", StringComparison.Ordinal)).Select(n => n.Split('.')[4])
                .Concat(_telemetry.Dependencies.Skip(dependencies).Select(d => d.Name))
                .Distinct()
                .Order(StringComparer.Ordinal),
        ];
    }

    private RedisCache Create(out ValueSerializerRegistry registry, bool cacheNullValues = false, bool removeUnreadable = true, bool keyReadTelemetry = false)
    {
        registry = FastRegistry();
        return Create(registry, cacheNullValues: cacheNullValues, removeUnreadable: removeUnreadable, keyReadTelemetry: keyReadTelemetry);
    }

    private RedisCache Create(ValueSerializerRegistry registry, ISerializerProxy<byte[]>? proxy = null, CachePolicy? defaultPolicy = null, bool cacheNullValues = false, bool removeUnreadable = true, bool keyReadTelemetry = false, IResiliencePipeline? write = null, bool connected = true, Func<CacheKey, RedisKey>? keyOf = null)
    {
        var connector = Substitute.For<IRedisConnector>();
        connector.Database.Returns(_database);
        connector.Version.Returns(new Version(6, 0));
        connector.IsConnected.Returns(connected);
        _database.Multiplexer.GetHashSlot(Arg.Any<RedisKey>()).Returns(0);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);
        _database.CreateTransaction(Arg.Any<object?>()).Returns(_transaction);
        var pipelines = Substitute.For<IResiliencePipelineProvider>();
        pipelines.Get(ResiliencePipelineNames.Read).Returns(EmptyResiliencePipeline.Instance);
        pipelines.Get(ResiliencePipelineNames.Write).Returns(write ?? _write);
        var strategy = Substitute.For<IRedisKeyStrategy>();
        strategy.GetRedisKey(Arg.Any<CacheKey>()).Returns(call => keyOf?.Invoke(call.Arg<CacheKey>()) ?? (RedisKey)$"test:str:{call.Arg<CacheKey>()}");
        var strategies = Substitute.For<IRedisKeyStrategyFactory>();
        strategies.Create(Arg.Any<CacheOptions>(), Arg.Any<Type>()).Returns(strategy);
        var options = new RedisCacheOptions { RedisKeyStrategyFactory = strategies, ReadScriptsEnabled = false, CacheNullValues = cacheNullValues, RemoveUnreadable = removeUnreadable, KeyReadTelemetryEnabled = keyReadTelemetry };
        var cacheOptions = new CacheOptions { AppShortName = "test", ConnectionMonitorEnabled = true };

        return new RedisCache(
            connector,
            proxy ?? new SystemJsonByteSerializerProxy(),
            pipelines,
            _telemetry,
            options,
            cacheOptions,
            new DefaultCachePolicyFactory(null, defaultPolicy, valueSerializers: registry),
            TimeProvider.System,
            NullLogger<RedisCache>.Instance)
        {
            ValueSerializers = registry,
        };
    }

    private (List<byte[]> Values, List<When> Conditions) CaptureWritesWithConditions()
    {
        List<byte[]> values = [];
        List<When> conditions = [];
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Do<RedisValue>(v => values.Add(((byte[]?)v)!)), Arg.Any<TimeSpan?>(), Arg.Do<When>(conditions.Add), Arg.Any<CommandFlags>()).Returns(true);
        _transaction.StringSetAsync(Arg.Any<RedisKey>(), Arg.Do<RedisValue>(v => values.Add(((byte[]?)v)!)), Arg.Any<TimeSpan?>(), Arg.Do<When>(conditions.Add), Arg.Any<CommandFlags>()).Returns(true);
        return (values, conditions);
    }

    private List<byte[]> CaptureWrites()
    {
        var captured = new List<byte[]>();
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Do<RedisValue>(v => captured.Add(((byte[]?)v)!)), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);
        _transaction.StringSetAsync(Arg.Any<RedisKey>(), Arg.Do<RedisValue>(v => captured.Add(((byte[]?)v)!)), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        return captured;
    }

    private void StubSnapshot(RedisValue value, TimeSpan? timeToLive)
    {
        _transaction.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(value);
        _transaction.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(timeToLive);
    }

    private void StubStore(bool set, bool delete)
    {
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(set);
        _database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(delete);
    }

    private sealed class OutOfMemoryProxy : ISerializerProxy<byte[]>
    {
        public byte[]? Serialize(object? value) => throw new NotSupportedException();

        public T? Deserialize<T>(byte[]? value) => throw new OutOfMemoryException();

        public bool TryDeserialize<T>(string? value, out T? result) => throw new NotSupportedException();

        public bool TryDeserialize<T>(object? value, out T? result) => throw new NotSupportedException();
    }

    private sealed class ByteProxy : ISerializerProxy<byte[]>
    {
        public byte[]? Serialize(object? value) => (byte[]?)value;

        public T? Deserialize<T>(byte[]? value) => (T?)(object?)value;

        public bool TryDeserialize<T>(string? value, out T? result) => throw new NotSupportedException();

        public bool TryDeserialize<T>(object? value, out T? result) => throw new NotSupportedException();
    }

    private sealed class GivingUpPipeline(TimeSpan timeout) : IResiliencePipeline
    {
        public async ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            try
            {
                return await callback(cts.Token).AsTask().WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return defaultValue;
            }
        }
    }
    private sealed class HookedPipeline(IResiliencePipeline inner, Func<Task> hook) : IResiliencePipeline
    {
        private int _hooked;

        public bool HookRan => Volatile.Read(ref _hooked) == 2;

        public async ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref _hooked, 1, 0) == 0)
            {
                var running = inner.ExecuteAsync(callback, defaultValue, cancellationToken).AsTask();
                await hook();
                Volatile.Write(ref _hooked, 2);
                return await running;
            }

            return await inner.ExecuteAsync(callback, defaultValue, cancellationToken);
        }
    }

    private sealed class RetryOncePipeline(TimeSpan wait, Func<Task> between) : IResiliencePipeline
    {
        public async ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var running = callback(cancellationToken).AsTask();
                var finished = await Task.WhenAny(running, Task.Delay(wait, cancellationToken));
                if (finished != running)
                {
                    await between();
                    continue;
                }

                try
                {
                    var result = await running;
                    if (!EqualityComparer<TResult>.Default.Equals(result, defaultValue))
                    {
                        return result;
                    }
                }
                catch (RedisException)
                {
                    // The attempt failed; the next one runs.
                }
            }

            return defaultValue;
        }
    }
}
