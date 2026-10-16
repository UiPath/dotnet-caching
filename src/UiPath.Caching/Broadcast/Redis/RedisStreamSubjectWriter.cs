using System.Threading.Channels;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Broadcast.Redis;

internal sealed partial class RedisStreamSubjectWriter<T> : IDisposable
    where T : IEvent
{
    // Runs on every read, so it returns only what the check needs: XINFO STREAM carries the first and last entries whole.
    // XINFO GROUPS grows with the number of groups, so a wake (ARGV[4] = 0) checks against the stream alone.
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
        if ARGV[4] == '1' then
            for _, group in ipairs(redis.call('XINFO', 'GROUPS', KEYS[1])) do
                for i = 1, #group, 2 do
                    if group[i] == 'name' and group[i + 1] == ARGV[1] then
                        groups[1] = group
                    end
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
    // One view of the group for the subscribes and the reader's recovery, held across their commands, so neither slips
    // between the other's check and its command.
    private readonly SemaphoreSlim _groupGate = new(1, 1);
    private readonly Action _onMessagesMissed;

    private bool _disposed;
    private RedisValue _lastId = StreamPosition.NewMessages;
    private int _consecutiveFailures;
    private volatile bool _unsupportedCommand;
    private volatile bool _checkForGap;
    // Set once the topic has subscribers or the group has been read: only then can a loss leave a local entry stale.
    private volatile bool _inUse;
    // A subscriber has joined; its entries can go stale.
    private volatile bool _subscribed;
    // The last entry read, for a subscribe that has to make the group again: what came since is replayed, as the reader's
    // own recreate does, rather than skipped.
    private volatile string? _replayFrom;
    // The group exists as far as this process knows (made or found under _groupGate), until a read proves it gone.
    private bool _groupExists;
    // _inUse, and the recoveries seen, when the read in flight was issued.
    private bool _readInUse;
    private int _recoveriesAtRead;
    private int _recoveries;
    // The recoveries seen when the streak of failures being counted began: a read from after a later one starts its own.
    private int _streakGeneration;
    private bool _noScripts;
    private bool _gapCheckDenied;
    // The last read returned fewer entries than asked: the group is at the head.
    private bool _caughtUp;
    // Environment.TickCount64 of the last check with XINFO GROUPS; 0 = never.
    private long _checkedAt;
    // The group's last-delivered id, from that check or the last read; Null until either. A wake is checked against it,
    // with the count _readCount holds for it.
    private RedisValue _position = RedisValue.Null;

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
        // The connector's own events: with the connection monitor off, the state passed in never reports a recovery.
        _redis.OnReconnected += OnConnectionRecovered;
        _redis.OnConnectionRestored += OnConnectionRecovered;
        FetchTask = Task.Run(FetchLoop, _cancelationToken);
        FetchTask.Forget();
    }

    private enum Recreate
    {
        Made,
        Found,
        Failed,
    }

    internal Task FetchTask { get; }

    /// <summary>Test seam: runs between the recovery check and the count of a failure.</summary>
    internal Action? BeforeCountingFailure { get; set; }

    private bool ContinueLoop => !(_disposed || _cancelationToken.IsCancellationRequested);

    // The connection came back since the read in flight went out: its failure was the outage's, not a new streak's.
    private bool RecoveredSinceRead => Volatile.Read(ref _recoveries) != _recoveriesAtRead;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _redis.OnReconnected -= OnConnectionRecovered;
        _redis.OnConnectionRestored -= OnConnectionRecovered;
        _stopTokenSource?.Cancel();
        _stopTokenSource?.Dispose();
        _groupGate.Dispose();
        _writer.TryComplete();
    }

    /// <summary>A subscriber is joining: makes sure the group exists, then marks the topic in use.</summary>
    /// <returns>A mark for <see cref="LostSince"/>.</returns>
    internal int MarkSubscribed()
    {
        _groupGate.Wait(_cancelationToken);
        try
        {
            if (!_groupExists)
            {
                _groupExists = EnsureGroup();
            }

            // Read before publishing _inUse: a loss before it cannot reach this subscriber, and one after it moves the mark.
            var mark = Volatile.Read(ref _losses);
            _subscribed = true;
            _inUse = true;
            return mark;
        }
        finally
        {
            _groupGate.Release();
        }
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
        // Redis answers again: the loop ends the wait it is in, or about to enter, and the failures from here on are a
        // streak of their own, such as the read that recreates a group lost with the server. The streak is the loop's to
        // reset, so nothing here races its count.
        Interlocked.Increment(ref _recoveries);
        _waiter.Signal();
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

        var events = await ReadBatchAsync().ConfigureAwait(false);

        _consecutiveFailures = 0;
        _inUse = true;
        _caughtUp = events.Length < _context.PollBatchSize;

        if (_unsupportedCommand)
        {
            _unsupportedCommand = false;
            LogStreamsSupportRecovered(_context.Topic);
        }

        if (events.Length > 0)
        {
            await DispatchEventsAsync(events).ConfigureAwait(false);
            _lastId = events[^1].Id;
            _position = _lastId;
            _replayFrom = _lastId;
            if (!_caughtUp)
            {
                return;
            }
        }
        // At the head: a read now would find nothing.
        await _waiter.WaitAsync(_cancelationToken).ConfigureAwait(false);
    }

    /// <summary>Every read the group's position matters for is checked; the one with XINFO GROUPS keeps the poll's cadence.</summary>
    private async Task<StreamEntry[]> ReadBatchAsync()
    {
        _readInUse = _inUse;
        _recoveriesAtRead = Volatile.Read(ref _recoveries);
        if (!_readInUse)
        {
            return await ReadAsync().ConfigureAwait(false);
        }

        if (!_noScripts)
        {
            var full = _checkForGap || !_caughtUp || _checkedAt == 0 || Environment.TickCount64 - _checkedAt >= (long)_context.PollInterval.TotalMilliseconds;
            return await CheckAndReadAsync(full).ConfigureAwait(false);
        }

        // Without XINFO a loss cannot be ruled out, so only a reconnect reports one rather than every read.
        if (_checkForGap || !_gapCheckDenied)
        {
            // A recovery raised during a check needs its own before the read moves the group past the evidence.
            do
            {
                await CheckForGapAsync().ConfigureAwait(false);
            }
            while (_checkForGap && _inUse);
        }

        return await ReadAsync().ConfigureAwait(false);
    }

    private async Task<bool> ProcessException(Exception ex)
    {
        var id = _lastId;
        if (ex is OperationCanceledException cancel && cancel.CancellationToken == _cancelationToken)
        {
            LogReadingStopped(_context.Topic, _context.ConsumerGroup, id);
            return false;
        }

        if (RedisUtils.IsUnsupportedCommand(ex))
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
            if (await RecreateGroupAsync(id).ConfigureAwait(false))
            {
                return true;
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

    /// <summary>Recreates the group a read found missing; <c>true</c> when it was there after all, so the next read need not wait.</summary>
    private async Task<bool> RecreateGroupAsync(RedisValue id)
    {
        if (id != StreamPosition.NewMessages)
        {
            LogRecreatingTopic(_context.Topic, _context.ConsumerGroup, id);
        }

        // Judged as of the read: a subscriber that joined while it was in flight made the group it did not find.
        var lost = _readInUse;
        Recreate outcome;
        await _groupGate.WaitAsync(_cancelationToken).ConfigureAwait(false);
        try
        {
            if (lost)
            {
                ForgetGroup();
            }

            // A group a subscriber relies on was made or found before this point, so one made now is proof it is gone.
            var relied = _groupExists && _subscribed;
            outcome = await CreateGroupAsync(id).ConfigureAwait(false);
            if (outcome == Recreate.Made && relied)
            {
                ForgetGroup();
                lost = true;
            }

            _groupExists |= outcome != Recreate.Failed;
        }
        finally
        {
            _groupGate.Release();
        }

        // Told once the gate is free: an observer told may subscribe again, which takes it.
        if (lost)
        {
            ReportMessagesMissed();
        }

        return outcome == Recreate.Found;
    }

    private async Task<Recreate> CreateGroupAsync(RedisValue id)
    {
        try
        {
            return await ConsumerGroups.CreateAsync(_redis.Database, _context.Topic, _context.ConsumerGroup, id).ConfigureAwait(false)
                ? Recreate.Made
                : Recreate.Failed;
        }
        catch (RedisServerException rex) when (rex.Message == StreamConstants.ConsumerGroupNameExistsErrorMessage)
        {
            LogConsumerGroupExists(_context.Topic, _context.ConsumerGroup);
            return Recreate.Found;
        }
        catch (Exception ex2)
        {
            LogRecreatingTopicException(ex2);
            return Recreate.Failed;
        }
    }

    /// <summary>Creates the group where the reader left off, at the newest entry if nowhere, or finds it; a failure is the subscriber's to see.</summary>
    private bool EnsureGroup()
    {
        try
        {
            return ConsumerGroups.Create(_redis.Database, _context.Topic, _context.ConsumerGroup, _replayFrom ?? StreamPosition.NewMessages);
        }
        catch (RedisServerException ex) when (ex.Message == StreamConstants.ConsumerGroupNameExistsErrorMessage)
        {
            LogConsumerGroupExists(_context.Topic, _context.ConsumerGroup);
            return true;
        }
    }

    /// <summary>The group or the whole stream was removed, so what it held past the last read is gone; the caller reports it.</summary>
    private void ForgetGroup()
    {
        _inUse = false;
        _groupExists = false;
        _checkForGap = false;
        _reportedGap = null;
        _readCount = null;
        _position = RedisValue.Null;
        // The recreated group's position comes from its first check.
        _checkedAt = 0;
    }

    private Task BackoffAsync()
    {
        if (RecoveredSinceRead)
        {
            return Task.CompletedTask;
        }

        BeforeCountingFailure?.Invoke();
        if (_streakGeneration != _recoveriesAtRead)
        {
            // The first failure since a recovery: the outage's streak does not pace it.
            _streakGeneration = _recoveriesAtRead;
            _consecutiveFailures = 0;
        }

        var failures = ++_consecutiveFailures;
        if (failures <= StreamConstants.ErrorBackoffThreshold)
        {
            // A ring, the poll or a recovery ends it.
            return _waiter.WaitAsync(_cancelationToken);
        }

        var exponent = Math.Min(failures - StreamConstants.ErrorBackoffThreshold, 16);
        var scaled = _context.PollInterval.Multiply(Math.Pow(2, exponent));
        var delay = scaled > StreamConstants.MaxErrorBackoff ? StreamConstants.MaxErrorBackoff : scaled;
        LogFetchLoopBackoff(failures, delay);
        return WaitForRetryAsync(delay);
    }

    /// <summary>Waits <paramref name="delay"/> out; rings wake the waiter but do not end it, a recovery does.</summary>
    private async Task WaitForRetryAsync(TimeSpan delay)
    {
        var until = Environment.TickCount64 + (long)delay.TotalMilliseconds;
        while (ContinueLoop && !RecoveredSinceRead && Environment.TickCount64 is var now && now < until)
        {
            await _waiter.WaitAsync(TimeSpan.FromMilliseconds(until - now), _cancelationToken).ConfigureAwait(false);
        }
    }

    private Task<StreamEntry[]> ReadAsync() => _redis.Database.StreamReadGroupAsync(
        _context.Topic,
        _context.ConsumerGroup,
        _context.ConsumerName,
        StreamConstants.UndeliveredMessages,
        _context.PollBatchSize);

    /// <summary>Checks for a gap and reads in one script, so a trim cannot land between the check and the read.</summary>
    /// <param name="full">Take the group's position from the server; otherwise check against the last one known.</param>
    private async Task<StreamEntry[]> CheckAndReadAsync(bool full)
    {
        _checkForGap = false;
        RedisResult reply;
        try
        {
            reply = await _redis.Database.ScriptEvaluateAsync(
                CheckAndReadScript,
                [_context.Topic],
                [_context.ConsumerGroup, _context.ConsumerName, _context.PollBatchSize, full ? 1 : 0]).ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("no such key", StringComparison.OrdinalIgnoreCase))
        {
            // A missing stream fails the read with NOGROUP, which reports the loss.
            return await ReadAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (RedisUtils.IsUnsupportedCommand(ex) || RedisUtils.IsDenied(ex))
        {
            // Scripts are off on this server: check and read apart from now on.
            LogScriptsUnavailable(ex, _context.Topic);
            _noScripts = true;
            _checkForGap = true;
            return [];
        }

        var parts = StreamIds.Items(reply);
        var stream = StreamIds.Parse(parts[0]);
        if (full)
        {
            _checkedAt = Environment.TickCount64;
            if (StreamIds.Group(parts[1], _context.ConsumerGroup) is { } group)
            {
                _position = group.LastDelivered;
                ReportGap(StreamIds.Gap(stream, group.LastDelivered, group.EntriesRead, ref _readCount));
            }
        }
        else if (!_position.IsNull)
        {
            ReportGap(StreamIds.Gap(stream, _position, null, ref _readCount));
        }

        var entries = StreamIds.Entries(parts[2]);
        if (entries.Length > 0 && entries.Length < _context.PollBatchSize)
        {
            // The read reached the head: the group's count is every entry added, whatever a trim took on the way.
            _readCount = new(entries[^1].Id, stream.EntriesAdded);
        }

        return entries;
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
        catch (Exception ex) when (RedisUtils.IsUnsupportedCommand(ex) || RedisUtils.IsDenied(ex))
        {
            // XINFO is denied or renamed away, so a loss cannot be ruled out: report one rather than retry a check that cannot succeed.
            LogGapCheckDenied(ex, _context.Topic);
            _gapCheckDenied = true;
            ReportMessagesMissed();
            return;
        }

        _checkedAt = Environment.TickCount64;
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

