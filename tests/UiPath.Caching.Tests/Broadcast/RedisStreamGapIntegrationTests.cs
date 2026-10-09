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
    private readonly RedisKey _stream = $"it:gap:{Guid.NewGuid():N}";
    private readonly RedisValue _group = "node";
    private readonly IConnectionState _state = Substitute.For<IConnectionState>();
    private readonly CancellationTokenSource _stop = new();
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
        await StartReadingAsync();

        await GoOfflineAsync();
        await AddAsync(5);
        await Db.StreamTrimAsync(_stream, maxLength: 2);
        Reconnect();

        (await EventuallyAsync(() => Volatile.Read(ref _missed) == 1)).Should().BeTrue();
    }

    [Fact]
    public async Task Entries_trimmed_between_two_polls_before_they_were_read_are_reported()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await StartReadingAsync();

        // One script, so no poll lands between the adds and the trim.
        await Db.ScriptEvaluateAsync(
            "for i = 1, 5 do redis.call('XADD', KEYS[1], '*', 'f', 'v') end redis.call('XTRIM', KEYS[1], 'MAXLEN', 2)",
            [_stream]);

        (await EventuallyAsync(() => Volatile.Read(ref _missed) == 1)).Should().BeTrue("the connection never dropped, so only a checked read can see it");
    }

    [Fact]
    public async Task A_stream_trimmed_empty_is_reported_once_however_often_it_is_polled()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await StartReadingAsync();

        await Db.ScriptEvaluateAsync(
            "for i = 1, 5 do redis.call('XADD', KEYS[1], '*', 'f', 'v') end redis.call('XTRIM', KEYS[1], 'MAXLEN', 0)",
            [_stream]);
        (await EventuallyAsync(() => Volatile.Read(ref _missed) == 1)).Should().BeTrue();
        await Task.Delay(PollInterval * 15, TestContext.Current.CancellationToken);
        Volatile.Read(ref _missed).Should().Be(1, "an empty read leaves the group where it was, so later checks find the same gap");

        var next = await AddAsync(1);
        (await EventuallyAsync(async () => await LastDeliveredAsync() == next)).Should().BeTrue();
        Volatile.Read(ref _missed).Should().Be(1, "the entry added after the trim was not lost");
    }

    [Fact]
    public async Task A_new_groups_first_entries_are_not_a_gap()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        StartWriter();

        // Reads that find nothing put the group in use before it has read an entry.
        await Task.Delay(PollInterval * 5, TestContext.Current.CancellationToken);
        var first = await AddAsync(1);

        (await EventuallyAsync(async () => await LastDeliveredAsync() == first)).Should().BeTrue();
        Volatile.Read(ref _missed).Should().Be(0, "a group that has not read yet counts nothing read, which no trim explains");
    }

    [Fact]
    public async Task A_group_created_after_the_history_was_trimmed_away_reports_nothing_for_its_first_entries()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await Db.StreamDeleteConsumerGroupAsync(_stream, _group);
        await AddAsync(5);
        await Db.StreamTrimAsync(_stream, maxLength: 0);
        await ConsumerGroups.CreateAsync(Db, _stream, _group, StreamPosition.NewMessages);
        var first = await AddAsync(1);

        StartSubscribedOffline();
        Reconnect();

        (await EventuallyAsync(async () => await LastDeliveredAsync() == first)).Should().BeTrue();
        Volatile.Read(ref _missed).Should().Be(0, "the entries trimmed before the group existed were never its to read");
    }

    [Fact]
    public async Task Entries_trimmed_before_a_new_group_first_read_are_reported()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        StartSubscribedOffline();
        await Db.ScriptEvaluateAsync(
            "for i = 1, 5 do redis.call('XADD', KEYS[1], '*', 'f', 'v') end redis.call('XTRIM', KEYS[1], 'MAXLEN', 2)",
            [_stream]);
        Reconnect();

        (await EventuallyAsync(() => Volatile.Read(ref _missed) == 1)).Should().BeTrue("no poll ran before the trim, so only the count the group was created with shows it");
    }

    [Fact]
    public async Task StackExchange_Redis_reads_a_new_groups_unknown_count_as_zero()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await AddAsync(3);
        await Db.StreamCreateConsumerGroupAsync(_stream, "fresh", StreamPosition.NewMessages);

        Array.Find(await Db.StreamGroupInfoAsync(_stream), g => g.Name == "fresh").EntriesRead.Should().Be(0, "XINFO GROUPS replies nil, which the gap check takes as unknown");
    }

    [Fact]
    public async Task Entries_kept_through_an_outage_are_replayed_without_a_report()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await StartReadingAsync();

        await GoOfflineAsync();
        var last = await AddAsync(5);
        Reconnect();

        (await EventuallyAsync(async () => await LastDeliveredAsync() == last)).Should().BeTrue("the stream still held them");
        Volatile.Read(ref _missed).Should().Be(0);
    }

    [Fact]
    public async Task Trimming_entries_already_read_is_not_reported()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await StartReadingAsync();
        var last = await AddAsync(3);
        (await EventuallyAsync(async () => await LastDeliveredAsync() == last)).Should().BeTrue();

        await GoOfflineAsync();
        await Db.StreamTrimAsync(_stream, maxLength: 0);
        var next = await AddAsync(1);
        Reconnect();

        (await EventuallyAsync(async () => await LastDeliveredAsync() == next)).Should().BeTrue();
        Volatile.Read(ref _missed).Should().Be(0, "the entry added after the trim is the only one not yet read");
    }

    [Fact]
    public async Task A_stream_removed_while_in_use_is_reported_once()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await StartReadingAsync();

        await Db.KeyDeleteAsync(_stream);

        (await EventuallyAsync(() => Volatile.Read(ref _missed) == 1)).Should().BeTrue();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Volatile.Read(ref _missed).Should().Be(1, "the recreated group is read again before another loss counts");
    }

    [Fact]
    public async Task A_group_removed_while_in_use_is_reported_once()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await StartReadingAsync();

        await Db.StreamDeleteConsumerGroupAsync(_stream, _group);

        (await EventuallyAsync(() => Volatile.Read(ref _missed) == 1)).Should().BeTrue("the script's read fails with NOGROUP while the stream remains");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Volatile.Read(ref _missed).Should().Be(1);
    }

    [Fact]
    public async Task A_topic_that_lost_entries_expires_what_it_keeps()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cacheOptions = new CacheOptions { Enabled = true, AppShortName = $"it{Guid.NewGuid():N}"[..10], SourceUri = new Uri("urn:node") };
        var options = new RedisStreamsTopicOptions { Enabled = true, PollInterval = PollInterval };
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
            _stop.Token);
        var entry = new KeptEntry();
        using var subscription = topic.Subscribe(entry);

        try
        {
            var id = await Db.StreamAddAsync(stream, options.FieldName, "v");
            (await EventuallyAsync(async () => Array.Find(await Db.StreamGroupInfoAsync(stream), g => g.Name == "urn:node").LastDeliveredId == id)).Should().BeTrue();
            await Db.KeyDeleteAsync(stream);

            (await Task.WhenAny(entry.Expired.Task, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))).Should().BeSameAs(entry.Expired.Task);
        }
        finally
        {
            await Db.KeyDeleteAsync(stream);
        }
    }

    public async ValueTask InitializeAsync()
    {
        if (!fixture.Enabled)
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
        await ConsumerGroups.CreateAsync(Db, _stream, _group, StreamPosition.NewMessages);
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

    private static Task<bool> EventuallyAsync(Func<bool> condition) => EventuallyAsync(() => Task.FromResult(condition()));

    private static async Task<bool> EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        return false;
    }

    private async Task StartReadingAsync()
    {
        StartWriter();
        var first = await AddAsync(1);
        (await EventuallyAsync(async () => await LastDeliveredAsync() == first)).Should().BeTrue("the group is in use once it has read");
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
        _sut = new RedisStreamSubjectWriter<ICacheEvent>(
            context,
            _state,
            _connector!,
            Channel.CreateUnbounded<ICacheEvent>().Writer,
            Substitute.For<IEventFormatterProxy<ICacheEvent>>(),
            NullLogger.Instance,
            NullTelemetryProvider.Instance,
            NullRedisProfiler.Instance,
            new TimedFetchWaiter(PollInterval),
            () => Interlocked.Increment(ref _missed),
            _stop.Token);
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
        (await EventuallyAsync(() => Volatile.Read(ref _offlineChecks) > seen)).Should().BeTrue("the reader parks before the outage's entries are added");
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
