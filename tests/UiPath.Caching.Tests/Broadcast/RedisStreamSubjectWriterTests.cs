using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;
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

    [Fact]
    public async Task Start_reads_the_consumers_pending_entries_once_before_new_ones()
    {
        var channel = Channel.CreateUnbounded<ICacheEvent>();
        var pendingId = _fixture.Create<string>();
        var positions = new ConcurrentQueue<string>();
        var ev = new TestCacheEvent { Valid = true, Source = new Uri("urn:other-source") };
        _formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns(ev);
        // Unacknowledged on purpose: the pending entry stays where it is, so reading the pending list again would find it again.
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(call => Read(positions, call, position => position == ">" ? [] : [Entry(pendingId)]));

        using var sut = CreateSut(channel.Writer, _logger, pollBatchSize: 2);

        var read = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        (await WaitUntil(() => positions.Count(p => p == ">") >= 3)).Should().BeTrue();
        _cancellationTokenSource.Cancel();
        await sut.FetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        read.Should().BeSameAs(ev);
        positions.First().Should().Be("0", "entries delivered to this consumer before it started are read before new ones");
        positions.Count(p => p == "0").Should().Be(1, "a pending entry that stays pending must not be read over and over");
    }

    [Fact]
    public async Task A_failed_read_is_followed_by_a_read_of_the_pending_entries()
    {
        var channel = Channel.CreateUnbounded<ICacheEvent>();
        var lostId = _fixture.Create<string>();
        var positions = new ConcurrentQueue<string>();
        var failed = 0;
        var ev = new TestCacheEvent { Valid = true, Source = new Uri("urn:other-source") };
        _formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns(ev);
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(call => Read(positions, call, position => position switch
            {
                ">" when Interlocked.Exchange(ref failed, 1) == 0 => throw Timeout(),
                ">" => [],
                // The entries the timed-out read was given.
                _ when Volatile.Read(ref failed) == 1 => [Entry(lostId)],
                _ => [],
            }));

        using var sut = CreateSut(channel.Writer, _logger, pollBatchSize: 2);

        var read = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        _cancellationTokenSource.Cancel();
        await sut.FetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        read.Should().BeSameAs(ev);
        positions.Take(3).Should().Equal("0", ">", "0");
    }

    [Fact]
    public async Task A_full_batch_of_pending_entries_is_followed_by_the_entries_after_it()
    {
        var channel = Channel.CreateUnbounded<ICacheEvent>();
        var positions = new ConcurrentQueue<string>();
        _formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns(new TestCacheEvent { Valid = true, Source = new Uri("urn:other-source") });
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(call => Read(positions, call, position => position switch
            {
                "0" => [Entry("1-0"), Entry("2-0")],
                "2-0" => [Entry("3-0")],
                _ => [],
            }));

        using var sut = CreateSut(channel.Writer, _logger, pollBatchSize: 2);

        (await WaitUntil(() => positions.Contains(">"))).Should().BeTrue();
        _cancellationTokenSource.Cancel();
        await sut.FetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        positions.Should().StartWith(["0", "2-0", ">"]);
        channel.Reader.Count.Should().Be(3);
    }

    [Fact]
    public async Task A_pending_entry_deleted_from_the_stream_is_acknowledged()
    {
        var acked = new TaskCompletionSource<RedisValue[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.StreamAcknowledgeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue[]>())
            .Returns(call => { acked.TrySetResult(call.Arg<RedisValue[]>()); return Task.FromResult(1L); });
        var positions = new ConcurrentQueue<string>();
        var id = _fixture.Create<string>();
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(call => Read(positions, call, position => position == "0" ? [new StreamEntry(id, [])] : []));
        var telemetry = new RecordingTelemetryProvider();
        var recordingLogger = new RecordingLogger();

        using var sut = CreateSut(Channel.CreateUnbounded<ICacheEvent>().Writer, recordingLogger, telemetry, pollBatchSize: 2);

        var ids = await acked.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        _cancellationTokenSource.Cancel();
        await sut.FetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        ids.Select(v => v.ToString()).Should().Equal(id);
        _formatter.DidNotReceiveWithAnyArgs().Decode(default);
        telemetry.Events.Should().NotContain(e => e.Name.EndsWith(".UndecodableEvent", StringComparison.Ordinal), "a deleted entry is not a bad payload");
        recordingLogger.Records.Should().ContainSingle(r => r.Level == LogLevel.Warning && r.Message.Contains("deleted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Read_timeouts_log_a_warning_once_per_hundred_and_never_an_error()
    {
        var recordingLogger = new RecordingLogger();
        var timeouts = 0;
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(call => Read(new ConcurrentQueue<string>(), call, position =>
                // Every other new-entry read times out; the successes between keep the backoff from growing.
                position == ">" && Interlocked.Increment(ref timeouts) % 2 == 1 ? throw Timeout() : []));

        using var sut = CreateSut(Channel.CreateUnbounded<ICacheEvent>().Writer, recordingLogger, pollInterval: TimeSpan.FromMilliseconds(1));

        (await WaitUntil(() => Volatile.Read(ref timeouts) >= 2 * 101 + 2)).Should().BeTrue();
        _cancellationTokenSource.Cancel();
        try { await sut.FetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken); } catch (OperationCanceledException) { }

        recordingLogger.Records.Should().NotContain(r => r.Level >= LogLevel.Error);
        recordingLogger.Records.Count(r => r.Level == LogLevel.Warning && r.Message.Contains("timed out", StringComparison.Ordinal))
            .Should().Be(2, "the 1st and the 101st timeouts are reported");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_entry_that_cannot_be_decoded_is_acknowledged(bool decodeThrows)
    {
        // Decoding the same bytes fails again, so left pending it would be read on every pass of the pending list.
        var acked = new TaskCompletionSource<RedisValue[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.StreamAcknowledgeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue[]>())
            .Returns(call => { acked.TrySetResult(call.Arg<RedisValue[]>()); return Task.FromResult(1L); });
        _formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns(_ => decodeThrows ? throw new FormatException("bad payload") : null);
        var id = _fixture.Create<string>();
        SetupSingleBatch([new StreamEntry(id, [new NameValueEntry(_fieldName, _fixture.Create<string>())])]);

        using var sut = CreateSut(Channel.CreateUnbounded<ICacheEvent>().Writer, _logger);

        var ids = await acked.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        _cancellationTokenSource.Cancel();
        await sut.FetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        ids.Select(v => v.ToString()).Should().Equal(id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_entry_that_cannot_be_decoded_is_tracked_and_logged_as_a_warning(bool decodeThrows)
    {
        var acked = new TaskCompletionSource<RedisValue[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.StreamAcknowledgeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue[]>())
            .Returns(call => { acked.TrySetResult(call.Arg<RedisValue[]>()); return Task.FromResult(1L); });
        _formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns(_ => decodeThrows ? throw new FormatException("bad payload") : null);
        var id = _fixture.Create<string>();
        SetupSingleBatch([new StreamEntry(id, [new NameValueEntry(_fieldName, _fixture.Create<string>())])]);
        var telemetry = new RecordingTelemetryProvider();
        var recordingLogger = new RecordingLogger();

        using var sut = CreateSut(Channel.CreateUnbounded<ICacheEvent>().Writer, recordingLogger, telemetry);

        await acked.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        _cancellationTokenSource.Cancel();
        await sut.FetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        telemetry.Events.Should().ContainSingle(e => e.Name == "Caching.RedisStreamSubjectWriter.DispatchEventsAsync.UndecodableEvent")
            .Which.Properties.Should().Contain("TopicKey", _context.Topic.ToString()).And.Contain("TransportId", id);
        recordingLogger.Records.Should().ContainSingle(r => r.Level == LogLevel.Warning && r.Message.Contains(id, StringComparison.Ordinal));
        recordingLogger.Records.Should().NotContain(r => r.Level >= LogLevel.Error, "a bad entry is dropped, not a failure of the reader");
    }

    [Fact]
    public async Task Undecodable_entries_are_each_tracked_but_logged_as_a_warning_once_per_hundred()
    {
        var acked = new TaskCompletionSource<RedisValue[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.StreamAcknowledgeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue[]>())
            .Returns(call => { acked.TrySetResult(call.Arg<RedisValue[]>()); return Task.FromResult(1L); });
        _formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns((ICacheEvent?)null);
        SetupSingleBatch([.. Enumerable.Range(1, 101).Select(i => new StreamEntry($"{i}-0", [new NameValueEntry(_fieldName, _fixture.Create<string>())]))]);
        var telemetry = new RecordingTelemetryProvider();
        var recordingLogger = new RecordingLogger();

        using var sut = CreateSut(Channel.CreateUnbounded<ICacheEvent>().Writer, recordingLogger, telemetry);

        (await acked.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken)).Should().HaveCount(101);
        _cancellationTokenSource.Cancel();
        await sut.FetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        telemetry.Events.Count(e => e.Name.EndsWith(".UndecodableEvent", StringComparison.Ordinal)).Should().Be(101);
        recordingLogger.Records.Count(r => r.Level == LogLevel.Warning).Should().Be(2, "the 1st and the 101st are reported");
    }

    [Fact]
    public async Task An_undecodable_entry_is_acknowledged_when_the_telemetry_sink_refuses_the_record()
    {
        var acked = new TaskCompletionSource<RedisValue[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.StreamAcknowledgeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue[]>())
            .Returns(call => { acked.TrySetResult(call.Arg<RedisValue[]>()); return Task.FromResult(1L); });
        _formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns((ICacheEvent?)null);
        var id = _fixture.Create<string>();
        SetupSingleBatch([new StreamEntry(id, [new NameValueEntry(_fieldName, _fixture.Create<string>())])]);
        var telemetry = new RefusingTelemetryProvider("Caching.RedisStreamSubjectWriter.DispatchEventsAsync.UndecodableEvent");
        var recordingLogger = new RecordingLogger();

        using var sut = CreateSut(Channel.CreateUnbounded<ICacheEvent>().Writer, recordingLogger, telemetry);

        var ids = await acked.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        _cancellationTokenSource.Cancel();
        await sut.FetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        ids.Select(v => v.ToString()).Should().Equal(id);
        telemetry.Exceptions.Should().Contain(RefusingTelemetryProvider.Failure);
    }

    [Fact]
    public async Task A_pending_batch_with_a_deleted_entry_warns_once_and_still_dispatches_the_rest()
    {
        var acked = new TaskCompletionSource<RedisValue[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.StreamAcknowledgeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue[]>())
            .Returns(call => { acked.TrySetResult(call.Arg<RedisValue[]>()); return Task.FromResult(1L); });
        var channel = Channel.CreateUnbounded<ICacheEvent>();
        var ev = new TestCacheEvent { Valid = true, Source = new Uri("urn:other-source") };
        _formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns(ev);
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(call => Read(new ConcurrentQueue<string>(), call, position => position == "0" ? [new StreamEntry("1-0", []), Entry("2-0")] : []));
        var recordingLogger = new RecordingLogger();

        using var sut = CreateSut(channel.Writer, recordingLogger, pollBatchSize: 4);

        var ids = await acked.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        _cancellationTokenSource.Cancel();
        await sut.FetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        ids.Select(v => v.ToString()).Should().BeEquivalentTo(["1-0", "2-0"]);
        recordingLogger.Records.Count(r => r.Level == LogLevel.Warning && r.Message.Contains("deleted", StringComparison.Ordinal)).Should().Be(1);
        channel.Reader.TryRead(out var read).Should().BeTrue();
        read.Should().BeSameAs(ev);
    }

    [Fact]
    public async Task A_deleted_pending_entry_is_logged_once_after_its_acknowledgement_succeeds()
    {
        var acks = 0;
        var acked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.StreamAcknowledgeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue[]>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref acks) == 1)
                {
                    return Task.FromException<long>(Timeout());
                }

                acked.TrySetResult();
                return Task.FromResult(1L);
            });
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(call => Read(new ConcurrentQueue<string>(), call, position => position == "0" ? [new StreamEntry("1-0", [])] : []));
        var recordingLogger = new RecordingLogger();

        using var sut = CreateSut(Channel.CreateUnbounded<ICacheEvent>().Writer, recordingLogger, pollBatchSize: 4);

        await acked.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        (await WaitUntil(() => recordingLogger.Records.Any(r => r.Message.Contains("deleted", StringComparison.Ordinal)))).Should().BeTrue();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        _cancellationTokenSource.Cancel();
        try { await sut.FetchTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken); } catch (OperationCanceledException) { }

        recordingLogger.Records.Count(r => r.Message.Contains("deleted", StringComparison.Ordinal)).Should().Be(1, "the first acknowledgement failed, so only the pass whose acknowledgement succeeded logs");
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

    private static RedisTimeoutException Timeout() => new(CommandFlags.None, "Timeout performing XREADGROUP", CommandStatus.Sent);

    private static Task<StreamEntry[]> Read(ConcurrentQueue<string> positions, NSubstitute.Core.CallInfo call, Func<string, StreamEntry[]> answer)
    {
        var position = call.ArgAt<RedisValue?>(3).ToString()!;
        positions.Enqueue(position);
        return Task.FromResult(answer(position));
    }

    private void SetupSingleBatch(StreamEntry[] entries)
    {
        var emitted = 0;
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ => ++emitted == 1 ? entries : []);
    }

    private StreamEntry Entry(string id) => new(id, [new NameValueEntry(_fieldName, _fixture.Create<string>())]);

    private RedisStreamSubjectWriter<ICacheEvent> CreateSut(ChannelWriter<ICacheEvent> writer, ILogger logger, ICachingTelemetryProvider? telemetry = null, int? pollBatchSize = null, TimeSpan? pollInterval = null)
    {
        var connectionState = _fixture.Create<IConnectionState>();
        connectionState.IsConnected.Returns(true);
        var redis = _fixture.Create<IRedisConnector>();
        redis.Database.Returns(_database);
        var context = _context with
        {
            PollBatchSize = pollBatchSize ?? _context.PollBatchSize,
            PollInterval = pollInterval ?? _context.PollInterval,
        };
        return new RedisStreamSubjectWriter<ICacheEvent>(
            context,
            connectionState,
            redis,
            writer,
            _formatter,
            logger,
            telemetry ?? _fixture.Create<ICachingTelemetryProvider>(),
            _fixture.Create<IRedisProfiler>(),
            new TimedFetchWaiter(context.PollInterval),
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
