using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;
using NSubstitute.ReceivedExtensions;
using StackExchange.Redis;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests.Broadcast;

public class RedisStreamSubjectWriterTests : IAsyncLifetime
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

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
    }

    [Fact]
    public async Task NOGROUP_recovery_swallows_BUSYGROUP_when_group_already_exists()
    {
        const string BusyGroupMessage = "BUSYGROUP Consumer Group name already exists";

        var createCalled = new TaskCompletionSource();
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ThrowsAsyncForAnyArgs(_ => throw new RedisException("NOGROUP unknown group"));
        _database.StreamCreateConsumerGroupAsync(_context.Topic, _context.ConsumerGroup, Arg.Any<RedisValue?>())
            .ThrowsAsyncForAnyArgs(_ => { createCalled.TrySetResult(); throw new RedisServerException(BusyGroupMessage); });
        _database.ClearReceivedCalls();

        var fetchTask = Sut.FetchTask;
        await createCalled.Task.WaitAsync(NogroupPollTimeout, TestContext.Current.CancellationToken);
        _cancellationTokenSource.Cancel();
        try { await fetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken); } catch (OperationCanceledException) { }
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
    }

    private static readonly TimeSpan NogroupPollTimeout = TimeSpan.FromSeconds(30);

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

        var fail = true;
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ => fail
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
            _cancellationTokenSource.Token);

        await loggedCritical.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        // Server now answers XREADGROUP; the reconnect must wake the loop instead of waiting out the backoff.
        fail = false;
        connectionState.OnReconnected += Raise.Event<EventHandler>(connectionState, EventArgs.Empty);

        var recovered = await WaitUntil(() => recordingLogger.Records.Any(
            r => r.Level == LogLevel.Information && r.Message.Contains("recovered", StringComparison.OrdinalIgnoreCase)));

        recovered.Should().BeTrue(
            "the reconnect must wake the fetch loop so the next successful read lifts the quarantine, rather than leaving it to wait out the 30s backoff");

        _cancellationTokenSource.Cancel();
        try { await sut.FetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken); } catch (OperationCanceledException) { }
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
        recordingLogger.OnRecord = r => { if (r.Message.Contains("Channel closed during dispatch")) loggedClosed.TrySetResult(true); };
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

    private void SetupSingleBatch(StreamEntry[] entries)
    {
        var emitted = 0;
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ => ++emitted == 1 ? entries : []);
    }

    private RedisStreamSubjectWriter<ICacheEvent> CreateSut(ChannelWriter<ICacheEvent> writer, ILogger logger)
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
            _fixture.Create<ICachingTelemetryProvider>(),
            _fixture.Create<IRedisProfiler>(),
            new TimedFetchWaiter(_pollInterval),
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
        public List<(LogLevel Level, string Message)> Records { get; } = [];
        public Action<(LogLevel Level, string Message)>? OnRecord { get; set; }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var record = (logLevel, formatter(state, exception));
            Records.Add(record);
            OnRecord?.Invoke(record);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
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
        _pollBatchSize = _fixture.Create<int>();
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
        return ValueTask.CompletedTask;
    }

    // RedisServerException(string) is obsolete as of StackExchange.Redis 3.1 and slated for removal in 3.2.
    // Its replacement takes RedisErrorKind, which upstream still marks [Experimental] (SER007), so the
    // suppression is centralized here rather than repeated at each call site.
#pragma warning disable SER007 // RedisErrorKind is for evaluation purposes only
    private static RedisServerException UnknownCommandError(string message) =>
        new(RedisErrorKind.UnknownCommand, CommandFlags.None, message);
#pragma warning restore SER007
}
