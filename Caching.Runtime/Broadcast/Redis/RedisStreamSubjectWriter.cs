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
                StreamEntry[] events = await _redis.Database.StreamReadGroupAsync(
                    _context.Topic,
                    _context.ConsumerGroup,
                    _context.ConsumerName,
                    ">",
                    _context.PollBatchSize).ConfigureAwait(false);

                if (events.Length > 0)
                {
                    await DispatchEventsAsync(events);
                    continue;
                }
                await Task.Delay(_context.PollInterval, _cancelationToken);
            }
            catch (Exception ex)
            {
                if (ex is TaskCanceledException cancel && cancel.CancellationToken == _cancelationToken)
                {
                    break;
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
            }
        }
        _logger.LogDebug("Fetch events loop stopped");
    }

    private async Task DispatchEventsAsync(StreamEntry[] events)
    {
        List<RedisValue> ids = new();

        foreach (StreamEntry @event in events)
        {
            if (@event.IsNull)
            {
                continue;
            }

            try
            {
                var ev = _formatter.Decode(@event[_context.FieldName].ToString());
                if (ev == null)
                {
                    continue;
                }

                if (ev.IsValid(_context.SourceUri))
                {
                    _logger.LogTrace("Event received. Id {}  Topic : {}", ev.Id, _context.Topic);
                    _writer.TryWrite(ev);
                }
                else
                {
                    _logger.LogDebug("Event received. Id {}  Topic : {}", ev.Id, _context.Topic);
                }
                ids.Add(@event.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OnMessage error. Topic : {}", _context.Topic);
            }
        }

        if(ids.Count == 0)
        {
            return;
        }

        await _redis.Database.StreamAcknowledgeAsync(_context.Topic, _context.ConsumerGroup, ids.ToArray());
        _logger.LogDebug("Dispatched {} messages. Topic : {}", events.Length, _context.Topic);
    }
}
