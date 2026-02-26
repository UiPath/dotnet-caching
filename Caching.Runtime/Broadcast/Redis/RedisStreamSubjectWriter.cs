using System.Threading.Channels;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Broadcast.Redis;

internal sealed partial class RedisStreamSubjectWriter<T> : IDisposable
    where T : IEvent
{
    private bool _disposed;
    private readonly RedisStreamContext _context;
    private readonly IRedisConnector _redis;
    private readonly IConnectionState _connectionState;
    private readonly ChannelWriter<T> _writer;
    private readonly IEventFormatterProxy<T> _formatter;
    private readonly ILogger _logger;
    private readonly ICachingTelemetryProvider _cachingTelemetryProvider;
    private readonly IRedisProfiler _redisProfiler;
    private readonly CancellationTokenSource _stopTokenSource;
    private readonly CancellationToken _cancelationToken;
    private RedisValue _lastId = StreamPosition.NewMessages;

    public RedisStreamSubjectWriter(
        RedisStreamContext context,
        IConnectionState connectionState,
        IRedisConnector redis,
        ChannelWriter<T> channelWriter,
        IEventFormatterProxy<T> formatter,
        ILogger logger,
        ICachingTelemetryProvider cachingTelemetryProvider,
        IRedisProfiler redisProfiler,
        CancellationToken stopToken)
    {
        _context = context;
        _connectionState = connectionState;
        _redis = redis;
        _writer = channelWriter;
        _formatter = formatter;
        _logger = logger;
        _cachingTelemetryProvider = cachingTelemetryProvider;
        _redisProfiler = redisProfiler;
        _stopTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        _cancelationToken = _stopTokenSource.Token;
        FetchTask = Task.Run(FetchLoop, _cancelationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _stopTokenSource?.Cancel();
        _stopTokenSource?.Dispose();
        _writer.TryComplete();
    }

    internal Task FetchTask { get; }

    private bool ContinueLoop => !(_disposed || _cancelationToken.IsCancellationRequested);

    private async Task FetchLoop()
    {
        LogFetchLoopStarted();
        while (ContinueLoop)
        {
            using (CreateProfilerSession())
            {
                try
                {
                    await FetchBatch().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    try
                    {
                        if (!await ProcessException(ex).ConfigureAwait(false))
                        {
                            break;
                        }
                    }
                    catch (Exception)
                    {
                        await Wait().ConfigureAwait(false);
                    }
                }
            }
        }
        LogFetchLoopStopped();
    }

    private IDisposable CreateProfilerSession()
    {
        var sessionId = _context.ProfilerEnabled ? $"{_context.Topic}:{Guid.NewGuid()}" : null;
        return _redisProfiler.CreateSession(sessionId);
    }

    private async Task FetchBatch()
    {
        if (!_connectionState.IsConnected)
        {
            await Wait().ConfigureAwait(false);
            return;
        }

        StreamEntry[] events = await _redis.Database.StreamReadGroupAsync(
            _context.Topic,
            _context.ConsumerGroup,
            _context.ConsumerName,
            StreamConstants.UndeliveredMessages,
            _context.PollBatchSize).ConfigureAwait(false);

        if (events.Length > 0)
        {
            await DispatchEventsAsync(events).ConfigureAwait(false);
            _lastId = events[^1].Id;
            return;
        }
        await Wait().ConfigureAwait(false);
    }

    private Task Wait() => Task.Delay(_context.PollInterval, _cancelationToken);

    private async Task<bool> ProcessException(Exception ex)
    {
        var id = _lastId;
        if (ex is TaskCanceledException cancel && cancel.CancellationToken == _cancelationToken)
        {
            LogReadingStopped(_context.Topic, _context.ConsumerGroup, id);
            return false;
        }

        if (ex.Message.StartsWith("NOGROUP", StringComparison.OrdinalIgnoreCase))
        {
            if(id != StreamPosition.NewMessages)
            {
                LogRecreatingTopic(_context.Topic, _context.ConsumerGroup, id);
            }

            try
            {
                await _redis.Database.StreamCreateConsumerGroupAsync(_context.Topic, _context.ConsumerGroup, id).ConfigureAwait(false);
            }
            catch (RedisServerException rex) when (rex.Message == StreamConstants.ConsumerGroupNameExistsErrorMessage)
            {
                LogConsumerGroupExists(_context.Topic, _context.ConsumerGroup);
                return true;
            }
            catch (Exception ex2)
            {
                LogRecreatingTopicException(ex2);
            }
        }
        else
        {
           LogFetchLoopError(ex);
        }

        await Wait().ConfigureAwait(false);
        return true;
    }

    private async Task DispatchEventsAsync(StreamEntry[] events)
    {
        List<RedisValue> ids = new(events.Length);

        foreach (StreamEntry @event in events)
        {
            if (@event.IsNull)
            {
                continue;
            }

            try
            {
                var ev = _formatter.Decode(@event[_context.FieldName].ToString());
                if (ev is null)
                {
                    continue;
                }
                ev.AttachTransportId(@event.Id);

                if (ev.IsValid())
                {
                    if (ev.SameSource(_context.SourceUri))
                    {
                        LogEventFromCurrentSource(ev.Id, _context.Topic, @event.Id);
                        _cachingTelemetryProvider.TrackTopicReadMetric(_context.Topic!, @event.Id);
                        TraceReceipt(ev);
                        ids.Add(@event.Id);
                    }
                    else
                    {
                        if (_writer.TryWrite(ev))
                        {
                            ids.Add(@event.Id);
                            TraceReceipt(ev);
                        }
                        else
                        {
                            LogFailedProcessingEntry(ev.Id, _context.Topic, @event.Id);
                        }
                    }
                }
                else
                {
                    _cachingTelemetryProvider.TrackEvent($"Caching.{nameof(RedisStreamSubjectWriter<T>)}.{nameof(DispatchEventsAsync)}.InvalidEvent", new Dictionary<string, string>
                    {
                        { "TopicKey", _context.Topic!},
                        { "TransportId", @event.Id! }
                    });
                    LogEventInvalid(ev.Id, _context.Topic, @event.Id);
                    ids.Add(@event.Id);
                }
            }
            catch (Exception ex)
            {
                LogOnMessageError(ex, _context.Topic);
            }
        }

        if (ids.Count == 0)
        {
            return;
        }


        await _redis.Database.StreamAcknowledgeAsync(_context.Topic, _context.ConsumerGroup, ids.ToArray()).ConfigureAwait(false);
        LogDispatched(events.Length, _context.Topic);
    }

    private void TraceReceipt(T ev)
    {
        LogEventReceived(ev.Id, _context.Topic, ev.TransportId);

        if (_context.EmitStreamReceivedEvent)
        {
            _cachingTelemetryProvider.TrackEvent($"Caching.{nameof(RedisStreamSubjectWriter<T>)}.{nameof(DispatchEventsAsync)}.EventReceived", new Dictionary<string, string>
        {
            { "EventId", ev.Id!},
            { "TopicKey", _context.Topic!},
            { "TransportId", ev.TransportId! }
        });
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fetch events loop started")]
    private partial void LogFetchLoopStarted();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fetch events loop stopped")]
    private partial void LogFetchLoopStopped();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Reading topic {Topic}, consumer group {ConsumerGroup} stopped at {Id}")]
    private partial void LogReadingStopped(RedisKey topic, RedisValue consumerGroup, RedisValue id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recreating Topic {Topic}, consumer group {ConsumerGroup}, last id {LastId}")]
    private partial void LogRecreatingTopic(RedisKey topic, RedisValue consumerGroup, RedisValue lastId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "On Topic {Topic} consumer group {ConsumerGroup} already exists")]
    private partial void LogConsumerGroupExists(RedisKey topic, RedisValue consumerGroup);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recreating topic exception.")]
    private partial void LogRecreatingTopicException(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Fetch events loop error")]
    private partial void LogFetchLoopError(Exception ex);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Event from current source. Id {EventId}  Topic : {Topic}, StreamId : {StreamId}")]
    private partial void LogEventFromCurrentSource(string? eventId, RedisKey topic, RedisValue streamId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed processing stream entry. Id {EventId} Topic : {Topic}, StreamId : {StreamId}. Will retry.")]
    private partial void LogFailedProcessingEntry(string? eventId, RedisKey topic, RedisValue streamId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event invalid. Id {EventId}  Topic : {Topic}, StreamId : {StreamId}")]
    private partial void LogEventInvalid(string? eventId, RedisKey topic, RedisValue streamId);

    [LoggerMessage(Level = LogLevel.Error, Message = "OnMessage error. Topic : {Topic}")]
    private partial void LogOnMessageError(Exception ex, RedisKey topic);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dispatched {Length} messages. Topic : {Topic}")]
    private partial void LogDispatched(int length, RedisKey topic);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Event received. Id {EventId}  Topic : {Topic}, StreamId: {StreamId}")]
    private partial void LogEventReceived(string? eventId, RedisKey topic, string? streamId);
}

