using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Policies;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.Broadcast;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class RedisStreamGapIntegrationTests(RedisContainerFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);
    private readonly ManualTimeProvider _clock = new(DateTimeOffset.UnixEpoch);
    private readonly RedisKey _stream = $"it:gap:{Guid.NewGuid():N}";
    private readonly RedisValue _group = "node";
    private readonly IConnectionState _state = Substitute.For<IConnectionState>();
    private readonly CancellationTokenSource _stop = new();
    private ObservedFetchWaiter? _waiter;
    private RedisConnector? _connector;
    private RedisStreamSubjectWriter<ICacheEvent>? _sut;
    private int _missed;
    private volatile bool _connected = true;
    private int _offlineChecks;

    private IDatabase Db => _connector!.Database;

    [Fact]
    public async Task Entries_trimmed_during_an_outage_before_they_were_read_are_reported()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        await StartReadingAsync();

        await GoOfflineAsync();
        await AddAsync(5);
        await Db.StreamTrimAsync(_stream, maxLength: 2);
        Reconnect();

        (await AfterReadAsync(() => Volatile.Read(ref _missed) == 1)).Should().BeTrue();
    }

    [Fact]
    public async Task Entries_trimmed_between_two_polls_before_they_were_read_are_reported()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        await StartReadingAsync();

        // One script, so no poll lands between the adds and the trim.
        await Db.ScriptEvaluateAsync(
            "for i = 1, 5 do redis.call('XADD', KEYS[1], '*', 'f', 'v') end redis.call('XTRIM', KEYS[1], 'MAXLEN', 2)",
            [_stream]);

        (await AfterReadAsync(() => Volatile.Read(ref _missed) == 1)).Should().BeTrue("the connection never dropped, so only a checked read can see it");
    }

    [Fact]
    public async Task A_stream_trimmed_empty_is_reported_once_however_often_it_is_polled()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        Assert.SkipWhen(fixture.ServerVersion.Major < 7, "Counts entries with ENTRIESREAD and max-deleted-entry-id, which Redis 7.0 added.");
        await StartReadingAsync();

        await Db.ScriptEvaluateAsync(
            "for i = 1, 5 do redis.call('XADD', KEYS[1], '*', 'f', 'v') end redis.call('XTRIM', KEYS[1], 'MAXLEN', 0)",
            [_stream]);
        (await AfterReadAsync(() => Volatile.Read(ref _missed) == 1)).Should().BeTrue();
        await PollAsync(15);
        Volatile.Read(ref _missed).Should().Be(1, "an empty read leaves the group where it was, so later checks find the same gap");

        var next = await AddAsync(1);
        (await AfterReadAsync(async () => await LastDeliveredAsync() == next)).Should().BeTrue();
        Volatile.Read(ref _missed).Should().Be(1, "the entry added after the trim was not lost");
    }

    [Fact]
    public async Task A_new_groups_first_entries_are_not_a_gap()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        Assert.SkipWhen(fixture.ServerVersion.Major < 7, "Counts entries with ENTRIESREAD and max-deleted-entry-id, which Redis 7.0 added.");
        StartWriter();

        // Reads that find nothing put the group in use before it has read an entry.
        await PollAsync(5);
        var first = await AddAsync(1);

        (await AfterReadAsync(async () => await LastDeliveredAsync() == first)).Should().BeTrue();
        Volatile.Read(ref _missed).Should().Be(0, "a group that has not read yet counts nothing read, which no trim explains");
    }

    [Fact]
    public async Task A_group_created_after_the_history_was_trimmed_away_reports_nothing_for_its_first_entries()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        Assert.SkipWhen(fixture.ServerVersion.Major < 7, "Counts entries with ENTRIESREAD and max-deleted-entry-id, which Redis 7.0 added.");
        Assert.SkipUnless(fixture.ReportsEntriesRead, "A group made by another node has only the server's count, which this server does not keep.");
        await Db.StreamDeleteConsumerGroupAsync(_stream, _group);
        await AddAsync(5);
        await Db.StreamTrimAsync(_stream, maxLength: 0);
        await ConsumerGroups.CreateAsync(Db, _stream, _group, StreamPosition.NewMessages, await ConsumerGroups.HeadAsync(Db, _stream, _clock));
        var first = await AddAsync(1);

        StartSubscribedOffline();
        Reconnect();

        (await AfterReadAsync(async () => await LastDeliveredAsync() == first)).Should().BeTrue();
        Volatile.Read(ref _missed).Should().Be(0, "the entries trimmed before the group existed were never its to read");
    }

    [Fact]
    public async Task A_group_this_node_makes_after_the_history_was_trimmed_away_reports_nothing_for_its_first_entries()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        Assert.SkipWhen(fixture.ServerVersion.Major < 7, "Counts entries with ENTRIESREAD and max-deleted-entry-id, which Redis 7.0 added.");
        await Db.StreamDeleteConsumerGroupAsync(_stream, _group);
        await AddAsync(5);
        await Db.StreamTrimAsync(_stream, maxLength: 0);

        // The node makes the group itself, so it knows the count it made it with even where the server does not report it.
        StartWriter();
        (await AfterReadAsync(async () => Array.Exists(await Db.StreamGroupInfoAsync(_stream), g => g.Name == _group))).Should().BeTrue();
        var first = await AddAsync(1);

        (await AfterReadAsync(async () => await LastDeliveredAsync() == first)).Should().BeTrue();
        Volatile.Read(ref _missed).Should().Be(0, "the entries trimmed before the group existed were never its to read");
    }

    [Fact]
    public async Task Entries_trimmed_before_a_new_group_first_read_are_reported()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        StartSubscribedOffline();
        await Db.ScriptEvaluateAsync(
            "for i = 1, 5 do redis.call('XADD', KEYS[1], '*', 'f', 'v') end redis.call('XTRIM', KEYS[1], 'MAXLEN', 2)",
            [_stream]);
        Reconnect();

        (await AfterReadAsync(() => Volatile.Read(ref _missed) == 1)).Should().BeTrue("no poll ran before the trim, so only the count the group was created with shows it");
    }

    [Fact]
    public async Task StackExchange_Redis_reads_a_new_groups_unknown_count_as_zero()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        await AddAsync(3);
        await Db.StreamCreateConsumerGroupAsync(_stream, "fresh", StreamPosition.NewMessages);

        Array.Find(await Db.StreamGroupInfoAsync(_stream), g => g.Name == "fresh").EntriesRead.Should().Be(0, "XINFO GROUPS replies nil, which the gap check takes as unknown");
    }

    [Fact]
    public async Task Entries_kept_through_an_outage_are_replayed_without_a_report()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        Assert.SkipWhen(fixture.ServerVersion.Major < 7, "Counts entries with ENTRIESREAD and max-deleted-entry-id, which Redis 7.0 added.");
        await StartReadingAsync();

        await GoOfflineAsync();
        var last = await AddAsync(5);
        Reconnect();

        (await AfterReadAsync(async () => await LastDeliveredAsync() == last)).Should().BeTrue("the stream still held them");
        Volatile.Read(ref _missed).Should().Be(0);
    }

    [Fact]
    public async Task Trimming_entries_already_read_is_not_reported()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        Assert.SkipWhen(fixture.ServerVersion.Major < 7, "Counts entries with ENTRIESREAD and max-deleted-entry-id, which Redis 7.0 added.");
        await StartReadingAsync();
        var last = await AddAsync(3);
        (await AfterReadAsync(async () => await LastDeliveredAsync() == last)).Should().BeTrue();

        await GoOfflineAsync();
        await Db.StreamTrimAsync(_stream, maxLength: 0);
        var next = await AddAsync(1);
        Reconnect();

        (await AfterReadAsync(async () => await LastDeliveredAsync() == next)).Should().BeTrue();
        Volatile.Read(ref _missed).Should().Be(0, "the entry added after the trim is the only one not yet read");
    }

    [Fact]
    public async Task A_stream_removed_while_in_use_is_reported_once()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        await StartReadingAsync();

        await Db.KeyDeleteAsync(_stream);

        (await AfterReadAsync(() => Volatile.Read(ref _missed) == 1)).Should().BeTrue();
        await PollAsync(15);
        Volatile.Read(ref _missed).Should().Be(1, "the recreated group is read again before another loss counts");
    }

    [Fact]
    public async Task A_group_removed_while_in_use_is_reported_once()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        await StartReadingAsync();

        await Db.StreamDeleteConsumerGroupAsync(_stream, _group);

        (await AfterReadAsync(() => Volatile.Read(ref _missed) == 1)).Should().BeTrue("the script's read fails with NOGROUP while the stream remains");
        await PollAsync(15);
        Volatile.Read(ref _missed).Should().Be(1);
    }

    [Fact]
    public async Task A_topic_that_lost_entries_expires_what_it_keeps()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        var cacheOptions = new CacheOptions { Enabled = true, AppShortName = $"it{Guid.NewGuid():N}"[..10], SourceUri = new Uri("urn:node") };
        var options = new RedisStreamsTopicOptions { Enabled = true, PollInterval = PollInterval, NotifyEnabled = false };
        var stream = new PrefixStrategy(RedisKeyspaces.Streams, cacheOptions).GetRedisKey("orders");
        using var topic = new RedisStreamsTopic<ICacheEvent>(
            "orders",
            _state,
            _connector!,
            () => new KeyedSubject<ICacheEvent>(NullLogger.Instance),
            Substitute.For<IEventFormatterProxy<ICacheEvent>>(),
            Substitute.For<IResiliencePipelineProvider>(),
            options,
            cacheOptions,
            NullLogger<RedisStreamsTopic<ICacheEvent>>.Instance,
            NullTelemetryProvider.Instance,
            NullRedisProfiler.Instance,
            _stop.Token,
            clock: _clock);
        var entry = new KeptEntry();
        using var subscription = topic.Subscribe(entry);
        await BroadcastNodeSignals.ReadyAsync(topic, _clock);

        try
        {
            var id = await Db.StreamAddAsync(stream, options.FieldName, "v");
            (await AfterReadAsync(async () => Array.Find(await Db.StreamGroupInfoAsync(stream), g => g.Name == "urn:node").LastDeliveredId == id)).Should().BeTrue();
            await Db.KeyDeleteAsync(stream);

            await PollAsync();
            await entry.Expired.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            await Db.KeyDeleteAsync(stream);
        }
    }

    [Fact]
    public async Task The_first_entry_of_a_group_made_on_an_empty_stream_is_not_a_gap_before_Redis_7()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        Assert.SkipWhen(fixture.ServerVersion.Major >= 7, "Redis 7.0 records max-deleted-entry-id, so it never mistakes the first entry for a trim.");
        await Db.StreamDeleteConsumerGroupAsync(_stream, _group);
        StartWriter();
        _sut!.MarkSubscribed();
        (await AfterReadAsync(async () => (await Db.StreamGroupInfoAsync(_stream)).Any(g => g.Name == _group))).Should().BeTrue("the writer makes the group on the empty stream");
        _state.OnConnectionRestored += Raise.Event<EventHandler>(_state, EventArgs.Empty);
        var first = await AddAsync(1);

        (await AfterReadAsync(async () => await LastDeliveredAsync() == first)).Should().BeTrue();
        await PollAsync(5);
        Volatile.Read(ref _missed).Should().Be(0);
    }

    public async ValueTask InitializeAsync()
    {
        if (!fixture.Enabled || !fixture.StreamsUsable)
        {
            return;
        }

        var options = Options.Create(new RedisConnectionOptions { ConnectionString = fixture.ConnectionString, EnableHangDetection = false });
        _connector = new RedisConnector(
            NullTelemetryProvider.Instance,
            new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, options),
            new ConnectionMultiplexerFactory(options, NullRedisProfiler.Instance),
            options);
        await _connector.ConnectAsync(TestContext.Current.CancellationToken);
        await ConsumerGroups.CreateAsync(Db, _stream, _group, StreamPosition.NewMessages, await ConsumerGroups.HeadAsync(Db, _stream, _clock));
        _state.IsConnected.Returns(_ =>
        {
            if (_connected)
            {
                return true;
            }

            Interlocked.Increment(ref _offlineChecks);
            return false;
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _sut?.Dispose();
        if (_connector is not null)
        {
            await Db.KeyDeleteAsync(_stream);
            _connector.Dispose();
        }

        _stop.Dispose();
    }

    private Task<bool> AfterReadAsync(Func<bool> condition) => AfterReadAsync(() => Task.FromResult(condition()));

    private async Task<bool> AfterReadAsync(Func<Task<bool>> condition)
    {
        await PollAsync();
        return await condition();
    }

    private async Task PollAsync(int count = 1)
    {
        for (var poll = 0; poll < count; poll++)
        {
            _clock.Advance(PollInterval);
            if (_waiter is not null)
            {
                await _waiter.ReadAsync();
            }
            else
            {
                var timers = _clock.TimersCreated;
                await _clock.WaitForTimersAsync(timers + 1);
            }
        }
    }

    // Added before the writer starts: an empty first read would put the group in use at 0-0, where a server below 7.0 takes the entry for a trim.
    private async Task StartReadingAsync()
    {
        var first = await AddAsync(1);
        StartWriter();
        (await AfterReadAsync(async () => await LastDeliveredAsync() == first)).Should().BeTrue("the group is in use once it has read");
    }

    // Subscribed before the writer could read, as a topic does, so its first read is checked.
    private void StartSubscribedOffline()
    {
        _connected = false;
        StartWriter();
        _sut!.MarkSubscribed();
    }

    private void StartWriter()
    {
        var context = new RedisStreamContext(_stream, "f", _group, _group, new Uri("urn:node"), 100, PollInterval, false, false);
        _waiter = new ObservedFetchWaiter(PollInterval, _clock);
        _sut = new RedisStreamSubjectWriter<ICacheEvent>(
            context,
            _state,
            _connector!,
            Channel.CreateUnbounded<ICacheEvent>().Writer,
            Substitute.For<IEventFormatterProxy<ICacheEvent>>(),
            NullLogger.Instance,
            NullTelemetryProvider.Instance,
            NullRedisProfiler.Instance,
            _waiter,
            () => Interlocked.Increment(ref _missed),
            _stop.Token,
            clock: _clock);
    }

    private async Task<RedisValue> AddAsync(int count)
    {
        RedisValue id = default;
        for (var i = 0; i < count; i++)
        {
            id = await Db.StreamAddAsync(_stream, "f", "v");
        }

        return id;
    }

    private async Task<RedisValue> LastDeliveredAsync()
    {
        var groups = await Db.StreamGroupInfoAsync(_stream);
        return Array.Find(groups, g => g.Name == _group).LastDeliveredId;
    }

    // Waits for the reader to see the outage itself: a read already past its connection check would otherwise still run.
    private async Task GoOfflineAsync()
    {
        var seen = Volatile.Read(ref _offlineChecks);
        _connected = false;
        (await AfterReadAsync(() => Volatile.Read(ref _offlineChecks) > seen)).Should().BeTrue("the reader parks before the outage's entries are added");
    }

    private void Reconnect()
    {
        _connected = true;
        _state.OnConnectionRestored += Raise.Event<EventHandler>(_state, EventArgs.Empty);
    }

    private sealed class KeptEntry : IKeyedObserver<ICacheEvent>, IMissedEventsObserver
    {
        public TaskCompletionSource Expired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Key => "order:1";

        public void OnNext(ICacheEvent value)
        {
        }

        public void OnEventsMissed(MissedEventsReason reason) => Expired.TrySetResult();

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
