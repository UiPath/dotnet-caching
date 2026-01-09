using FluentAssertions.Extensions;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;
using NSubstitute.ReceivedExtensions;
using StackExchange.Redis;

namespace UiPath.Platform.Caching.Tests.Broadcast;

public class RedisStreamSubjectWriterTests : IAsyncLifetime
{
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
        _formatter.Received(0).Decode(Arg.Any<string>());
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
        _formatter.Received(0).Decode(Arg.Any<string>());
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
        _formatter.Received(0).Decode(Arg.Any<string>());
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
        _formatter.Received(0).Decode(Arg.Any<string>());
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
        _formatter.Received(0).Decode(Arg.Any<string>());
    }

    [Fact]
    public async Task StreamReadGroupAsync_null_event()
    {
        var entries = new[] { new StreamEntry(_fixture.Create<string>(), new[] { new NameValueEntry(_fieldName, _fixture.Create<string>()) }) };
        _formatter.Decode(Arg.Any<string>()).Returns(default(ICacheEvent?));
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ => entries);

        Func<Task> act = async () => await Sut.FetchTask;
        await act.Should().NotCompleteWithinAsync(_pollInterval.Multiply(5));
        _cancellationTokenSource.Cancel();
        await act.Should().CompleteWithinAsync(_pollInterval.Multiply(10));
        _formatter.Received().Decode(Arg.Any<string>());
    }

    [Fact]
    public async Task StreamReadGroupAsync_invalid_event()
    {
        var entries = new[] { new StreamEntry(_fixture.Create<string>(), new[] { new NameValueEntry(_fieldName, _fixture.Create<string>()) }) };
        _formatter.Decode(Arg.Any<string>()).Returns(new TestCacheEvent
        {
            Valid = false,
        });
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ => entries);

        Func<Task> act = async () => await Sut.FetchTask;
        await act.Should().NotCompleteWithinAsync(_pollInterval.Multiply(7));
        _cancellationTokenSource.Cancel();
        await act.Should().CompleteWithinAsync(_pollInterval.Multiply(10));
        _formatter.Received().Decode(Arg.Any<string>());
    }

    [Fact]
    public async Task StreamReadGroupAsync_valid_event()
    {
        var entries = new[] { new StreamEntry(_fixture.Create<string>(), new[] { new NameValueEntry(_fieldName, _fixture.Create<string>()) }) };
        _formatter.Decode(Arg.Any<string>()).Returns(new TestCacheEvent
        {
            Valid = true,
        });
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .ReturnsForAnyArgs(_ => entries);

        Func<Task> act = async () => await Sut.FetchTask;
        await act.Should().NotCompleteWithinAsync(_pollInterval.Multiply(5));
        _cancellationTokenSource.Cancel();
        await act.Should().CompleteWithinAsync(_pollInterval.Multiply(10));
        _formatter.Received().Decode(Arg.Any<string>());
    }

    [Fact]
    public async Task StreamReadGroupAsync_valid_event_subject_exception()
    {
        var entries = new[] { new StreamEntry(_fixture.Create<string>(), new[] { new NameValueEntry(_fieldName, _fixture.Create<string>()) }) };
        _formatter.Decode(Arg.Any<string>()).Returns(new TestCacheEvent
        {
            Valid = true,
        });
        _database.StreamReadGroupAsync(_context.Topic, _context.ConsumerGroup, _context.ConsumerName, ">", _context.PollBatchSize)
            .Returns(_ => entries);

        Func<Task> act = async () => await Sut.FetchTask;
        await act.Should().NotCompleteWithinAsync(_pollInterval.Multiply(100));
        _cancellationTokenSource.Cancel();
        _formatter.Received().Decode(Arg.Any<string>());
    }

    [Fact]
    public void Dispose_works_as_expected()
    {
        Action act = () => Sut.Dispose();
        act.Should().NotThrow();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _topic = _fixture.Create<string>();
        _fieldName = _fixture.Create<string>();
        _consumerName = _fixture.Create<string>();
        _consumerGroup = _fixture.Create<string>();
        _sourceUri = new Uri("urn:" + _fixture.Create<string>());
        _pollBatchSize = _fixture.Create<int>();
        _pollInterval = TimeSpan.FromMilliseconds(50);
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
        return Task.CompletedTask;
    }
}
