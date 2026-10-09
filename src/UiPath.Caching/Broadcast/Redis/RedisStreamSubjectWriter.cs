using System.Threading.Channels;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Broadcast.Redis;

internal sealed partial class RedisStreamSubjectWriter<T> : IDisposable
    where T : IEvent
{
    // Runs on every read, so it returns only what the check needs: XINFO STREAM carries the first and last entries whole.
    private const string CheckAndReadScript = """
        local info = redis.call('XINFO', 'STREAM', KEYS[1])
        local stream = {}
        for i = 1, #info, 2 do
            local field = info[i]
            if field == 'length' or field == 'last-generated-id' or field == 'max-deleted-entry-id' or field == 'entries-added' then
                stream[#stream + 1] = field
                stream[#stream + 1] = info[i + 1]
            elseif field == 'first-entry' and info[i + 1] then
                stream[#stream + 1] = field
                stream[#stream + 1] = { info[i + 1][1] }
            end
        end
        local groups = {}
        for _, group in ipairs(redis.call('XINFO', 'GROUPS', KEYS[1])) do
            for i = 1, #group, 2 do
                if group[i] == 'name' and group[i + 1] == ARGV[1] then
                    groups[1] = group
                end
            end
        end
        local entries = redis.call('XREADGROUP', 'GROUP', ARGV[1], ARGV[2], 'COUNT', ARGV[3], 'STREAMS', KEYS[1], '>')
        return { stream, groups, entries }
        """;

    private const string EventInvalid = "Caching." + nameof(RedisStreamSubjectWriter<T>) + "." + nameof(DispatchEventsAsync) + ".InvalidEvent";
    private const string EventReceived = "Caching." + nameof(RedisStreamSubjectWriter<T>) + "." + nameof(DispatchEventsAsync) + ".EventReceived";
    private const string PropTopicKey = "TopicKey";
    private const string PropTransportId = "TransportId";
    private const string PropEventId = "EventId";
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
    private readonly IFetchWaiter _waiter;
    private readonly SemaphoreSlim _retryGate = new(0, 1);
    private readonly Action _onMessagesMissed;

    private bool _disposed;
    private RedisValue _lastId = StreamPosition.NewMessages;
    private int _consecutiveFailures;
    private volatile bool _unsupportedCommand;
    private volatile bool _checkForGap;
    // Set once the topic has subscribers or the group has been read: only then can a loss leave a local entry stale.
    private volatile bool _inUse;
    private bool _noScripts;
    private bool _gapCheckDenied;

    // An empty read cannot move the group past a trim, so every later check finds the gap this one reported.
    private StreamGap? _reportedGap;
    private ReadCount? _readCount;
    private int _losses;

    public RedisStreamSubjectWriter(
        RedisStreamContext context,
        IConnectionState connectionState,
        IRedisConnector redis,
        ChannelWriter<T> channelWriter,
        IEventFormatterProxy<T> formatter,
        ILogger logger,
        ICachingTelemetryProvider cachingTelemetryProvider,
        IRedisProfiler redisProfiler,
        IFetchWaiter waiter,
        Action onMessagesMissed,
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
        _waiter = waiter;
        _onMessagesMissed = onMessagesMissed;
        _stopTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        _cancelationToken = _stopTokenSource.Token;
        _connectionState.OnReconnected += OnConnectionRecovered;
        _connectionState.OnConnectionRestored += OnConnectionRecovered;
        FetchTask = Task.Run(FetchLoop, _cancelationToken);
        FetchTask.Forget();
    }

    internal Task FetchTask { get; }

    private bool ContinueLoop => !(_disposed || _cancelationToken.IsCancellationRequested);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _connectionState.OnReconnected -= OnConnectionRecovered;
        _connectionState.OnConnectionRestored -= OnConnectionRecovered;
        _stopTokenSource?.Cancel();
        _stopTokenSource?.Dispose();
        _retryGate.Dispose();
        _writer.TryComplete();
    }

    /// <summary>The topic is gaining a subscriber, whose entry a loss can leave stale before this group is ever read.</summary>
    /// <returns>A mark for <see cref="LostSince"/>.</returns>
    internal int MarkSubscribed()
    {
        // Read before publishing _inUse: a loss before it cannot reach this subscriber, and one after it moves the mark.
        var mark = Volatile.Read(ref _losses);
        _inUse = true;
        return mark;
    }

    /// <summary>Whether a loss was reported since <paramref name="mark"/>, expiring the subscribers already there.</summary>
    internal bool LostSince(int mark) => Volatile.Read(ref _losses) != mark;

    /// <summary>Expires what the topic keeps for a loss that was not a trim, such as an event dropped at a full channel.</summary>
    internal void ReportLoss()
    {
        // Counted first, so a subscriber joining while the others are told sees it and is told itself.
        Interlocked.Increment(ref _losses);
        _onMessagesMissed();
    }

    private static bool IsDenied(Exception ex) =>
        ex is RedisServerException && ex.Message.StartsWith("NOPERM", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnsupportedCommand(Exception ex) =>
        ex is RedisCommandException ||
        (ex is RedisServerException &&
         ex.Message.Contains(StreamConstants.UnknownCommandErrorMessage, StringComparison.OrdinalIgnoreCase));

    private void ReportMessagesMissed()
    {
        LogMessagesMissed(_context.Topic, _context.ConsumerGroup);
        ReportLoss();
    }

    private void ReportGap(StreamGap? gap)
    {
        if (gap is not { } found || _reportedGap == found)
        {
            return;
        }

        _reportedGap = found;
        ReportMessagesMissed();
    }

    private void OnConnectionRecovered(object? sender, EventArgs e)
    {
        _checkForGap = true;
        ReleaseRetryGate();
    }

    private void ReleaseRetryGate()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _retryGate.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already pending; collapsing to one is the desired behavior.
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently; there is no loop left to wake.
        }
    }

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
                        await _waiter.WaitAsync(_cancelationToken).ConfigureAwait(false);
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
            // IsConnected turns true before the restored event fires, so the next read may come first.
            _checkForGap = true;
            await _waiter.WaitAsync(_cancelationToken).ConfigureAwait(false);
            return;
        }

        // A trim can reach unread entries between any two polls, so every read the group's position matters for is checked.
        StreamEntry[] events;
        if (_inUse && !_noScripts)
        {
            events = await CheckAndReadAsync().ConfigureAwait(false);
        }
        else
        {
            // Without XINFO a loss cannot be ruled out, so only a reconnect reports one rather than every read.
            if (_inUse && (_checkForGap || !_gapCheckDenied))
            {
                // A recovery raised during a check needs its own before the read moves the group past the evidence.
                do
                {
                    await CheckForGapAsync().ConfigureAwait(false);
                }
                while (_checkForGap && _inUse);
            }

            events = await ReadAsync().ConfigureAwait(false);
        }

        _consecutiveFailures = 0;
        _inUse = true;

        if (_unsupportedCommand)
        {
            _unsupportedCommand = false;
            LogStreamsSupportRecovered(_context.Topic);
        }

        if (events.Length > 0)
        {
            await DispatchEventsAsync(events).ConfigureAwait(false);
            _lastId = events[^1].Id;
            return;
        }
        await _waiter.WaitAsync(_cancelationToken).ConfigureAwait(false);
    }

    private async Task<bool> ProcessException(Exception ex)
    {
        var id = _lastId;
        if (ex is OperationCanceledException cancel && cancel.CancellationToken == _cancelationToken)
        {
            LogReadingStopped(_context.Topic, _context.ConsumerGroup, id);
            return false;
        }

        if (IsUnsupportedCommand(ex))
        {
            if (!_unsupportedCommand)
            {
                _unsupportedCommand = true;
                LogStreamsUnsupported(ex, _context.Topic);
            }

            await WaitForRetryAsync(StreamConstants.MaxErrorBackoff).ConfigureAwait(false);
            return true;
        }

        if (ex.Message.Contains("NOGROUP", StringComparison.OrdinalIgnoreCase))
        {
            if(id != StreamPosition.NewMessages)
            {
                LogRecreatingTopic(_context.Topic, _context.ConsumerGroup, id);
            }

            if (_inUse)
            {
                // The group or the whole stream was removed while in use, so what it held past the last read is gone.
                _inUse = false;
                _checkForGap = false;
                _reportedGap = null;
                _readCount = null;
                ReportMessagesMissed();
            }

            try
            {
                await ConsumerGroups.CreateAsync(_redis.Database, _context.Topic, _context.ConsumerGroup, id).ConfigureAwait(false);
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
            _checkForGap = true;
            LogFetchLoopError(ex);
        }

        await BackoffAsync().ConfigureAwait(false);
        return true;
    }

    private Task BackoffAsync()
    {
        var failures = ++_consecutiveFailures;
        if (failures <= StreamConstants.ErrorBackoffThreshold)
        {
            return _waiter.WaitAsync(_cancelationToken);
        }

        var exponent = Math.Min(failures - StreamConstants.ErrorBackoffThreshold, 16);
        var scaled = _context.PollInterval.Multiply(Math.Pow(2, exponent));
        var delay = scaled > StreamConstants.MaxErrorBackoff ? StreamConstants.MaxErrorBackoff : scaled;
        LogFetchLoopBackoff(failures, delay);
        return WaitForRetryAsync(delay);
    }

    private async Task WaitForRetryAsync(TimeSpan delay)
    {
        try
        {
            await _retryGate.WaitAsync(delay, _cancelationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently with the wait; treat as a wake-up so the loop observes disposal.
        }
    }

    private Task<StreamEntry[]> ReadAsync() => _redis.Database.StreamReadGroupAsync(
        _context.Topic,
        _context.ConsumerGroup,
        _context.ConsumerName,
        StreamConstants.UndeliveredMessages,
        _context.PollBatchSize);

    /// <summary>Checks for a gap and reads in one script, so a trim cannot land between the check and the read.</summary>
    private async Task<StreamEntry[]> CheckAndReadAsync()
    {
        _checkForGap = false;
        RedisResult reply;
        try
        {
            reply = await _redis.Database.ScriptEvaluateAsync(
                CheckAndReadScript,
                [_context.Topic],
                [_context.ConsumerGroup, _context.ConsumerName, _context.PollBatchSize]).ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("no such key", StringComparison.OrdinalIgnoreCase))
        {
            // A missing stream fails the read with NOGROUP, which reports the loss.
            return await ReadAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUnsupportedCommand(ex) || IsDenied(ex))
        {
            // Scripts are off on this server: check and read apart from now on.
            LogScriptsUnavailable(ex, _context.Topic);
            _noScripts = true;
            _checkForGap = true;
            return [];
        }

        var parts = StreamIds.Items(reply);
        ReportGap(StreamIds.Gap(parts[0], parts[1], _context.ConsumerGroup, ref _readCount));

        return StreamIds.Entries(parts[2]);
    }

    /// <summary>Entries trimmed before the group read them were never delivered.</summary>
    private async Task CheckForGapAsync()
    {
        // Cleared first, so a recovery raised while the check awaits arms the next one.
        _checkForGap = false;
        StreamInfo stream;
        StreamGroupInfo[] groups;
        try
        {
            stream = await _redis.Database.StreamInfoAsync(_context.Topic).ConfigureAwait(false);
            groups = await _redis.Database.StreamGroupInfoAsync(_context.Topic).ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("ERR no such key", StringComparison.OrdinalIgnoreCase))
        {
            // A missing stream fails the read with NOGROUP, which reports the loss.
            return;
        }
        catch (Exception ex) when (IsUnsupportedCommand(ex) || IsDenied(ex))
        {
            // XINFO is denied or renamed away, so a loss cannot be ruled out: report one rather than retry a check that cannot succeed.
            LogGapCheckDenied(ex, _context.Topic);
            _gapCheckDenied = true;
            ReportMessagesMissed();
            return;
        }

        var group = Array.Find(groups, g => g.Name == _context.ConsumerGroup);
        if (group.Name is not null)
        {
            ReportGap(StreamIds.Gap(stream, group, ref _readCount));
        }
    }

    private async Task DispatchEventsAsync(StreamEntry[] events)
    {
        List<RedisValue> ids = new(events.Length);
        try
        {
            foreach (StreamEntry @event in events)
            {
                await ProcessEvent(@event, ids).ConfigureAwait(false);
            }
        }
        finally
        {
            if (ids.Count > 0)
            {
                await _redis.Database.StreamAcknowledgeAsync(_context.Topic, _context.ConsumerGroup, [.. ids]).ConfigureAwait(false);
                LogDispatched(ids.Count, _context.Topic);
            }
        }
    }

    private ValueTask ProcessEvent(StreamEntry @event, List<RedisValue> ids)
    {
        if (@event.IsNull)
        {
            return default;
        }

        try
        {
            var ev = _formatter.Decode((ReadOnlyMemory<byte>)@event[_context.FieldName]);
            if (ev is null)
            {
                return default;
            }
            ev.AttachTransportId(@event.Id);

            if (!ev.IsValid())
            {
                HandleInvalidEvent(ev, @event, ids);
                return default;
            }

            if (ev.SameSource(_context.SourceUri))
            {
                // Acknowledged first, so a throw below cannot leave the entry pending.
                ids.Add(@event.Id);
                LogEventFromCurrentSource(ev.Id, _context.Topic, @event.Id);
                _cachingTelemetryProvider.TrackTopicReadMetric(_context.Topic.ToString(), @event.Id);
                TraceReceipt(ev);
                return default;
            }

            return DispatchValidEventAsync(ev, @event, ids);
        }
        catch (Exception ex)
        {
            LogOnMessageError(ex, _context.Topic);
            return default;
        }
    }

    private async ValueTask DispatchValidEventAsync(T ev, StreamEntry @event, List<RedisValue> ids)
    {
        try
        {
            if (!_writer.TryWrite(ev))
            {
                await _writer.WriteAsync(ev, _cancelationToken).ConfigureAwait(false);
            }

            ids.Add(@event.Id);
            TraceReceipt(ev);
        }
        catch (OperationCanceledException) when (_cancelationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ChannelClosedException)
        {
            LogChannelClosed(ev.Id, _context.Topic, @event.Id);
            try
            {
                await _stopTokenSource.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Dispose ran concurrently; cancellation already happened.
            }
        }
        catch (Exception ex)
        {
            LogDispatchFailed(ex, ev.Id, _context.Topic, @event.Id);
        }
    }

    private void HandleInvalidEvent(T ev, StreamEntry @event, List<RedisValue> ids)
    {
        // Acknowledged first, so a throw below cannot leave the entry pending.
        ids.Add(@event.Id);
        _cachingTelemetryProvider.TryTrackEvent(EventInvalid,
        [
            new(PropTopicKey, _context.Topic.ToString()),
            new(PropTransportId, @event.Id.ToString()),
        ]);
        LogEventInvalid(ev.Id, _context.Topic, @event.Id);
    }

    private void TraceReceipt(T ev)
    {
        LogEventReceived(ev.Id, _context.Topic, ev.TransportId);

        if (_context.EmitStreamReceivedEvent)
        {
            _cachingTelemetryProvider.TryTrackEvent(EventReceived,
            [
                new(PropEventId, ev.Id!),
                new(PropTopicKey, _context.Topic.ToString()),
                new(PropTransportId, ev.TransportId!),
            ]);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Topic {Topic}, consumer group {ConsumerGroup}: entries were removed before they were read, so their invalidations were missed")]
    private partial void LogMessagesMissed(RedisKey topic, RedisValue consumerGroup);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recreating topic exception.")]
    private partial void LogRecreatingTopicException(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Fetch events loop error")]
    private partial void LogFetchLoopError(Exception ex);

    [LoggerMessage(
        Level = LogLevel.Critical,
        Message = "Redis Streams reads are not supported by the server for topic {Topic}. This node will not receive cross-node cache invalidations over Streams. Retrying slowly in case the connection moves to a server that supports XREADGROUP; to fix it now switch to Pub/Sub (DefaultTopic: RedisPubSub) or disable broadcast.")]
    private partial void LogStreamsUnsupported(Exception ex, RedisKey topic);

    [LoggerMessage(Level = LogLevel.Information, Message = "Redis Streams reads recovered for topic {Topic}; resuming normal polling.")]
    private partial void LogStreamsSupportRecovered(RedisKey topic);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fetch events loop backing off for {Delay} after {Failures} consecutive failures")]
    private partial void LogFetchLoopBackoff(int failures, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Event from current source. Id {EventId}  Topic : {Topic}, StreamId : {StreamId}")]
    private partial void LogEventFromCurrentSource(string? eventId, RedisKey topic, RedisValue streamId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event invalid. Id {EventId}  Topic : {Topic}, StreamId : {StreamId}")]
    private partial void LogEventInvalid(string? eventId, RedisKey topic, RedisValue streamId);

    [LoggerMessage(Level = LogLevel.Error, Message = "OnMessage error. Topic : {Topic}")]
    private partial void LogOnMessageError(Exception ex, RedisKey topic);

    [LoggerMessage(Level = LogLevel.Information, Message = "Channel closed during dispatch. Id {EventId} Topic : {Topic}, StreamId : {StreamId}.")]
    private partial void LogChannelClosed(string? eventId, RedisKey topic, RedisValue streamId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to dispatch event. Id {EventId} Topic : {Topic}, StreamId : {StreamId}.")]
    private partial void LogDispatchFailed(Exception ex, string? eventId, RedisKey topic, RedisValue streamId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dispatched {Length} messages. Topic : {Topic}")]
    private partial void LogDispatched(int length, RedisKey topic);

    [LoggerMessage(Level = LogLevel.Warning, Message = "XINFO is unavailable for stream {Topic}; a reconnect is reported as a loss")]
    private partial void LogGapCheckDenied(Exception ex, RedisKey topic);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scripts are unavailable for stream {Topic}; gap checks and reads now run apart")]
    private partial void LogScriptsUnavailable(Exception ex, RedisKey topic);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Event received. Id {EventId}  Topic : {Topic}, StreamId: {StreamId}")]
    private partial void LogEventReceived(string? eventId, RedisKey topic, string? streamId);
}

