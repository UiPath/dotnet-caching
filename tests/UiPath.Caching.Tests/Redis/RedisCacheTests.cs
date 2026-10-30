using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Internal;
using NSubstitute.ExceptionExtensions;
using NSubstitute.ReceivedExtensions;
using StackExchange.Redis;
using UiPath.Caching;
using UiPath.Caching.Policies;
using UiPath.Caching.Redis;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Redis;

public class RedisCacheTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();
    private readonly RecordingTelemetryProvider _telemetry = new();
    private ISystemClock _clock = default!;
    private IResiliencePipelineProvider _resiliencePipelineProvider = default!;
    private RedisCacheOptions _cacheOptions = default!;
    private IDatabase _database = default!;
    private ITransaction _transaction = default!;
    private SystemJsonByteSerializerProxy _serializer = default!;
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private CacheKey _cacheKey = default!;
    private RedisKey _redisKey = default!;
    private CacheKey _multiKey = default!;
    private RedisKey _redisMultiKey = default!;
    private ICacheKeyStrategy _cacheKeyStrategy = default!;
    private IRedisKeyStrategy _redisKeyStrategy = default!;
    private string _prefix = default!;
    private IRedisConnector _connector = default!;
    private bool _isConnected = true;
    private Version _version = new(6, 0);
    private RedisCache? _sut = null;

    private RedisCache Sut => _sut ??= _fixture.Create<RedisCache>();

    private int HitCount => _telemetry.Metrics.Count(m => m.Name.Contains(".Hits."));
    private int MissCount => _telemetry.Metrics.Count(m => m.Name.Contains(".Misses."));
    private IEnumerable<DependencyRecord> ReadDeps => _telemetry.Dependencies.Where(d => d.Type == TelemetryScope.DependencyType);

    [Fact]
    public async Task Get_works_as_expected()
    {
        var expectedValue = _fixture.Create<string>();
        _database.StringGetAsync(_redisKey, CommandFlags.PreferMaster)
            .Returns(ci =>
            {
                return _serializer.Serialize(expectedValue);
            });
        var actualValue = await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        actualValue.Should().Be(expectedValue);
        Sut.Name.Should().Be("Redis");
    }

    [Fact]
    public void Reads_go_to_the_master_by_default()
    {
        new RedisCacheOptions().ReadFrom.Should().Be(RedisReadFrom.Master);
        Sut.ReadFlagsFor(_redisKey).Should().Be(CommandFlags.PreferMaster);
    }

    [Fact]
    public void Reads_inside_a_master_scope_demand_the_master()
    {
        _cacheOptions.ReadFrom = RedisReadFrom.Replica;

        using (((IMasterReadScope)Sut).DemandMaster())
        {
            Sut.ReadFlagsFor(_redisKey).Should().Be(CommandFlags.DemandMaster);
            Sut.ReadFlagsFor(new[] { _redisKey }).Should().Be(CommandFlags.DemandMaster);
        }
    }

    [Fact]
    public async Task The_master_scope_flows_across_awaits_and_ends_on_dispose()
    {
        _cacheOptions.ReadFrom = RedisReadFrom.Replica;
        var scope = ((IMasterReadScope)Sut).DemandMaster();
        try
        {
            await Task.Yield();
            Sut.ReadFlagsFor(_redisKey).Should().Be(CommandFlags.DemandMaster);
        }
        finally
        {
            scope.Dispose();
        }

        Sut.ReadFlagsFor(_redisKey).Should().Be(CommandFlags.PreferReplica);
    }

    [Fact]
    public async Task Reads_outside_a_master_scope_keep_their_flags()
    {
        var opened = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        async Task HoldScope()
        {
            using (((IMasterReadScope)Sut).DemandMaster())
            {
                opened.SetResult();
                await release.Task;
            }
        }

        var other = Task.Run(HoldScope, testContextAccessor.Current.CancellationToken);
        await opened.Task;
        try
        {
            Sut.ReadFlagsFor(_redisKey).Should().Be(CommandFlags.PreferMaster);
        }
        finally
        {
            release.SetResult();
            await other;
        }
    }

    [Theory]
    [InlineData("Master", RedisReadFrom.Master)]
    [InlineData("Replica", RedisReadFrom.Replica)]
    [InlineData("replica", RedisReadFrom.Replica)]
    public void ReadFrom_binds_from_configuration_strings(string configured, RedisReadFrom expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ReadFrom"] = configured }).Build();

        configuration.Get<RedisCacheOptions>()!.ReadFrom.Should().Be(expected);
    }

    [Fact]
    public void An_undefined_ReadFrom_is_rejected()
    {
        _cacheOptions.ReadFrom = (RedisReadFrom)7;

        var act = () => Sut;

        act.Should().Throw<Exception>().WithMessage("*ReadFrom has the unsupported value 7*");
    }

    [Fact]
    public void MasterReadWindow_defaults_to_one_second()
    {
        new RedisCacheOptions().MasterReadWindow.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_MasterReadWindow_is_rejected(int seconds)
    {
        _cacheOptions.MasterReadWindow = TimeSpan.FromSeconds(seconds);

        var act = () => Sut;

        act.Should().Throw<Exception>().WithMessage("*MasterReadWindow must be positive*");
    }

    [Fact]
    public async Task A_configured_master_read_window_holds_reads_on_the_master_for_its_length()
    {
        _cacheOptions.ReadFrom = RedisReadFrom.Replica;
        _cacheOptions.MasterReadWindow = TimeSpan.FromSeconds(5);
        StubReads(out var flags);
        StubWrites();
        await Sut.SetAsync<string>(_cacheKey, "a", TimeSpan.FromMinutes(1), policy: null, testContextAccessor.Current.CancellationToken);

        _now += TimeSpan.FromSeconds(5) - TimeSpan.FromMilliseconds(1);
        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        _now += TimeSpan.FromMilliseconds(1);
        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        flags.Should().Equal(CommandFlags.PreferMaster, CommandFlags.PreferReplica);
    }

    [Fact]
    public async Task A_replica_read_right_after_a_TryAdd_that_outlasted_the_window_goes_to_the_master()
    {
        _cacheOptions.ReadFrom = RedisReadFrom.Replica;
        StubReads(out var flags);
        var landed = new TaskCompletionSource<bool>();
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists, Arg.Any<CommandFlags>()).Returns(landed.Task);
        var add = Sut.TryAddAsync<string>(_cacheKey, "a", TimeSpan.FromMinutes(1), policy: null, testContextAccessor.Current.CancellationToken).AsTask();

        _now += RecentTouches.DefaultWindow + RecentTouches.DefaultWindow;
        landed.SetResult(true);
        (await add).Should().BeTrue();
        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        _now += RecentTouches.DefaultWindow;
        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        flags.Should().Equal(CommandFlags.PreferMaster, CommandFlags.PreferReplica);
    }

    [Fact]
    public async Task A_replica_read_right_after_a_batch_remove_that_outlasted_the_window_goes_to_the_master_for_each_key()
    {
        _cacheOptions.ReadFrom = RedisReadFrom.Replica;
        StubReads(out var flags);
        var landed = new TaskCompletionSource<long>();
        _database.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns(landed.Task);
        var remove = Sut.RemoveAsync<string>([_cacheKey, _multiKey], testContextAccessor.Current.CancellationToken).AsTask();

        _now += RecentTouches.DefaultWindow + RecentTouches.DefaultWindow;
        landed.SetResult(2);
        await remove;
        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        await Sut.GetAsync<string>(_multiKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        _now += RecentTouches.DefaultWindow;
        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        flags.Should().Equal(CommandFlags.PreferMaster, CommandFlags.PreferMaster, CommandFlags.PreferReplica);
    }

    [Fact]
    public async Task Master_reads_stay_on_the_master_and_track_nothing()
    {
        _cacheOptions.ReadFrom = RedisReadFrom.Master;
        StubReads(out var flags);
        StubWrites();

        await Sut.SetAsync<string>(_cacheKey, "a", TimeSpan.FromMinutes(1), policy: null, testContextAccessor.Current.CancellationToken);
        await Sut.RemoveAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        ((IInvalidationSink)Sut).Invalidated(_cacheKey);
        ((IInvalidationSink)Sut).InvalidatedAll();
        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        flags.Single().Should().Be(CommandFlags.PreferMaster);
        Sut.RecentTouches.Should().BeNull();
    }

    [Fact]
    public void Master_flag_lookups_and_write_notes_allocate_nothing()
    {
        _cacheOptions.ReadFrom = RedisReadFrom.Master;
        RedisKey[] keys = [_redisKey, _redisMultiKey];
        var sut = Sut;
        var sink = (IInvalidationSink)sut;
        sut.ReadFlagsFor(_redisKey);
        sut.ReadFlagsFor(keys);
        sut.NoteWritten(_redisKey);
        sut.NoteWritten(keys);
        sink.Invalidated(_cacheKey);
        sink.InvalidatedAll();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            sut.ReadFlagsFor(_redisKey);
            sut.ReadFlagsFor(keys);
            sut.NoteWritten(_redisKey);
            sut.NoteWritten(keys);
            sink.Invalidated(_cacheKey);
            sink.InvalidatedAll();
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    [Fact]
    public async Task A_replica_read_of_an_untouched_key_goes_to_a_replica()
    {
        _cacheOptions.ReadFrom = RedisReadFrom.Replica;
        StubReads(out var flags);

        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        flags.Single().Should().Be(CommandFlags.PreferReplica);
    }

    [Fact]
    public async Task A_replica_read_inside_the_window_after_a_set_goes_to_the_master_and_after_it_to_a_replica()
    {
        _cacheOptions.ReadFrom = RedisReadFrom.Replica;
        StubReads(out var flags);
        StubWrites();
        await Sut.SetAsync<string>(_cacheKey, "a", TimeSpan.FromMinutes(1), policy: null, testContextAccessor.Current.CancellationToken);

        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        _now += RecentTouches.DefaultWindow - TimeSpan.FromMilliseconds(1);
        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        _now += TimeSpan.FromMilliseconds(1);
        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        flags.Should().Equal(CommandFlags.PreferMaster, CommandFlags.PreferMaster, CommandFlags.PreferReplica);
    }

    [Fact]
    public async Task A_replica_read_right_after_a_set_that_outlasted_the_window_goes_to_the_master()
    {
        _cacheOptions.ReadFrom = RedisReadFrom.Replica;
        StubReads(out var flags);
        var landed = new TaskCompletionSource<bool>();
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(landed.Task);
        var set = Sut.SetAsync<string>(_cacheKey, "a", TimeSpan.FromMinutes(1), policy: null, testContextAccessor.Current.CancellationToken).AsTask();

        _now += RecentTouches.DefaultWindow + RecentTouches.DefaultWindow;
        landed.SetResult(true);
        await set;
        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        _now += RecentTouches.DefaultWindow;
        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        flags.Should().Equal(CommandFlags.PreferMaster, CommandFlags.PreferReplica);
    }

    [Fact]
    public async Task A_replica_read_after_a_refresh_goes_to_the_master_until_the_window_passes()
    {
        _cacheOptions.ReadFrom = RedisReadFrom.Replica;
        StubReads(out var flags);
        _database.KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), Arg.Any<CommandFlags>()).Returns(true);
        await Sut.RefreshAsync<string>(_cacheKey, TimeSpan.FromMinutes(1), policy: null, testContextAccessor.Current.CancellationToken);

        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        _now += RecentTouches.DefaultWindow;
        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        flags.Should().Equal(CommandFlags.PreferMaster, CommandFlags.PreferReplica);
    }

    [Fact]
    public async Task A_replica_read_inside_the_window_after_a_remove_goes_to_the_master_and_after_it_to_a_replica()
    {
        _cacheOptions.ReadFrom = RedisReadFrom.Replica;
        StubReads(out var flags);
        StubWrites();
        await Sut.RemoveAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);

        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        _now += RecentTouches.DefaultWindow;
        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        flags.Should().Equal(CommandFlags.PreferMaster, CommandFlags.PreferReplica);
    }

    [Fact]
    public async Task A_set_of_another_key_leaves_a_replica_read_on_a_replica()
    {
        _cacheOptions.ReadFrom = RedisReadFrom.Replica;
        StubReads(out var flags);
        StubWrites();
        await Sut.SetAsync<string>(_multiKey, "a", TimeSpan.FromMinutes(1), policy: null, testContextAccessor.Current.CancellationToken);

        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        flags.Single().Should().Be(CommandFlags.PreferReplica);
    }

    [Fact]
    public async Task A_replica_read_after_a_batch_set_of_the_key_goes_to_the_master()
    {
        _cacheOptions.ReadFrom = RedisReadFrom.Replica;
        StubReads(out var flags);
        StubWrites();
        await Sut.SetAsync<string>([new(_cacheKey, "a")], TimeSpan.FromMinutes(1), policy: null, testContextAccessor.Current.CancellationToken);

        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        flags.Single().Should().Be(CommandFlags.PreferMaster);
    }

    [Fact]
    public async Task A_replica_read_after_a_received_invalidation_goes_to_the_master_until_the_window_passes()
    {
        _cacheOptions.ReadFrom = RedisReadFrom.Replica;
        StubReads(out var flags);
        ((IInvalidationSink)Sut).Invalidated(_cacheKey);

        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        await Sut.GetAsync<string>(_multiKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        _now += RecentTouches.DefaultWindow;
        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        flags.Should().Equal(CommandFlags.PreferMaster, CommandFlags.PreferReplica, CommandFlags.PreferReplica);
    }

    [Fact]
    public async Task A_replica_batch_read_with_one_guarded_key_goes_to_the_master()
    {
        _cacheOptions.ReadFrom = RedisReadFrom.Replica;
        var batchFlags = new List<CommandFlags>();
        _database.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Do<CommandFlags>(batchFlags.Add)).Returns(_ => new RedisValue[] { _serializer.Serialize("a"), _serializer.Serialize("b") });
        var keys = new CacheKey[] { _cacheKey, _multiKey };

        await Sut.GetAsync<string>(keys, policy: null, token: testContextAccessor.Current.CancellationToken);
        ((IInvalidationSink)Sut).Invalidated(_multiKey);
        await Sut.GetAsync<string>(keys, policy: null, token: testContextAccessor.Current.CancellationToken);
        _now += RecentTouches.DefaultWindow;
        await Sut.GetAsync<string>(keys, policy: null, token: testContextAccessor.Current.CancellationToken);

        batchFlags.Should().Equal(CommandFlags.PreferReplica, CommandFlags.PreferMaster, CommandFlags.PreferReplica);
    }

    [Fact]
    public async Task A_replica_read_after_a_clear_goes_to_the_master_for_every_key_until_the_window_passes()
    {
        _cacheOptions.ReadFrom = RedisReadFrom.Replica;
        StubReads(out var flags);
        ((IInvalidationSink)Sut).InvalidatedAll();

        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        await Sut.GetAsync<string>(_multiKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        _now += RecentTouches.DefaultWindow;
        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        flags.Should().Equal(CommandFlags.PreferMaster, CommandFlags.PreferMaster, CommandFlags.PreferReplica);
    }

    [Fact]
    public async Task A_replica_read_goes_to_the_master_for_every_key_while_the_tracker_is_full()
    {
        _cacheOptions.ReadFrom = RedisReadFrom.Replica;
        StubReads(out var flags);
        for (var i = 0; i <= RecentTouches.MaxKeys; i++)
        {
            Sut.NoteWritten((RedisKey)$"filler:{i}");
        }

        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        _now += RecentTouches.DefaultWindow;
        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        flags.Should().Equal(CommandFlags.PreferMaster, CommandFlags.PreferReplica);
    }

    [Fact]
    public async Task Get_works_as_expected_when_disconnected()
    {
        _isConnected = false;
        var value = _fixture.Create<string>();
        _database.StringGetAsync(_redisKey, CommandFlags.PreferMaster)
            .Returns(ci =>
            {
                return _serializer.Serialize(value);
            });
        var actualValue = await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        actualValue.Should().Be(null);
    }

    [Fact]
    public async Task Get_has_no_redis_exceptions()
    {
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisException("test"));
        var actualValue = await Sut.GetAsync<int?>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        actualValue.Should().Be(default(int?));
    }

    [Fact]
    public async Task Multi_get_works_as_expected()
    {
        var expectedValue = _fixture.Create<string>();
        _database.StringGetAsync(Arg.Is<RedisKey[]>(k => k != null && k.Contains(_redisKey) && k.Contains(_redisMultiKey)), CommandFlags.PreferMaster)
            .Returns(ci =>
            {
                return new RedisValue[] { _serializer.Serialize(expectedValue), _serializer.Serialize(expectedValue) };
            });
        var actualValue = await Sut.GetAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);
        actualValue.Should().BeEquivalentTo(new KeyValuePair<CacheKey, string>[] { new(_cacheKey, expectedValue), new(_multiKey, expectedValue) });
    }

    [Fact]
    public async Task Multi_get_reads_each_slot_with_its_own_command()
    {
        GiveKeysDifferentSlots();
        var (first, second) = (_fixture.Create<string>(), _fixture.Create<string>());
        _database.StringGetAsync(Arg.Is<RedisKey[]>(k => k.SequenceEqual(new[] { _redisKey })), CommandFlags.PreferMaster)
            .Returns(new RedisValue[] { _serializer.Serialize(first) });
        _database.StringGetAsync(Arg.Is<RedisKey[]>(k => k.SequenceEqual(new[] { _redisMultiKey })), CommandFlags.PreferMaster)
            .Returns(new RedisValue[] { _serializer.Serialize(second) });

        var actual = await Sut.GetAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Should().Equal(new KeyValuePair<CacheKey, string?>(_cacheKey, first), new KeyValuePair<CacheKey, string?>(_multiKey, second));
        await _database.Received(2).StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
        HitCount.Should().Be(1);
        _telemetry.Metrics.Should().ContainSingle(m => m.Name.Contains(".Hits.") && m.Properties![TelemetryScope.KeysTag] == "2");
    }

    [Fact]
    public async Task Multi_get_keeps_the_slots_that_answered_when_one_does_not()
    {
        GiveKeysDifferentSlots();
        var first = _fixture.Create<string>();
        _database.StringGetAsync(Arg.Is<RedisKey[]>(k => k.SequenceEqual(new[] { _redisKey })), CommandFlags.PreferMaster)
            .Returns(new RedisValue[] { _serializer.Serialize(first) });
        _database.StringGetAsync(Arg.Is<RedisKey[]>(k => k.SequenceEqual(new[] { _redisMultiKey })), CommandFlags.PreferMaster)
            .Returns(Array.Empty<RedisValue>());

        var actual = await Sut.GetAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Should().Equal(new KeyValuePair<CacheKey, string?>(_cacheKey, first), new KeyValuePair<CacheKey, string?>(_multiKey, null));
    }

    [Fact]
    public async Task Multi_get_does_not_split_keys_the_key_prefix_puts_in_one_slot()
    {
        // KeyPrefix mirrors the prefix the application's connector applies with WithKeyPrefix, so a hash
        // tag there is what the server hashes; the composed keys alone say nothing about the slot.
        _cacheOptions.KeyPrefix = "{tag}:";
        GiveKeysDifferentSlots();
        _database.Multiplexer.GetHashSlot(_redisKey.Prepend(_cacheOptions.KeyPrefix)).Returns(7);
        _database.Multiplexer.GetHashSlot(_redisMultiKey.Prepend(_cacheOptions.KeyPrefix)).Returns(7);
        var expectedValue = _fixture.Create<string>();
        _database.StringGetAsync(Arg.Is<RedisKey[]>(k => k != null && k.Contains(_redisKey) && k.Contains(_redisMultiKey)), CommandFlags.PreferMaster)
            .Returns(_ => new RedisValue[] { _serializer.Serialize(expectedValue), _serializer.Serialize(expectedValue) });

        var actualValue = await Sut.GetAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        actualValue.Should().BeEquivalentTo(new KeyValuePair<CacheKey, string>[] { new(_cacheKey, expectedValue), new(_multiKey, expectedValue) });
        await _database.Received(1).StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Multi_get_hashes_the_prefix_the_connector_applies_when_KeyPrefix_is_not_set()
    {
        _cacheOptions.KeyPrefix = string.Empty;
        _database.ExecuteAsync("ECHO", Arg.Any<ICollection<object>>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)("{tag}:" + ConnectorKeyPrefix.ProbeMarker)));
        GiveKeysDifferentSlots();
        _database.Multiplexer.GetHashSlot(_redisKey.Prepend("{tag}:")).Returns(7);
        _database.Multiplexer.GetHashSlot(_redisMultiKey.Prepend("{tag}:")).Returns(7);
        _database.StringGetAsync(Arg.Any<RedisKey[]>(), CommandFlags.PreferMaster)
            .Returns(ci => new RedisValue[ci.Arg<RedisKey[]>().Length]);

        await Sut.GetAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        await _database.Received(1).StringGetAsync(Arg.Is<RedisKey[]>(k => k.Length == 2), CommandFlags.PreferMaster);
    }

    [Fact]
    public async Task A_multi_get_does_not_wait_for_a_connector_probe_still_in_flight()
    {
        // The slot grouping runs outside the pipeline's latency cap, so the configured prefix stands in rather than a wait;
        // a wait bounded by Timeout would take the full minute.
        _cacheOptions.KeyPrefix = "{tag}:";
        _cacheOptions.Timeout = TimeSpan.FromMinutes(1);
        _database.ExecuteAsync("ECHO", Arg.Any<ICollection<object>>(), Arg.Any<CommandFlags>())
            .Returns(new TaskCompletionSource<RedisResult>().Task);
        GiveKeysDifferentSlots();
        _database.Multiplexer.GetHashSlot(_redisKey.Prepend("{tag}:")).Returns(7);
        _database.Multiplexer.GetHashSlot(_redisMultiKey.Prepend("{tag}:")).Returns(7);
        _database.StringGetAsync(Arg.Any<RedisKey[]>(), CommandFlags.PreferMaster)
            .Returns(_ => new RedisValue[] { RedisValue.Null, RedisValue.Null });

        var act = () => Sut.GetAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken).AsTask();

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await act.Should().NotThrowAsync();
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_fallback_prefix_is_not_kept_while_the_connector_probe_can_still_answer()
    {
        _cacheOptions.KeyPrefix = string.Empty;
        _database.ExecuteAsync("ECHO", Arg.Any<ICollection<object>>(), Arg.Any<CommandFlags>())
            .Returns(
                _ => Task.FromException<RedisResult>(new RedisException("not yet")),
                _ => Task.FromResult(RedisResult.Create((RedisValue)("{tag}:" + ConnectorKeyPrefix.ProbeMarker))));
        GiveKeysDifferentSlots();
        _database.Multiplexer.GetHashSlot(_redisKey.Prepend("{tag}:")).Returns(7);
        _database.Multiplexer.GetHashSlot(_redisMultiKey.Prepend("{tag}:")).Returns(7);
        _database.StringGetAsync(Arg.Any<RedisKey[]>(), CommandFlags.PreferMaster)
            .Returns(ci => new RedisValue[ci.Arg<RedisKey[]>().Length]);
        var keys = new CacheKey[] { _cacheKey, _multiKey };

        await Sut.GetAsync<string>(keys, policy: null, token: testContextAccessor.Current.CancellationToken);
        await _database.Received(2).StringGetAsync(Arg.Is<RedisKey[]>(k => k.Length == 1), CommandFlags.PreferMaster);

        await Sut.GetAsync<string>(keys, policy: null, token: testContextAccessor.Current.CancellationToken);
        await _database.Received(1).StringGetAsync(Arg.Is<RedisKey[]>(k => k.Length == 2), CommandFlags.PreferMaster);
    }

    [Fact]
    public async Task Multi_get_splits_keys_the_key_prefix_puts_in_different_slots()
    {
        _cacheOptions.KeyPrefix = "prefix:";
        _database.Multiplexer.GetHashSlot(_redisKey.Prepend(_cacheOptions.KeyPrefix)).Returns(1);
        _database.Multiplexer.GetHashSlot(_redisMultiKey.Prepend(_cacheOptions.KeyPrefix)).Returns(2);
        _database.StringGetAsync(Arg.Any<RedisKey[]>(), CommandFlags.PreferMaster)
            .Returns(ci => new RedisValue[ci.Arg<RedisKey[]>().Length]);

        await Sut.GetAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        await _database.Received(2).StringGetAsync(Arg.Is<RedisKey[]>(k => k.Length == 1), CommandFlags.PreferMaster);
    }

    [Fact]
    public async Task Multi_get_cache_entries_reads_each_slot_in_its_own_transaction()
    {
        GiveKeysDifferentSlots();
        var (first, second) = (_fixture.Create<string>(), _fixture.Create<string>());
        _transaction.StringGetAsync(Arg.Is<RedisKey[]>(k => k.SequenceEqual(new[] { _redisKey })), CommandFlags.PreferMaster)
            .Returns(new RedisValue[] { _serializer.Serialize(first) });
        _transaction.StringGetAsync(Arg.Is<RedisKey[]>(k => k.SequenceEqual(new[] { _redisMultiKey })), CommandFlags.PreferMaster)
            .Returns(new RedisValue[] { _serializer.Serialize(second) });
        _transaction.KeyTimeToLiveAsync(_redisKey, CommandFlags.PreferMaster).Returns(TimeSpan.FromMinutes(5));
        _transaction.KeyTimeToLiveAsync(_redisMultiKey, CommandFlags.PreferMaster).Returns(TimeSpan.FromMinutes(10));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var actual = await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Select(e => (e.Key, e.Value.Value, e.Value.Expiration)).Should().Equal(
            (_cacheKey, first, _now.AddMinutes(5)),
            (_multiKey, second, _now.AddMinutes(10)));
        _database.Received(2).CreateTransaction();
    }

    [Fact]
    public async Task Multi_get_cache_entries_keeps_the_slots_that_committed_when_one_does_not()
    {
        GiveKeysDifferentSlots();
        var first = _fixture.Create<string>();
        _transaction.StringGetAsync(Arg.Any<RedisKey[]>(), CommandFlags.PreferMaster)
            .Returns(new RedisValue[] { _serializer.Serialize(first) });
        _transaction.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), CommandFlags.PreferMaster).Returns(TimeSpan.FromMinutes(5));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true, false);

        var actual = await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Select(e => (e.Key, e.Value.Found)).Should().Equal((_cacheKey, true), (_multiKey, false));
    }

    [Fact]
    public async Task Multi_remove_deletes_each_slot_with_its_own_command()
    {
        GiveKeysDifferentSlots();
        _database.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns(1L);

        var removed = await Sut.RemoveAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, testContextAccessor.Current.CancellationToken);

        removed.Should().BeTrue();
        await _database.Received(1).KeyDeleteAsync(Arg.Is<RedisKey[]>(k => k.SequenceEqual(new[] { _redisKey })), CommandFlags.DemandMaster);
        await _database.Received(1).KeyDeleteAsync(Arg.Is<RedisKey[]>(k => k.SequenceEqual(new[] { _redisMultiKey })), CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task Multi_remove_reports_failure_when_one_slot_fails()
    {
        GiveKeysDifferentSlots();
        _database.KeyDeleteAsync(Arg.Is<RedisKey[]>(k => k.SequenceEqual(new[] { _redisKey })), Arg.Any<CommandFlags>()).Returns(1L);
        _database.KeyDeleteAsync(Arg.Is<RedisKey[]>(k => k.SequenceEqual(new[] { _redisMultiKey })), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisException("test"));

        var removed = await Sut.RemoveAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, testContextAccessor.Current.CancellationToken);

        removed.Should().BeFalse();
        await _database.Received(1).KeyDeleteAsync(Arg.Is<RedisKey[]>(k => k.SequenceEqual(new[] { _redisKey })), CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task Multi_set_writes_each_slot_in_its_own_transaction()
    {
        GiveKeysDifferentSlots();
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);
        var (first, second) = (_fixture.Create<string>(), _fixture.Create<string>());

        var stored = await Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, first), new(_multiKey, second) }, _fixture.Create<TimeSpan>(), policy: null, token: testContextAccessor.Current.CancellationToken);

        stored.Should().BeTrue();
        _database.Received(2).CreateTransaction(Arg.Any<object?>());
        await _transaction.Received(2).ExecuteAsync(CommandFlags.DemandMaster);
        await _transaction.Received(1).StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.Always, CommandFlags.DemandMaster);
        await _transaction.Received(1).StringSetAsync(_redisMultiKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.Always, CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task Multi_get_keeps_the_slots_that_answered_when_one_throws()
    {
        GiveKeysDifferentSlots();
        var first = _fixture.Create<string>();
        _database.StringGetAsync(Arg.Is<RedisKey[]>(k => k.SequenceEqual(new[] { _redisKey })), CommandFlags.PreferMaster)
            .Returns(new RedisValue[] { _serializer.Serialize(first) });
        _database.StringGetAsync(Arg.Is<RedisKey[]>(k => k.SequenceEqual(new[] { _redisMultiKey })), CommandFlags.PreferMaster)
            .ThrowsAsync(new RedisException("test"));

        var actual = await Sut.GetAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Should().Equal(new KeyValuePair<CacheKey, string?>(_cacheKey, first), new KeyValuePair<CacheKey, string?>(_multiKey, null));
    }

    [Fact]
    public async Task A_multi_set_whose_second_slot_fails_to_start_returns_only_after_the_first_slot_has_written()
    {
        GiveKeysDifferentSlots();
        var written = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failing = Substitute.For<ITransaction>();
        failing.When(t => t.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>()))
            .Do(_ => throw new RedisException("test"));
        _database.CreateTransaction(Arg.Any<object?>()).Returns(_transaction, failing);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(written.Task);
        var value = _fixture.Create<string>();

        var set = Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, value), new(_multiKey, value) }, _fixture.Create<TimeSpan>(), policy: null, token: testContextAccessor.Current.CancellationToken).AsTask();
        await Task.Delay(100, testContextAccessor.Current.CancellationToken);

        set.IsCompleted.Should().BeFalse("the first slot's write still holds the serialized payload");
        written.SetResult(true);
        (await set).Should().BeFalse();
    }

    [Fact]
    public async Task Multi_set_reports_failure_when_one_slot_does_not_commit()
    {
        GiveKeysDifferentSlots();
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true, false);
        var value = _fixture.Create<string>();

        var stored = await Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, value), new(_multiKey, value) }, _fixture.Create<TimeSpan>(), policy: null, token: testContextAccessor.Current.CancellationToken);

        stored.Should().BeFalse();
        await _transaction.Received(2).ExecuteAsync(CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task Multi_get_still_reports_a_miss_when_the_connection_cannot_be_resolved()
    {
        // The slot check reads the multiplexer, so a Redis that cannot be reached must not turn a
        // graceful miss into a thrown connection error.
        _connector.Database.Throws(new RedisException("test"));
        var actual = await Sut.GetAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);
        actual.Should().BeEquivalentTo(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, null), new(_multiKey, null) });
    }

    [Fact]
    public async Task Multi_get_has_no_redis_exceptions()
    {
        _database.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisException("test"));
        var actualValue = await Sut.GetAsync<int?>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);
        actualValue.Should().BeEquivalentTo(new KeyValuePair<CacheKey, int?>[] { new(_cacheKey, default), new(_multiKey, default) });
    }

    [Fact]
    public async Task Multi_get_returns_defaults_without_redis_or_telemetry_when_disconnected()
    {
        _isConnected = false;

        var actual = await Sut.GetAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Should().BeEquivalentTo(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, default), new(_multiKey, default) });
        await _database.DidNotReceive().StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
        HitCount.Should().Be(0);
        MissCount.Should().Be(0);
    }

    [Fact]
    public async Task Single_get_tracks_one_hit_for_the_key()
    {
        _database.StringGetAsync(_redisKey, CommandFlags.PreferMaster)
            .Returns(ci => _serializer.Serialize(_fixture.Create<string>()));

        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        HitCount.Should().Be(1);
        MissCount.Should().Be(0);
    }

    [Fact]
    public async Task Single_get_tracks_one_miss_for_the_key()
    {
        _database.StringGetAsync(_redisKey, CommandFlags.PreferMaster)
            .Returns(RedisValue.Null);

        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        HitCount.Should().Be(0);
        MissCount.Should().Be(1);
    }

    [Fact]
    public async Task Multi_get_emits_one_metric_per_operation_with_key_count()
    {
        _database.StringGetAsync(Arg.Is<RedisKey[]>(k => k != null && k.Contains(_redisKey) && k.Contains(_redisMultiKey)), CommandFlags.PreferMaster)
            .Returns(new RedisValue[] { _serializer.Serialize(_fixture.Create<string>()), RedisValue.Null });

        await Sut.GetAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        HitCount.Should().Be(1);
        MissCount.Should().Be(0);
        _telemetry.Metrics.Should().ContainSingle(m => m.Name.Contains(".Hits.") && m.Properties![TelemetryScope.KeysTag] == "2");
    }

    [Fact]
    public async Task Multi_get_emits_one_miss_metric_when_no_values_returned()
    {
        _database.StringGetAsync(Arg.Any<RedisKey[]>(), CommandFlags.PreferMaster)
            .Returns(Array.Empty<RedisValue>());

        await Sut.GetAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        HitCount.Should().Be(0);
        MissCount.Should().Be(1);
        _telemetry.Metrics.Should().ContainSingle(m => m.Name.Contains(".Misses.") && m.Properties![TelemetryScope.KeysTag] == "2");
    }

    [Fact]
    public async Task Multi_get_returns_defaults_when_value_array_is_partial()
    {
        _database.StringGetAsync(Arg.Is<RedisKey[]>(k => k != null && k.Contains(_redisKey) && k.Contains(_redisMultiKey)), CommandFlags.PreferMaster)
            .Returns(new RedisValue[] { _serializer.Serialize(_fixture.Create<string>()) });

        var actual = await Sut.GetAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        actual.Should().BeEquivalentTo(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, default), new(_multiKey, default) });
        HitCount.Should().Be(0);
        MissCount.Should().Be(1);
    }

    [Fact]
    public async Task Multi_set_metric_carries_the_key_count()
    {
        _transaction.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        await Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, "a"), new(_multiKey, "b") }, policy: null, token: testContextAccessor.Current.CancellationToken);

        _telemetry.Metrics.Should().Contain(m => m.Name.Contains(".Hits.") && m.Properties![TelemetryScope.KeysTag] == "2");
    }

    [Fact]
    public async Task Multi_get_emits_one_miss_metric_on_redis_exception()
    {
        _database.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisException("test"));

        await Sut.GetAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        HitCount.Should().Be(0);
        MissCount.Should().Be(1);
    }

    [Fact]
    public async Task GetCacheEntries_emits_one_metric_per_operation_with_key_count()
    {
        _transaction.StringGetAsync(Arg.Is<RedisKey[]>(k => k != null && k.Contains(_redisKey) && k.Contains(_redisMultiKey)), CommandFlags.PreferMaster)
            .Returns(new RedisValue[] { _serializer.Serialize(_fixture.Create<string>()), RedisValue.Null });
        _transaction.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), CommandFlags.PreferMaster)
            .Returns(TimeSpan.FromMinutes(15));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        HitCount.Should().Be(1);
        MissCount.Should().Be(0);
        _telemetry.Metrics.Should().ContainSingle(m => m.Name.Contains(".Hits.") && m.Properties![TelemetryScope.KeysTag] == "2");
    }

    [Fact]
    public async Task GetCacheEntries_emits_one_miss_metric_on_transaction_failure()
    {
        _transaction.StringGetAsync(Arg.Any<RedisKey[]>(), CommandFlags.PreferMaster)
            .Returns(new RedisValue[] { _serializer.Serialize(_fixture.Create<string>()), _serializer.Serialize(_fixture.Create<string>()) });
        _transaction.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), CommandFlags.PreferMaster)
            .Returns(TimeSpan.FromMinutes(15));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(false);

        await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        HitCount.Should().Be(0);
        MissCount.Should().Be(1);
    }

    [Fact]
    public async Task GetCacheEntries_emits_one_miss_metric_on_redis_exception()
    {
        _transaction.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisException("test"));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        HitCount.Should().Be(0);
        MissCount.Should().Be(1);
    }

    [Fact]
    public async Task Multi_get_does_not_carry_keys_by_default()
    {
        _database.StringGetAsync(Arg.Is<RedisKey[]>(k => k != null && k.Contains(_redisKey) && k.Contains(_redisMultiKey)), CommandFlags.PreferMaster)
            .Returns(new RedisValue[] { _serializer.Serialize(_fixture.Create<string>()), RedisValue.Null });

        await Sut.GetAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        ReadDeps.Should().BeEmpty();
    }

    [Fact]
    public async Task Multi_get_carries_key_per_hit_and_miss_when_opt_in()
    {
        _cacheOptions.KeyReadTelemetryEnabled = true;
        _database.StringGetAsync(Arg.Is<RedisKey[]>(k => k != null && k.Contains(_redisKey) && k.Contains(_redisMultiKey)), CommandFlags.PreferMaster)
            .Returns(new RedisValue[] { _serializer.Serialize(_fixture.Create<string>()), RedisValue.Null });

        await Sut.GetAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        ReadDeps.Should().Contain(d => d.Data == _redisKey.ToString() && d.Success && d.ResultCode == "Hit");
        ReadDeps.Should().Contain(d => d.Data == _redisMultiKey.ToString() && d.Success && d.ResultCode == "Miss");
    }

    [Fact]
    public async Task GetCacheEntries_carries_key_per_hit_and_miss_when_opt_in()
    {
        _cacheOptions.KeyReadTelemetryEnabled = true;
        _transaction.StringGetAsync(Arg.Is<RedisKey[]>(k => k != null && k.Contains(_redisKey) && k.Contains(_redisMultiKey)), CommandFlags.PreferMaster)
            .Returns(new RedisValue[] { _serializer.Serialize(_fixture.Create<string>()), RedisValue.Null });
        _transaction.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), CommandFlags.PreferMaster)
            .Returns(TimeSpan.FromMinutes(15));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        ReadDeps.Should().Contain(d => d.Data == _redisKey.ToString() && d.Success && d.ResultCode == "Hit");
        ReadDeps.Should().Contain(d => d.Data == _redisMultiKey.ToString() && d.Success && d.ResultCode == "Miss");
    }

    [Fact]
    public async Task Single_get_carries_key_when_opt_in()
    {
        _cacheOptions.KeyReadTelemetryEnabled = true;
        _database.StringGetAsync(_redisKey, CommandFlags.PreferMaster)
            .Returns(ci => _serializer.Serialize(_fixture.Create<string>()));

        await Sut.GetAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        ReadDeps.Should().ContainSingle(d => d.Data == _redisKey.ToString() && d.Success && d.ResultCode == "Hit");
    }

    [Fact]
    public async Task GetCacheEntry_bundles_get_and_pttl_in_single_transaction()
    {
        var expectedValue = _fixture.Create<string>();
        var expectedTtl = TimeSpan.FromMinutes(15);
        _transaction.StringGetAsync(_redisKey, CommandFlags.PreferMaster)
            .Returns(_serializer.Serialize(expectedValue));
        _transaction.KeyTimeToLiveAsync(_redisKey, CommandFlags.PreferMaster)
            .Returns(expectedTtl);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var entry = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        entry.Value.Should().Be(expectedValue);
        entry.Expiration.Should().Be(_clock.UtcNow.Add(expectedTtl));
        // Bundled in transaction; no separate StringGetAsync against the database directly.
        await _database.DidNotReceive().StringGetAsync(_redisKey, Arg.Any<CommandFlags>());
        await _database.DidNotReceive().KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>());
        await _database.DidNotReceive().KeyExpireTimeAsync(_redisKey, Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task GetCacheEntry_rebuilds_the_transaction_when_the_read_is_retried()
    {
        _resiliencePipelineProvider.Get(ResiliencePipelineNames.Read).Returns(new RetryOnceResiliencePipeline());
        var expected = _fixture.Create<string>();
        var retried = Substitute.For<ITransaction>();
        _database.CreateTransaction().Returns(_transaction, retried);
        _transaction.StringGetAsync(_redisKey, CommandFlags.PreferMaster).Returns(Task.FromException<RedisValue>(new RedisException("lost")));
        _transaction.KeyTimeToLiveAsync(_redisKey, CommandFlags.PreferMaster).Returns(Task.FromException<TimeSpan?>(new RedisException("lost")));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(Task.FromException<bool>(new RedisException("timed out")), Task.FromResult(true));
        retried.StringGetAsync(_redisKey, CommandFlags.PreferMaster).Returns(_serializer.Serialize(expected));
        retried.KeyTimeToLiveAsync(_redisKey, CommandFlags.PreferMaster).Returns(TimeSpan.FromMinutes(15));
        retried.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var entry = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        entry.Value.Should().Be(expected);
    }

    [Fact]
    public async Task Transactional_reads_run_the_read_pipeline_as_bool()
    {
        // One breaker per result type, so any other type would trip on its own.
        var pipeline = new RecordingResiliencePipeline();
        _resiliencePipelineProvider.Get(ResiliencePipelineNames.Read).Returns(pipeline);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        pipeline.ResultTypes.Should().OnlyContain(t => t == typeof(bool)).And.HaveCount(2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failed_transaction_leaves_no_unobserved_result(bool throws)
    {
        var marker = "unobserved-" + Guid.NewGuid().ToString("N");
        var unobserved = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions.Any(ex => ex.Message == marker))
            {
                Interlocked.Increment(ref unobserved);
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await ReadThroughAFailingTransactionAsync(marker, throws);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        unobserved.Should().Be(0);
    }

    [Fact]
    public async Task GetCacheEntries_rebuilds_the_transaction_when_the_read_is_retried()
    {
        _resiliencePipelineProvider.Get(ResiliencePipelineNames.Read).Returns(new RetryOnceResiliencePipeline());
        var expected = _fixture.Create<string>();
        var retried = Substitute.For<ITransaction>();
        _database.CreateTransaction().Returns(_transaction, retried);
        _transaction.StringGetAsync(Arg.Any<RedisKey[]>(), CommandFlags.PreferMaster).Returns(Task.FromException<RedisValue[]>(new RedisException("lost")));
        _transaction.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), CommandFlags.PreferMaster).Returns(Task.FromException<TimeSpan?>(new RedisException("lost")));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(Task.FromException<bool>(new RedisException("timed out")), Task.FromResult(true));
        retried.StringGetAsync(Arg.Any<RedisKey[]>(), CommandFlags.PreferMaster).Returns(new RedisValue[] { _serializer.Serialize(expected), RedisValue.Null });
        retried.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), CommandFlags.PreferMaster).Returns(TimeSpan.FromMinutes(15));
        retried.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var entries = await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        entries[0].Value.Value.Should().Be(expected);
    }

    [Fact]
    public async Task GetCacheEntry_reads_value_and_expire_time_in_one_script_on_redis_v7()
    {
        _version = new(7, 0);
        var expectedValue = _fixture.Create<string>();
        var expectedExpiration = DateTimeOffset.UtcNow.AddMinutes(15);
        StubReadScript(_serializer.Serialize(expectedValue), expectedExpiration.ToUnixTimeMilliseconds());

        var entry = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        entry.Value.Should().Be(expectedValue);
        entry.Expiration.Should().BeCloseTo(expectedExpiration, TimeSpan.FromMilliseconds(1));
        _database.DidNotReceive().CreateTransaction();
    }

    [Fact]
    public async Task GetCacheEntry_misses_when_the_script_finds_no_key_v7()
    {
        _version = new(7, 0);
        _database.ScriptEvaluateReadOnlyAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create(RedisValue.Null));

        var entry = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        entry.Found.Should().BeFalse();
        MissCount.Should().Be(1);
    }

    [Fact]
    public async Task GetCacheEntry_falls_back_to_a_transaction_for_good_when_scripts_are_denied_v7()
    {
        _version = new(7, 0);
        var expectedValue = _fixture.Create<string>();
        var expectedExpiration = DateTimeOffset.UtcNow.AddMinutes(15);
        _database.ScriptEvaluateReadOnlyAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "NOPERM this user has no permissions to run the 'eval_ro' command"));
        _transaction.StringGetAsync(_redisKey, CommandFlags.PreferMaster)
            .Returns(_serializer.Serialize(expectedValue));
        _transaction.KeyExpireTimeAsync(_redisKey, CommandFlags.PreferMaster)
            .Returns(expectedExpiration.UtcDateTime);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var first = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        var second = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        first.Value.Should().Be(expectedValue);
        second.Value.Should().Be(expectedValue);
        first.Expiration.Should().BeCloseTo(expectedExpiration, TimeSpan.FromSeconds(1));
        await _database.Received(1).ScriptEvaluateReadOnlyAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>());
        _database.Received(2).CreateTransaction();
    }

    [Fact]
    public void Read_scripts_are_on_by_default() => new RedisCacheOptions().ReadScriptsEnabled.Should().BeTrue();

    /// <summary>The default never sends a script to a server without EVAL_RO, so it is safe before Redis 7.</summary>
    [Fact]
    public async Task GetCacheEntry_uses_a_transaction_before_redis_v7_by_default()
    {
        _version = new(6, 2);
        _transaction.StringGetAsync(_redisKey, CommandFlags.PreferMaster).Returns(RedisValue.Null);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        (await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken)).Found.Should().BeFalse();

        _database.Received(1).CreateTransaction();
        await _database.DidNotReceive().ScriptEvaluateReadOnlyAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task GetCacheEntry_uses_a_transaction_when_read_scripts_are_turned_off_v7()
    {
        _version = new(7, 0);
        _cacheOptions.ReadScriptsEnabled = false;
        _transaction.StringGetAsync(_redisKey, CommandFlags.PreferMaster).Returns(RedisValue.Null);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        (await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken)).Found.Should().BeFalse();

        _database.Received(1).CreateTransaction();
        await _database.DidNotReceive().ScriptEvaluateReadOnlyAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task GetCacheEntry_keeps_scripts_after_a_timeout_once_a_script_has_worked_v7()
    {
        _version = new(7, 0);
        var expectedValue = _fixture.Create<string>();
        var hit = RedisResult.Create([RedisResult.Create((RedisValue)_serializer.Serialize(expectedValue)), RedisResult.Create((RedisValue)(-1))]);
        _database.ScriptEvaluateReadOnlyAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
            .Returns(
                Task.FromResult(hit),
                Task.FromException<RedisResult>(new RedisTimeoutException(CommandFlags.None, "timed out", CommandStatus.Sent)),
                Task.FromResult(hit));

        var first = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        var second = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        var third = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        first.Value.Should().Be(expectedValue);
        second.Found.Should().BeFalse();
        third.Value.Should().Be(expectedValue);
        _database.DidNotReceive().CreateTransaction();
    }

    [Theory]
    [InlineData("ERR unsupported by this proxy")]
    [InlineData("timeout")]
    public async Task GetCacheEntry_falls_back_to_a_transaction_for_good_when_the_first_script_fails_for_any_reason_v7(string failure)
    {
        _version = new(7, 0);
        var expectedValue = _fixture.Create<string>();
        Exception error = failure == "timeout"
            ? new RedisTimeoutException(CommandFlags.None, "timed out", CommandStatus.Sent)
            : new RedisServerException(RedisErrorKind.None, CommandFlags.None, failure);
        _database.ScriptEvaluateReadOnlyAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(error);
        _transaction.StringGetAsync(_redisKey, CommandFlags.PreferMaster).Returns(_serializer.Serialize(expectedValue));
        _transaction.KeyExpireTimeAsync(_redisKey, CommandFlags.PreferMaster).Returns(DateTime.UtcNow.AddMinutes(5));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var first = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        var second = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        first.Value.Should().Be(expectedValue);
        second.Value.Should().Be(expectedValue);
        await _database.Received(1).ScriptEvaluateReadOnlyAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>());
        _database.Received(2).CreateTransaction();
    }

    [Fact]
    public async Task GetCacheEntry_falls_back_to_a_transaction_when_the_script_command_is_unknown_v7()
    {
        _version = new(7, 0);
        _database.ScriptEvaluateReadOnlyAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisCommandException("This operation has been disabled in the command-map and cannot be used: EVAL_RO"));
        _transaction.StringGetAsync(_redisKey, CommandFlags.PreferMaster).Returns(RedisValue.Null);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        (await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken)).Found.Should().BeFalse();

        _database.Received(1).CreateTransaction();
    }

    [Fact]
    public async Task GetCacheEntry_returns_default_when_disconnected()
    {
        _isConnected = false;
        var entry = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        entry.Should().NotBeNull();
        entry.Value.Should().BeNull();
        await _transaction.DidNotReceive().ExecuteAsync(Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task GetCacheEntries_bundles_mget_and_pttls_in_single_transaction()
    {
        var expected = _fixture.Create<string>();
        var expectedTtl = TimeSpan.FromMinutes(15);
        _transaction.StringGetAsync(Arg.Is<RedisKey[]>(k => k != null && k.Contains(_redisKey) && k.Contains(_redisMultiKey)), CommandFlags.PreferMaster)
            .Returns(new RedisValue[] { _serializer.Serialize(expected), _serializer.Serialize(expected) });
        _transaction.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), CommandFlags.PreferMaster)
            .Returns(expectedTtl);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var entries = await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        entries.Should().HaveCount(2);
        entries[0].Value.Value.Should().Be(expected);
        entries[1].Value.Value.Should().Be(expected);
        entries[0].Value.Expiration.Should().Be(_clock.UtcNow.Add(expectedTtl));
        // Single MGET inside the transaction; PTTLs are also inside the transaction.
        await _database.DidNotReceive().StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
        await _database.DidNotReceive().KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task GetCacheEntries_returns_defaults_when_disconnected()
    {
        _isConnected = false;
        var entries = await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);
        entries.Should().HaveCount(2);
        entries.Should().AllSatisfy(kv => kv.Value.Value.Should().BeNull());
    }

    [Fact]
    public async Task GetCacheEntry_validates_null_key_when_disconnected()
    {
        _isConnected = false;
        Func<Task> act = async () => await Sut.GetCacheEntryAsync<string>((string?)null!, policy: null, token: testContextAccessor.Current.CancellationToken);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task GetCacheEntries_validates_null_key_when_disconnected()
    {
        _isConnected = false;
        Func<Task> act = async () => await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { (string?)null! }, policy: null, token: testContextAccessor.Current.CancellationToken);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    /// <summary>A read reports what storage holds; a default is a write-side rule, and <c>ExpireTimeAsync</c> agrees.</summary>
    [Fact]
    public async Task GetCacheEntry_reports_no_ttl_as_MaxValue_even_when_a_default_is_configured_v7()
    {
        _version = new(7, 0);
        var expectedValue = _fixture.Create<string>();
        _cacheOptions.DefaultExpiration = TimeSpan.FromHours(1);
        StubReadScript(_serializer.Serialize(expectedValue), -1);

        var entry = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        entry.Value.Should().Be(expectedValue);
        entry.Expiration.Should().Be(DateTimeOffset.MaxValue);
    }

    [Fact]
    public async Task GetCacheEntry_returns_max_value_when_remote_has_no_ttl_and_no_default_v7()
    {
        _version = new(7, 0);
        var expectedValue = _fixture.Create<string>();
        _cacheOptions.DefaultExpiration = null;
        StubReadScript(_serializer.Serialize(expectedValue), -1);

        var entry = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        entry.Value.Should().Be(expectedValue);
        entry.Expiration.Should().Be(DateTimeOffset.MaxValue);
    }

    [Fact]
    public async Task GetCacheEntries_returns_defaults_on_redis_exception()
    {
        _transaction.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisException("test"));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);
        var entries = await Sut.GetCacheEntriesAsync<int?>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);
        entries.Should().HaveCount(2);
        entries.Should().AllSatisfy(kv => kv.Value.Value.Should().BeNull());
    }

    [Fact]
    public async Task GetCacheEntries_returns_defaults_and_records_miss_when_a_per_key_subtask_throws()
    {
        _transaction.StringGetAsync(Arg.Is<RedisKey[]>(k => k != null && k.Contains(_redisKey) && k.Contains(_redisMultiKey)), CommandFlags.PreferMaster)
            .Returns(new RedisValue[] { _serializer.Serialize(_fixture.Create<string>()), _serializer.Serialize(_fixture.Create<string>()) });
        _transaction.KeyTimeToLiveAsync(_redisKey, CommandFlags.PreferMaster).Returns(TimeSpan.FromMinutes(5));
        _transaction.KeyTimeToLiveAsync(_redisMultiKey, CommandFlags.PreferMaster).ThrowsAsync(new RedisException("ttl boom"));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var entries = await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        entries.Should().HaveCount(2);
        entries.Should().AllSatisfy(kv => kv.Value.Value.Should().BeNull());
        HitCount.Should().Be(0);
        MissCount.Should().Be(1);
    }

    [Fact]
    public async Task GetCacheEntry_returns_default_on_redis_exception()
    {
        _transaction.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisException("test"));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);
        var entry = await Sut.GetCacheEntryAsync<int?>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        entry.Should().NotBeNull();
        entry.Value.Should().BeNull();
    }

    [Fact]
    public async Task GetCacheEntry_routes_transaction_to_replica()
    {
        var expectedValue = _fixture.Create<string>();
        _transaction.StringGetAsync(_redisKey, CommandFlags.PreferMaster)
            .Returns(_serializer.Serialize(expectedValue));
        _transaction.KeyTimeToLiveAsync(_redisKey, CommandFlags.PreferMaster)
            .Returns(TimeSpan.FromMinutes(15));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        // Inner queued flags do not propagate to transaction routing in SE.Redis;
        // the flag must be passed to ExecuteAsync explicitly so the transaction lands on a replica.
        await _transaction.Received(1).ExecuteAsync(CommandFlags.PreferMaster);
    }

    [Fact]
    public async Task GetCacheEntries_routes_transaction_to_replica()
    {
        _transaction.StringGetAsync(Arg.Any<RedisKey[]>(), CommandFlags.PreferMaster)
            .Returns(new RedisValue[] { RedisValue.Null, RedisValue.Null });
        _transaction.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), CommandFlags.PreferMaster)
            .Returns(TimeSpan.FromMinutes(15));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        await _transaction.Received(1).ExecuteAsync(CommandFlags.PreferMaster);
    }

    [Fact]
    public async Task GetCacheEntries_reads_values_and_expirations_with_one_script_v7()
    {
        _version = new(7, 0);
        var expected = _fixture.Create<string>();
        var expectedExpiration = DateTimeOffset.UtcNow.AddMinutes(15);
        _database.ScriptEvaluateReadOnlyAsync(Arg.Any<string>(), Arg.Is<RedisKey[]?>(k => k != null && k.Length == 2), Arg.Any<RedisValue[]?>(), CommandFlags.PreferMaster)
            .Returns(RedisResult.Create(
            [
                RedisResult.Create((RedisValue)_serializer.Serialize(expected)),
                RedisResult.Create((RedisValue)expectedExpiration.ToUnixTimeMilliseconds()),
                RedisResult.Create(RedisValue.Null, ResultType.BulkString),
                RedisResult.Create((RedisValue)(-2)),
            ]));

        var entries = await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        entries.Should().HaveCount(2);
        entries[0].Value.Value.Should().Be(expected);
        entries[0].Value.Expiration.Should().BeCloseTo(expectedExpiration, TimeSpan.FromMilliseconds(1));
        entries[1].Value.Found.Should().BeFalse();
        _database.DidNotReceive().CreateTransaction();
    }

    [Fact]
    public async Task GetCacheEntries_falls_back_to_a_transaction_when_scripts_are_denied_v7()
    {
        _version = new(7, 0);
        var expected = _fixture.Create<string>();
        var expectedExpiration = DateTimeOffset.UtcNow.AddMinutes(15);
        _database.ScriptEvaluateReadOnlyAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "NOPERM this user has no permissions to run the 'eval_ro' command"));
        _transaction.StringGetAsync(Arg.Is<RedisKey[]>(k => k != null && k.Contains(_redisKey) && k.Contains(_redisMultiKey)), CommandFlags.PreferMaster)
            .Returns(new RedisValue[] { _serializer.Serialize(expected), _serializer.Serialize(expected) });
        _transaction.KeyExpireTimeAsync(Arg.Any<RedisKey>(), CommandFlags.PreferMaster)
            .Returns(expectedExpiration.UtcDateTime);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var entries = await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        entries.Should().AllSatisfy(kv => kv.Value.Value.Should().Be(expected));
        entries[0].Value.Expiration.Should().BeCloseTo(expectedExpiration, TimeSpan.FromSeconds(1));
        await _transaction.Received(1).KeyExpireTimeAsync(_redisKey, Arg.Any<CommandFlags>());
        await _transaction.DidNotReceive().KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task GetCacheEntries_returns_empty_array_for_empty_keys()
    {
        var entries = await Sut.GetCacheEntriesAsync<string>([], policy: null, token: testContextAccessor.Current.CancellationToken);

        entries.Should().BeEmpty();
        await _transaction.DidNotReceive().ExecuteAsync(Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task GetCacheEntry_returns_default_when_transaction_fails()
    {
        _transaction.StringGetAsync(_redisKey, CommandFlags.PreferMaster)
            .Returns(_serializer.Serialize(_fixture.Create<string>()));
        _transaction.KeyTimeToLiveAsync(_redisKey, CommandFlags.PreferMaster)
            .Returns(TimeSpan.FromMinutes(15));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(false);

        var entry = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        entry.Should().NotBeNull();
        entry.Value.Should().BeNull();
    }

    [Fact]
    public async Task GetCacheEntries_returns_defaults_when_transaction_fails()
    {
        _transaction.StringGetAsync(Arg.Any<RedisKey[]>(), CommandFlags.PreferMaster)
            .Returns(new RedisValue[] { _serializer.Serialize(_fixture.Create<string>()), _serializer.Serialize(_fixture.Create<string>()) });
        _transaction.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), CommandFlags.PreferMaster)
            .Returns(TimeSpan.FromMinutes(15));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(false);

        var entries = await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        entries.Should().HaveCount(2);
        entries.Should().AllSatisfy(kv => kv.Value.Value.Should().BeNull());
    }


    [Theory]
    [InlineData(null, null, true, 0)]
    [InlineData("v1", null, false, 0)]
    [InlineData(null, "myValue", true, 1)]
    public Task GetOrAdd_works_as_expected_timespan_expiration(string? redisReturn, string? generatorReturn, bool expectedGeneratorCall, int stringSetCalls) =>
        GetOrAdd_works_as_expected(redisReturn, generatorReturn, expectedGeneratorCall, stringSetCalls, typeof(TimeSpan));

    [Theory]
    [InlineData(null, null, true, 0)]
    [InlineData("v1", null, false, 0)]
    [InlineData(null, "myValue", true, 1)]
    public Task GetOrAdd_works_as_expected_datetime_expiration(string? redisReturn, string? generatorReturn, bool expectedGeneratorCall, int stringSetCalls) =>
        GetOrAdd_works_as_expected(redisReturn, generatorReturn, expectedGeneratorCall, stringSetCalls, typeof(DateTimeOffset));

    [Theory]
    [InlineData(null, null, true, 0)]
    [InlineData("v1", null, false, 0)]
    [InlineData(null, "myValue", true, 1)]
    public Task GetOrAdd_works_as_expected_no_expiration(string? redisReturn, string? generatorReturn, bool expectedGeneratorCall, int stringSetCalls) =>
        GetOrAdd_works_as_expected(redisReturn, generatorReturn, expectedGeneratorCall, stringSetCalls, typeof(object));

    [Fact]
    public async Task GetOrAdd_null_generator()
    {
        Func<CancellationToken, Task<string?>> generator = default!;
        Func<Task> act = async () => await Sut.GetOrAddAsync(_fixture.Create<string>(), generator, (CachePolicy?)null, testContextAccessor.Current.CancellationToken);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task GetOrAdd_generator()
    {
        var expectedValue = _fixture.Create<string?>();
        Func<CancellationToken, Task<string?>> generator = _ => Task.FromResult(expectedValue);
        var actual = await Sut.GetOrAddAsync(_fixture.Create<string>(), generator, TimeSpan.FromSeconds(5), token: testContextAccessor.Current.CancellationToken);
        actual.Should().Be(expectedValue);
    }

    [Fact]
    public async Task GetOrAdd_stateful_generator_receives_its_state_on_a_miss_and_is_stored()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var calls = 0;

        var actual = await ((ICache)Sut).GetOrAddAsync<string, string>(_cacheKey, "state", (state, _) => { calls++; return Task.FromResult<string?>(state + "!"); }, TimeSpan.FromSeconds(5), policy: null, token);

        actual.Should().Be("state!");
        calls.Should().Be(1);
        await _database.Received(1).StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task GetOrAdd_stateful_generator_does_not_run_on_a_hit()
    {
        var token = testContextAccessor.Current.CancellationToken;
        _database.StringGetAsync(_redisKey, CommandFlags.PreferMaster).Returns(_serializer.Serialize("cached"));

        var actual = await ((ICache)Sut).GetOrAddAsync<string, string>(_cacheKey, "state", static (_, _) => throw new InvalidOperationException("the generator ran on a hit"), policy: null, token);

        actual.Should().Be("cached");
    }

    [Fact]
    public async Task GetOrAdd_stateful_policy_FactoryTimeout_cancels_slow_generator()
    {
        using var caller = new CancellationTokenSource();
        var policy = new CachePolicy { FactoryTimeout = TimeSpan.FromMilliseconds(50) };

        var act = async () => await ((ICache)Sut).GetOrAddAsync<string, int>(_cacheKey, 1, SlowGenerator, policy, caller.Token);

        await act.Should().ThrowAsync<TimeoutException>();

        async Task<string?> SlowGenerator(int state, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return "never";
        }
    }

    [Fact]
    public async Task GetOrAddWithExpiration_stores_for_the_time_left_until_the_returned_expiration()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var deadline = _now.AddMinutes(10);

        var actual = await ((ICache)Sut).GetOrAddWithExpirationAsync<string>(_cacheKey, _ => Task.FromResult(new GeneratedValue<string>("jwt", deadline)), policy: null, token);

        actual.Should().Be("jwt");
        await _database.Received(1).StringSetAsync(_redisKey, Arg.Any<RedisValue>(), TimeSpan.FromMinutes(10), When.Always, CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task GetOrAddWithExpiration_without_an_expiration_stores_for_the_policy_lifetime()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var policy = new CachePolicy { DistributedExpiration = TimeSpan.FromMinutes(30) };

        await ((ICache)Sut).GetOrAddWithExpirationAsync<string>(_cacheKey, _ => Task.FromResult(new GeneratedValue<string>("jwt")), policy, token);

        await _database.Received(1).StringSetAsync(_redisKey, Arg.Any<RedisValue>(), TimeSpan.FromMinutes(30), When.Always, CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task GetOrAddWithExpiration_does_not_store_a_value_that_has_already_expired()
    {
        var token = testContextAccessor.Current.CancellationToken;

        var actual = await ((ICache)Sut).GetOrAddWithExpirationAsync<string>(_cacheKey, _ => Task.FromResult(new GeneratedValue<string>("jwt", _now.AddMinutes(-1))), policy: null, token);

        actual.Should().Be("jwt");
        await _database.DidNotReceiveWithAnyArgs().StringSetAsync(default, default, (TimeSpan?)null, default, default);
    }

    [Fact]
    public async Task GetOrAddWithExpiration_stateful_generator_receives_its_state_on_a_miss_and_is_stored()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var deadline = _now.AddMinutes(10);

        var actual = await ((ICache)Sut).GetOrAddWithExpirationAsync<string, DateTimeOffset>(_cacheKey, deadline, static (expiration, _) => Task.FromResult(new GeneratedValue<string>("jwt", expiration)), policy: null, token);

        actual.Should().Be("jwt");
        await _database.Received(1).StringSetAsync(_redisKey, Arg.Any<RedisValue>(), TimeSpan.FromMinutes(10), When.Always, CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task GetOrAddWithExpiration_stateful_does_not_run_the_generator_on_a_hit()
    {
        var token = testContextAccessor.Current.CancellationToken;
        _database.StringGetAsync(_redisKey, CommandFlags.PreferMaster).Returns(_serializer.Serialize("cached"));

        var actual = await ((ICache)Sut).GetOrAddWithExpirationAsync<string, int>(_cacheKey, 1, static (_, _) => throw new InvalidOperationException("the generator ran on a hit"), policy: null, token);

        actual.Should().Be("cached");
    }

    [Fact]
    public async Task GetOrAddWithExpiration_does_not_run_the_generator_on_a_hit()
    {
        var token = testContextAccessor.Current.CancellationToken;
        _database.StringGetAsync(_redisKey, CommandFlags.PreferMaster).Returns(_serializer.Serialize("cached"));

        var actual = await ((ICache)Sut).GetOrAddWithExpirationAsync<string>(_cacheKey, _ => throw new InvalidOperationException("the generator ran on a hit"), policy: null, token);

        actual.Should().Be("cached");
    }

    [Fact]
    public async Task GetOrAdd_concurrent_misses_on_one_key_share_one_generator_run_and_one_write()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<string?> Generate(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task;
            return "loaded";
        }

        var first = Sut.GetOrAddAsync(_cacheKey, Generate, TimeSpan.FromSeconds(5), policy: null, token).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        var others = Enumerable.Range(0, 9).Select(_ => Sut.GetOrAddAsync(_cacheKey, Generate, TimeSpan.FromSeconds(5), policy: null, token).AsTask()).ToArray();
        await Task.Delay(100, token);
        release.SetResult();

        (await Task.WhenAll(others.Append(first))).Should().AllBe("loaded");
        calls.Should().Be(1);
        await _database.Received(1).StringGetAsync(_redisKey, Arg.Any<CommandFlags>());
        await _database.Received(1).StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task GetOrAdd_calls_whose_redis_keys_differ_for_one_cache_key_do_not_share_a_run()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var tenant = new AsyncLocal<string?>();
        RedisKey tenantA = "tenant-a:" + _cacheKey;
        RedisKey tenantB = "tenant-b:" + _cacheKey;
        _redisKeyStrategy.GetRedisKey(_cacheKey).Returns(_ => tenant.Value == "a" ? tenantA : tenantB);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Func<CancellationToken, Task<string?>> Generate(string value) => async _ =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task;
            return value;
        };

        tenant.Value = "a";
        var first = Sut.GetOrAddAsync(_cacheKey, Generate("from-a"), TimeSpan.FromSeconds(5), policy: null, token).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        tenant.Value = "b";
        var second = Sut.GetOrAddAsync(_cacheKey, Generate("from-b"), TimeSpan.FromSeconds(5), policy: null, token).AsTask();
        await Task.Delay(100, token);
        release.SetResult();

        (await first).Should().Be("from-a");
        (await second).Should().Be("from-b");
        calls.Should().Be(2);
        await _database.Received(1).StringGetAsync(tenantA, Arg.Any<CommandFlags>());
        await _database.Received(1).StringGetAsync(tenantB, Arg.Any<CommandFlags>());
        await _database.Received(1).StringSetAsync(tenantA, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
        await _database.Received(1).StringSetAsync(tenantB, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task GetOrAdd_callers_whose_strategy_builds_a_fresh_equal_key_each_time_share_one_run()
    {
        var token = testContextAccessor.Current.CancellationToken;
        // As PrefixRedisKeyStrategy does: a new key per call, a byte prefix over the caller's text, so sharing rests on value equality.
        var prefix = System.Text.Encoding.UTF8.GetBytes(_prefix + ":");
        var suffix = ((string)_redisKey!)[(_prefix.Length + 1)..];
        _redisKeyStrategy.GetRedisKey(_cacheKey).Returns(_ => new RedisKey(suffix).Prepend(prefix));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<string?> Generate(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task;
            return "shared";
        }

        var first = Sut.GetOrAddAsync(_cacheKey, Generate, TimeSpan.FromSeconds(5), policy: null, token).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        var second = Sut.GetOrAddAsync(_cacheKey, Generate, TimeSpan.FromSeconds(5), policy: null, token).AsTask();
        await Task.Delay(100, token);
        release.SetResult();

        (await first).Should().Be("shared");
        (await second).Should().Be("shared");
        calls.Should().Be(1);
        await _database.Received(1).StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task GetOrAdd_calls_for_one_redis_key_with_different_value_types_do_not_share_a_run()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<string?> GenerateText(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task;
            return "text";
        }

        async Task<int?> GenerateNumber(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return 42;
        }

        var text = Sut.GetOrAddAsync(_cacheKey, GenerateText, TimeSpan.FromSeconds(5), policy: null, token).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        var number = Sut.GetOrAddAsync<int?>(_cacheKey, GenerateNumber, TimeSpan.FromSeconds(5), policy: null, token).AsTask();
        await Task.Delay(100, token);
        release.SetResult();

        (await text).Should().Be("text");
        (await number).Should().Be(42);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task GetOrAdd_concurrent_callers_on_a_cached_key_share_one_read_and_run_no_generator()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var release = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        _database.StringGetAsync(_redisKey, Arg.Any<CommandFlags>()).Returns(_ =>
        {
            Interlocked.Increment(ref reads);
            return release.Task;
        });
        var generated = 0;
        Task<string?> Generate(CancellationToken _)
        {
            Interlocked.Increment(ref generated);
            return Task.FromResult<string?>("generated");
        }

        var callers = Enumerable.Range(0, 10).Select(_ => Sut.GetOrAddAsync(_cacheKey, Generate, TimeSpan.FromSeconds(5), policy: null, token).AsTask()).ToArray();
        await Task.Delay(100, token);
        release.SetResult(_serializer.Serialize("cached"));

        (await Task.WhenAll(callers)).Should().AllBe("cached");
        reads.Should().Be(1);
        generated.Should().Be(0);
    }

    [Fact]
    public async Task GetOrAdd_sequential_callers_read_once_each()
    {
        var token = testContextAccessor.Current.CancellationToken;
        _database.StringGetAsync(_redisKey, Arg.Any<CommandFlags>()).Returns(_serializer.Serialize("cached"));

        Task<string?> Generate(CancellationToken _) => throw new InvalidOperationException("the generator ran on a hit");

        for (var i = 0; i < 3; i++)
        {
            (await Sut.GetOrAddAsync(_cacheKey, Generate, TimeSpan.FromSeconds(5), policy: null, token)).Should().Be("cached");
        }

        await _database.Received(3).StringGetAsync(_redisKey, Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task GetOrAdd_a_caller_after_a_run_ended_reads_once_and_finds_the_written_value()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var reads = 0;
        _database.StringGetAsync(_redisKey, Arg.Any<CommandFlags>()).Returns(_ =>
            Interlocked.Increment(ref reads) == 1 ? RedisValue.Null : (RedisValue)_serializer.Serialize("written"));
        var calls = 0;
        Task<string?> Generate(CancellationToken _)
        {
            calls++;
            return Task.FromResult<string?>("written");
        }

        (await Sut.GetOrAddAsync(_cacheKey, Generate, TimeSpan.FromSeconds(5), policy: null, token)).Should().Be("written");
        (await Sut.GetOrAddAsync(_cacheKey, Generate, TimeSpan.FromSeconds(5), policy: null, token)).Should().Be("written");

        calls.Should().Be(1);
        reads.Should().Be(2);
    }

    [Fact]
    public async Task GetOrAdd_a_failed_read_is_one_read_and_one_generator_run_for_every_joiner()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var release = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        _database.StringGetAsync(_redisKey, Arg.Any<CommandFlags>()).Returns(_ =>
        {
            Interlocked.Increment(ref reads);
            return release.Task;
        });
        var generated = 0;
        Task<string?> Generate(CancellationToken _)
        {
            Interlocked.Increment(ref generated);
            return Task.FromResult<string?>("generated");
        }

        var callers = Enumerable.Range(0, 5).Select(_ => Sut.GetOrAddAsync(_cacheKey, Generate, TimeSpan.FromSeconds(5), policy: null, token).AsTask()).ToArray();
        await Task.Delay(100, token);
        release.SetException(new InvalidOperationException("read failed"));

        (await Task.WhenAll(callers)).Should().AllBe("generated");
        reads.Should().Be(1);
        generated.Should().Be(1);
    }

    [Fact]
    public async Task GetOrAdd_the_expiring_generator_shares_one_read_between_concurrent_callers()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var release = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        _database.StringGetAsync(_redisKey, Arg.Any<CommandFlags>()).Returns(_ =>
        {
            Interlocked.Increment(ref reads);
            return release.Task;
        });

        var callers = Enumerable.Range(0, 10)
            .Select(_ => ((ICache)Sut).GetOrAddWithExpirationAsync<string>(_cacheKey, _ => throw new InvalidOperationException("the generator ran on a hit"), policy: null, token).AsTask())
            .ToArray();
        await Task.Delay(100, token);
        release.SetResult(_serializer.Serialize("cached"));

        (await Task.WhenAll(callers)).Should().AllBe("cached");
        reads.Should().Be(1);
    }

    [Fact]
    public async Task GetOrAdd_misses_on_different_keys_do_not_share_a_run()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var calls = 0;
        Task<string?> Generate(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<string?>("loaded");
        }

        await Task.WhenAll(
            Sut.GetOrAddAsync(_cacheKey, Generate, TimeSpan.FromSeconds(5), policy: null, token).AsTask(),
            Sut.GetOrAddAsync(_multiKey, Generate, TimeSpan.FromSeconds(5), policy: null, token).AsTask());

        calls.Should().Be(2);
    }

    [Fact]
    public async Task GetOrAdd_a_caller_that_cancels_stops_waiting_and_the_run_is_cancelled_once_all_have()
    {
        var token = testContextAccessor.Current.CancellationToken;
        using var firstCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var secondCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string?> Generate(CancellationToken ct)
        {
            started.TrySetResult();
            using var registration = ct.Register(() => cancelled.TrySetResult());
            await Task.Delay(Timeout.Infinite, ct);
            return "never";
        }

        var first = Sut.GetOrAddAsync(_cacheKey, Generate, TimeSpan.FromSeconds(5), policy: null, firstCancellation.Token).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        var second = Sut.GetOrAddAsync(_cacheKey, Generate, TimeSpan.FromSeconds(5), policy: null, secondCancellation.Token).AsTask();

        await firstCancellation.CancelAsync();
        await ((Func<Task>)(() => first)).Should().ThrowAsync<OperationCanceledException>();
        cancelled.Task.IsCompleted.Should().BeFalse("a caller still waits for the run");
        await secondCancellation.CancelAsync();
        await ((Func<Task>)(() => second)).Should().ThrowAsync<OperationCanceledException>();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
    }

    [Fact]
    public async Task GetOrAdd_a_failed_run_fails_the_callers_that_joined_it_and_the_next_call_starts_a_new_one()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string?> Failing(CancellationToken _)
        {
            started.TrySetResult();
            await release.Task;
            throw new InvalidOperationException("the load failed");
        }

        var first = Sut.GetOrAddAsync(_cacheKey, Failing, TimeSpan.FromSeconds(5), policy: null, token).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        var second = Sut.GetOrAddAsync(_cacheKey, Failing, TimeSpan.FromSeconds(5), policy: null, token).AsTask();
        release.SetResult();

        await ((Func<Task>)(() => first)).Should().ThrowAsync<InvalidOperationException>();
        await ((Func<Task>)(() => second)).Should().ThrowAsync<InvalidOperationException>();
        (await Sut.GetOrAddAsync(_cacheKey, _ => Task.FromResult<string?>("again"), TimeSpan.FromSeconds(5), policy: null, token)).Should().Be("again");
    }

    [Fact]
    public async Task Every_command_waits_for_the_first_connect_asynchronously_and_never_reads_the_database_before_it()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connected = false;
        var earlyReads = 0;
        ValueTaskStubs.Returns(_connector.ConnectAsync(Arg.Any<CancellationToken>()), _ => WaitForConnectAsync());
        _connector.Database.Returns(_ =>
        {
            if (!Volatile.Read(ref connected))
            {
                Interlocked.Increment(ref earlyReads);
            }

            return _database;
        });
        _connector.Version.Returns(_ =>
        {
            if (!Volatile.Read(ref connected))
            {
                Interlocked.Increment(ref earlyReads);
            }

            return _version;
        });
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(_ => (RedisValue)_serializer.Serialize("v"));
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        _database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        _database.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);
        CacheKey[] keys = [_cacheKey, _multiKey];
        KeyValuePair<CacheKey, string?>[] pairs = [new(_cacheKey, "a"), new(_multiKey, "b")];

        var calls = new Task[]
        {
            Sut.GetAsync<string>(_cacheKey, policy: null, token).AsTask(),
            Sut.GetAsync<string>(keys, policy: null, token).AsTask(),
            Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token).AsTask(),
            Sut.GetCacheEntriesAsync<string>(keys, policy: null, token).AsTask(),
            Sut.GetOrAddAsync(_cacheKey, _ => Task.FromResult<string?>("g"), TimeSpan.FromMinutes(1), policy: null, token).AsTask(),
            Sut.SetAsync<string>(_cacheKey, "a", TimeSpan.FromMinutes(1), policy: null, token).AsTask(),
            Sut.SetAsync(pairs, TimeSpan.FromMinutes(1), policy: null, token).AsTask(),
            Sut.TryAddAsync<string>(_cacheKey, "a", TimeSpan.FromMinutes(1), policy: null, token).AsTask(),
            Sut.RemoveAsync<string>(_cacheKey, token).AsTask(),
            Sut.RemoveAsync<string>(keys, token).AsTask(),
            Sut.ContainsAsync<string>(_cacheKey, token).AsTask(),
            Sut.RefreshAsync<string>(_cacheKey, TimeSpan.FromMinutes(1), policy: null, token).AsTask(),
            Sut.TimeToLiveAsync<string>(_cacheKey, token).AsTask(),
            Sut.ExpireTimeAsync<string>(_cacheKey, token).AsTask(),
        };
        await Task.Delay(100, token);
        earlyReads.Should().Be(0, "the library waits for the connect without reading the blocking database accessor");
        gate.SetResult();
        await Task.WhenAll(calls);

        earlyReads.Should().Be(0);

        async ValueTask WaitForConnectAsync()
        {
            await gate.Task;
            Volatile.Write(ref connected, true);
        }
    }

    [Fact]
    public async Task A_multi_key_command_gives_up_on_a_pending_first_connect_when_the_pipeline_does()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var never = new TaskCompletionSource();
        ValueTaskStubs.Returns(_connector.ConnectAsync(Arg.Any<CancellationToken>()), _ => new ValueTask(never.Task));
        var givingUp = new GivingUpPipeline(TimeSpan.FromMilliseconds(50));
        _resiliencePipelineProvider.Get(ResiliencePipelineNames.Read).Returns(givingUp);
        _resiliencePipelineProvider.Get(ResiliencePipelineNames.Write).Returns(givingUp);
        CacheKey[] keys = [_cacheKey, _multiKey];
        KeyValuePair<CacheKey, string?>[] pairs = [new(_cacheKey, "a"), new(_multiKey, "b")];

        // A wait outside the pipeline would hold every call for as long as the connect takes, here forever.
        var reads = Sut.GetAsync<string>(keys, policy: null, token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), token);
        var entries = Sut.GetCacheEntriesAsync<string>(keys, policy: null, token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), token);
        var set = Sut.SetAsync(pairs, TimeSpan.FromMinutes(1), policy: null, token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), token);
        var removed = Sut.RemoveAsync<string>(keys, token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), token);

        (await reads).Should().OnlyContain(kv => kv.Value == null);
        (await entries).Should().OnlyContain(kv => !kv.Value.Found);
        (await set).Should().BeFalse();
        (await removed).Should().BeFalse();
    }

    [Fact]
    public async Task GetOrAdd_when_disconnected_runs_generator_without_redis_probe()
    {
        _isConnected = false;
        var expected = _fixture.Create<string>();
        var generatorCalled = false;
        Func<CancellationToken, Task<string?>> generator = _ =>
        {
            generatorCalled = true;
            return Task.FromResult<string?>(expected);
        };

        var actual = await Sut.GetOrAddAsync(_cacheKey, generator, TimeSpan.FromSeconds(5), token: testContextAccessor.Current.CancellationToken);

        actual.Should().Be(expected);
        generatorCalled.Should().BeTrue();
        await _database.DidNotReceive().StringGetAsync(_redisKey, Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Refresh_timespan_works_as_expected()
    {
        var expiration =  _clock.UtcNow.AddDays(1);
        var actualKey = string.Empty;
        bool called = false;
        _database.KeyExpireAsync(_redisKey, Arg.Any<DateTime>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                called = true;
                return Task.FromResult(_fixture.Create<bool>());
            });
        await Sut.RefreshAsync<string>(_cacheKey, expiration, token: testContextAccessor.Current.CancellationToken);
        called.Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_datetime_works_as_expected()
    {
        var expiration = _fixture.Create<TimeSpan>();
        var actualKey = string.Empty;
        _database.KeyExpireAsync(_redisKey, Arg.Any<DateTime>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                return _fixture.Create<bool>();
            });
        await Sut.RefreshAsync<string>(_cacheKey, expiration, token: testContextAccessor.Current.CancellationToken);
        await _database.Received(1).KeyExpireAsync(_redisKey, Arg.Any<DateTime>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Refresh_no_expiration_works_as_expected()
    {
        var expiration = _fixture.Create<TimeSpan>();
        var actualKey = string.Empty;
        DateTime? actualExpiration = default;
        _database.KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                actualExpiration = ci.Arg<DateTime?>();
                return _fixture.Create<bool>();
            });
        await Sut.RefreshAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
        await _database.Received(1).KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), Arg.Any<CommandFlags>());
        actualExpiration.Should().NotBeNull();
        DateTimeOffset actualOffset = DateTime.SpecifyKind(actualExpiration!.Value, DateTimeKind.Utc);
        var expected = _now.Add(_cacheOptions.DefaultExpiration.GetValueOrDefault());
        actualOffset.Should().Be(expected);
    }

    [Fact]
    public async Task Refresh_with_no_expiration_and_no_default_takes_the_library_default()
    {
        _cacheOptions.DefaultExpiration = null;
        DateTime? actualExpiration = default;
        _database.KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                actualExpiration = ci.Arg<DateTime?>();
                return true;
            });

        await Sut.RefreshAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        await _database.DidNotReceive().KeyPersistAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        actualExpiration.Should().Be(_now.Add(CachePolicy.DefaultDistributedExpiration).UtcDateTime);
    }

    /// <summary>Unbounded is still reachable — it just has to be asked for.</summary>
    [Fact]
    public async Task Refresh_persists_the_key_when_the_default_is_configured_unbounded()
    {
        _cacheOptions.DefaultExpiration = TimeSpan.MaxValue;

        await Sut.RefreshAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        await _database.DidNotReceive().KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<DateTime?>(), Arg.Any<CommandFlags>());
        await _database.Received(1).KeyPersistAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    /// <summary>The per-call spelling of unbounded lands on the same PERSIST as the configured one.</summary>
    [Fact]
    public async Task Refresh_persists_the_key_for_a_per_call_unbounded_duration()
    {
        await Sut.RefreshAsync<string>(_cacheKey, TimeSpan.MaxValue, policy: null, token: testContextAccessor.Current.CancellationToken);

        await _database.DidNotReceive().KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<DateTime?>(), Arg.Any<CommandFlags>());
        await _database.Received(1).KeyPersistAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task GetOrAdd_default_key_expiration()
    {
        DateTime? actualExpiration = default;
        _database.KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                actualExpiration = ci.Arg<DateTime?>();
                return _fixture.Create<bool>();
            });
        await Sut.GetOrAddAsync(_cacheKey, _ => Task.FromResult(_fixture.Create<string?>()), token: testContextAccessor.Current.CancellationToken);
        var expectedExpiration = _clock.UtcNow.Add(_cacheOptions.DefaultExpiration!.Value).Subtract(_clock.UtcNow);

        await _database.Received(1).StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Is<TimeSpan?>(t => expectedExpiration == t), When.Always, CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task GetOrAdd_policy_DistributedExpiration_overrides_default_when_caller_omits_expiration()
    {
        var policyTtl = TimeSpan.FromMinutes(7);
        var policy = new CachePolicy { DistributedExpiration = policyTtl };
        _database.KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), Arg.Any<CommandFlags>())
            .Returns(_fixture.Create<bool>());

        await Sut.GetOrAddAsync(_cacheKey, _ => Task.FromResult(_fixture.Create<string?>()), policy: policy, token: testContextAccessor.Current.CancellationToken);

        await _database.Received(1).StringSetAsync(
            _redisKey,
            Arg.Any<RedisValue>(),
            Arg.Is<TimeSpan?>(t => t.HasValue && t.Value > policyTtl - TimeSpan.FromSeconds(5) && t.Value < policyTtl + TimeSpan.FromSeconds(5)),
            When.Always,
            CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task GetOrAdd_CacheOptions_DefaultCachePolicy_DistributedExpiration_drives_TTL_when_caller_omits_policy()
    {
        // Provider's DefaultExpiration is null so user DefaultCachePolicy.DistributedExpiration
        // fills the gap and drives TTL (provider wins per "specific over generic" otherwise).
        var defaultTtl = TimeSpan.FromMinutes(9);
        _cacheOptions.DefaultExpiration = null;
        _fixture.Inject(new CacheOptions
        {
            AppShortName = "test",
            ConnectionMonitorEnabled = true,
            DefaultCachePolicy = new CachePolicy { DistributedExpiration = defaultTtl },
        });
        _sut = null;
        _database.KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), Arg.Any<CommandFlags>())
            .Returns(_fixture.Create<bool>());

        await Sut.GetOrAddAsync(_cacheKey, _ => Task.FromResult(_fixture.Create<string?>()), policy: null, token: testContextAccessor.Current.CancellationToken);

        await _database.Received(1).StringSetAsync(
            _redisKey,
            Arg.Any<RedisValue>(),
            Arg.Is<TimeSpan?>(t => t.HasValue && t.Value > defaultTtl - TimeSpan.FromSeconds(5) && t.Value < defaultTtl + TimeSpan.FromSeconds(5)),
            When.Always,
            CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task GetOrAdd_policy_FactoryTimeout_cancels_slow_generator()
    {
        // FactoryTimeout only surfaces as TimeoutException while the caller's own token is
        // uncancelled — see the catch filter in FactoryTimeout.RunAsync. Pass a token this test
        // owns rather than the ambient one the runner may cancel under load; the 50ms budget is
        // what bounds the call, so nothing here can hang.
        using var caller = new CancellationTokenSource();
        var policy = new CachePolicy { FactoryTimeout = TimeSpan.FromMilliseconds(50) };
        Func<CancellationToken, Task<string?>> generator = async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return "never";
        };

        var act = async () => await Sut.GetOrAddAsync(_cacheKey, generator, policy: policy, token: caller.Token);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task Batch_GetOrAdd_policy_FactoryTimeout_cancels_slow_generator()
    {
        var token = testContextAccessor.Current.CancellationToken;
        // FactoryTimeout only surfaces as TimeoutException while the caller's own token is
        // uncancelled — see the catch filter in FactoryTimeout.RunAsync. Pass a token this test
        // owns rather than the ambient one the runner may cancel under load; the 50ms budget is
        // what bounds the call, so nothing here can hang. The ambient token still guards the race
        // below.
        using var caller = new CancellationTokenSource();
        var policy = new CachePolicy { FactoryTimeout = TimeSpan.FromMilliseconds(50) };
        var entries = new KeyValuePair<CacheKey, string>[] { new(_cacheKey, "one"), new(_multiKey, "two") };
        static async Task<KeyValuePair<string, string?>[]> Generator(string[] _, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return [];
        }

        var call = ((ICache)Sut).GetOrAddAsync<string, string>(entries, Generator, policy, caller.Token).AsTask();

        var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(10), token));
        finished.Should().BeSameAs(call, "the batch generator must be bounded by CachePolicy.FactoryTimeout");
        var act = async () => await call;
        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task Batch_GetOrAdd_without_FactoryTimeout_returns_generator_values()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var policy = new CachePolicy { FactoryTimeout = null };
        var entries = new KeyValuePair<CacheKey, string>[] { new(_cacheKey, "one"), new(_multiKey, "two") };
        var calls = 0;
        Task<KeyValuePair<string, string?>[]> Generator(string[] states, CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Array.ConvertAll(states, s => new KeyValuePair<string, string?>(s, s + "-value")));
        }

        var call = ((ICache)Sut).GetOrAddAsync<string, string>(entries, Generator, policy, token).AsTask();

        var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(10), token));
        finished.Should().BeSameAs(call, "an untimed batch generator must not be cut short");
        var actual = await call;

        calls.Should().Be(1);
        actual.Should().Equal(
            new[] { new KeyValuePair<string, string?>("one", "one-value"), new KeyValuePair<string, string?>("two", "two-value") },
            "the untimed path still projects the generator's values onto the caller's states");
    }

    [Fact]
    public async Task Batch_GetOrAdd_argument_members_pass_the_argument_to_a_static_generator()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var entries = new KeyValuePair<CacheKey, string>[] { new(_cacheKey, "one"), new(_multiKey, "two") };
        var suffix = new Suffix("-value");

        var actual = new[]
        {
            await ((ICache)Sut).GetOrAddAsync<string, string, Suffix>(entries, suffix, static (s, states, _) => s.Apply(states), policy: null, token),
            await ((ICache)Sut).GetOrAddAsync<string, string, Suffix>(entries, suffix, static (s, states, _) => s.Apply(states), TimeSpan.FromMinutes(1), policy: null, token),
            await ((ICache)Sut).GetOrAddAsync<string, string, Suffix>(entries, suffix, static (s, states, _) => s.Apply(states), _now.AddMinutes(10), policy: null, token),
        };

        actual.Should().AllSatisfy(result => result.Should().Equal(new KeyValuePair<string, string?>("one", "one-value"), new KeyValuePair<string, string?>("two", "two-value")));
        suffix.Runs.Should().Be(3, "each call missed and ran the generator once for both keys");
    }

    [Fact]
    public async Task Batch_GetOrAdd_argument_member_under_FactoryTimeout_passes_the_argument()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var policy = new CachePolicy { FactoryTimeout = TimeSpan.FromMinutes(1) };
        var entries = new KeyValuePair<CacheKey, string>[] { new(_cacheKey, "one") };
        var suffix = new Suffix("-timed");

        var actual = await ((ICache)Sut).GetOrAddAsync<string, string, Suffix>(entries, suffix, static (s, states, _) => s.Apply(states), policy, token);

        actual.Should().Equal(new[] { new KeyValuePair<string, string?>("one", "one-timed") });
        suffix.Runs.Should().Be(1);
    }

    [Fact]
    public async Task GetOrAdd_key_expiration_no_default_expiration()
    {
        _cacheOptions.DefaultExpiration = null;
        DateTime? actualExpiration = default;
        _database.KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                actualExpiration = ci.Arg<DateTime?>();
                return _fixture.Create<bool>();
            });
        var expiration = _clock.UtcNow.AddMinutes(5);
        await Sut.GetOrAddAsync(_cacheKey, _ => Task.FromResult(_fixture.Create<string?>()), expiration: expiration, policy: null, token: testContextAccessor.Current.CancellationToken);
        var expectedExpiration = expiration.Subtract(_clock.UtcNow);
        await _database.Received(1).StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Is<TimeSpan?>(t => expectedExpiration == t), When.Always, CommandFlags.DemandMaster);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(5)]
    public async Task Refresh_redis_exception(int? expirationMinutes)
    {
        var expectedExpiration = expirationMinutes.HasValue
            ? TimeSpan.FromMinutes(expirationMinutes.Value)
            : _cacheOptions.DefaultExpiration ?? TimeSpan.MinValue;

        DateTime? actualExpiration = default;
        _database.KeyExpireAsync(_redisKey, Arg.Any<DateTime?>(), CommandFlags.DemandMaster| CommandFlags.FireAndForget)
                .ThrowsAsync(ci =>
                {
                    actualExpiration = ci.Arg<DateTime?>();
                    return new RedisException("test");
                });
        // No expiration minutes means the caller omits the argument entirely, which is now the
        // only way to ask for the policy default.
        await (expirationMinutes.HasValue
            ? Sut.RefreshAsync<string>(_cacheKey, TimeSpan.FromMinutes(expirationMinutes.Value), policy: null, testContextAccessor.Current.CancellationToken)
            : Sut.RefreshAsync<string>(_cacheKey, policy: null, testContextAccessor.Current.CancellationToken));
        actualExpiration.GetValueOrDefault().Subtract(_now.UtcDateTime).Should().BeCloseTo(expectedExpiration, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Remove_redis_exception()
    {
        _database.KeyDeleteAsync(_redisKey, CommandFlags.DemandMaster)
                .ThrowsAsync(ci =>
                {
                    return new RedisException("test");
                });
        var actualResponse = await Sut.RemoveAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        actualResponse.Should().BeFalse();
    }

    [Fact]
    public async Task Remove_works_as_expected()
    {
        var apiResponse = _fixture.Create<bool>();
        _database.KeyDeleteAsync(_redisKey, CommandFlags.DemandMaster)
                .Returns(apiResponse);
        var actualResponse = await Sut.RemoveAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        actualResponse.Should().Be(true);
    }

    [Fact]
    public async Task Multi_remove_redis_exception()
    {
        _database.KeyDeleteAsync(Arg.Is<RedisKey[]>(keys => keys != null && keys.Contains(_redisKey )), CommandFlags.DemandMaster)
                .ThrowsAsync(ci =>
                {
                    return new RedisException("test");
                });
        var actualResponse = await Sut.RemoveAsync<string>(new CacheKey[] { _cacheKey }, testContextAccessor.Current.CancellationToken);
        actualResponse.Should().BeFalse();
    }

    [Fact]
    public async Task Multi_remove_works_as_expected()
    {
        var keyDeleteResponse = _fixture.Create<bool>();
        _database.KeyDeleteAsync(_redisKey, CommandFlags.DemandMaster)
                .Returns(keyDeleteResponse);
        var actualResponse = await Sut.RemoveAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        actualResponse.Should().Be(true);
    }

    [Fact]
    public Task Set_works_as_expected_timespan_expiration() =>
        Set_works_as_expected(typeof(TimeSpan));

    [Fact]
    public Task Set_works_as_expected_datetime_expiration() =>
        Set_works_as_expected(typeof(DateTimeOffset));

    [Fact]
    public Task Set_works_as_expected_no_expiration() =>
        Set_works_as_expected(typeof(object));

    [Fact]
    public Task Multi_set_works_as_expected_timespan_expiration() =>
        Multi_set_works_as_expected(typeof(TimeSpan));

    [Fact]
    public Task Multi_set_works_as_expected_datetime_expiration() =>
        Multi_set_works_as_expected(typeof(DateTimeOffset));

    [Fact]
    public Task Multi_set_works_as_expected_no_expiration() =>
        Multi_set_works_as_expected(typeof(object));

    [Fact]
    public async Task Multi_set_when_no_connection()
    {
        _isConnected = false;
        var value = _fixture.Create<string>();
        var actualResponse = await Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, value), new(_multiKey, value) }, _fixture.Create<TimeSpan>(), policy: null, token: testContextAccessor.Current.CancellationToken);
        actualResponse.Should().BeFalse();
    }

    [Fact]
    public async Task Multi_set_when_defaultValue()
    {
        string? value = default;
        var actualResponse = await Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, value), new(_multiKey, value) }, _fixture.Create<TimeSpan>(), policy: null, token: testContextAccessor.Current.CancellationToken);
        await _transaction.Received().KeyDeleteAsync(Arg.Any<RedisKey>(),Arg.Is<CommandFlags>(f => f.HasFlag(CommandFlags.DemandMaster)));
    }

    [Fact]
    public async Task Multi_set_when_redis_exceptions()
    {
        var value = _fixture.Create<string>();
        _database.CreateTransaction(Arg.Any<object?>()).Throws(new RedisException("test"));
        var actualResponse = await Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, value), new(_multiKey, value) }, _fixture.Create<TimeSpan>(), policy: null, token: testContextAccessor.Current.CancellationToken);
        actualResponse.Should().BeFalse();
    }



    [Theory]
    [InlineData("")]
    [InlineData("    ")]
    public async Task Set_invalid_key(string key)
    {
        CacheKey cacheKey = key;
        Func<Task> act = async () => { await Sut.SetAsync(cacheKey, _fixture.Create<string>(), _fixture.Create<TimeSpan>()); };
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Set_redis_exception()
    {
        var value = _fixture.Create<string>();
        var expiration = _fixture.Create<TimeSpan>();
        _database.StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), CommandFlags.DemandMaster)
                .ThrowsAsync(ci =>
                {
                    return new RedisException("test");
                });
        var actualResponse = await Sut.SetAsync(_cacheKey, value, expiration, token: testContextAccessor.Current.CancellationToken);
        actualResponse.Should().BeFalse();
    }

    [Fact]
    public async Task Set_default_key()
    {
        var value = default(string);
        var expiration = _fixture.Create<TimeSpan>();
        var apiResponse = _fixture.Create<bool>();
        _database.KeyDeleteAsync(_redisKey, CommandFlags.DemandMaster)
                .Returns(ci =>
                {
                    return Task.FromResult(apiResponse);
                });
        var actualResponse = await Sut.SetAsync(_cacheKey, value, expiration, token: testContextAccessor.Current.CancellationToken);
        actualResponse.Should().Be(true);
    }


    [Fact]
    public async Task Contains_works_as_expected()
    {
        var cacheKeyCalled = false;
        _database.KeyExistsAsync(_redisKey, CommandFlags.PreferMaster)
            .Returns(ci =>
            {
                cacheKeyCalled = true;
                return Task.FromResult(true);
            });

        var actual = await Sut.ContainsAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        cacheKeyCalled.Should().BeTrue();
        actual.Should().BeTrue();
    }

    [Fact]
    public async Task Contains_redis_exception()
    {
        _database.KeyExistsAsync(Arg.Any<RedisKey>(), CommandFlags.PreferMaster)
            .ThrowsAsync(new Exception());

        var actual = await Sut.ContainsAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Read_ExpireTime_For_Unknown_Key_v7()
    {
        var wasCalled = false;
        _version = new(7,0);
        _database.KeyExpireTimeAsync(_redisKey, Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                wasCalled = true;
                return Task.FromResult(default(DateTime?));
            });
        var actual = await Sut.ExpireTimeAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        wasCalled.Should().BeTrue();
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Read_ExpireTime_For_Unknown_Key()
    {
        _database.KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>())
            .Returns(default(TimeSpan?));
        var actual = await Sut.ExpireTimeAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        await _database.Received(1).KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>());
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Read_ExpireTime_For_Known_Key_v7()
    {
        _version = new(7, 0);
        DateTimeOffset? expected = _fixture.Create<DateTimeOffset>();
        _database.KeyExpireTimeAsync(_redisKey, Arg.Any<CommandFlags>())
            .Returns((DateTime?)expected.GetValueOrDefault().UtcDateTime);
        var actual = await Sut.ExpireTimeAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        await _database.Received(1).KeyExpireTimeAsync(_redisKey, Arg.Any<CommandFlags>());
        actual.Should().Be(expected.GetValueOrDefault());
    }

    [Fact]
    public async Task Read_ExpireTime_For_Known_Key()
    {
        TimeSpan? expected = _fixture.Create<TimeSpan>();
        _database.KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>())
            .Returns(expected);
        var actual = await Sut.ExpireTimeAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        await _database.Received(1).KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>());
        actual.Should().Be(_clock.UtcNow.Add(expected.Value));
    }

    [Fact]
    public async Task Read_TimeToLive_For_Unknown_Key()
    {
        _database.KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>())
            .Returns(default(TimeSpan?));
        var actual = await Sut.TimeToLiveAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        await _database.Received(1).KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>());
        actual.Should().BeNull();
    }

    [Fact]
    public async Task Read_TimeToLive_For_Known_Key()
    {
        TimeSpan? expected = _fixture.Create<TimeSpan>();
        _database.KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>())
            .Returns(expected);
        var actual = await Sut.TimeToLiveAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        await _database.Received(1).KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>());
        actual.Should().Be(expected.GetValueOrDefault());
    }

    [Fact]
    public async Task TimeToLive_for_a_null_key_is_answered_as_a_miss()
    {
        var actual = await Sut.TimeToLiveAsync<string>(CacheKey.Null, testContextAccessor.Current.CancellationToken);

        actual.Should().BeNull();
        await _database.DidNotReceive().KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        MissCount.Should().Be(1);
    }

    [Fact]
    public async Task ExpireTime_with_a_cancelled_token_is_answered_as_a_miss()
    {
        var actual = await Sut.ExpireTimeAsync<string>(_cacheKey, new CancellationToken(canceled: true));

        actual.Should().BeNull();
        await _database.DidNotReceive().KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        await _database.DidNotReceive().KeyExpireTimeAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        MissCount.Should().Be(1);
    }

    [Fact]
    public async Task Read_TimeToLive_throws_Exception()
    {
        _database.KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>())
            .ThrowsAsync<Exception>();
        var actual = await Sut.TimeToLiveAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        await _database.Received(1).KeyTimeToLiveAsync(_redisKey, Arg.Any<CommandFlags>());
        actual.Should().Be(null);
    }

    [Fact]
    public async Task Read_ExpireTime_throw_exception_v7()
    {
        _version = new(7, 0);
        _database.KeyExpireTimeAsync(_redisKey, Arg.Any<CommandFlags>())
            .ThrowsAsync(new Exception());
        var actual = await Sut.ExpireTimeAsync<string>(_cacheKey, testContextAccessor.Current.CancellationToken);
        await _database.Received(1).KeyExpireTimeAsync(_redisKey, Arg.Any<CommandFlags>());
        actual.Should().BeNull();
    }

    [Fact]
    public void Dispose_can_be_called()
    {
        Action act = () => Sut.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task GetCacheEntryAsync_returns_miss_for_RedisValue_Null()
    {
        _cacheOptions.CacheNullValues = true;
        _transaction.StringGetAsync(_redisKey, CommandFlags.PreferMaster)
            .Returns(RedisValue.Null);
        _transaction.KeyExpireTimeAsync(_redisKey, CommandFlags.PreferMaster).Returns((DateTime?)null);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var entry = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        entry.Found.Should().BeFalse();
    }

    [Fact]
    public async Task GetCacheEntryAsync_returns_cached_null_when_CacheNullValues_true_and_value_is_empty()
    {
        _cacheOptions.CacheNullValues = true;
        _transaction.StringGetAsync(_redisKey, CommandFlags.PreferMaster)
            .Returns(RedisValue.EmptyString);
        _transaction.KeyExpireTimeAsync(_redisKey, CommandFlags.PreferMaster).Returns(_now.AddMinutes(5).UtcDateTime);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var entry = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        entry.Found.Should().BeTrue("Length==0 with CacheNullValues=true is the explicit cached-null sentinel");
        entry.Value.Should().BeNull();
    }

    [Fact]
    public async Task GetCacheEntryAsync_returns_miss_for_empty_value_when_CacheNullValues_false()
    {
        _cacheOptions.CacheNullValues = false;
        _transaction.StringGetAsync(_redisKey, CommandFlags.PreferMaster)
            .Returns(RedisValue.EmptyString);
        _transaction.KeyExpireTimeAsync(_redisKey, CommandFlags.PreferMaster).Returns(_now.AddMinutes(5).UtcDateTime);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var entry = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);

        entry.Found.Should().BeFalse();
    }

    [Fact]
    public async Task GetCacheEntriesAsync_returns_cached_null_when_CacheNullValues_true_and_value_is_empty()
    {
        _cacheOptions.CacheNullValues = true;
        var expectedValue = _fixture.Create<string>();
        var expectedTtl = TimeSpan.FromMinutes(15);
        _transaction.StringGetAsync(Arg.Is<RedisKey[]>(k => k != null && k.Contains(_redisKey) && k.Contains(_redisMultiKey)), CommandFlags.PreferMaster)
            .Returns(new RedisValue[] { RedisValue.EmptyString, _serializer.Serialize(expectedValue) });
        _transaction.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), CommandFlags.PreferMaster).Returns(expectedTtl);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var entries = await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        entries.Should().HaveCount(2);
        entries[0].Value.Found.Should().BeTrue("Length==0 with CacheNullValues=true is the explicit cached-null sentinel in the batched path");
        entries[0].Value.Value.Should().BeNull();
        entries[0].Value.Expiration.Should().Be(_clock.UtcNow.Add(expectedTtl));
        entries[1].Value.Found.Should().BeTrue();
        entries[1].Value.Value.Should().Be(expectedValue);
    }

    [Fact]
    public async Task GetCacheEntriesAsync_returns_miss_for_empty_value_when_CacheNullValues_false()
    {
        _cacheOptions.CacheNullValues = false;
        _transaction.StringGetAsync(Arg.Is<RedisKey[]>(k => k != null && k.Contains(_redisKey) && k.Contains(_redisMultiKey)), CommandFlags.PreferMaster)
            .Returns(new RedisValue[] { RedisValue.EmptyString, RedisValue.EmptyString });
        _transaction.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), CommandFlags.PreferMaster).Returns(TimeSpan.FromMinutes(15));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);

        var entries = await Sut.GetCacheEntriesAsync<string>(new CacheKey[] { _cacheKey, _multiKey }, policy: null, token: testContextAccessor.Current.CancellationToken);

        entries.Should().HaveCount(2);
        entries.Should().AllSatisfy(kv => kv.Value.Found.Should().BeFalse("the empty-value sentinel must be ignored when the opt-in is off"));
    }

    [Fact]
    public async Task SetAsync_asks_a_memory_serializer_for_memory_instead_of_an_array()
    {
        var serializer = new RecordingRawSerializer();
        _fixture.Inject<ISerializerProxy<byte[]>>(serializer);
        RedisValue? captured = null;
        _database.StringSetAsync(_redisKey, Arg.Do<RedisValue>(v => captured = v), Arg.Any<TimeSpan?>(), When.Always, Arg.Any<CommandFlags>())
            .Returns(true);
        ReadOnlyMemory<byte> payload = new byte[] { 1, 2, 3 };

        var ok = await Sut.SetAsync(_cacheKey, payload, TimeSpan.FromMinutes(1), policy: null, token: testContextAccessor.Current.CancellationToken);

        ok.Should().BeTrue();
        serializer.MemoryCalls.Should().Be(1);
        serializer.ArrayCalls.Should().Be(0);
        ((byte[]?)captured!.Value).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task SetAsync_writes_empty_value_when_CacheNullValues_true_and_value_is_null()
    {
        _cacheOptions.CacheNullValues = true;
        RedisValue? captured = null;
        _database.StringSetAsync(_redisKey, Arg.Do<RedisValue>(v => captured = v), Arg.Any<TimeSpan?>(), When.Always, Arg.Any<CommandFlags>())
            .Returns(true);

        var ok = await Sut.SetAsync<string>(_cacheKey, value: null, policy: null, token: testContextAccessor.Current.CancellationToken);

        ok.Should().BeTrue();
        captured.HasValue.Should().BeTrue();
        captured!.Value.Length().Should().Be(0, "explicit null caching writes Redis EmptyString (Length==0), not a JSON 'null' literal");
        await _database.DidNotReceive().KeyDeleteAsync(_redisKey, Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task SetAsync_deletes_key_when_CacheNullValues_false_and_value_is_null()
    {
        _cacheOptions.CacheNullValues = false;
        var ok = await Sut.SetAsync<string>(_cacheKey, value: null, policy: null, token: testContextAccessor.Current.CancellationToken);

        ok.Should().BeTrue();
        await _database.Received().KeyDeleteAsync(_redisKey, Arg.Any<CommandFlags>());
        await _database.DidNotReceive().StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    /// <summary>
    /// The write used to accept a deadline in the past and turn it into a delete. Expiration is no
    /// longer nullable, so a past deadline is a bad argument rather than a shorthand, and it is
    /// rejected before the key is touched at all. (<c>SetInternalAsync</c> still guards a
    /// non-positive resolved duration — that path is now only reachable from a misconfigured
    /// provider default, not from a caller.)
    /// </summary>
    [Fact]
    public async Task SetAsync_rejects_a_past_expiration_even_when_CacheNullValues_true()
    {
        _cacheOptions.CacheNullValues = true;
        var pastExpiration = _clock.UtcNow.AddMinutes(-5);

        var act = async () => await Sut.SetAsync<string>(_cacheKey, value: null, pastExpiration, token: testContextAccessor.Current.CancellationToken);

        (await act.Should().ThrowAsync<ArgumentOutOfRangeException>()).And.ParamName.Should().Be("expiration");
        await _database.DidNotReceive().KeyDeleteAsync(_redisKey, Arg.Any<CommandFlags>());
        await _database.DidNotReceive().StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task GetOrAddAsync_returns_cached_null_without_invoking_generator_when_CacheNullValues_true()
    {
        _cacheOptions.CacheNullValues = true;
        _database.StringGetAsync(_redisKey, Arg.Is<CommandFlags>(f => f.HasFlag(CommandFlags.PreferMaster)))
            .Returns(RedisValue.EmptyString);
        var generatorCalled = false;

        var ret = await Sut.GetOrAddAsync<string>(
            _cacheKey,
            _ => { generatorCalled = true; return Task.FromResult<string?>("fresh"); },
            (CachePolicy?)null,
            testContextAccessor.Current.CancellationToken);

        ret.Should().BeNull();
        generatorCalled.Should().BeFalse("an explicit cached null must short-circuit the generator");
    }

    [Fact]
    public async Task GetOrAddAsync_stores_null_when_generator_yields_null_and_CacheNullValues_true()
    {
        _cacheOptions.CacheNullValues = true;
        _database.StringGetAsync(_redisKey, Arg.Is<CommandFlags>(f => f.HasFlag(CommandFlags.PreferMaster)))
            .Returns(RedisValue.Null);
        RedisValue? captured = null;
        _database.StringSetAsync(_redisKey, Arg.Do<RedisValue>(v => captured = v), Arg.Any<TimeSpan?>(), When.Always, Arg.Any<CommandFlags>())
            .Returns(true);

        var ret = await Sut.GetOrAddAsync<string>(
            _cacheKey,
            _ => Task.FromResult<string?>(null),
            (CachePolicy?)null,
            testContextAccessor.Current.CancellationToken);

        ret.Should().BeNull();
        captured.HasValue.Should().BeTrue("a null generator result must be persisted when CacheNullValues=true");
        captured!.Value.Length().Should().Be(0);
    }

    [Fact]
    public async Task GetOrAddAsync_does_not_store_null_when_generator_yields_null_and_CacheNullValues_false()
    {
        _cacheOptions.CacheNullValues = false;
        _database.StringGetAsync(_redisKey, Arg.Is<CommandFlags>(f => f.HasFlag(CommandFlags.PreferMaster)))
            .Returns(RedisValue.Null);

        var ret = await Sut.GetOrAddAsync<string>(
            _cacheKey,
            _ => Task.FromResult<string?>(null),
            (CachePolicy?)null,
            testContextAccessor.Current.CancellationToken);

        ret.Should().BeNull();
        await _database.DidNotReceiveWithAnyArgs().StringSetAsync(default, default, default, default, default);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask InitializeAsync()
    {
        _prefix = "test";
        _cacheKey = _fixture.Create<string>();
        _redisKey = string.Join(':', _prefix, RedisKeyspaces.String, _cacheKey).ToLowerInvariant();
        _multiKey = _fixture.Create<string>();
        _redisMultiKey = string.Join(':', _prefix, RedisKeyspaces.String, _multiKey).ToLowerInvariant();
        _clock = _fixture.Freeze<ISystemClock>();
        _fixture.Inject<TimeProvider>(new SystemClockTimeProvider(_clock));
        _clock.UtcNow.Returns(c => _now);
        _resiliencePipelineProvider = _fixture.Freeze<IResiliencePipelineProvider>();
        var noOpExecutor = new EmptyResiliencePipeline();
        _resiliencePipelineProvider.Get(ResiliencePipelineNames.Read).Returns(noOpExecutor);
        _resiliencePipelineProvider.Get(ResiliencePipelineNames.Write).Returns(noOpExecutor);
        _cacheKeyStrategy = _fixture.Create<ICacheKeyStrategy>();
        var redisKeyStrategyFactory = _fixture.Create<IRedisKeyStrategyFactory>();
        _redisKeyStrategy = _fixture.Create<IRedisKeyStrategy>();
        _redisKeyStrategy.GetRedisKey(_cacheKey).Returns(_redisKey);
        _redisKeyStrategy.GetRedisKey(_multiKey).Returns(_redisMultiKey);
        redisKeyStrategyFactory.Create(Arg.Any<CacheOptions>(), Arg.Any<Type>())
            .Returns(_redisKeyStrategy);
        _cacheKeyStrategy.GetCacheKey<string>(_cacheKey).Returns(_cacheKey);
        _cacheOptions = new RedisCacheOptions
        {
            CacheKeyStrategy = _cacheKeyStrategy,
            RedisKeyStrategyFactory = redisKeyStrategyFactory,
        };

        _database = _fixture.Freeze<IDatabase>();
        // A non-clustered server answers every key with the same slot; the fixture's auto-values would hand each one a random slot.
        _database.Multiplexer.GetHashSlot(Arg.Any<RedisKey>()).Returns(0);
        _transaction = _fixture.Freeze<ITransaction>();
        _database.CreateTransaction().Returns(_transaction);
        _serializer = new SystemJsonByteSerializerProxy();
        _fixture.Inject<ISerializerProxy<byte[]>>(_serializer);
        var opt = Options.Create(_cacheOptions);
        _fixture.Inject(opt);
        _fixture.Inject(_cacheOptions);
        _fixture.Inject<ICachingTelemetryProvider>(_telemetry);
        // Wires real ConnectionStateMonitor so disconnected-path tests can flip _isConnected.
        _fixture.Inject(new CacheOptions { AppShortName = "test", ConnectionMonitorEnabled = true });
        _connector = _fixture.Freeze<IRedisConnector>();
        _connector.Database.Returns(_ => _database);
        _connector.Version.Returns(_ => _version);
        _connector.IsConnected.Returns(ctx => _isConnected);
        return ValueTask.CompletedTask;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private async Task ReadThroughAFailingTransactionAsync(string marker, bool throws)
    {
        _version = new(6, 0);
        _transaction.StringGetAsync(_redisKey, CommandFlags.PreferMaster).Returns(_ => Task.FromException<RedisValue>(new RedisException(marker)));
        _transaction.KeyTimeToLiveAsync(_redisKey, CommandFlags.PreferMaster).Returns(_ => Task.FromException<TimeSpan?>(new RedisException(marker)));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(_ => throws ? Task.FromException<bool>(new RedisException("transaction failed")) : Task.FromResult(false));

        await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: testContextAccessor.Current.CancellationToken);
    }

    private void StubReads(out List<CommandFlags> flags)
    {
        var seen = new List<CommandFlags>();
        flags = seen;
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Do<CommandFlags>(seen.Add)).Returns(_ => (RedisValue)_serializer.Serialize("a"));
    }

    private void StubWrites()
    {
        _database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        _database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        _database.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns(1L);
    }

    private void StubReadScript(RedisValue value, long expireAtMilliseconds) =>
        _database.ScriptEvaluateReadOnlyAsync(Arg.Any<string>(), Arg.Is<RedisKey[]?>(k => k != null && k.Length == 1 && k[0] == _redisKey), Arg.Any<RedisValue[]?>(), CommandFlags.PreferMaster)
            .Returns(RedisResult.Create([RedisResult.Create(value), RedisResult.Create((RedisValue)expireAtMilliseconds)]));

    private void GiveKeysDifferentSlots()
    {
        _database.Multiplexer.GetHashSlot(_redisKey).Returns(1);
        _database.Multiplexer.GetHashSlot(_redisMultiKey).Returns(2);
    }

    private async Task GetOrAdd_works_as_expected(string? redisReturn, string? generatorReturn, bool expectedGeneratorCall, int stringSetCalls, Type expirationType)
    {
        var generatorWasCalled = false;
        _database.StringGetAsync(_redisKey, Arg.Is<CommandFlags>(f => f.HasFlag(CommandFlags.PreferMaster)))
            .Returns(_ => (RedisValue)_serializer.Serialize(redisReturn));

        string? actualValue = default;
        if (expirationType == typeof(TimeSpan))
        {
            actualValue = await Sut.GetOrAddAsync(_cacheKey, _ => { generatorWasCalled = true; return Task.FromResult(generatorReturn); }, _fixture.Create<TimeSpan>(), token: testContextAccessor.Current.CancellationToken);
        }
        else if (expirationType == typeof(DateTimeOffset))
        {
            actualValue = await Sut.GetOrAddAsync(_cacheKey, _ => { generatorWasCalled = true; return Task.FromResult(generatorReturn); }, expiration: _clock.UtcNow.Add(TimeSpan.FromSeconds(2)), token: testContextAccessor.Current.CancellationToken);
        }
        else
        {
            actualValue = await Sut.GetOrAddAsync(_cacheKey, _ => { generatorWasCalled = true; return Task.FromResult(generatorReturn); }, (CachePolicy?)null, testContextAccessor.Current.CancellationToken);
        }
        actualValue.Should().Be(redisReturn ?? generatorReturn);
        generatorWasCalled.Should().Be(expectedGeneratorCall);
        await _database.Received(stringSetCalls).StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    private async Task Set_works_as_expected(Type expirationType)
    {
        var value = _fixture.Create<string>();
        var expiration = _fixture.Create<TimeSpan>();
        var expectedResponse = _fixture.Create<bool>();
        _database.StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Is<CommandFlags>(f => f.HasFlag(CommandFlags.DemandMaster)))
                .Returns(_ => Task.FromResult(expectedResponse));
        bool? actualResponse = default;
        if (expirationType == typeof(TimeSpan))
        {
            actualResponse = await Sut.SetAsync(_cacheKey, value, _fixture.Create<TimeSpan>(), token: testContextAccessor.Current.CancellationToken);
        }
        else if (expirationType == typeof(DateTimeOffset))
        {
            actualResponse = await Sut.SetAsync(_cacheKey, value, DateTimeOffset.UtcNow.AddMinutes(5), token: testContextAccessor.Current.CancellationToken);
        }
        else
        {
            actualResponse = await Sut.SetAsync(_cacheKey, value, policy: null, token: testContextAccessor.Current.CancellationToken);
        }

        actualResponse.Should().Be(expectedResponse);
        await _database.Received(1).StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Is<CommandFlags>(f => f.HasFlag(CommandFlags.DemandMaster)));
    }

    private async Task Multi_set_works_as_expected(Type expirationType)
    {
        var value = _fixture.Create<string?>();
        var expiration = _fixture.Create<TimeSpan>();
        var expectedResponse = _fixture.Create<bool>();
        _transaction.StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Is<CommandFlags>(f => f.HasFlag(CommandFlags.DemandMaster)))
                .Returns(_ => Task.FromResult(expectedResponse));
        _transaction.StringSetAsync(_redisMultiKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Is<CommandFlags>(f => f.HasFlag(CommandFlags.DemandMaster)))
                .Returns(_ => Task.FromResult(expectedResponse));
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(_ => Task.FromResult(expectedResponse));
        bool? actualResponse = default;
        if (expirationType == typeof(TimeSpan))
        {
            actualResponse = await Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, value), new(_multiKey, value) }, _fixture.Create<TimeSpan>(), policy: null, token: testContextAccessor.Current.CancellationToken);
        }
        else if (expirationType == typeof(DateTimeOffset))
        {
            actualResponse = await Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, value), new(_multiKey, value) }, DateTimeOffset.UtcNow.AddMinutes(5), policy: null, token: testContextAccessor.Current.CancellationToken);
        }
        else
        {
            actualResponse = await Sut.SetAsync(new KeyValuePair<CacheKey, string?>[] { new(_cacheKey, value), new(_multiKey, value) }, policy: null, token: testContextAccessor.Current.CancellationToken);
        }

        actualResponse.Should().Be(expectedResponse);
        await _transaction.Received(1).StringSetAsync(_redisKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Is<CommandFlags>(f => f.HasFlag(CommandFlags.DemandMaster)));
        await _transaction.Received(1).StringSetAsync(_redisMultiKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Is<CommandFlags>(f => f.HasFlag(CommandFlags.DemandMaster)));
    }

    private sealed class RecordingRawSerializer : RawByteSerializerProxy
    {
        public int ArrayCalls { get; private set; }

        public int MemoryCalls { get; private set; }

        public override byte[]? Serialize(object? value)
        {
            ArrayCalls++;
            return base.Serialize(value);
        }

        public override ReadOnlyMemory<byte> SerializeToMemory<T>(T? value) where T : default
        {
            MemoryCalls++;
            return base.SerializeToMemory(value);
        }
    }

    /// <summary>Gives up on a callback that outlasts its timeout and returns the default, as a timeout behind a fallback does.</summary>
    private sealed class Suffix(string suffix)
    {
        public int Runs { get; private set; }

        public Task<KeyValuePair<string, string?>[]> Apply(string[] states)
        {
            Runs++;
            return Task.FromResult(Array.ConvertAll(states, s => new KeyValuePair<string, string?>(s, s + suffix)));
        }
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
}
