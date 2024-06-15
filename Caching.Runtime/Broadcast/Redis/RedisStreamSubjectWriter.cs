using System.Threading.Channels;

namespace UiPath.Platform.Caching.Broadcast.Redis;

internal sealed class RedisStreamSubjectWriter<T> : IDisposable
    where T : IEvent
{
    private bool _disposed;
    private readonly RedisStreamContext _context;
    private readonly IRedisConnector _redis;
    private readonly ChannelWriter<T> _writer;
    private readonly IEventFormatterProxy<T> _formatter;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopTokenSource;
    private readonly CancellationToken _cancelationToken;
    private RedisValue _lastId = StreamPosition.NewMessages;

    public RedisStreamSubjectWriter(
        RedisStreamContext context,
        IRedisConnector redis,
        ChannelWriter<T> channelWriter,
        IEventFormatterProxy<T> formatter,
        ILogger logger,
        CancellationToken stopToken)
    {
        _context = context;
        _redis = redis;
        _writer = channelWriter;
        _formatter = formatter;
        _logger = logger;
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
            try
            {
                await FetchBatch().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!await ProcessException(ex).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        _logger.LogDebug("Fetch events loop stopped");
    }

    private async Task FetchBatch()
    {
        StreamEntry[] events = await _redis.Database.StreamReadGroupAsync(
            _context.Topic,
            _context.ConsumerGroup,
            _context.ConsumerName,
            StreamConstants.UndeliveredMessages,
            _context.PollBatchSize).ConfigureAwait(false);

        if (events.Length > 0)
        {
            await DispatchEventsAsync(events);
            _lastId = events[^1].Id;
            return;
        }
        await Task.Delay(_context.PollInterval, _cancelationToken);
    }

    private async Task<bool> ProcessException(Exception ex)
    {
        if (ex is RedisServerException server && server.Message.Contains("NOGROUP", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(ex, "NOGROUP. Recreating Topic {Topic}, consumer group {ConsumerGroup}, last id {LastId}", _context.Topic, _context.ConsumerGroup, _lastId);
            try
            {
                await _redis.Database.StreamCreateConsumerGroupAsync(_context.Topic, _context.ConsumerGroup, _lastId);
                return true;
            }
            catch(Exception ex2)
            {
                _logger.LogError(ex2, "Recreating topic exception.");
            }
        }

        if (ex is TaskCanceledException cancel && cancel.CancellationToken == _cancelationToken)
        {
            _logger.LogDebug("Reading topic {Topic}, consumer group {ConsumerGroup} stopped at {Id}", _context.Topic, _context.ConsumerGroup, _lastId);
            return false;
        }

        try
        {
            _logger.LogError(ex, "Fetch events loop error");
            await Task.Delay(_context.PollInterval, _cancelationToken);
        }
        catch
        {
            // no op
        }

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

                if (ev.IsValid(_context.SourceUri))
                {
                    _logger.LogTrace("Event received. Id {EventId}  Topic : {Topic}", ev.Id, _context.Topic);
                    _writer.TryWrite(ev);
                }
                else
                {
                    _logger.LogDebug("Event received. Id {EventId}  Topic : {Topic}", ev.Id, _context.Topic);
                }
                ids.Add(@event.Id);
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

        await _redis.Database.StreamAcknowledgeAsync(_context.Topic, _context.ConsumerGroup, ids.ToArray());
        _logger.LogDebug("Dispatched {Length} messages. Topic : {Topic}", events.Length, _context.Topic);
    }
}

