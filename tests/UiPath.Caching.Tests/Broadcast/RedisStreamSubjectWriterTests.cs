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
            new TimedFetchWaiter(_pollInterval),
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
                connectionState.OnConnectionRestored += Raise.Event<EventHandler>(connectionState, EventArgs.Empty);
            }

            return Task.FromResult(default(StreamInfo));
        });
        _database.Configure().StreamGroupInfoAsync(_context.Topic).ReturnsForAnyArgs(Task.FromResult(Array.Empty<StreamGroupInfo>()));
        ScriptsUnavailable();
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
            new TimedFetchWaiter(_pollInterval),
            () => { },
            _cancellationTokenSource.Token);

        (await WaitUntil(() => _database.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IDatabase.StreamReadGroupAsync)))).Should().BeTrue();
        connectionState.OnConnectionRestored += Raise.Event<EventHandler>(connectionState, EventArgs.Empty);

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
            new TimedFetchWaiter(_pollInterval),
            () => Interlocked.Increment(ref missed),
            _cancellationTokenSource.Token);

        (await WaitUntil(() => Volatile.Read(ref reads) > 0)).Should().BeTrue();
        connectionState.OnConnectionRestored += Raise.Event<EventHandler>(connectionState, EventArgs.Empty);
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
            new TimedFetchWaiter(_pollInterval),
            () => { },
            _cancellationTokenSource.Token);

        (await WaitUntil(() => _database.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IDatabase.StreamReadGroupAsync)))).Should().BeTrue();
        connectionState.OnConnectionRestored += Raise.Event<EventHandler>(connectionState, EventArgs.Empty);

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
            new TimedFetchWaiter(_pollInterval),
            () => { },
            _cancellationTokenSource.Token);

        (await WaitUntil(() => _database.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IDatabase.StreamReadGroupAsync)))).Should().BeTrue();
        connectionState.OnConnectionRestored += Raise.Event<EventHandler>(connectionState, EventArgs.Empty);

        (await WaitUntil(() => Volatile.Read(ref scripts) > 0)).Should().BeTrue();
        _database.ReceivedCalls().Should().NotContain(c => c.GetMethodInfo().Name == nameof(IDatabase.StreamInfoAsync), "a trim must not land between a check and the read it guards");
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
            new TimedFetchWaiter(_pollInterval),
            () => { },
            _cancellationTokenSource.Token);

        await loggedCritical.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        // Server now answers XREADGROUP; the reconnect must wake the loop instead of waiting out the backoff.
        // Written before the release below, so a thread that observes the release also observes this.
        Volatile.Write(ref fail, false);
        connectionState.OnReconnected += Raise.Event<EventHandler>(connectionState, EventArgs.Empty);

        var recovered = await WaitUntil(() => recordingLogger.Records.Any(
            r => r.Level == LogLevel.Information && r.Message.Contains("recovered", StringComparison.OrdinalIgnoreCase)));

        recovered.Should().BeTrue(
            "the reconnect must wake the fetch loop so the next successful read lifts the quarantine, rather than leaving it to wait out the 30s backoff");

        _cancellationTokenSource.Cancel();
        try { await sut.FetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken); } catch (OperationCanceledException) { }
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
        _fixture.Inject<IFetchWaiter>(new TimedFetchWaiter(_pollInterval));
        _database.Configure().ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(_ => Task.FromResult(UntrimmedReply()));
        _database.Configure().StreamInfoAsync(Arg.Any<RedisKey>()).ReturnsForAnyArgs(Task.FromResult(default(StreamInfo)));
        _database.Configure().StreamGroupInfoAsync(Arg.Any<RedisKey>()).ReturnsForAnyArgs(Task.FromResult(Array.Empty<StreamGroupInfo>()));
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

    private static RedisResult UntrimmedReply() => RedisResult.Create(
    [
        RedisResult.Create(new RedisValue[] { "length", 0, "last-generated-id", "1-0", "entries-added", 1 }),
        RedisResult.Create([RedisResult.Create(new RedisValue[] { "name", "group", "last-delivered-id", "1-0", "entries-read", 1 })]),
        RedisResult.Create(RedisValue.Null),
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

    private RedisStreamSubjectWriter<ICacheEvent> CreateSut(ChannelWriter<ICacheEvent> writer, ILogger logger, ICachingTelemetryProvider? telemetry = null, Action? onMessagesMissed = null)
    {
        var connectionState = _fixture.Create<IConnectionState>();
        connectionState.IsConnected.Returns(true);
        var redis = _fixture.Create<IRedisConnector>();
        redis.Database.Returns(_database);
        return new RedisStreamSubjectWriter<ICacheEvent>(
            _context,
            connectionState,
            redis,
            writer,
            _formatter,
            logger,
            telemetry ?? _fixture.Create<ICachingTelemetryProvider>(),
            _fixture.Create<IRedisProfiler>(),
            new TimedFetchWaiter(_pollInterval),
            onMessagesMissed ?? (() => { }),
            _cancellationTokenSource.Token);
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
