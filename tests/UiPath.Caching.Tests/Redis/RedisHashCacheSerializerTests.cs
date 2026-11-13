using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Policies;
using UiPath.Caching.Serialization;
using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Redis;

[Collection(WriterPoolCollection.Name)]
public class RedisHashCacheSerializerTests(ITestContextAccessor testContextAccessor)
{
    private const string UnreadableMetric = "cache.serializer.unreadable";

    private readonly IDatabase _database = Substitute.For<IDatabase>();
    private readonly ITransaction _transaction = Substitute.For<ITransaction>();
    private readonly RecordingTelemetryProvider _telemetry = new();
    private readonly RecordingResiliencePipeline _write = new();
    private readonly PassthroughValueSerializer _fast = new(0x10);

    private CancellationToken Token => testContextAccessor.Current.CancellationToken;

    public static TheoryData<string> Scenarios() => ["no registry", "registry, json write", "registry, fast write"];

    [Fact]
    public async Task Fields_and_metadata_use_the_policy_serializer()
    {
        var sut = Create(out var registry);
        var written = CaptureWrites();
        var options = new HashCacheEntryOptions(DateTimeOffset.UtcNow.AddMinutes(5), Metadata: new Dictionary<string, string?> { ["m"] = "v" });

        await sut.SetAsync("k", new Dictionary<string, string?> { ["a"] = "x" }, options, ResolvedPolicy(registry), Token);

        var entries = written.Should().ContainSingle().Subject;
        entries.Should().HaveCount(2);
        Bytes(entries, "a").Should().Equal(Header(0x10, "x"));
        Bytes(entries, "_metadata_").Should().Equal(Header(0x10, "{\"m\":\"v\"}"));
    }

    [Fact]
    public async Task The_json_default_writes_the_same_bytes_as_before()
    {
        var sut = Create(out _);
        var written = CaptureWrites();
        var options = new HashCacheEntryOptions(DateTimeOffset.UtcNow.AddMinutes(5), Metadata: new Dictionary<string, string?> { ["m"] = "v" });

        await sut.SetAsync("k", new Dictionary<string, string?> { ["a"] = "x" }, options, policy: null, Token);

        var entries = written.Should().ContainSingle().Subject;
        Bytes(entries, "a").Should().Equal(new SystemJsonByteSerializerProxy().Serialize("x"));
        Bytes(entries, "_metadata_").Should().Equal(Encoding.UTF8.GetBytes("{\"m\":\"v\"}"));
        _fast.Serialized.Should().Be(0);
    }

    [Fact]
    public async Task Empty_metadata_is_not_written_when_nulls_are_not_cached()
    {
        var sut = Create(out var registry);
        var written = CaptureWrites();
        var options = new HashCacheEntryOptions(DateTimeOffset.UtcNow.AddMinutes(5), Metadata: new Dictionary<string, string?>());

        await sut.SetAsync("k", new Dictionary<string, string?> { ["a"] = "x" }, options, ResolvedPolicy(registry), Token);

        var entries = written.Should().ContainSingle().Subject;
        entries.Select(e => e.Name.ToString()).Should().Equal("a");
    }

    [Fact]
    public async Task A_hash_replace_with_empty_metadata_deletes_the_old_metadata_field_for_a_non_json_write()
    {
        var sut = Create(out var registry);
        var written = CaptureWrites();
        var options = new HashCacheEntryOptions(DateTimeOffset.UtcNow.AddMinutes(5), Metadata: new Dictionary<string, string?>(), SetOption: HashCacheSetOption.HashReplace);

        await sut.SetAsync("k", new Dictionary<string, string?> { ["a"] = "x" }, options, ResolvedPolicy(registry), Token);

        written.Should().ContainSingle().Which.Select(e => e.Name.ToString()).Should().Equal("a");
        _ = _transaction.Received(1).HashDeleteAsync((RedisKey)"test:hash:k", Arg.Is<RedisValue>(f => f == "_metadata_"), CommandFlags.DemandMaster);
        _ = _database.DidNotReceive().HashDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task A_key_replace_with_empty_metadata_needs_no_field_delete_for_a_non_json_write()
    {
        var sut = Create(out var registry);
        CaptureWrites();
        var options = new HashCacheEntryOptions(DateTimeOffset.UtcNow.AddMinutes(5), Metadata: new Dictionary<string, string?>());

        await sut.SetAsync("k", new Dictionary<string, string?> { ["a"] = "x" }, options, ResolvedPolicy(registry), Token);

        _ = _database.DidNotReceive().HashDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
        _ = _transaction.DidNotReceive().HashDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task A_hash_replace_with_metadata_writes_it_and_deletes_nothing()
    {
        var sut = Create(out var registry);
        var written = CaptureWrites();
        var options = new HashCacheEntryOptions(DateTimeOffset.UtcNow.AddMinutes(5), Metadata: new Dictionary<string, string?> { ["m"] = "v" }, SetOption: HashCacheSetOption.HashReplace);

        await sut.SetAsync("k", new Dictionary<string, string?> { ["a"] = "x" }, options, ResolvedPolicy(registry), Token);

        written.Should().ContainSingle().Which.Select(e => e.Name.ToString()).Should().Equal("a", "_metadata_");
        _ = _database.DidNotReceive().HashDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
        _ = _transaction.DidNotReceive().HashDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task The_json_default_hash_replace_with_empty_metadata_writes_the_same_bytes_and_deletes_nothing()
    {
        var sut = Create(out _);
        var written = CaptureWrites();
        var options = new HashCacheEntryOptions(DateTimeOffset.UtcNow.AddMinutes(5), Metadata: new Dictionary<string, string?>(), SetOption: HashCacheSetOption.HashReplace);

        await sut.SetAsync("k", new Dictionary<string, string?> { ["a"] = "x" }, options, policy: null, Token);

        Bytes(written.Single(), "_metadata_").Should().Equal(Encoding.UTF8.GetBytes("{}"));
        _ = _database.DidNotReceive().HashDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
        _ = _transaction.DidNotReceive().HashDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Empty_metadata_reads_as_none()
    {
        var sut = Create(out _);
        _database.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns([Entry("a", Header(0x10, "x"))]);
        _database.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(TimeSpan.FromMinutes(1));

        var entry = await sut.GetCacheEntryAsync<string>("k", policy: null, Token);

        entry.Found.Should().BeTrue();
        entry.Metadata.Should().BeNull();
    }

    [Fact]
    public async Task The_json_default_still_writes_empty_metadata()
    {
        var sut = Create(out _);
        var written = CaptureWrites();
        var options = new HashCacheEntryOptions(DateTimeOffset.UtcNow.AddMinutes(5), Metadata: new Dictionary<string, string?>());

        await sut.SetAsync("k", new Dictionary<string, string?> { ["a"] = "x" }, options, policy: null, Token);

        Bytes(written.Single(), "_metadata_").Should().Equal(Encoding.UTF8.GetBytes("{}"));
    }

    [Fact]
    public async Task Cached_empty_marker_is_kept()
    {
        var sut = Create(out var registry, cacheNullValues: true);
        var written = CaptureWrites();
        _database.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns([]);

        var generated = await sut.GetOrAddAsync<string>("k", _ => Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?>()), TimeSpan.FromMinutes(1), ResolvedPolicy(registry), Token);

        generated.Should().BeEmpty();
        var entries = written.Should().ContainSingle().Subject;
        var marker = entries.Should().ContainSingle().Subject;
        marker.Name.ToString().Should().Be("_metadata_");
        marker.Value.Length().Should().Be(0);
    }

    [Fact]
    public async Task Headered_fields_read_under_any_policy()
    {
        var sut = Create(out _);
        _database.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns([Entry("a", Header(0x10, "x")), Entry("b", Json("y"))]);

        var values = await sut.GetAsync<string>("k", policy: null, Token);

        values.Should().BeEquivalentTo(new Dictionary<string, string?> { ["a"] = "x", ["b"] = "y" });
    }

    [Fact]
    public async Task Version_field_is_never_sniffed()
    {
        var sut = Create(out _);
        _database.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(
        [
            Entry("a", Header(0x10, "x")),
            Entry("_version_", Encoding.UTF8.GetBytes("12")),
        ]);
        _database.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(TimeSpan.FromMinutes(1));

        var entry = await sut.GetCacheEntryAsync<string>("k", policy: null, Token);

        HashVersions.NumberOf(entry).Should().Be(12);
        entry.Value.Should().ContainKey("a");
        _telemetry.Metrics.Should().NotContain(m => m.Name == UnreadableMetric);
    }

    [Fact]
    public async Task A_version_field_that_looks_like_a_header_is_not_an_unreadable_format()
    {
        var sut = Create(out _);
        _database.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(
        [
            Entry("a", Header(0x10, "x")),
            Entry("_version_", [0x1E, (byte)'1']),
        ]);
        _database.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(TimeSpan.FromMinutes(1));

        var entry = await sut.GetCacheEntryAsync<string>("k", policy: null, Token);

        entry.Found.Should().BeTrue();
        await _database.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Every_operation_reports_the_method_name_it_reported_before_serializers(string scenario)
    {
        var registry = scenario == "no registry" ? ValueSerializerRegistry.Empty : new ValueSerializerRegistry([("fast", _fast, false)]);
        var fastWrite = scenario.EndsWith("fast write", StringComparison.Ordinal);
        var sut = Create(registry, defaultPolicy: fastWrite ? new CachePolicy { Serializer = new SerializerPolicy { Write = "fast" } } : null);
        var policy = fastWrite ? ResolvedPolicy(registry) : null;
        CaptureWrites();
        _database.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        var values = new Dictionary<string, string?> { ["a"] = "x" };
        var options = new HashCacheEntryOptions(DateTimeOffset.UtcNow.AddMinutes(5), Metadata: new Dictionary<string, string?> { ["m"] = "v" });
        Task<IDictionary<string, string?>> Generate(CancellationToken token) => Task.FromResult<IDictionary<string, string?>>(values);

        (await MethodsOf(async () => await sut.GetItemAsync<string>("k", "a", policy, Token))).Should().Equal("GetAsync");
        (await MethodsOf(async () => await sut.GetAsync<string>("k", policy, Token))).Should().Equal("GetInnerAsync");
        (await MethodsOf(async () => await sut.GetAsync<string>("k", ["a", "b"], policy, Token))).Should().Equal("GetInnerAsync");
        (await MethodsOf(async () => await sut.GetCacheEntryAsync<string>("k", policy, Token))).Should().Equal("GetInnerCacheEntryAsync");
        (await MethodsOf(async () => await sut.GetMetadataAsync<string>("k", Token))).Should().Equal("GetAsync");
        (await MethodsOf(async () => await sut.SetAsync("k", values, options, policy, Token))).Should().Equal("SetAsync");
        (await MethodsOf(async () => await sut.GetOrAddAsync<string>("k", Generate, TimeSpan.FromMinutes(1), policy, Token))).Should().Equal("GetOrAddAsync", "SetAsync");
        (await MethodsOf(async () => await sut.RefreshAsync<string>("k", options, policy, Token))).Should().Equal("RefreshAsync");
        (await MethodsOf(async () => await sut.SetMetadataAsync<string>("k", new Dictionary<string, string?> { ["m"] = "v" }, Token))).Should().Equal("SetMetadataAsync");
    }

    [Fact]
    public async Task One_unreadable_field_is_a_whole_hash_miss_and_the_hash_stays()
    {
        var sut = Create(out _);
        _database.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns([Entry("a", Json("x")), Entry("b", [0x1E, 1])]);

        var values = await sut.GetAsync<string>("k", policy: null, Token);

        values.Should().BeEmpty();
        AssertNothingDeleted();
        _telemetry.Metrics.Should().ContainSingle(m => m.Name == UnreadableMetric).Which.Properties.Should().Contain("format", "0x1E");
    }

    [Fact]
    public async Task A_field_whose_serializer_throws_is_a_whole_hash_miss_and_the_hash_stays()
    {
        var sut = Create(new ValueSerializerRegistry([("bad", new ThrowingValueSerializer(), false)]));
        _database.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns([Entry("a", Json("x")), Entry("b", [0x10, 1])]);

        var values = await sut.GetAsync<string>("k", policy: null, Token);

        values.Should().BeEmpty();
        AssertNothingDeleted();
        _telemetry.Metrics.Should().NotContain(m => m.Name == UnreadableMetric);
    }

    [Fact]
    public async Task An_out_of_memory_from_a_serializer_propagates_from_every_hash_read_and_runs_no_generator()
    {
        var sut = Create(new ValueSerializerRegistry([("bad", new ThrowingValueSerializer { Failure = new OutOfMemoryException() }, false)]));
        _database.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns([Entry("a", [0x10, 1])]);
        _database.HashGetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>()).Returns((RedisValue)new byte[] { 0x10, 1 });
        _database.HashGetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>()).Returns([(RedisValue)new byte[] { 0x10, 1 }]);
        var generated = 0;
        Task<IDictionary<string, string?>> Generate(CancellationToken token)
        {
            generated++;
            return Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?> { ["a"] = "x" });
        }

        await FluentActions.Awaiting(() => sut.GetAsync<string>("k", policy: null, Token).AsTask()).Should().ThrowAsync<OutOfMemoryException>();
        await FluentActions.Awaiting(() => sut.GetAsync<string>("k", ["a"], policy: null, Token).AsTask()).Should().ThrowAsync<OutOfMemoryException>();
        await FluentActions.Awaiting(() => sut.GetItemAsync<string>("k", "a", policy: null, Token).AsTask()).Should().ThrowAsync<OutOfMemoryException>();
        await FluentActions.Awaiting(() => sut.GetCacheEntryAsync<string>("k", policy: null, Token).AsTask()).Should().ThrowAsync<OutOfMemoryException>();
        await FluentActions.Awaiting(() => sut.GetOrAddAsync<string>("k", Generate, policy: null, Token).AsTask()).Should().ThrowAsync<OutOfMemoryException>();

        generated.Should().Be(0);
    }

    [Fact]
    public async Task Fields_round_trip_through_the_serializer_the_policy_names()
    {
        var sut = Create(out var registry);
        var written = CaptureWrites();
        var options = new HashCacheEntryOptions(DateTimeOffset.UtcNow.AddMinutes(5), Metadata: new Dictionary<string, string?> { ["m"] = "v" });
        var policy = ResolvedPolicy(registry);

        await sut.SetAsync("k", new Dictionary<string, string?> { ["a"] = "x", ["b"] = "y" }, options, policy, Token);
        _database.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(written.Single());
        _database.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(TimeSpan.FromMinutes(1));
        var entry = await sut.GetCacheEntryAsync<string>("k", policy, Token);

        entry.Value.Should().BeEquivalentTo(new Dictionary<string, string?> { ["a"] = "x", ["b"] = "y" });
        entry.Metadata.Should().BeEquivalentTo(new Dictionary<string, string?> { ["m"] = "v" });
        _fast.Serialized.Should().Be(3);
        _fast.Deserialized.Should().Be(3);
    }

    [Fact]
    public async Task A_field_round_trips_through_a_two_byte_format_id()
    {
        var extended = new PassthroughValueSerializer(0x105);
        var registry = new ValueSerializerRegistry([("extended", extended, false)]);
        var sut = Create(registry);
        var written = CaptureWrites();
        var policy = new DefaultCachePolicyFactory(null, new CachePolicy { Serializer = new SerializerPolicy { Write = "extended" } }, valueSerializers: registry).Default!;

        await sut.SetAsync("k", new Dictionary<string, string?> { ["a"] = "x" }, new HashCacheEntryOptions(DateTimeOffset.UtcNow.AddMinutes(5)), policy, Token);
        _database.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(written.Single());
        _database.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(TimeSpan.FromMinutes(1));
        var entry = await sut.GetCacheEntryAsync<string>("k", policy: null, Token);

        Bytes(written.Single(), "a").Take(2).Should().Equal(0x1F, 0x05);
        entry.Value.Should().BeEquivalentTo(new Dictionary<string, string?> { ["a"] = "x" });
        extended.LastFormatId.Should().Be(0x105);
    }

    [Fact]
    public async Task A_binary_field_that_is_not_utf8_round_trips()
    {
        var sut = Create(out var registry);
        var written = CaptureWrites();
        var policy = ResolvedPolicy(registry);
        byte[] payload = [0xFF, 0xFE, 0x00, 0x80, 0xC3, 0x28];

        await sut.SetAsync("k", new Dictionary<string, byte[]?> { ["a"] = payload }, new HashCacheEntryOptions(DateTimeOffset.UtcNow.AddMinutes(5)), policy, Token);
        _database.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(written.Single());
        _database.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(TimeSpan.FromMinutes(1));
        var entry = await sut.GetCacheEntryAsync<byte[]>("k", policy: null, Token);

        Bytes(written.Single(), "a").Should().Equal([0x10, .. payload]);
        entry.Value!["a"].Should().Equal(payload);
    }

    [Fact]
    public async Task An_unreadable_metadata_field_is_a_whole_hash_miss_and_the_hash_stays()
    {
        var sut = Create(out _);
        _database.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns([Entry("a", Json("x")), Entry("_metadata_", [0x1E, 1])]);
        _database.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(TimeSpan.FromMinutes(1));

        var entry = await sut.GetCacheEntryAsync<string>("k", policy: null, Token);

        entry.Found.Should().BeFalse();
        AssertNothingDeleted();
    }

    [Theory]
    [InlineData(false, "fast", "headerless")]
    [InlineData(false, "json", "0x10")]
    [InlineData(true, "fast", "headerless")]
    [InlineData(true, "json", "0x10")]
    public async Task A_field_or_metadata_outside_the_read_list_is_counted_under_its_format(bool metadata, string reads, string format)
    {
        var sut = Create(out var registry);
        var policy = ReadingPolicy(registry, reads);
        var readable = reads == "fast" ? Header(0x10, "x") : Json("x");
        var excluded = reads == "fast" ? Json("x") : Header(0x10, "x");
        var readableMeta = reads == "fast" ? Header(0x10, "{\"m\":\"v\"}") : Encoding.UTF8.GetBytes("{\"m\":\"v\"}");
        var excludedMeta = reads == "fast" ? Encoding.UTF8.GetBytes("{\"m\":\"v\"}") : Header(0x10, "{\"m\":\"v\"}");
        _database.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(metadata ? [Entry("a", readable), Entry("_metadata_", excludedMeta)] : [Entry("a", excluded), Entry("_metadata_", readableMeta)]);
        _database.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(TimeSpan.FromMinutes(1));

        var entry = await sut.GetCacheEntryAsync<string>("k", policy, Token);

        entry.Found.Should().BeFalse();
        AssertNothingDeleted();
        _telemetry.Metrics.Should().ContainSingle(m => m.Name == UnreadableMetric).Which.Properties.Should().Contain("format", format);
    }

    [Fact]
    public async Task A_field_whose_serializer_throws_makes_a_hash_replace_get_or_add_replace_the_whole_key()
    {
        var sut = Create(new ValueSerializerRegistry([("bad", new ThrowingValueSerializer(), false)]));
        CaptureWrites();
        _transaction.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        _database.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns([Entry("a", [0x10, 1])]);

        await sut.GetOrAddAsync<string>("k", _ => Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?> { ["a"] = "fresh" }), DateTimeOffset.UtcNow.AddMinutes(1), HashCacheSetOption.HashReplace, policy: null, Token);

        _ = _transaction.Received().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        _telemetry.Metrics.Should().NotContain(m => m.Name == UnreadableMetric);
    }

    [Fact]
    public async Task An_unreadable_field_in_a_field_read_is_a_whole_hash_miss_and_the_hash_stays()
    {
        var sut = Create(out _);
        _database.HashGetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>()).Returns([(RedisValue)Json("x"), (RedisValue)new byte[] { 0x1E, 1 }]);

        var values = await sut.GetAsync<string>("k", ["a", "b"], policy: null, Token);

        values.Should().BeEmpty();
        AssertNothingDeleted();
    }

    [Fact]
    public async Task An_unreadable_single_field_is_a_miss_and_the_hash_stays()
    {
        var sut = Create(out _);
        _database.HashGetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>()).Returns((RedisValue)new byte[] { 0x1E, 1 });

        var value = await sut.GetItemAsync<string>("k", "a", policy: null, Token);

        value.Should().BeNull();
        AssertNothingDeleted();
    }

    [Fact]
    public async Task An_unreadable_field_in_a_get_or_add_read_regenerates_and_writes_over_the_hash()
    {
        var sut = Create(out _);
        var order = new List<string>();
        CaptureWrites();
        _database.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns([Entry("a", [0x1E, 1])]);
        _transaction.HashSetAsync(Arg.Any<RedisKey>(), Arg.Do<HashEntry[]>(_ => order.Add("write")), Arg.Any<CommandFlags>()).Returns(Task.CompletedTask);

        Task<IDictionary<string, string?>> Generate(CancellationToken token)
        {
            order.Add("generate");
            return Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?> { ["a"] = "fresh" });
        }

        var values = await sut.GetOrAddAsync<string>("k", Generate, TimeSpan.FromMinutes(1), policy: null, Token);

        values["a"].Should().Be("fresh");
        order.Should().Equal("generate", "write");
        _telemetry.Metrics.Should().ContainSingle(m => m.Name == UnreadableMetric);
    }

    [Fact]
    public async Task GetMetadata_sniffs_with_the_exact_json_proxy()
    {
        var sut = Create(out _);
        _database.HashGetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>()).Returns((RedisValue)Header(0x10, "{\"m\":\"v\"}"));

        var metadata = await sut.GetMetadataAsync<string>("k", Token);

        metadata.Should().BeEquivalentTo(new Dictionary<string, string?> { ["m"] = "v" });
    }

    [Fact]
    public async Task GetMetadata_does_not_sniff_for_a_custom_proxy()
    {
        var sut = Create(new ValueSerializerRegistry([("fast", _fast, false)]), new ByteProxy());
        _database.HashGetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>()).Returns((RedisValue)Header(0x10, "{\"m\":\"v\"}"));

        var metadata = await sut.GetMetadataAsync<string>("k", Token);

        metadata.Should().BeNull();
        _fast.Deserialized.Should().Be(0);
    }

    [Fact]
    public async Task SetMetadata_writes_the_default_policy_serializer()
    {
        var registry = new ValueSerializerRegistry([("fast", _fast, false)]);
        var sut = Create(registry, defaultPolicy: new CachePolicy { Serializer = new SerializerPolicy { Write = "fast" } });
        _database.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        _database.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);
        var written = new List<byte[]>();
        _transaction.HashSetAsync(Arg.Any<RedisKey>(), Arg.Do<HashEntry[]>(e => written.Add(((byte[]?)e[0].Value)!)), Arg.Any<CommandFlags>()).Returns(Task.CompletedTask);
        _database.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Do<RedisValue>(v => written.Add(((byte[]?)v)!)), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);

        await sut.SetMetadataAsync<string>("k", new Dictionary<string, string?> { ["m"] = "v" }, Token);

        written.Should().ContainSingle().Which.Should().Equal(Header(0x10, "{\"m\":\"v\"}"));
    }

    [Fact]
    public void Peer_fields_with_headered_bytes_decode_through_the_reader()
    {
        var sut = Create(out _);
        var encoded = new Dictionary<string, byte[]?> { ["a"] = Header(0x10, "x"), ["b"] = Json("y"), ["c"] = null };

        sut.TryDecodeFields<string>(encoded, new CachePolicy(), out var values).Should().BeTrue();

        values.Should().BeEquivalentTo(new Dictionary<string, string?> { ["a"] = "x", ["b"] = "y", ["c"] = null });
    }

    [Fact]
    public void Peer_fields_in_a_format_this_node_cannot_read_are_not_applied()
    {
        var sut = Create(out _);

        sut.TryDecodeFields<string>(new Dictionary<string, byte[]?> { ["a"] = [0x1E, 1] }, new CachePolicy(), out var values).Should().BeFalse();

        values.Should().BeNull();
    }

    [Fact]
    public void Peer_fields_are_not_decoded_where_a_custom_proxy_could_mistake_headered_bytes_for_its_own()
    {
        var sut = Create(new ValueSerializerRegistry([("fast", _fast, false)]), new ByteProxy());

        sut.TryDecodeFields<byte[]>(new Dictionary<string, byte[]?> { ["a"] = Header(0x10, "x") }, new CachePolicy(), out var values).Should().BeFalse();

        values.Should().BeNull();
        _fast.Deserialized.Should().Be(0);
    }

    [Fact]
    public void Peer_fields_decode_through_the_reader_for_a_custom_proxy_when_the_default_policy_writes_a_serializer()
    {
        var sut = Create(new ValueSerializerRegistry([("fast", _fast, false)]), new ByteProxy(), new CachePolicy { Serializer = new SerializerPolicy { Write = "fast" } });

        sut.TryDecodeFields<string>(new Dictionary<string, byte[]?> { ["a"] = Header(0x10, "x") }, new CachePolicy(), out var values).Should().BeTrue();

        values.Should().BeEquivalentTo(new Dictionary<string, string?> { ["a"] = "x" });
    }

    [Fact]
    public void Peer_fields_decode_through_a_custom_proxy_when_no_serializer_is_registered()
    {
        var sut = Create(ValueSerializerRegistry.Empty, new ByteProxy());

        sut.TryDecodeFields<byte[]>(new Dictionary<string, byte[]?> { ["a"] = [0x10, 1] }, new CachePolicy(), out var values).Should().BeTrue();

        values!["a"].Should().Equal(0x10, 1);
    }

    [Fact]
    public void Peer_fields_of_a_key_whose_policy_writes_json_are_not_decoded_by_the_default_policys_gate()
    {
        var sut = Create(new ValueSerializerRegistry([("fast", _fast, false)]), new ByteProxy(), new CachePolicy { Serializer = new SerializerPolicy { Write = "fast" } });
        var keyPolicy = new CachePolicy { Serializer = new SerializerPolicy { Write = "json" } };

        sut.TryDecodeFields<byte[]>(new Dictionary<string, byte[]?> { ["a"] = [0x10, 1, 2] }, keyPolicy, out var values).Should().BeFalse();

        values.Should().BeNull();
        _fast.Deserialized.Should().Be(0);
    }

    [Fact]
    public void Peer_fields_decode_for_an_unknown_key_policy_with_the_default_proxy()
    {
        var sut = Create(out _);

        sut.TryDecodeFields<string>(new Dictionary<string, byte[]?> { ["a"] = Json("y"), ["b"] = Header(0x10, "x") }, policy: null, out var values).Should().BeTrue();

        values.Should().BeEquivalentTo(new Dictionary<string, string?> { ["a"] = "y", ["b"] = "x" });
    }

    [Fact]
    public void Peer_fields_are_not_decoded_for_an_unknown_key_policy_with_a_custom_proxy()
    {
        var sut = Create(new ValueSerializerRegistry([("fast", _fast, false)]), new ByteProxy());

        sut.TryDecodeFields<byte[]>(new Dictionary<string, byte[]?> { ["a"] = [1, 2] }, policy: null, out var values).Should().BeFalse();

        values.Should().BeNull();
    }

    [Fact]
    public void Peer_fields_whose_serializer_throws_are_not_applied()
    {
        var sut = Create(new ValueSerializerRegistry([("bad", new ThrowingValueSerializer(), false)]));

        sut.TryDecodeFields<string>(new Dictionary<string, byte[]?> { ["a"] = [0x10, 1] }, new CachePolicy(), out var values).Should().BeFalse();

        values.Should().BeNull();
    }

    [Fact]
    public void Peer_fields_that_fail_to_decode_for_any_reason_are_not_applied()
    {
        var sut = Create(ValueSerializerRegistry.Empty, new FailingProxy());

        sut.TryDecodeFields<string>(new Dictionary<string, byte[]?> { ["a"] = [1] }, new CachePolicy(), out var values).Should().BeFalse();

        values.Should().BeNull();
    }

    [Fact]
    public async Task Stored_size_audit_reports_the_stored_length_including_the_header()
    {
        var logger = Substitute.For<ILogger<RedisHashCache>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var sut = Create(out var registry, logger: logger, auditThreshold: 1000);
        CaptureWrites();
        var body = new string('x', 1000);

        await sut.SetAsync("k", new Dictionary<string, string?> { ["a"] = body }, new HashCacheEntryOptions(DateTimeOffset.UtcNow.AddMinutes(5)), ResolvedPolicy(registry), Token);

        var warnings = logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ILogger.Log) && (LogLevel)c.GetArguments()[0]! == LogLevel.Warning)
            .Select(c => c.GetArguments()[2]?.ToString() ?? string.Empty)
            .Where(m => m.Contains("large value", StringComparison.Ordinal))
            .ToArray();
        warnings.Should().ContainSingle().Which.Should().Contain("length 1001");
    }

    private static byte[] Header(byte id, string body) => [id, .. Encoding.UTF8.GetBytes(body)];

    private static byte[] Json(string value) => new SystemJsonByteSerializerProxy().Serialize(value)!;

    private static HashEntry Entry(string name, byte[] value) => new(Encoding.UTF8.GetBytes(name), value);

    private static byte[] Bytes(HashEntry[] entries, string name) => ((byte[]?)entries.Single(e => e.Name.ToString() == name).Value)!;

    private static CachePolicy ResolvedPolicy(ValueSerializerRegistry registry) =>
        new DefaultCachePolicyFactory(null, new CachePolicy { Serializer = new SerializerPolicy { Write = "fast" } }, valueSerializers: registry).Default!;

    private static CachePolicy ReadingPolicy(ValueSerializerRegistry registry, string format) =>
        new DefaultCachePolicyFactory(null, new CachePolicy { Serializer = new SerializerPolicy { Write = format, Read = [format] } }, valueSerializers: registry, negotiation: FormatNegotiationMode.Force).Default!;

    private void AssertNothingDeleted()
    {
        _database.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        _transaction.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    private RedisHashCache Create(out ValueSerializerRegistry registry, bool cacheNullValues = false, ILogger<RedisHashCache>? logger = null, int auditThreshold = 0)
    {
        registry = new ValueSerializerRegistry([("fast", _fast, false)]);
        return Create(registry, cacheNullValues: cacheNullValues, logger: logger, auditThreshold: auditThreshold);
    }

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

    private RedisHashCache Create(ValueSerializerRegistry registry, ISerializerProxy<byte[]>? proxy = null, CachePolicy? defaultPolicy = null, bool cacheNullValues = false, ILogger<RedisHashCache>? logger = null, int auditThreshold = 0)
    {
        var connector = Substitute.For<IRedisConnector>();
        connector.Database.Returns(_database);
        connector.Version.Returns(new Version(6, 0));
        connector.IsConnected.Returns(true);
        _database.Multiplexer.GetHashSlot(Arg.Any<RedisKey>()).Returns(0);
        _database.CreateTransaction(Arg.Any<object?>()).Returns(_transaction);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);
        var pipelines = Substitute.For<IResiliencePipelineProvider>();
        pipelines.Get(ResiliencePipelineNames.Read).Returns(EmptyResiliencePipeline.Instance);
        pipelines.Get(ResiliencePipelineNames.Write).Returns(_write);
        var strategy = Substitute.For<IRedisKeyStrategy>();
        strategy.GetRedisKey(Arg.Any<CacheKey>()).Returns(call => (RedisKey)$"test:hash:{call.Arg<CacheKey>()}");
        var strategies = Substitute.For<IRedisKeyStrategyFactory>();
        strategies.Create(Arg.Any<CacheOptions>(), Arg.Any<Type>()).Returns(strategy);
        var options = new RedisCacheOptions { RedisKeyStrategyFactory = strategies, ReadScriptsEnabled = false, CacheNullValues = cacheNullValues };
        var cacheOptions = new CacheOptions { AppShortName = "test", ConnectionMonitorEnabled = true, AuditEnabled = auditThreshold > 0, LargeValueThreshold = auditThreshold };

        return new RedisHashCache(
            connector,
            proxy ?? new SystemJsonByteSerializerProxy(),
            pipelines,
            _telemetry,
            options,
            cacheOptions,
            new DefaultCachePolicyFactory(null, defaultPolicy, valueSerializers: registry),
            TimeProvider.System,
            logger ?? NullLogger<RedisHashCache>.Instance)
        {
            ValueSerializers = registry,
        };
    }

    private List<HashEntry[]> CaptureWrites()
    {
        var written = new List<HashEntry[]>();
        _transaction.HashSetAsync(Arg.Any<RedisKey>(), Arg.Do<HashEntry[]>(written.Add), Arg.Any<CommandFlags>()).Returns(Task.CompletedTask);
        _transaction.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<DateTime?>(), Arg.Any<CommandFlags>()).Returns(true);
        _database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        return written;
    }

    private sealed class FailingProxy : ISerializerProxy<byte[]>
    {
        public byte[]? Serialize(object? value) => throw new NotSupportedException();

        public T? Deserialize<T>(byte[]? value) => throw new ArgumentException("not decodable");

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
}
