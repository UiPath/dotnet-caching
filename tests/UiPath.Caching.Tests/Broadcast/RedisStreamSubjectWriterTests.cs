using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;
using NSubstitute.Extensions;
using NSubstitute.ReceivedExtensions;
using StackExchange.Redis;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Broadcast;

public class RedisStreamSubjectWriterTests : IAsyncLifetime
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan NogroupPollTimeout = TimeSpan.FromSeconds(30);

    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private IEventFormatterProxy<ICacheEvent> _formatter = default!;
    private CancellationTokenSource _cancellationTokenSource = default!;
    private IDatabase _database = default!;
    private ILogger _logger = default!;
    private RedisStreamContext _context = default!;
    private RedisKey _topic = default!;
    private RedisValue _fieldName = default!;
    private RedisValue _consumerName = default!;
    private RedisValue _consumerGroup = default!;
    private Uri _sourceUri = default!;
    private int _pollBatchSize = default!;
    private TimeSpan _pollInterval = default!;
    private RedisStreamSubjectWriter<ICacheEvent>? _sut;
    private RedisStreamSubjectWriter<ICacheEvent> Sut => _sut ??= _fixture.Create<RedisStreamSubjectWriter<ICacheEvent>>();

    [Fact]
    public async Task Reiceive_redis_null()
    {
        var entries = new[] { StreamEntry.Null };
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", 100)
            .ReturnsForAnyArgs(_ => entries);

        Func<Task> act = async () => await Sut.FetchTask;
        await act.Should().NotCompleteWithinAsync(500.Microseconds());
        _cancellationTokenSource.Cancel();
        _formatter.Received(0).Decode(Arg.Any<ReadOnlyMemory<byte>>());
    }

    [Fact]
    public async Task Reiceive_redis_no_messages()
    {
        var entries = Array.Empty<StreamEntry>();
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", 100)
            .ReturnsForAnyArgs(_ => entries);

        Func<Task> act = async () => await Sut.FetchTask;
        await act.Should().NotCompleteWithinAsync(_pollInterval.Multiply(5));
        _cancellationTokenSource.Cancel();
        _formatter.Received(0).Decode(Arg.Any<ReadOnlyMemory<byte>>());
    }

    [Fact]
    public async Task StreamReadGroupAsync_Cancel_exceptions()
    {
        var entries = new[] { StreamEntry.Null };
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ThrowsAsyncForAnyArgs(_ => throw new TaskCanceledException(_fixture.Create<string>(), _fixture.Create<Exception>(), _cancellationTokenSource.Token));
 
        Func<Task> act = async () => await Sut.FetchTask;
        await act.Should().NotCompleteWithinAsync(500.Microseconds());
        _cancellationTokenSource.Cancel();
        _formatter.Received(0).Decode(Arg.Any<ReadOnlyMemory<byte>>());
    }

    [Fact]
    public async Task StreamReadGroupAsync_exceptions()
    {
        var entries = new[] { StreamEntry.Null };
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ThrowsAsyncForAnyArgs(_ => throw new Exception());

        Func<Task> act = async () => await Sut.FetchTask;
        await act.Should().NotCompleteWithinAsync(_pollInterval.Multiply(50));
        _cancellationTokenSource.Cancel();
        try
        {
            await act.Should().CompleteWithinAsync(_pollInterval.Multiply(200));
        }
        catch (Exception ex)
        {
            ex.Should().BeOfType<TaskCanceledException>();
        }
        _formatter.Received(0).Decode(Arg.Any<ReadOnlyMemory<byte>>());
    }

    [Fact]
    public async Task StreamReadGroupAsync_logger_exceptions()
    {
        var entries = new[] { StreamEntry.Null };
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ThrowsAsyncForAnyArgs(_ => throw new Exception());
        _logger.When(l =>l.Log(LogLevel.Error, Arg.Any<EventId>(), Arg.Any<object>(), Arg.Any<Exception?>(), Arg.Any<Func<object, Exception?, string>>()))
            .Do(ctx =>
            {
                if(ctx.Arg<LogLevel>() == LogLevel.Error)
                {
                    throw new Exception();
                }
            });

        var s = Sut;
        Func<Task> act = async () => await Sut.FetchTask;
        await act.Should().NotCompleteWithinAsync(_pollInterval.Multiply(5));
        _cancellationTokenSource.Cancel();
        _formatter.Received(0).Decode(Arg.Any<ReadOnlyMemory<byte>>());
    }

    [Fact]
    public async Task StreamReadGroupAsync_null_event()
    {
        var entries = new[] { new StreamEntry(_fixture.Create<string>(), new[] { new NameValueEntry(_fieldName, _fixture.Create<string>()) }) };
        var decodeCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns(_ =>
        {
            decodeCalled.TrySetResult(true);
            return default(ICacheEvent?);
        });
        SetupSingleBatch(entries);

        var fetchTask = Sut.FetchTask;
        await decodeCalled.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        _cancellationTokenSource.Cancel();
        await fetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        _formatter.Received().Decode(Arg.Any<ReadOnlyMemory<byte>>());
    }

    [Fact]
    public async Task StreamReadGroupAsync_invalid_event()
    {
        var entries = new[] { new StreamEntry(_fixture.Create<string>(), new[] { new NameValueEntry(_fieldName, _fixture.Create<string>()) }) };
        var decodeCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns(_ =>
        {
            decodeCalled.TrySetResult(true);
            return new TestCacheEvent { Valid = false };
        });
        SetupSingleBatch(entries);

        var fetchTask = Sut.FetchTask;
        await decodeCalled.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        _cancellationTokenSource.Cancel();
        await fetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        _formatter.Received().Decode(Arg.Any<ReadOnlyMemory<byte>>());
    }

    [Fact]
    public async Task StreamReadGroupAsync_valid_event()
    {
        var entries = new[] { new StreamEntry(_fixture.Create<string>(), new[] { new NameValueEntry(_fieldName, _fixture.Create<string>()) }) };
        var decodeCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns(_ =>
        {
            decodeCalled.TrySetResult(true);
            return new TestCacheEvent { Valid = true };
        });
        SetupSingleBatch(entries);

        var fetchTask = Sut.FetchTask;
        await decodeCalled.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        _cancellationTokenSource.Cancel();
        await fetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        _formatter.Received().Decode(Arg.Any<ReadOnlyMemory<byte>>());
    }

    [Fact]
    public async Task StreamReadGroupAsync_valid_event_subject_exception()
    {
        var entries = new[] { new StreamEntry(_fixture.Create<string>(), new[] { new NameValueEntry(_fieldName, _fixture.Create<string>()) }) };
        _formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns(new TestCacheEvent
        {
            Valid = true,
        });
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .Returns(_ => entries);

        Func<Task> act = async () => await Sut.FetchTask;
        await act.Should().NotCompleteWithinAsync(_pollInterval.Multiply(100));
        _cancellationTokenSource.Cancel();
        _formatter.Received().Decode(Arg.Any<ReadOnlyMemory<byte>>());
    }

    [Fact]
    public void Dispose_works_as_expected()
    {
        Action act = () => Sut.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var sut = Sut;
        sut.Dispose();

        Action act = () => sut.Dispose();

        act.Should().NotThrow("the second Dispose must short-circuit on the _disposed flag rather than re-canceling the inner CTS");
    }

    [Fact]
    public async Task NOGROUP_error_triggers_StreamCreateConsumerGroup()
    {
        // ProcessException must detect the StackExchange "NOGROUP" error and call StreamCreateConsumerGroup.
        var createCalled = new TaskCompletionSource();
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ThrowsAsyncForAnyArgs(_ => throw new RedisException("NOGROUP No such key or consumer group"));
        _database.StreamCreateConsumerGroupAsync(_context.Topic, _context.ConsumerGroup, Arg.Any<RedisValue?>())
            .ReturnsForAnyArgs(_ => { createCalled.TrySetResult(); return Task.FromResult(true); });
        _database.ClearReceivedCalls();

        var fetchTask = Sut.FetchTask;
        await createCalled.Task.WaitAsync(NogroupPollTimeout, TestContext.Current.CancellationToken);
        _cancellationTokenSource.Cancel();
        try { await fetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken); } catch (OperationCanceledException) { }

        await _database.ReceivedWithAnyArgs().StreamCreateConsumerGroupAsync(_context.Topic, _context.ConsumerGroup, Arg.Any<RedisValue?>());
    }

    [Fact]
    public async Task A_group_lost_after_it_was_read_reports_missed_messages_once()
    {
        ScriptsUnavailable();
        var reads = 0;
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ => Interlocked.Increment(ref reads) == 1
                ? Task.FromResult(Array.Empty<StreamEntry>())
                : Task.FromException<StreamEntry[]>(new RedisException("NOGROUP No such key or consumer group")));
        _database.StreamCreateConsumerGroupAsync(_context.Topic, _context.ConsumerGroup, Arg.Any<RedisValue?>()).ReturnsForAnyArgs(false);
        var missed = 0;

        using var sut = CreateSut(Channel.CreateUnbounded<ICacheEvent>().Writer, _logger, onMessagesMissed: () => Interlocked.Increment(ref missed));

        (await WaitUntil(() => Volatile.Read(ref reads) >= 4)).Should().BeTrue();
        Volatile.Read(ref missed).Should().Be(1, "the group is not read again in between");
    }

    [Fact]
    public async Task A_connection_seen_down_is_checked_for_a_gap_before_the_next_read()
    {
        var connected = 1;
        var offlineChecks = 0;
        var readsAfterOutage = 0;
        var gapChecks = 0;
        var checkedFirst = false;
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ =>
            {
                if (Volatile.Read(ref offlineChecks) > 0 && Interlocked.Increment(ref readsAfterOutage) == 1)
                {
                    Volatile.Write(ref checkedFirst, Volatile.Read(ref gapChecks) > 0);
                }

                return Task.FromResult(Array.Empty<StreamEntry>());
            });
        _database.Configure().StreamInfoAsync(_context.Topic).ReturnsForAnyArgs(_ =>
            {
                Interlocked.Increment(ref gapChecks);
                return Task.FromResult(default(StreamInfo));
            });
        _database.Configure().StreamGroupInfoAsync(_context.Topic).ReturnsForAnyArgs(Task.FromResult(Array.Empty<StreamGroupInfo>()));
        ScriptsUnavailable();
        var connectionState = _fixture.Create<IConnectionState>();
        connectionState.IsConnected.Returns(_ =>
        {
            if (Volatile.Read(ref connected) == 1)
            {
                return true;
            }

            Interlocked.Increment(ref offlineChecks);
            return false;
        });
        var redis = _fixture.Create<IRedisConnector>();
        redis.Database.Returns(_database);

        using var sut = new RedisStreamSubjectWriter<ICacheEvent>(
            _context,
            connectionState,
            redis,
            Channel.CreateUnbounded<ICacheEvent>().Writer,
            _formatter,
            _logger,
            _fixture.Create<ICachingTelemetryProvider>(),
            _fixture.Create<IRedisProfiler>(),
            new SignalingFetchWaiter(_pollInterval),
            () => { },
            _cancellationTokenSource.Token);

        (await WaitUntil(() => _database.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IDatabase.StreamReadGroupAsync)))).Should().BeTrue();
        Volatile.Write(ref connected, 0);
        (await WaitUntil(() => Volatile.Read(ref offlineChecks) > 0)).Should().BeTrue();
        Volatile.Write(ref connected, 1);

        (await WaitUntil(() => Volatile.Read(ref readsAfterOutage) > 0)).Should().BeTrue();
        Volatile.Read(ref checkedFirst).Should().BeTrue("the restored event may not have fired yet");
    }

    [Fact]
    public async Task A_recovery_raised_during_a_gap_check_is_checked_before_the_next_read()
    {
        var gapChecks = 0;
        var readBetweenChecks = false;
        var connectionState = _fixture.Create<IConnectionState>();
        connectionState.IsConnected.Returns(true);
        var redis = _fixture.Create<IRedisConnector>();
        redis.Database.Returns(_database);
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ =>
            {
                if (Volatile.Read(ref gapChecks) == 1)
                {
                    Volatile.Write(ref readBetweenChecks, true);
                }

                return Task.FromResult(Array.Empty<StreamEntry>());
            });
        _database.Configure().StreamInfoAsync(_context.Topic).ReturnsForAnyArgs(_ =>
        {
            if (Interlocked.Increment(ref gapChecks) == 1)
            {
                redis.OnConnectionRestored += Raise.Event<EventHandler>(redis, EventArgs.Empty);
            }

            return Task.FromResult(default(StreamInfo));
        });
        _database.Configure().StreamGroupInfoAsync(_context.Topic).ReturnsForAnyArgs(Task.FromResult(Array.Empty<StreamGroupInfo>()));
        ScriptsUnavailable();

        using var sut = new RedisStreamSubjectWriter<ICacheEvent>(
            _context,
            connectionState,
            redis,
            Channel.CreateUnbounded<ICacheEvent>().Writer,
            _formatter,
            _logger,
            _fixture.Create<ICachingTelemetryProvider>(),
            _fixture.Create<IRedisProfiler>(),
            new SignalingFetchWaiter(_pollInterval),
            () => { },
            _cancellationTokenSource.Token);

        (await WaitUntil(() => _database.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IDatabase.StreamReadGroupAsync)))).Should().BeTrue();
        redis.OnConnectionRestored += Raise.Event<EventHandler>(redis, EventArgs.Empty);

        (await WaitUntil(() => Volatile.Read(ref gapChecks) >= 2)).Should().BeTrue("the second recovery came after the first check began");
        Volatile.Read(ref readBetweenChecks).Should().BeFalse("a read in between moves the group past the entries the second check compares");
    }

    [Theory]
    [InlineData("NOPERM this user has no permissions to run the 'xinfo|stream' command")]
    [InlineData("ERR unknown command 'XINFO'")]
    public async Task A_denied_gap_check_reports_a_loss_and_keeps_reading(string error)
    {
        var connectionState = _fixture.Create<IConnectionState>();
        connectionState.IsConnected.Returns(true);
        var reads = 0;
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult(Array.Empty<StreamEntry>());
            });
        ScriptsUnavailable();
        _database.Configure().StreamInfoAsync(_context.Topic).ReturnsForAnyArgs(Task.FromException<StreamInfo>(error.StartsWith("ERR", StringComparison.Ordinal) ? UnknownCommandError(error) : new RedisServerException(RedisErrorKind.None, CommandFlags.None, error)));
        var redis = _fixture.Create<IRedisConnector>();
        redis.Database.Returns(_database);
        var missed = 0;

        using var sut = new RedisStreamSubjectWriter<ICacheEvent>(
            _context,
            connectionState,
            redis,
            Channel.CreateUnbounded<ICacheEvent>().Writer,
            _formatter,
            _logger,
            _fixture.Create<ICachingTelemetryProvider>(),
            _fixture.Create<IRedisProfiler>(),
            new SignalingFetchWaiter(_pollInterval),
            () => Interlocked.Increment(ref missed),
            _cancellationTokenSource.Token);

        (await WaitUntil(() => Volatile.Read(ref reads) > 0)).Should().BeTrue();
        redis.OnConnectionRestored += Raise.Event<EventHandler>(redis, EventArgs.Empty);
        (await WaitUntil(() => Volatile.Read(ref missed) == 1)).Should().BeTrue("a loss cannot be ruled out without XINFO");
        var readsAfterLoss = Volatile.Read(ref reads);

        (await WaitUntil(() => Volatile.Read(ref reads) > readsAfterLoss + 2)).Should().BeTrue("the denied check must not stop consumption");
        Volatile.Read(ref missed).Should().Be(1);
    }

    [Fact]
    public async Task Scripts_refused_by_the_client_fall_back_to_checking_and_reading_apart()
    {
        var connectionState = _fixture.Create<IConnectionState>();
        connectionState.IsConnected.Returns(true);
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(Task.FromResult(Array.Empty<StreamEntry>()));
        _database.Configure().ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(Task.FromException<RedisResult>(new RedisCommandException("This operation has been disabled in the command-map and cannot be used: EVALSHA")));
        var infos = 0;
        _database.Configure().StreamInfoAsync(_context.Topic).ReturnsForAnyArgs(_ =>
        {
            Interlocked.Increment(ref infos);
            return Task.FromResult(default(StreamInfo));
        });
        _database.Configure().StreamGroupInfoAsync(_context.Topic).ReturnsForAnyArgs(Task.FromResult(Array.Empty<StreamGroupInfo>()));
        var redis = _fixture.Create<IRedisConnector>();
        redis.Database.Returns(_database);

        using var sut = new RedisStreamSubjectWriter<ICacheEvent>(
            _context,
            connectionState,
            redis,
            Channel.CreateUnbounded<ICacheEvent>().Writer,
            _formatter,
            _logger,
            _fixture.Create<ICachingTelemetryProvider>(),
            _fixture.Create<IRedisProfiler>(),
            new SignalingFetchWaiter(_pollInterval),
            () => { },
            _cancellationTokenSource.Token);

        (await WaitUntil(() => _database.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IDatabase.StreamReadGroupAsync)))).Should().BeTrue();
        redis.OnConnectionRestored += Raise.Event<EventHandler>(redis, EventArgs.Empty);

        (await WaitUntil(() => Volatile.Read(ref infos) > 0)).Should().BeTrue("a command map that disables scripts must not stop the gap check");
    }

    [Fact]
    public async Task A_read_after_a_partial_batch_is_checked_for_a_gap()
    {
        _formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns(_ => new TestCacheEvent { Valid = true, Source = new Uri("urn:other") });
        SetupSingleBatch([new StreamEntry("1-0", [new NameValueEntry(_fieldName, "a")])]);
        var scripts = 0;
        _database.Configure().ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(_ =>
            {
                Interlocked.Increment(ref scripts);
                return Task.FromResult(UntrimmedReply());
            });

        using var sut = CreateSut(Channel.CreateUnbounded<ICacheEvent>().Writer, _logger);

        (await WaitUntil(() => Volatile.Read(ref scripts) > 0)).Should().BeTrue("a trim can reach unread entries between any two polls");
    }

    [Fact]
    public async Task A_doorbell_within_a_poll_interval_of_the_last_check_is_checked_against_the_stream_alone()
    {
        var reads = 0;
        var checks = new ConcurrentQueue<int>();
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ => Task.FromResult(Interlocked.Increment(ref reads) == 1 ? [new StreamEntry("1-0", [new NameValueEntry(_fieldName, "a")])] : Array.Empty<StreamEntry>()));
        _formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns(_ => new TestCacheEvent { Valid = true, Source = new Uri("urn:other") });
        _database.Configure().ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(ci =>
            {
                checks.Enqueue(FullCheck(ci));
                return Task.FromResult(UntrimmedReply());
            });
        using var waiter = new SignalingFetchWaiter(TimeSpan.FromSeconds(30));

        using var sut = CreateSut(Channel.CreateUnbounded<ICacheEvent>().Writer, _logger, waiter: waiter, context: _context with { PollInterval = TimeSpan.FromSeconds(30) });

        (await WaitUntil(() => Volatile.Read(ref reads) == 1)).Should().BeTrue();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        checks.Should().BeEmpty("a partial batch left the group at the head, so the loop waits rather than reads again");

        waiter.Signal();
        (await WaitUntil(() => checks.Count == 1)).Should().BeTrue("the group was never checked, so the first wake is");
        waiter.Signal();
        (await WaitUntil(() => checks.Count == 2)).Should().BeTrue();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        checks.Should().Equal([1, 0], "the first wake takes the group's position from the server, the one within a poll interval of it checks the stream against that position");
        Volatile.Read(ref reads).Should().Be(1, "every read in use is checked");
    }

    [Fact]
    public async Task A_doorbell_a_poll_interval_after_the_last_check_takes_the_position_from_the_server_again()
    {
        var checks = new ConcurrentQueue<int>();
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(Task.FromResult(Array.Empty<StreamEntry>()));
        _database.Configure().ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(ci =>
            {
                checks.Enqueue(FullCheck(ci));
                return Task.FromResult(UntrimmedReply());
            });
        using var waiter = new SignalingFetchWaiter(TimeSpan.FromSeconds(30));

        using var sut = CreateSut(Channel.CreateUnbounded<ICacheEvent>().Writer, _logger, waiter: waiter, context: _context with { PollInterval = TimeSpan.FromMilliseconds(100) });
        sut.MarkSubscribed();
        waiter.Signal();
        (await WaitUntil(() => !checks.IsEmpty)).Should().BeTrue();

        await Task.Delay(200, TestContext.Current.CancellationToken);
        var before = checks.Count;
        waiter.Signal();

        (await WaitUntil(() => checks.Count == before + 1)).Should().BeTrue();
        checks.Last().Should().Be(1, "the check with XINFO GROUPS keeps the poll's cadence under a stream of doorbells");
    }

    [Fact]
    public async Task Doorbells_rung_during_a_read_wake_one_more_read()
    {
        var reads = 0;
        var scripts = 0;
        var release = new TaskCompletionSource<RedisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult(Array.Empty<StreamEntry>());
            });
        _database.Configure().ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(_ => Interlocked.Increment(ref scripts) == 2 ? release.Task : Task.FromResult(UntrimmedReply()));
        using var waiter = new SignalingFetchWaiter(TimeSpan.FromSeconds(30));

        using var sut = CreateSut(Channel.CreateUnbounded<ICacheEvent>().Writer, _logger, waiter: waiter, context: _context with { PollInterval = TimeSpan.FromSeconds(30) });
        (await WaitUntil(() => Volatile.Read(ref reads) == 1)).Should().BeTrue("the first read, before any subscriber, is plain");
        sut.MarkSubscribed();
        waiter.Signal();
        (await WaitUntil(() => Volatile.Read(ref scripts) == 1)).Should().BeTrue("the first read in use is checked");
        waiter.Signal();
        (await WaitUntil(() => Volatile.Read(ref scripts) == 2)).Should().BeTrue("the wake's read is held here");

        for (var i = 0; i < 5; i++)
        {
            waiter.Signal();
        }
        release.SetResult(UntrimmedReply());

        (await WaitUntil(() => Volatile.Read(ref scripts) == 3)).Should().BeTrue("the rings collapse into one read after the one in flight");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Volatile.Read(ref scripts).Should().Be(3);
    }

    /// <summary>
    /// The group at 1-0 (from the check before). A trim shows in the counts: a loss when more was added past 1-0 than the
    /// stream holds. An XDEL of unread 2-0 shows in the newest id deleted, whatever the counts.
    /// </summary>
    [Theory]
    [InlineData(3, 2, "3-0", "0-0", 0)]
    [InlineData(4, 2, "3-0", "0-0", 1)]
    [InlineData(3, 1, "3-0", "0-0", 1)]
    [InlineData(3, 2, "1-0", "2-0", 1)]
    public async Task A_doorbell_read_reports_entries_removed_past_the_group_without_asking_the_server_for_its_position(long entriesAdded, long length, string first, string maxDeleted, int lost)
    {
        var reads = 0;
        var scripts = 0;
        var missed = 0;
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult(Array.Empty<StreamEntry>());
            });
        _database.Configure().ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(ci => Task.FromResult(Interlocked.Increment(ref scripts) == 2 && FullCheck(ci) == 0
                ? WakeReply(entriesAdded, length, first, maxDeleted)
                : UntrimmedReply()));
        using var waiter = new SignalingFetchWaiter(TimeSpan.FromSeconds(30));

        using var sut = CreateSut(Channel.CreateUnbounded<ICacheEvent>().Writer, _logger, onMessagesMissed: () => Interlocked.Increment(ref missed), waiter: waiter, context: _context with { PollInterval = TimeSpan.FromSeconds(30) });
        (await WaitUntil(() => Volatile.Read(ref reads) == 1)).Should().BeTrue();
        sut.MarkSubscribed();
        waiter.Signal();
        (await WaitUntil(() => Volatile.Read(ref scripts) == 1)).Should().BeTrue("the first read in use takes the group's position from the server");
        waiter.Signal();
        (await WaitUntil(() => Volatile.Read(ref scripts) == 2)).Should().BeTrue();

        await Task.Delay(300, TestContext.Current.CancellationToken);
        Volatile.Read(ref missed).Should().Be(lost, "the wake read compares the stream with the position it knows: more unread entries added than the stream holds is a loss, exactly as many is not");
    }

    [Fact]
    public async Task A_wake_after_a_loss_counts_the_entries_it_never_saw()
    {
        // The group at 1-0 (from the check before); 2-0 trimmed unread, 3-0 read; then 4-0 added and 3-0, read, trimmed.
        var reads = 0;
        var scripts = 0;
        var missed = 0;
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult(Array.Empty<StreamEntry>());
            });
        _database.Configure().ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(ci => Task.FromResult(Interlocked.Increment(ref scripts) switch
            {
                2 => WakeReply(entriesAdded: 3, length: 1, first: "3-0", maxDeleted: "0-0", "3-0"),
                3 => WakeReply(entriesAdded: 4, length: 1, first: "4-0", maxDeleted: "0-0", "4-0"),
                _ => UntrimmedReply(),
            }));
        using var waiter = new SignalingFetchWaiter(TimeSpan.FromSeconds(30));

        using var sut = CreateSut(Channel.CreateUnbounded<ICacheEvent>().Writer, _logger, onMessagesMissed: () => Interlocked.Increment(ref missed), waiter: waiter, context: _context with { PollInterval = TimeSpan.FromSeconds(30) });
        (await WaitUntil(() => Volatile.Read(ref reads) == 1)).Should().BeTrue();
        sut.MarkSubscribed();
        waiter.Signal();
        (await WaitUntil(() => Volatile.Read(ref scripts) == 1)).Should().BeTrue();
        waiter.Signal();
        (await WaitUntil(() => Volatile.Read(ref missed) == 1)).Should().BeTrue("2-0 was trimmed before it was read");
        waiter.Signal();
        (await WaitUntil(() => Volatile.Read(ref scripts) == 3)).Should().BeTrue();

        await Task.Delay(300, TestContext.Current.CancellationToken);
        Volatile.Read(ref missed).Should().Be(1, "the read that found 3-0 at the head took its count from the stream, so the entry it never saw is not held against 4-0");
    }

    [Fact]
    public async Task The_first_read_of_a_recreated_group_takes_its_position_from_the_server()
    {
        var reads = 0;
        var checks = new ConcurrentQueue<int>();
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult(Array.Empty<StreamEntry>());
            });
        _database.Configure().ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(ci =>
            {
                checks.Enqueue(FullCheck(ci));
                return checks.Count == 2
                    ? Task.FromException<RedisResult>(new RedisException("NOGROUP No such key or consumer group"))
                    : Task.FromResult(UntrimmedReply());
            });
        _database.StreamCreateConsumerGroupAsync(_context.Topic, _context.ConsumerGroup, Arg.Any<RedisValue?>()).ReturnsForAnyArgs(true);
        using var waiter = new SignalingFetchWaiter(TimeSpan.FromSeconds(30));

        using var sut = CreateSut(Channel.CreateUnbounded<ICacheEvent>().Writer, _logger, waiter: waiter, context: _context with { PollInterval = TimeSpan.FromSeconds(30) });
        (await WaitUntil(() => Volatile.Read(ref reads) == 1)).Should().BeTrue();
        sut.MarkSubscribed();
        waiter.Signal();
        (await WaitUntil(() => checks.Count == 1)).Should().BeTrue();
        waiter.Signal();
        (await WaitUntil(() => _database.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IDatabase.StreamCreateConsumerGroupAsync)))).Should().BeTrue("the wake read met NOGROUP");
        waiter.Signal();
        (await WaitUntil(() => Volatile.Read(ref reads) == 2)).Should().BeTrue("the loss was reported, so the recreated group's first read is plain");
        waiter.Signal();

        (await WaitUntil(() => checks.Count == 3)).Should().BeTrue();
        checks.Should().Equal([1, 0, 1], "the recreated group's position comes from the server, however recent the last check was");
    }

    [Fact]
    public async Task A_pending_gap_check_reads_in_the_same_script()
    {
        var connectionState = _fixture.Create<IConnectionState>();
        connectionState.IsConnected.Returns(true);
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(Task.FromResult(Array.Empty<StreamEntry>()));
        var scripts = 0;
        _database.Configure().ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(_ =>
            {
                Interlocked.Increment(ref scripts);
                return Task.FromResult(UntrimmedReply());
            });
        var redis = _fixture.Create<IRedisConnector>();
        redis.Database.Returns(_database);

        using var sut = new RedisStreamSubjectWriter<ICacheEvent>(
            _context,
            connectionState,
            redis,
            Channel.CreateUnbounded<ICacheEvent>().Writer,
            _formatter,
            _logger,
            _fixture.Create<ICachingTelemetryProvider>(),
            _fixture.Create<IRedisProfiler>(),
            new SignalingFetchWaiter(_pollInterval),
            () => { },
            _cancellationTokenSource.Token);

        (await WaitUntil(() => _database.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IDatabase.StreamReadGroupAsync)))).Should().BeTrue();
        redis.OnConnectionRestored += Raise.Event<EventHandler>(redis, EventArgs.Empty);

        (await WaitUntil(() => Volatile.Read(ref scripts) > 0)).Should().BeTrue();
        _database.ReceivedCalls().Should().NotContain(c => c.GetMethodInfo().Name == nameof(IDatabase.StreamInfoAsync), "a trim must not land between a check and the read it guards");
    }

    /// <summary>
    /// The first read goes out before the subscribe that creates the group, and answers NOGROUP after it. Whether the
    /// subscriber's group is still there decides: it is (BUSYGROUP), nothing was lost; it is gone too (this reader creates
    /// one), what the subscriber cached since is stale.
    /// </summary>
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task A_subscriber_joining_while_a_read_finds_no_group_yet_is_told_of_a_loss_only_if_its_group_is_gone_too(bool survived, int lost)
    {
        var reads = 0;
        var missed = 0;
        var firstRead = new TaskCompletionSource<StreamEntry[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ => Interlocked.Increment(ref reads) == 1 ? firstRead.Task : Task.FromResult(Array.Empty<StreamEntry>()));
        _database.StreamCreateConsumerGroupAsync(_context.Topic, _context.ConsumerGroup, Arg.Any<RedisValue?>())
            .ReturnsForAnyArgs(_ => survived
                ? Task.FromException<bool>(new RedisServerException(RedisErrorKind.None, CommandFlags.None, StreamConstants.ConsumerGroupNameExistsErrorMessage))
                : Task.FromResult(true));

        using var sut = CreateSut(Channel.CreateUnbounded<ICacheEvent>().Writer, _logger, onMessagesMissed: () => Interlocked.Increment(ref missed));
        (await WaitUntil(() => Volatile.Read(ref reads) == 1)).Should().BeTrue();
        sut.MarkSubscribed();
        firstRead.SetException(new RedisException("NOGROUP No such key or consumer group"));

        (await WaitUntil(() => _database.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IDatabase.ScriptEvaluateAsync)))).Should().BeTrue("the reader goes on, checked now that it is in use");
        Volatile.Read(ref missed).Should().Be(lost);
    }

    [Fact]
    public async Task A_subscriber_joining_while_the_reader_recreates_the_group_waits_for_it_and_finds_it()
    {
        // The first read goes out before any subscriber and answers NOGROUP; the recreate is under way when one joins.
        var reads = 0;
        var missed = 0;
        var firstRead = new TaskCompletionSource<StreamEntry[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var create = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ => Interlocked.Increment(ref reads) == 1 ? firstRead.Task : Task.FromResult(Array.Empty<StreamEntry>()));
        _database.StreamCreateConsumerGroupAsync(_context.Topic, _context.ConsumerGroup, Arg.Any<RedisValue?>()).ReturnsForAnyArgs(create.Task);

        using var sut = CreateSut(Channel.CreateUnbounded<ICacheEvent>().Writer, _logger, onMessagesMissed: () => Interlocked.Increment(ref missed));
        (await WaitUntil(() => Volatile.Read(ref reads) == 1)).Should().BeTrue();
        firstRead.SetException(new RedisException("NOGROUP No such key or consumer group"));
        (await WaitUntil(() => _database.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IDatabase.StreamCreateConsumerGroupAsync)))).Should().BeTrue("the group is recreated");
        var joining = Task.Run(sut.MarkSubscribed, TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        joining.IsCompleted.Should().BeFalse("the subscribe waits for the recreate rather than racing it");
        create.SetResult(true);
        await joining.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        (await WaitUntil(() => _database.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IDatabase.ScriptEvaluateAsync)))).Should().BeTrue("the reader goes on, checked now that it is in use");
        Volatile.Read(ref missed).Should().Be(0, "the subscriber joined the group the reader made");
        _database.DidNotReceive().StreamCreateConsumerGroup(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue?>());
    }

    [Fact]
    public async Task Subscribers_join_one_at_a_time_so_the_group_one_makes_is_known_before_another_relies_on_it()
    {
        // The first subscriber's create is slow; the second must not skip ahead of it, nor the reader's recreate.
        var reads = 0;
        var missed = 0;
        using var creating = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var firstRead = new TaskCompletionSource<StreamEntry[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ => Interlocked.Increment(ref reads) == 1 ? firstRead.Task : Task.FromResult(Array.Empty<StreamEntry>()));
        _database.StreamCreateConsumerGroup(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue?>()).ReturnsForAnyArgs(_ =>
        {
            creating.Set();
            release.Wait(WaitTimeout);
            return true;
        });
        _database.StreamCreateConsumerGroupAsync(_context.Topic, _context.ConsumerGroup, Arg.Any<RedisValue?>()).ReturnsForAnyArgs(true);

        using var sut = CreateSut(Channel.CreateUnbounded<ICacheEvent>().Writer, _logger, onMessagesMissed: () => Interlocked.Increment(ref missed));
        (await WaitUntil(() => Volatile.Read(ref reads) == 1)).Should().BeTrue();
        var first = Task.Run(sut.MarkSubscribed, TestContext.Current.CancellationToken);
        creating.Wait(WaitTimeout, TestContext.Current.CancellationToken).Should().BeTrue();
        var second = Task.Run(sut.MarkSubscribed, TestContext.Current.CancellationToken);
        firstRead.SetException(new RedisException("NOGROUP No such key or consumer group"));
        await Task.Delay(300, TestContext.Current.CancellationToken);

        second.IsCompleted.Should().BeFalse("the second subscriber waits for the first's create");
        release.Set();
        await Task.WhenAll(first, second).WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        (await WaitUntil(() => Volatile.Read(ref missed) == 1)).Should().BeTrue("the group the first subscriber made is gone, or the reader's recreate could not have succeeded");
        _database.Received(1).StreamCreateConsumerGroup(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue?>());
    }

    [Fact]
    public async Task A_recreate_that_failed_once_does_not_report_the_loss_again_when_it_succeeds()
    {
        // Checked apart, so the reads in use come through the plain read as well.
        ScriptsUnavailable();
        var reads = 0;
        var missed = 0;
        var creates = 0;
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ => Interlocked.Increment(ref reads) is 2 or 3
                ? Task.FromException<StreamEntry[]>(new RedisException("NOGROUP No such key or consumer group"))
                : Task.FromResult(Array.Empty<StreamEntry>()));
        _database.StreamCreateConsumerGroupAsync(_context.Topic, _context.ConsumerGroup, Arg.Any<RedisValue?>())
            .ReturnsForAnyArgs(_ => Interlocked.Increment(ref creates) == 1
                ? Task.FromException<bool>(new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "down"))
                : Task.FromResult(true));

        using var sut = CreateSut(Channel.CreateUnbounded<ICacheEvent>().Writer, _logger, onMessagesMissed: () => Interlocked.Increment(ref missed));
        sut.MarkSubscribed();

        (await WaitUntil(() => Volatile.Read(ref reads) >= 4 && Volatile.Read(ref creates) == 2)).Should().BeTrue();
        Volatile.Read(ref missed).Should().Be(1, "the second recreate made good the first, for the loss already reported");
    }

    [Fact]
    public async Task An_observer_told_of_a_loss_can_subscribe_again_from_the_telling()
    {
        var reads = 0;
        var resubscribed = 0;
        RedisStreamSubjectWriter<ICacheEvent>? sut = null;
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ => Interlocked.Increment(ref reads) == 2
                ? Task.FromException<StreamEntry[]>(new RedisException("NOGROUP No such key or consumer group"))
                : Task.FromResult(Array.Empty<StreamEntry>()));
        _database.StreamCreateConsumerGroupAsync(_context.Topic, _context.ConsumerGroup, Arg.Any<RedisValue?>()).ReturnsForAnyArgs(true);
        ScriptsUnavailable();

        // What a change token does when told: it subscribes again, which takes the gate the loss was decided under.
        void Told()
        {
            sut!.MarkSubscribed();
            Interlocked.Increment(ref resubscribed);
        }

        using var writer = sut = CreateSut(Channel.CreateUnbounded<ICacheEvent>().Writer, _logger, onMessagesMissed: Told);
        sut.MarkSubscribed();

        (await WaitUntil(() => Volatile.Read(ref resubscribed) == 1)).Should().BeTrue("the telling runs with the gate free");
        (await WaitUntil(() => Volatile.Read(ref reads) >= 3)).Should().BeTrue("the reader goes on");
    }

    [Fact]
    public async Task A_loss_told_under_another_lock_that_a_subscribe_holds_completes()
    {
        // The dispatcher holds a token's lock while subscribing again; the reader must not hold the gate while it tells.
        var reads = 0;
        var missed = 0;
        var lose = 0;
        var tokenLock = new object();
        using var holding = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        using var telling = new ManualResetEventSlim();
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ => Interlocked.Increment(ref reads) > 0 && Interlocked.Exchange(ref lose, 0) == 1
                ? Task.FromException<StreamEntry[]>(new RedisException("NOGROUP No such key or consumer group"))
                : Task.FromResult(Array.Empty<StreamEntry>()));
        _database.StreamCreateConsumerGroupAsync(_context.Topic, _context.ConsumerGroup, Arg.Any<RedisValue?>()).ReturnsForAnyArgs(true);
        ScriptsUnavailable();
        void Told()
        {
            telling.Set();
            lock (tokenLock)
            {
                Interlocked.Increment(ref missed);
            }
        }

        using var sut = CreateSut(Channel.CreateUnbounded<ICacheEvent>().Writer, _logger, onMessagesMissed: Told);
        sut.MarkSubscribed();
        (await WaitUntil(() => Volatile.Read(ref reads) >= 1)).Should().BeTrue();
        void Refresh()
        {
            lock (tokenLock)
            {
                holding.Set();
                proceed.Wait(WaitTimeout);
                sut.MarkSubscribed();
            }
        }

        var refreshing = Task.Run(Refresh, TestContext.Current.CancellationToken);
        holding.Wait(WaitTimeout, TestContext.Current.CancellationToken).Should().BeTrue();
        Volatile.Write(ref lose, 1);
        telling.Wait(WaitTimeout, TestContext.Current.CancellationToken).Should().BeTrue("the loss is told while the lock is held elsewhere");
        proceed.Set();

        await refreshing.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        (await WaitUntil(() => Volatile.Read(ref missed) == 1)).Should().BeTrue("the subscribe under the lock took the gate, and the telling then took the lock");
    }

    [Fact]
    public async Task A_recovery_between_the_backoff_check_and_its_count_is_neither_waited_out_nor_counted()
    {
        var reads = 0;
        var recovered = 0;
        var redis = _fixture.Create<IRedisConnector>();
        redis.Database.Returns(_database);
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ => Interlocked.Increment(ref reads) <= 8 ? Task.FromException<StreamEntry[]>(new RedisException("down")) : Task.FromResult(Array.Empty<StreamEntry>()));
        using var waiter = new SignalingFetchWaiter(TimeSpan.FromSeconds(30));

        using var sut = new RedisStreamSubjectWriter<ICacheEvent>(
            _context with { PollInterval = TimeSpan.FromSeconds(30) },
            NullConnectionStateMonitor.Instance,
            redis,
            Channel.CreateUnbounded<ICacheEvent>().Writer,
            _formatter,
            _logger,
            _fixture.Create<ICachingTelemetryProvider>(),
            _fixture.Create<IRedisProfiler>(),
            waiter,
            () => { },
            _cancellationTokenSource.Token)
        {
            // The fourth failure's back-off has passed its recovery check when the connection comes back.
            // By the read's own count: the loop starts in the constructor, so the seam may have missed a failure or two.
            BeforeCountingFailure = () =>
            {
                if (Volatile.Read(ref reads) == 4 && Interlocked.Exchange(ref recovered, 1) == 0)
                {
                    redis.OnConnectionRestored += Raise.Event<EventHandler>(redis, EventArgs.Empty);
                }
            },
        };
        (await WaitUntil(() => Volatile.Read(ref reads) == 1)).Should().BeTrue();
        for (var i = 2; i <= 4; i++)
        {
            waiter.Signal();
            (await WaitUntil(() => Volatile.Read(ref reads) >= i)).Should().BeTrue();
        }

        (await WaitUntil(() => Volatile.Read(ref reads) >= 5)).Should().BeTrue("the recovery ends the back-off it landed in the middle of");

        // Not counted either: three more failures go at the poll's pace, the fourth backs off.
        for (var i = 6; i <= 8; i++)
        {
            waiter.Signal();
            (await WaitUntil(() => Volatile.Read(ref reads) >= i)).Should().BeTrue("retry {0} of the new streak is at the poll's pace", i - 5);
        }

        waiter.Signal();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Volatile.Read(ref reads).Should().Be(8, "the fourth failure of the new streak backs off past the poll");
    }

    [Fact]
    public async Task A_subscriber_joining_while_the_readers_recreate_is_pending_makes_the_group_where_the_reader_left_off()
    {
        // In use, the group goes and the recreate fails once; broadcasts land and a subscriber joins before the retry.
        ScriptsUnavailable();
        var reads = 0;
        var missed = 0;
        var held = new TaskCompletionSource<StreamEntry[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns(_ => new TestCacheEvent { Valid = true, Source = new Uri("urn:other") });
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ => Interlocked.Increment(ref reads) switch
            {
                1 => Task.FromResult<StreamEntry[]>([new StreamEntry("1-0", [new NameValueEntry(_fieldName, "a")])]),
                2 => Task.FromException<StreamEntry[]>(new RedisException("NOGROUP No such key or consumer group")),
                _ => held.Task,
            });
        _database.StreamCreateConsumerGroupAsync(_context.Topic, _context.ConsumerGroup, Arg.Any<RedisValue?>())
            .ReturnsForAnyArgs(Task.FromException<bool>(new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "down")));

        using var sut = CreateSut(Channel.CreateUnbounded<ICacheEvent>().Writer, _logger, onMessagesMissed: () => Interlocked.Increment(ref missed));
        sut.MarkSubscribed();
        (await WaitUntil(() => Volatile.Read(ref missed) == 1 && Volatile.Read(ref reads) >= 3)).Should().BeTrue("the group went while in use and its recreate failed");
        _database.ClearReceivedCalls();

        sut.MarkSubscribed();

        _database.Received(1).StreamCreateConsumerGroup(_context.Topic, _context.ConsumerGroup, (RedisValue)"1-0");
        held.SetResult([]);
        Volatile.Read(ref missed).Should().Be(1, "what came since the last read is replayed to the group the subscriber made, not skipped");
    }

    [Fact]
    public async Task A_backoff_longer_than_the_poll_asks_the_waiter_for_what_remains_of_it()
    {
        // A poll of 20 s and the fourth failure: the back-off is the 30 s cap, which two poll-sized steps would stretch to 40 s.
        var reads = 0;
        var redis = _fixture.Create<IRedisConnector>();
        redis.Database.Returns(_database);
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ => Interlocked.Increment(ref reads) <= 4 ? Task.FromException<StreamEntry[]>(new RedisException("down")) : Task.FromResult(Array.Empty<StreamEntry>()));
        var waiter = new RecordingWaiter(redis);

        using var sut = new RedisStreamSubjectWriter<ICacheEvent>(
            _context with { PollInterval = TimeSpan.FromSeconds(20) },
            NullConnectionStateMonitor.Instance,
            redis,
            Channel.CreateUnbounded<ICacheEvent>().Writer,
            _formatter,
            _logger,
            _fixture.Create<ICachingTelemetryProvider>(),
            _fixture.Create<IRedisProfiler>(),
            waiter,
            () => { },
            _cancellationTokenSource.Token);

        (await WaitUntil(() => Volatile.Read(ref reads) >= 5)).Should().BeTrue("the recovery the waiter raised ended the back-off");
        waiter.Timeouts.Should().HaveCountGreaterThanOrEqualTo(2);
        waiter.Timeouts[0].Should().BeCloseTo(StreamConstants.MaxErrorBackoff, TimeSpan.FromSeconds(1), "the first step asks for the whole back-off, not the poll");
        waiter.Timeouts[1].Should().BeLessThanOrEqualTo(waiter.Timeouts[0], "after a ring the step asks for what remains");
    }

    [Fact]
    public async Task A_read_that_fails_after_a_recovery_moved_on_but_before_its_signal_landed_starts_a_streak_of_its_own()
    {
        // The recovery is in two steps the loop can run between: the reads after it are a new streak whenever the signal lands.
        var reads = 0;
        var redis = _fixture.Create<IRedisConnector>();
        redis.Database.Returns(_database);
        using var inner = new SignalingFetchWaiter(TimeSpan.FromSeconds(30));
        var waiter = new DeferringWaiter(inner);
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ =>
            {
                var read = Interlocked.Increment(ref reads);
                if (read == 4)
                {
                    waiter.Deferring = true;
                    redis.OnConnectionRestored += Raise.Event<EventHandler>(redis, EventArgs.Empty);
                }

                return read <= 8 ? Task.FromException<StreamEntry[]>(new RedisException("down")) : Task.FromResult(Array.Empty<StreamEntry>());
            });

        using var sut = new RedisStreamSubjectWriter<ICacheEvent>(
            _context with { PollInterval = TimeSpan.FromSeconds(30) },
            NullConnectionStateMonitor.Instance,
            redis,
            Channel.CreateUnbounded<ICacheEvent>().Writer,
            _formatter,
            _logger,
            _fixture.Create<ICachingTelemetryProvider>(),
            _fixture.Create<IRedisProfiler>(),
            waiter,
            () => { },
            _cancellationTokenSource.Token);
        (await WaitUntil(() => Volatile.Read(ref reads) == 1)).Should().BeTrue();
        for (var i = 2; i <= 4; i++)
        {
            inner.Signal();
            (await WaitUntil(() => Volatile.Read(ref reads) >= i)).Should().BeTrue();
        }

        (await WaitUntil(() => Volatile.Read(ref reads) == 5)).Should().BeTrue("the read overtaken by the recovery is not backed off");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Volatile.Read(ref reads).Should().Be(5, "the read after the recovery failed and waits at the poll's pace, four outage failures notwithstanding");
        waiter.Release();

        (await WaitUntil(() => Volatile.Read(ref reads) >= 6)).Should().BeTrue("the recovery's signal, landing late, still ends that wait");
        for (var i = 7; i <= 8; i++)
        {
            inner.Signal();
            (await WaitUntil(() => Volatile.Read(ref reads) >= i)).Should().BeTrue("retry {0} of the new streak is at the poll's pace", i - 5);
        }

        inner.Signal();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Volatile.Read(ref reads).Should().Be(8, "the fourth failure of the new streak backs off past the poll");
    }

    [Fact]
    public async Task A_recovery_between_a_failed_read_and_its_backoff_is_not_waited_out()
    {
        var reads = 0;
        var redis = _fixture.Create<IRedisConnector>();
        redis.Database.Returns(_database);
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ =>
            {
                var read = Interlocked.Increment(ref reads);
                if (read == 4)
                {
                    // Back before the failure is handled: the streak is reset, but the loop has yet to choose its wait.
                    redis.OnConnectionRestored += Raise.Event<EventHandler>(redis, EventArgs.Empty);
                }

                return read <= 8 ? Task.FromException<StreamEntry[]>(new RedisException("down")) : Task.FromResult(Array.Empty<StreamEntry>());
            });
        using var waiter = new SignalingFetchWaiter(TimeSpan.FromSeconds(30));

        using var sut = new RedisStreamSubjectWriter<ICacheEvent>(
            _context with { PollInterval = TimeSpan.FromSeconds(30) },
            NullConnectionStateMonitor.Instance,
            redis,
            Channel.CreateUnbounded<ICacheEvent>().Writer,
            _formatter,
            _logger,
            _fixture.Create<ICachingTelemetryProvider>(),
            _fixture.Create<IRedisProfiler>(),
            waiter,
            () => { },
            _cancellationTokenSource.Token);
        (await WaitUntil(() => Volatile.Read(ref reads) == 1)).Should().BeTrue();
        for (var i = 2; i <= 4; i++)
        {
            waiter.Signal();
            (await WaitUntil(() => Volatile.Read(ref reads) >= i)).Should().BeTrue();
        }

        (await WaitUntil(() => Volatile.Read(ref reads) >= 5)).Should().BeTrue("a recovery since the read went out ends its back-off, whichever wait it would have been");

        // The read the recovery overtook is not a failure of the new streak: three more go at the poll's pace, the fourth backs off.
        for (var i = 6; i <= 8; i++)
        {
            waiter.Signal();
            (await WaitUntil(() => Volatile.Read(ref reads) >= i)).Should().BeTrue("retry {0} of the new streak is at the poll's pace", i - 5);
        }

        waiter.Signal();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Volatile.Read(ref reads).Should().Be(8, "the fourth failure of the new streak backs off past the poll, and the recovery's release went to no wait");
    }

    [Fact]
    public async Task A_group_missing_before_the_first_read_reports_nothing()
    {
        var reads = 0;
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromException<StreamEntry[]>(new RedisException("NOGROUP No such key or consumer group"));
            });
        var missed = 0;

        using var sut = CreateSut(Channel.CreateUnbounded<ICacheEvent>().Writer, _logger, onMessagesMissed: () => Interlocked.Increment(ref missed));

        (await WaitUntil(() => Volatile.Read(ref reads) >= 3)).Should().BeTrue();
        Volatile.Read(ref missed).Should().Be(0, "nothing was read through the group yet");
    }

    [Fact]
    public async Task NOGROUP_recovery_swallows_BUSYGROUP_when_group_already_exists()
    {
        const string BusyGroupMessage = "BUSYGROUP Consumer Group name already exists";

        var createCalled = new TaskCompletionSource();
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ThrowsAsyncForAnyArgs(_ => throw new RedisException("NOGROUP unknown group"));
        _database.StreamCreateConsumerGroupAsync(_context.Topic, _context.ConsumerGroup, Arg.Any<RedisValue?>())
            .ThrowsAsyncForAnyArgs(_ => { createCalled.TrySetResult(); throw new RedisServerException(RedisErrorKind.None, CommandFlags.None, BusyGroupMessage); });
        _database.ClearReceivedCalls();

        var fetchTask = Sut.FetchTask;
        await createCalled.Task.WaitAsync(NogroupPollTimeout, TestContext.Current.CancellationToken);
        _cancellationTokenSource.Cancel();
        try { await fetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken); } catch (OperationCanceledException) { }

        fetchTask.IsFaulted.Should().BeFalse("BUSYGROUP only means the group is already there, so recovery must swallow it and leave the fetch loop running");
    }

    [Fact]
    public async Task NOGROUP_recovery_logs_and_continues_when_StreamCreate_throws_unexpectedly()
    {
        var createCalled = new TaskCompletionSource();
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ThrowsAsyncForAnyArgs(_ => throw new RedisException("NOGROUP unknown group"));
        _database.StreamCreateConsumerGroupAsync(_context.Topic, _context.ConsumerGroup, Arg.Any<RedisValue?>())
            .ThrowsAsyncForAnyArgs(_ => { createCalled.TrySetResult(); throw new InvalidOperationException("create failed"); });
        _database.ClearReceivedCalls();

        var fetchTask = Sut.FetchTask;
        await createCalled.Task.WaitAsync(NogroupPollTimeout, TestContext.Current.CancellationToken);
        _cancellationTokenSource.Cancel();
        try { await fetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken); } catch (OperationCanceledException) { }

        fetchTask.IsFaulted.Should().BeFalse("an unexpected StreamCreate failure must be logged and the fetch loop kept alive, not surfaced as a fault");
    }

    [Theory]
    [InlineData("ERR unknown command 'XREADGROUP'")]
    [InlineData("err unknown command 'xreadgroup', with args beginning with:")]
    [InlineData("unknown command XREADGROUP")] // proxy stripped the ERR prefix
    public async Task Unknown_command_is_logged_once_and_stops_hammering_the_server(string message)
    {
        // A RESP server without XREADGROUP (Garnet, for example) fails identically on every attempt. The loop
        // must not re-issue it once per poll interval, and must not log the same critical error every time.
        var recordingLogger = new RecordingLogger();
        var loggedCritical = new TaskCompletionSource();
        recordingLogger.OnRecord = r =>
        {
            if (r.Level == LogLevel.Critical)
            {
                loggedCritical.TrySetResult();
            }
        };
        var attempts = 0;
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs<StreamEntry[]>(_ =>
            {
                Interlocked.Increment(ref attempts);
                throw UnknownCommandError(message);
            });

        using var sut = CreateSut(Channel.CreateUnbounded<ICacheEvent>().Writer, recordingLogger);
        await loggedCritical.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        // The quarantine backoff is far longer than several poll intervals, so nothing more should happen.
        await Task.Delay(_pollInterval.Multiply(10), TestContext.Current.CancellationToken);

        recordingLogger.Records.Count(r => r.Level == LogLevel.Critical)
            .Should().Be(1, "the unsupported-command error must be reported once, not once per poll interval");
        Volatile.Read(ref attempts)
            .Should().Be(1, "a command the server does not implement must not be re-issued every poll interval");

        sut.FetchTask.IsCompleted.Should().BeFalse("the loop stays alive so a reconnect to a capable server can recover");

        _cancellationTokenSource.Cancel();
        try { await sut.FetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Unknown_command_quarantine_is_lifted_when_the_connection_reconnects()
    {
        var recordingLogger = new RecordingLogger();
        var loggedCritical = new TaskCompletionSource();
        recordingLogger.OnRecord = r =>
        {
            if (r.Level == LogLevel.Critical)
            {
                loggedCritical.TrySetResult();
            }
        };

        // Read through Volatile, like `attempts` above: this flag is written by the test thread and
        // read by the fetch loop's thread, so a plain capture is a data race. Closing it does not
        // fully de-flake this test — it was still seen failing under parallel load afterwards, with
        // `recovered` false after the 10s budget — but an unsynchronized cross-thread flag is not
        // something to leave in place while chasing that.
        var fail = true;
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ => Volatile.Read(ref fail)
                ? throw UnknownCommandError("ERR unknown command 'XREADGROUP'")
                : Task.FromResult(Array.Empty<StreamEntry>()));

        var connectionState = _fixture.Create<IConnectionState>();
        connectionState.IsConnected.Returns(true);
        var redis = _fixture.Create<IRedisConnector>();
        redis.Database.Returns(_database);

        using var sut = new RedisStreamSubjectWriter<ICacheEvent>(
            _context,
            connectionState,
            redis,
            Channel.CreateUnbounded<ICacheEvent>().Writer,
            _formatter,
            recordingLogger,
            _fixture.Create<ICachingTelemetryProvider>(),
            _fixture.Create<IRedisProfiler>(),
            new SignalingFetchWaiter(_pollInterval),
            () => { },
            _cancellationTokenSource.Token);

        await loggedCritical.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        // Server now answers XREADGROUP; the reconnect must wake the loop instead of waiting out the backoff.
        // Written before the release below, so a thread that observes the release also observes this.
        Volatile.Write(ref fail, false);
        redis.OnReconnected += Raise.Event<EventHandler>(redis, EventArgs.Empty);

        var recovered = await WaitUntil(() => recordingLogger.Records.Any(
            r => r.Level == LogLevel.Information && r.Message.Contains("recovered", StringComparison.OrdinalIgnoreCase)));

        recovered.Should().BeTrue(
            "the reconnect must wake the fetch loop so the next successful read lifts the quarantine, rather than leaving it to wait out the 30s backoff");

        _cancellationTokenSource.Cancel();
        try { await sut.FetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task A_restored_connection_ends_the_backoff_with_the_monitor_off()
    {
        // With the monitor off the state passed in never reports a recovery, so only the connector's own restored event
        // can end the backoff a restart built up. The read it wakes meets NOGROUP, the stream having gone with the
        // server, and the recreated group is read at the poll's pace again rather than the backoff's.
        var reads = 0;
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ => Interlocked.Increment(ref reads) switch
            {
                <= 4 => Task.FromException<StreamEntry[]>(new RedisException("down")),
                5 => Task.FromException<StreamEntry[]>(new RedisException("NOGROUP No such key or consumer group")),
                _ => Task.FromResult(Array.Empty<StreamEntry>()),
            });
        _database.StreamCreateConsumerGroupAsync(_context.Topic, _context.ConsumerGroup, Arg.Any<RedisValue?>()).ReturnsForAnyArgs(true);
        var redis = _fixture.Create<IRedisConnector>();
        redis.Database.Returns(_database);
        using var waiter = new SignalingFetchWaiter(TimeSpan.FromSeconds(30));

        using var sut = new RedisStreamSubjectWriter<ICacheEvent>(
            _context with { PollInterval = TimeSpan.FromSeconds(30) },
            NullConnectionStateMonitor.Instance,
            redis,
            Channel.CreateUnbounded<ICacheEvent>().Writer,
            _formatter,
            _logger,
            _fixture.Create<ICachingTelemetryProvider>(),
            _fixture.Create<IRedisProfiler>(),
            waiter,
            () => { },
            _cancellationTokenSource.Token);

        // Three failures retried at the poll interval, rung through here; the fourth starts the exponential backoff.
        (await WaitUntil(() => Volatile.Read(ref reads) == 1)).Should().BeTrue();
        for (var i = 2; i <= 4; i++)
        {
            waiter.Signal();
            (await WaitUntil(() => Volatile.Read(ref reads) == i)).Should().BeTrue();
        }
        await Task.Delay(_pollInterval.Multiply(4), TestContext.Current.CancellationToken);
        Volatile.Read(ref reads).Should().Be(4, "the fourth failure backs off past the poll interval");

        redis.OnConnectionRestored += Raise.Event<EventHandler>(redis, EventArgs.Empty);
        (await WaitUntil(() => Volatile.Read(ref reads) == 5)).Should().BeTrue("the restored connection ends the backoff");
        (await WaitUntil(() => _database.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IDatabase.StreamCreateConsumerGroupAsync))))
            .Should().BeTrue("the group went with the server");
        await Task.Delay(_pollInterval.Multiply(4), TestContext.Current.CancellationToken);
        Volatile.Read(ref reads).Should().Be(5, "the recreated group waits for the poll or a ring, not for a read right away");
        waiter.Signal();
        (await WaitUntil(() => Volatile.Read(ref reads) == 6)).Should().BeTrue("the ring is answered at once: the outage's failures no longer pace the reads");
    }

    [Fact]
    public async Task Reads_that_keep_failing_after_a_recovery_back_off_again()
    {
        var reads = 0;
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromException<StreamEntry[]>(new RedisException("down"));
            });
        var redis = _fixture.Create<IRedisConnector>();
        redis.Database.Returns(_database);
        using var waiter = new SignalingFetchWaiter(TimeSpan.FromSeconds(30));

        using var sut = new RedisStreamSubjectWriter<ICacheEvent>(
            _context with { PollInterval = TimeSpan.FromSeconds(30) },
            NullConnectionStateMonitor.Instance,
            redis,
            Channel.CreateUnbounded<ICacheEvent>().Writer,
            _formatter,
            _logger,
            _fixture.Create<ICachingTelemetryProvider>(),
            _fixture.Create<IRedisProfiler>(),
            waiter,
            () => { },
            _cancellationTokenSource.Token);

        (await WaitUntil(() => Volatile.Read(ref reads) == 1)).Should().BeTrue();
        for (var i = 2; i <= 4; i++)
        {
            waiter.Signal();
            (await WaitUntil(() => Volatile.Read(ref reads) == i)).Should().BeTrue();
        }
        redis.OnConnectionRestored += Raise.Event<EventHandler>(redis, EventArgs.Empty);
        (await WaitUntil(() => Volatile.Read(ref reads) == 5)).Should().BeTrue("the restored connection ends the backoff");

        // The streak starts over: three more retries at the poll interval, then the backoff, which a ring does not shorten.
        for (var i = 6; i <= 8; i++)
        {
            waiter.Signal();
            (await WaitUntil(() => Volatile.Read(ref reads) == i)).Should().BeTrue();
        }
        waiter.Signal();
        await Task.Delay(_pollInterval.Multiply(4), TestContext.Current.CancellationToken);
        Volatile.Read(ref reads).Should().Be(8, "reads that keep failing after a recovery back off again");
    }

    [Fact]
    public async Task ProcessEvent_swallows_formatter_exception()
    {
        // Decode throwing inside ProcessEvent must hit the outer catch (LogOnMessageError) so the
        // fetch loop doesn't blow up on a single malformed message.
        var decodeCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns<ICacheEvent?>(_ =>
        {
            decodeCalled.TrySetResult(true);
            throw new InvalidOperationException("decode boom");
        });
        var entries = new[] { new StreamEntry(_fixture.Create<string>(), [new NameValueEntry(_fieldName, _fixture.Create<string>())]) };
        SetupSingleBatch(entries);

        var fetchTask = Sut.FetchTask;
        await decodeCalled.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        _cancellationTokenSource.Cancel();
        Func<Task> act = async () => await fetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        await act.Should().NotThrowAsync("a single bad event must not tear down the fetch loop");
    }

    [Fact]
    public async Task SameSource_event_is_acknowledged_without_writing_to_channel()
    {
        var channel = Channel.CreateBounded<ICacheEvent>(new BoundedChannelOptions(10));
        var ackCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.StreamAcknowledgeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue[]>())
            .Returns(_ => { ackCalled.TrySetResult(true); return Task.FromResult(1L); });
        var entries = new[] { new StreamEntry(_fixture.Create<string>(), [new NameValueEntry(_fieldName, _fixture.Create<string>())]) };
        _formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns(new TestCacheEvent { Valid = true, Source = _sourceUri });
        SetupSingleBatch(entries);

        using var sut = CreateSut(channel.Writer, _logger);

        await ackCalled.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        _cancellationTokenSource.Cancel();
        await sut.FetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        channel.Reader.TryRead(out _).Should().BeFalse("events from the current source must not enter the dispatcher channel");
    }

    [Fact]
    public async Task Invalid_event_is_acknowledged_when_the_telemetry_sink_refuses_the_record()
    {
        var channel = Channel.CreateBounded<ICacheEvent>(new BoundedChannelOptions(10));
        var acked = new TaskCompletionSource<RedisValue[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.StreamAcknowledgeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue[]>())
            .Returns(call => { acked.TrySetResult(call.Arg<RedisValue[]>()); return Task.FromResult(1L); });
        var id = _fixture.Create<string>();
        var entries = new[] { new StreamEntry(id, [new NameValueEntry(_fieldName, _fixture.Create<string>())]) };
        _formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns(new TestCacheEvent { Valid = false });
        SetupSingleBatch(entries);
        var telemetry = new RefusingTelemetryProvider("Caching.RedisStreamSubjectWriter.DispatchEventsAsync.InvalidEvent");

        using var sut = CreateSut(channel.Writer, _logger, telemetry);

        var ids = await acked.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        _cancellationTokenSource.Cancel();
        await sut.FetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        ids.Select(v => v.ToString()).Should().Contain(id, "the poison entry must still be acknowledged");
        telemetry.Exceptions.Should().Contain(RefusingTelemetryProvider.Failure, "the refusal is reported rather than swallowed");
    }

    [Fact]
    public async Task SameSource_event_is_acknowledged_when_the_telemetry_sink_refuses_the_receipt()
    {
        var channel = Channel.CreateBounded<ICacheEvent>(new BoundedChannelOptions(10));
        var acked = new TaskCompletionSource<RedisValue[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.StreamAcknowledgeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue[]>())
            .Returns(call => { acked.TrySetResult(call.Arg<RedisValue[]>()); return Task.FromResult(1L); });
        var id = _fixture.Create<string>();
        var entries = new[] { new StreamEntry(id, [new NameValueEntry(_fieldName, _fixture.Create<string>())]) };
        _formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns(new TestCacheEvent { Valid = true, Source = _sourceUri });
        SetupSingleBatch(entries);
        // Metrics too: TrackTopicReadMetric also runs on this path.
        var telemetry = new RefusingTelemetryProvider("Caching.RedisStreamSubjectWriter.DispatchEventsAsync.EventReceived", refuseMetrics: true);

        using var sut = CreateSut(channel.Writer, _logger, telemetry);

        var ids = await acked.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        _cancellationTokenSource.Cancel();
        await sut.FetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        ids.Select(v => v.ToString()).Should().Contain(id, "the entry must still be acknowledged");
    }

    [Fact]
    public async Task Valid_event_is_dispatched_and_acknowledged_when_the_telemetry_sink_refuses_the_receipt()
    {
        var channel = Channel.CreateBounded<ICacheEvent>(new BoundedChannelOptions(10));
        var acked = new TaskCompletionSource<RedisValue[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.StreamAcknowledgeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue[]>())
            .Returns(call => { acked.TrySetResult(call.Arg<RedisValue[]>()); return Task.FromResult(1L); });
        var id = _fixture.Create<string>();
        var entries = new[] { new StreamEntry(id, [new NameValueEntry(_fieldName, _fixture.Create<string>())]) };
        _formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns(new TestCacheEvent { Valid = true, Source = new Uri("urn:other-source") });
        SetupSingleBatch(entries);
        var telemetry = new RefusingTelemetryProvider("Caching.RedisStreamSubjectWriter.DispatchEventsAsync.EventReceived");

        using var sut = CreateSut(channel.Writer, _logger, telemetry);

        var ids = await acked.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        _cancellationTokenSource.Cancel();
        await sut.FetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        channel.Reader.TryRead(out _).Should().BeTrue("the event must still reach the dispatcher");
        ids.Select(v => v.ToString()).Should().Contain(id);
        telemetry.Exceptions.Should().Contain(RefusingTelemetryProvider.Failure);
    }

    [Fact]
    public async Task Valid_event_is_written_to_channel_and_acknowledged()
    {
        var channel = Channel.CreateBounded<ICacheEvent>(new BoundedChannelOptions(10));
        var ackCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.StreamAcknowledgeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue[]>())
            .Returns(_ => { ackCalled.TrySetResult(true); return Task.FromResult(1L); });
        var entries = new[] { new StreamEntry(_fixture.Create<string>(), [new NameValueEntry(_fieldName, _fixture.Create<string>())]) };
        var ev = new TestCacheEvent { Valid = true, Source = new Uri("urn:other-source") };
        _formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns(ev);
        SetupSingleBatch(entries);

        using var sut = CreateSut(channel.Writer, _logger);

        var read = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        read.Should().BeSameAs(ev);
        await ackCalled.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        _cancellationTokenSource.Cancel();
        await sut.FetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ChannelClosed_during_dispatch_logs_information_and_skips_ack()
    {
        var channel = Channel.CreateBounded<ICacheEvent>(new BoundedChannelOptions(1));
        channel.Writer.Complete();
        var recordingLogger = new RecordingLogger();
        var loggedClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        recordingLogger.OnRecord = r => { if (r.Message.Contains("Channel closed during dispatch")) { loggedClosed.TrySetResult(true); } };
        var entries = new[] { new StreamEntry(_fixture.Create<string>(), [new NameValueEntry(_fieldName, _fixture.Create<string>())]) };
        _formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns(new TestCacheEvent { Valid = true, Source = new Uri("urn:other-source") });
        SetupSingleBatch(entries);

        using var sut = CreateSut(channel.Writer, recordingLogger);

        await loggedClosed.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        _cancellationTokenSource.Cancel();
        await sut.FetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        await _database.DidNotReceiveWithAnyArgs().StreamAcknowledgeAsync(default, default, default(RedisValue[])!);
    }

    [Fact]
    public async Task DispatchEventsAsync_acks_collected_ids_when_cancellation_aborts_foreach()
    {
        var channel = Channel.CreateBounded<ICacheEvent>(new BoundedChannelOptions(1));
        var ackCalled = new TaskCompletionSource<RedisValue[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.StreamAcknowledgeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue[]>())
            .Returns(ci => { ackCalled.TrySetResult(ci.Arg<RedisValue[]>()!); return Task.FromResult(1L); });
        var firstId = _fixture.Create<string>();
        var secondId = _fixture.Create<string>();
        var entries = new[]
        {
            new StreamEntry(firstId, [new NameValueEntry(_fieldName, _fixture.Create<string>())]),
            new StreamEntry(secondId, [new NameValueEntry(_fieldName, _fixture.Create<string>())]),
        };
        _formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns(new TestCacheEvent { Valid = true, Source = new Uri("urn:other-source") });
        SetupSingleBatch(entries);

        using var sut = CreateSut(channel.Writer, _logger);

        await channel.Reader.WaitToReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        _cancellationTokenSource.Cancel();

        var ackArgs = await ackCalled.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        ackArgs.Should().ContainSingle().Which.Should().Be((RedisValue)firstId);

        await sut.FetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Dispatch_failure_logs_error_with_event_and_stream_ids()
    {
        var throwingWriter = new ThrowingChannelWriter(new InvalidOperationException("boom"));
        var recordingLogger = new RecordingLogger();
        var loggedFailed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamEntryId = _fixture.Create<string>();
        var eventId = _fixture.Create<string>();
        recordingLogger.OnRecord = r =>
        {
            if (r.Message.Contains("Failed to dispatch event") && r.Message.Contains(eventId) && r.Message.Contains(streamEntryId))
            {
                loggedFailed.TrySetResult(true);
            }
        };
        var entries = new[] { new StreamEntry(streamEntryId, [new NameValueEntry(_fieldName, _fixture.Create<string>())]) };
        _formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns(new TestCacheEvent { Id = eventId, Valid = true, Source = new Uri("urn:other-source") });
        SetupSingleBatch(entries);

        using var sut = CreateSut(throwingWriter, recordingLogger);

        await loggedFailed.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        _cancellationTokenSource.Cancel();
        await sut.FetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        await _database.DidNotReceiveWithAnyArgs().StreamAcknowledgeAsync(default, default, default(RedisValue[])!);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask InitializeAsync()
    {
        _topic = _fixture.Create<string>();
        _fieldName = _fixture.Create<string>();
        _consumerName = _fixture.Create<string>();
        _consumerGroup = _fixture.Create<string>();
        _sourceUri = new Uri("urn:" + _fixture.Create<string>());
        _pollBatchSize = 100;
        _pollInterval = DefaultPollInterval;
        _context = new RedisStreamContext(_topic, _fieldName, _consumerName, _consumerGroup, _sourceUri, _pollBatchSize, _pollInterval, false, true);
        _fixture.Inject(_context);
        _cancellationTokenSource = new CancellationTokenSource();
        _fixture.Inject(_cancellationTokenSource.Token);
        _database = _fixture.Freeze<IDatabase>();
        _logger = _fixture.Freeze<ILogger>();
        _formatter = _fixture.Freeze<IEventFormatterProxy<ICacheEvent>>();

        var connectionState = _fixture.Freeze<IConnectionState>();
        connectionState.IsConnected.Returns(true);

        var redisConnector = _fixture.Freeze<IRedisConnector>();
        redisConnector.Database.Returns(_database);

        _fixture.Inject(_formatter);
        _fixture.Inject<IFetchWaiter>(new SignalingFetchWaiter(_pollInterval));
        _database.Configure().ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(_ => Task.FromResult(UntrimmedReply()));
        _database.Configure().StreamInfoAsync(Arg.Any<RedisKey>()).ReturnsForAnyArgs(Task.FromResult(default(StreamInfo)));
        _database.Configure().StreamGroupInfoAsync(Arg.Any<RedisKey>()).ReturnsForAnyArgs(Task.FromResult(Array.Empty<StreamGroupInfo>()));
        // A subscribe makes the group with these, synchronously.
        _database.Configure().StreamInfo(Arg.Any<RedisKey>()).ReturnsForAnyArgs(default(StreamInfo));
        _database.Configure().StreamCreateConsumerGroup(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue?>()).ReturnsForAnyArgs(true);
        return ValueTask.CompletedTask;
    }

    private static async Task<bool> WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        return false;
    }

    private static RedisServerException UnknownCommandError(string message) =>
        new(RedisErrorKind.UnknownCommand, CommandFlags.None, message);

    /// <summary>The script's fourth argument: 1 takes the group's position from the server, 0 checks the stream against the one known.</summary>
    private static int FullCheck(NSubstitute.Core.CallInfo call) => (int)call.Arg<RedisValue[]>()[3];

    /// <summary>A wake read's reply on Redis 7: the stream without the group, its oldest entry <paramref name="first"/>, and the <paramref name="entries"/> read, if any.</summary>
    private static RedisResult WakeReply(long entriesAdded, long length, string first, string maxDeleted, params string[] entries) => RedisResult.Create(
    [
        RedisResult.Create(
        [
            RedisResult.Create((RedisValue)"length"), RedisResult.Create((RedisValue)length),
            RedisResult.Create((RedisValue)"last-generated-id"), RedisResult.Create((RedisValue)(entries.Length > 0 ? entries[^1] : "3-0")),
            RedisResult.Create((RedisValue)"max-deleted-entry-id"), RedisResult.Create((RedisValue)maxDeleted),
            RedisResult.Create((RedisValue)"entries-added"), RedisResult.Create((RedisValue)entriesAdded),
            RedisResult.Create((RedisValue)"first-entry"), RedisResult.Create([RedisResult.Create((RedisValue)first)]),
        ]),
        RedisResult.Create(Array.Empty<RedisResult>()),
        entries.Length == 0
            ? RedisResult.Create(RedisValue.Null)
            : RedisResult.Create([RedisResult.Create([RedisResult.Create((RedisValue)"stream"), RedisResult.Create([.. entries.Select(id => RedisResult.Create([RedisResult.Create((RedisValue)id), RedisResult.Create(new RedisValue[] { "f", "v" })]))])])]),
    ]);

    private void SetupSingleBatch(StreamEntry[] entries)
    {
        var emitted = 0;
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ => ++emitted == 1 ? entries : []);
    }

    private void ScriptsUnavailable() =>
        _database.Configure().ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(Task.FromException<RedisResult>(UnknownCommandError("ERR unknown command 'EVALSHA'")));

    /// <summary>The group at the newest entry, 1-0, with every entry added read: nothing to report.</summary>
    private RedisResult UntrimmedReply() => RedisResult.Create(
    [
        RedisResult.Create(new RedisValue[] { "length", 0, "last-generated-id", "1-0", "entries-added", 1 }),
        RedisResult.Create([RedisResult.Create(new RedisValue[] { "name", _consumerGroup, "last-delivered-id", "1-0", "entries-read", 1 })]),
        RedisResult.Create(RedisValue.Null),
    ]);

    /// <summary>The group at 1-0 with 2-0 trimmed away: the oldest entry left is past its last delivery.</summary>
    private RedisResult TrimmedReply() => RedisResult.Create(
    [
        RedisResult.Create(
        [
            RedisResult.Create((RedisValue)"length"), RedisResult.Create((RedisValue)1),
            RedisResult.Create((RedisValue)"last-generated-id"), RedisResult.Create((RedisValue)"3-0"),
            RedisResult.Create((RedisValue)"entries-added"), RedisResult.Create((RedisValue)3),
            RedisResult.Create((RedisValue)"first-entry"), RedisResult.Create([RedisResult.Create((RedisValue)"3-0")]),
        ]),
        RedisResult.Create([RedisResult.Create(new RedisValue[] { "name", _consumerGroup, "last-delivered-id", "1-0", "entries-read", 1 })]),
        RedisResult.Create(RedisValue.Null),
    ]);

    private RedisStreamSubjectWriter<ICacheEvent> CreateSut(ChannelWriter<ICacheEvent> writer, ILogger logger, ICachingTelemetryProvider? telemetry = null, Action? onMessagesMissed = null, IFetchWaiter? waiter = null, RedisStreamContext? context = null)
    {
        var connectionState = _fixture.Create<IConnectionState>();
        connectionState.IsConnected.Returns(true);
        var redis = _fixture.Create<IRedisConnector>();
        redis.Database.Returns(_database);
        return new RedisStreamSubjectWriter<ICacheEvent>(
            context ?? _context,
            connectionState,
            redis,
            writer,
            _formatter,
            logger,
            telemetry ?? _fixture.Create<ICachingTelemetryProvider>(),
            _fixture.Create<IRedisProfiler>(),
            waiter ?? new SignalingFetchWaiter(_pollInterval),
            onMessagesMissed ?? (() => { }),
            _cancellationTokenSource.Token);
    }

    /// <summary>Records what each back-off step asks for; the first returns as a ring would, the second raises a recovery and returns.</summary>
    private sealed class RecordingWaiter(IRedisConnector redis) : IFetchWaiter
    {
        public List<TimeSpan> Timeouts { get; } = [];

        public Task WaitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            lock (Timeouts)
            {
                Timeouts.Add(timeout);
                if (Timeouts.Count == 2)
                {
                    redis.OnConnectionRestored += Raise.Event<EventHandler>(redis, EventArgs.Empty);
                }
            }

            return Task.CompletedTask;
        }

        public void Signal()
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>A waiter whose signals can be held back, so a recovery's can land after the next read failed.</summary>
    private sealed class DeferringWaiter(SignalingFetchWaiter inner) : IFetchWaiter
    {
        public volatile bool Deferring;

        private int _held;

        public Task WaitAsync(CancellationToken cancellationToken) => inner.WaitAsync(cancellationToken);

        public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => inner.WaitAsync(timeout, cancellationToken);

        public void Signal()
        {
            if (Deferring)
            {
                Interlocked.Exchange(ref _held, 1);
                return;
            }

            inner.Signal();
        }

        public void Release()
        {
            Deferring = false;
            if (Interlocked.Exchange(ref _held, 0) == 1)
            {
                inner.Signal();
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingChannelWriter(Exception toThrow) : ChannelWriter<ICacheEvent>
    {
        public override bool TryWrite(ICacheEvent item) => throw toThrow;
        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) => ValueTask.FromException<bool>(toThrow);
        public override ValueTask WriteAsync(ICacheEvent item, CancellationToken cancellationToken = default) => ValueTask.FromException(toThrow);
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _records = new();

        /// <summary>A snapshot: the writer logs from its fetch loop while the test reads.</summary>
        public IReadOnlyList<(LogLevel Level, string Message)> Records => [.. _records];

        public Action<(LogLevel Level, string Message)>? OnRecord { get; set; }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var record = (logLevel, formatter(state, exception));
            _records.Enqueue(record);
            OnRecord?.Invoke(record);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
