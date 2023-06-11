using System.Reactive.Subjects;

namespace UiPath.Platform.Caching.Broadcast.Redis;

internal sealed class RedisStreamSubjectWriter<T> : IDisposable
    where T : IEvent
{
    private bool _disposed;
    private readonly RedisStreamContext _context;
    private readonly IDatabase _database;
    private readonly ISubject<T> _subject;
    private readonly IEventFormatterProxy<T> _formatter;
    private readonly ILogger _logger;
    private readonly CancellationToken _stopToken;
#pragma warning disable IDE0052 // Remove unread private members
    [SuppressMessage("SonarLint.Rule", "S4487:\"Unread\" members should be removed")]
    private readonly Task _fetchLoop;
#pragma warning restore IDE0052 // Remove unread private members

    public RedisStreamSubjectWriter(
        RedisStreamContext context,
        IDatabase database,
        ISubject<T> subject,
        IEventFormatterProxy<T> formatter,
        ILogger logger,
        CancellationToken stopToken)
    {
        _context = context;
        _database = database;
        _subject = subject;
        _formatter = formatter;
        _logger = logger;
        _stopToken = stopToken;
        _fetchLoop = Task.Run(FetchLoop, default);        
    }

    internal TaskStatus FetchTaskStatus => _fetchLoop.Status;

    public void Dispose()
    {
        _subject.OnCompleted();
        _disposed = true;
    }

    private bool ContinueLoop => !(_disposed || _stopToken.IsCancellationRequested);

    private async Task FetchLoop()
    {
        _logger.LogDebug("Fetch events loop started");
        while (ContinueLoop)
        {
            try
            {
                StreamEntry[] events = await _database.StreamReadGroupAsync(
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
                await Task.Delay(_context.PollInterval, _stopToken);
            }
            catch (Exception ex)
            {
                if (ex is TaskCanceledException cancel && cancel.CancellationToken == _stopToken)
                {
                    break;
                }

                try
                {
                    _logger.LogError("Fetch events loop error", ex);
                    await Task.Delay(_context.PollInterval, _stopToken);
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
                    _subject.OnNext(ev);
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
        await _database.StreamAcknowledgeAsync(_context.Topic, _context.ConsumerGroup, ids.ToArray());
        _logger.LogDebug("Dispatched {} messages. Topic : {}", events.Length, _context.Topic);
    }
}
