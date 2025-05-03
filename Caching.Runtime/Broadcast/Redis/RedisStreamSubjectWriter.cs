using System.Threading.Channels;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Broadcast.Redis;

internal sealed class RedisStreamSubjectWriter<T> : IDisposable
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
        _logger.LogDebug("Fetch events loop started");
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
        _logger.LogDebug("Fetch events loop stopped");
    }

    private IDisposable CreateProfilerSession() => _context.ProfilerEnabled ? _redisProfiler.CreateSession($"{_context.Topic}:{Guid.NewGuid()}") : Disposable.Empty;

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
            _logger.LogDebug("Reading topic {Topic}, consumer group {ConsumerGroup} stopped at {Id}", _context.Topic, _context.ConsumerGroup, id);
            return false;
        }

        if (ex.Message.StartsWith("NOGROUP", StringComparison.OrdinalIgnoreCase))
        {
            if(id != StreamPosition.NewMessages)
            {
                _logger.LogWarning("Recreating Topic {Topic}, consumer group {ConsumerGroup}, last id {LastId}", _context.Topic, _context.ConsumerGroup, id);
            }

            try
            {
                await _redis.Database.StreamCreateConsumerGroupAsync(_context.Topic, _context.ConsumerGroup, id).ConfigureAwait(false);
            }
            catch (RedisServerException rex) when (rex.Message == StreamConstants.ConsumerGroupNameExistsErrorMessage)
            {
                _logger.LogDebug("On Topic {Topic} consumer group {ConsumerGroup} already exists", _context.Topic, _context.ConsumerGroup);
                return true;
            }
            catch (Exception ex2)
            {
                _logger.LogError(ex2, "Recreating topic exception.");
            }
        }
        else
        {
           _logger.LogError(ex, "Fetch events loop error");
        }

        await Wait().ConfigureAwait(false);
        return true;
    }

    private async Task DispatchEventsAsync(StreamEntry[] events)
    {
        List<RedisValue> ids = [];

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

                _logger.LogInformation("Event received. Id {EventId}  Topic : {Topic}, StreamId: {StreamId}, Key: {Key}", ev.Id, _context.Topic, ev.TransportId, ev.Key);

                if (ev.IsValid())
                {
                    if (ev.SameSource(_context.SourceUri))
                    {
                        _logger.LogTrace("Event from current source. Id {EventId}  Topic : {Topic}, StreamId : {StreamId}", ev.Id, _context.Topic, @event.Id);
                        _cachingTelemetryProvider.TrackTopicReadMetric(_context.Topic!, @event.Id);
                        ids.Add(@event.Id);
                    }
                    else
                    {
                        if (_writer.TryWrite(ev))
                        {
                            ids.Add(@event.Id);
                        }
                        else
                        {
                            _logger.LogWarning("Failed processing stream entry. Id {EventId} Topic : {Topic}, StreamId : {StreamId}. Will retry.", ev.Id, _context.Topic, @event.Id);
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
                    _logger.LogWarning("Event invalid. Id {EventId}  Topic : {Topic}, StreamId : {StreamId}", ev.Id, _context.Topic, @event.Id);
                    ids.Add(@event.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OnMessage error. Topic : {Topic}", _context.Topic);
            }
        }

        if (ids.Count == 0)
        {
            return;
        }


        await _redis.Database.StreamAcknowledgeAsync(_context.Topic, _context.ConsumerGroup, ids.ToArray()).ConfigureAwait(false);
        _logger.LogDebug("Dispatched {Length} messages. Topic : {Topic}", events.Length, _context.Topic);
    }
}

