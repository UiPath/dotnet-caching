using System.Diagnostics;
using System.Threading.Channels;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Broadcast.Redis;

internal sealed partial class RedisStreamSubjectWriter<T> : IDisposable
    where T : IEvent
{
    private const string EventInvalid = "Caching." + nameof(RedisStreamSubjectWriter<T>) + "." + nameof(DispatchEventsAsync) + ".InvalidEvent";
    private const string EventUndecodable = "Caching." + nameof(RedisStreamSubjectWriter<T>) + "." + nameof(DispatchEventsAsync) + ".UndecodableEvent";
    private const string EventReceived = "Caching." + nameof(RedisStreamSubjectWriter<T>) + "." + nameof(DispatchEventsAsync) + ".EventReceived";
    // The entries a group made at the head skipped, read back to see whose they were; more than this counts as a loss.
    private const int SkippedEntriesChecked = 256;
    private const string PropTopicKey = "TopicKey";
    private const string PropTransportId = "TransportId";
    private const string PropEventId = "EventId";
    private readonly RedisStreamContext _context;
    private readonly IRedisConnector _redis;
    private readonly IStreamPort _stream;
    private readonly IConnectionState _connectionState;
    private readonly ChannelWriter<T> _writer;
    private readonly IEventFormatterProxy<T> _formatter;
    private readonly ILogger _logger;
    private readonly ICachingTelemetryProvider _cachingTelemetryProvider;
    private readonly IRedisProfiler _redisProfiler;
    private readonly CancellationTokenSource _stopTokenSource;
    private readonly CancellationToken _cancelationToken;
    private readonly IFetchWaiter _waiter;
    private readonly Action _onMessagesMissed;

    private bool _disposed;
    private RedisValue _lastId = StreamPosition.NewMessages;
    // Where the next read of this consumer's pending entries starts; null once they are all read. With AutoAck the entries
    // an earlier run left pending are drained once, at start.
    private RedisValue? _pendingFrom = StreamConstants.PendingMessages;
    private long _timeouts;
    private long _undecodable;
    private int _consecutiveFailures;
    private volatile bool _unsupportedCommand;
    private volatile bool _checkForGap;
    // Set once the topic has subscribers or the group has been read: only then can a loss leave a local entry stale.
    private volatile bool _inUse;
    // A subscriber has joined; its entries can go stale.
    private volatile bool _subscribed;
    // The group exists as far as this process knows (made or found by the reader), until a read proves it gone.
    private volatile bool _groupExists;
    // The Stopwatch timestamp of the first subscriber that joined while no group was known, 0 for none: a group then made
    // at the head skipped what was added since.
    private long _unanchoredAt;
    // _inUse, and the recoveries seen, when the read in flight was issued.
    private bool _readInUse;
    private int _recoveriesAtRead;
    private int _recoveries;
    // The recoveries seen when the streak of failures being counted began: a read from after a later one starts its own.
    private int _streakGeneration;
    private bool _noScripts;
    // A NOACK read is out, or its reply is being handled: the group has moved on whether or not it came back.
    private bool _autoAckReadInFlight;
    // The loss of a failed NOACK read was reported, for the failures that follow it until a read succeeds, which reports again.
    private bool _autoAckLossReported;
    // A NOACK entry of the batch being dispatched was lost.
    private bool _dispatchLost;
    // The batch came back empty without a read, so it says nothing about the group.
    private bool _readSkipped;
    // A create at the head failed with the group perhaps made; the head read before it, for the group found later.
    private bool _createSent;
    private StreamHead? _sentHead;
    // This node made the group on a stream that had never held an entry, so Redis 6 cannot tell its first entry from a trim.
    private bool _createdOnEmpty;
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
        CancellationToken stopToken,
        IStreamPort? stream = null)
    {
        _context = context;
        _connectionState = connectionState;
        _redis = redis;
        _stream = stream ?? new RedisStreamPort(context, redis);
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
        _writer.TryComplete();
    }

    /// <summary>A subscriber is joining: marks the topic in use and, with no group known, wakes the reader to make it.</summary>
    /// <returns>A mark for <see cref="LostSince"/>.</returns>
    /// <remarks>Runs on the caller's thread, often a request's, so it neither waits nor sends a command.</remarks>
    [SuppressMessage("Major Code Smell", "S2589:Boolean expressions should not be gratuitous", Justification = "The reader sets the volatile _groupExists between the two reads; the second one is the handshake.")]
    internal int MarkSubscribed()
    {
        // Read before publishing _inUse: a loss before it cannot reach this subscriber, and one after it moves the mark.
        var mark = Volatile.Read(ref _losses);
        _subscribed = true;
        _inUse = true;
        if (!_groupExists)
        {
            // The exchange fences the instant ahead of the second look; the reader publishes the group before it takes the
            // instant, so one of the two sees the other. The first instant is kept: it is the earliest a loss could start.
            Interlocked.CompareExchange(ref _unanchoredAt, Math.Max(1, Stopwatch.GetTimestamp()), 0);
            if (!_groupExists)
            {
                _waiter.Signal();
            }
        }

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

    private static bool IsEmptyHead(StreamHead? head) => head is { } h && (!h.Exists || h.LastId == "0-0");

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
                        await _waiter.WaitAsync(_context.PollInterval, _cancelationToken).ConfigureAwait(false);
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
            await _waiter.WaitAsync(_context.PollInterval, _cancelationToken).ConfigureAwait(false);
            return;
        }

        if (_pendingFrom is { } from)
        {
            await FetchPendingBatch(from).ConfigureAwait(false);
            return;
        }

        var events = await ReadBatchAsync().ConfigureAwait(false);

        if (!_groupExists && !_readSkipped)
        {
            // The read found the group: there for the subscribers that joined without one, unless a create of this node
            // that seemed to fail made it at the head after they joined.
            _groupExists = true;
            if (await FoundGroupSkippedAsync().ConfigureAwait(false))
            {
                ReportMessagesMissed();
            }
        }

        _consecutiveFailures = 0;
        _autoAckReadInFlight = false;
        _inUse = true;
        if (_autoAckLossReported)
        {
            // What the tier cached during the streak may predate a lost read, and the report at its start came before that.
            _autoAckLossReported = false;
            ReportMessagesMissed();
        }

        _caughtUp = events.Length < _context.PollBatchSize;

        if (_unsupportedCommand)
        {
            _unsupportedCommand = false;
            LogStreamsSupportRecovered(_context.Topic);
        }

        if (events.Length > 0)
        {
            await DispatchEventsAsync(events, !_context.AutoAck).ConfigureAwait(false);
            _lastId = events[^1].Id;
            _position = _lastId;
            if (!_caughtUp)
            {
                return;
            }
        }
        // At the head: a read now would find nothing.
        await _waiter.WaitAsync(_cancelationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the entries Redis gave this consumer without an acknowledgement, such as those of a read that timed out on the client.</summary>
    private async Task FetchPendingBatch(RedisValue from)
    {
        // A pending read does not move the group, so it needs no gap check, but a failure of it is handled as a read's.
        _readSkipped = false;
        _readInUse = _inUse;
        _recoveriesAtRead = Volatile.Read(ref _recoveries);
        var events = await ReadAsync(from).ConfigureAwait(false);
        if (events.Length > 0)
        {
            await DispatchEventsAsync(events, true).ConfigureAwait(false);
        }

        // Given to this consumer, then trimmed or deleted before it read them: their invalidations are lost. Told only once
        // acknowledged, so a failed acknowledgement's retry does not tell it again.
        var deleted = events.Count(static e => !e.IsNull && e.Values is not { Length: > 0 });
        if (deleted > 0)
        {
            LogPendingEntriesDeleted(_context.Topic, deleted);
            ReportLoss();
        }

        // Read on from the last entry, not from the start: an entry left unacknowledged would be read again and again.
        _pendingFrom = events.Length < _context.PollBatchSize || events[^1].Id.IsNull ? null : (RedisValue?)events[^1].Id;
    }

    /// <summary>Every read the group's position matters for is checked; the one with XINFO GROUPS runs at most once a PollInterval, and only on a read, so while the doorbell is live as rarely as once a NotifiedPollInterval.</summary>
    private async Task<StreamEntry[]> ReadBatchAsync()
    {
        _readSkipped = false;
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

        var readInFlight = _autoAckReadInFlight;
        _autoAckReadInFlight = false;
        if (!_context.AutoAck)
        {
            _pendingFrom = StreamConstants.PendingMessages;
        }
        else if (readInFlight && !_autoAckLossReported && !RedisUtils.IsUnsupportedCommand(ex) && !ex.Message.Contains("NOGROUP", StringComparison.OrdinalIgnoreCase))
        {
            // Without the pending list a read that failed after Redis delivered it cannot be redone, so the local tier must not
            // stay stale. Once at the start of a streak, and once more when it ends, rather than once per failure.
            _autoAckLossReported = true;
            ReportMessagesMissed();
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
            LogFetchFailed(ex);
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

        // Judged as of the read: a subscriber that joined while it was in flight relied on no group yet. A group already
        // known gone was reported when it went, and one never known is the unanchored subscribers' case below.
        var lost = _readInUse && _groupExists;
        if (lost)
        {
            ForgetGroup();
        }

        // A group a subscriber relies on was made or found before this point, so one made now is proof it is gone.
        var relied = _groupExists && _subscribed;
        var (outcome, head) = await CreateGroupAsync(id).ConfigureAwait(false);
        if (outcome == Recreate.Made && relied)
        {
            ForgetGroup();
            lost = true;
        }

        if (outcome == Recreate.Made)
        {
            _createdOnEmpty = IsEmptyHead(head);
        }
        else if (outcome == Recreate.Found && _createSent)
        {
            // A create of this node that seemed to fail made the group; a retry then finds it, and it is still ours.
            _createdOnEmpty |= IsEmptyHead(_sentHead);
        }

        if (outcome == Recreate.Made)
        {
            // Published before the instant is taken (the exchange fences it), so a subscriber that missed the group set it.
            _groupExists = true;
            _createSent = false;
            var since = Interlocked.Exchange(ref _unanchoredAt, 0);

            // At the head it skipped what was added since those subscribers joined, if anything was; from the last read it
            // replays it.
            lost |= since != 0 && id == StreamPosition.NewMessages && await SkippedSinceAsync(head, since).ConfigureAwait(false);
        }
        else if (outcome == Recreate.Found)
        {
            _groupExists = true;
            lost |= await FoundGroupSkippedAsync().ConfigureAwait(false);
        }

        if (lost)
        {
            ReportMessagesMissed();
        }

        return outcome == Recreate.Found;
    }

    /// <summary>For a group found rather than made: whether a create of this node made it at the head and skipped something.</summary>
    private Task<bool> FoundGroupSkippedAsync()
    {
        var since = Interlocked.Exchange(ref _unanchoredAt, 0);
        var sent = _createSent;
        _createSent = false;
        return since != 0 && sent ? SkippedSinceAsync(_sentHead, since) : Task.FromResult(false);
    }

    /// <summary>Whether a group made at <paramref name="head"/> skipped an entry this node would have acted on, added since <paramref name="since"/>.</summary>
    private async Task<bool> SkippedSinceAsync(StreamHead? head, long since)
    {
        if (!ConsumerGroups.AddedSince(head, since))
        {
            return false;
        }

        if (head is not { LastMs: not null } h)
        {
            return true;
        }

        try
        {
            var skipped = await _stream.RangeAsync($"{Math.Max(0, ConsumerGroups.SinceMs(h, since))}-0", h.LastId, SkippedEntriesChecked).ConfigureAwait(false);
            return skipped.Length >= SkippedEntriesChecked || Array.Exists(skipped, IsActedOn);
        }
        catch (Exception ex)
        {
            LogSkippedEntriesUnchecked(ex, _context.Topic);
            return true;
        }
    }

    /// <summary>What <see cref="ProcessEvent"/> dispatches: a valid event from another source.</summary>
    private bool IsActedOn(StreamEntry entry)
    {
        try
        {
            return !entry.IsNull
                && _formatter.Decode((ReadOnlyMemory<byte>)entry[_context.FieldName]) is { } ev
                && ev.IsValid()
                && !ev.SameSource(_context.SourceUri);
        }
        catch (Exception)
        {
            // Undecodable, so the read would skip it too.
            return false;
        }
    }

    private async Task<(Recreate Outcome, StreamHead? Head)> CreateGroupAsync(RedisValue id)
    {
        var atHead = id == StreamPosition.NewMessages;
        StreamHead? head = null;
        try
        {
            if (atHead)
            {
                head = await _stream.HeadAsync().ConfigureAwait(false);
            }

            var created = await _stream.CreateGroupAsync(id, head).ConfigureAwait(false);
            return (created ? Recreate.Made : Recreate.Failed, head);
        }
        catch (RedisServerException rex) when (rex.Message == StreamConstants.ConsumerGroupNameExistsErrorMessage)
        {
            LogConsumerGroupExists(_context.Topic, _context.ConsumerGroup);
            return (Recreate.Found, null);
        }
        catch (Exception ex2)
        {
            if (atHead)
            {
                // A timeout or a dropped connection may follow a create the server ran: the group found next may be it.
                _createSent = true;
                _sentHead = head;
            }

            LogRecreatingTopicException(ex2);
            return (Recreate.Failed, null);
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
        _createdOnEmpty = false;
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

        if (_streakGeneration != _recoveriesAtRead)
        {
            // The first failure since a recovery: the outage's streak does not pace it.
            _streakGeneration = _recoveriesAtRead;
            _consecutiveFailures = 0;
        }

        var failures = ++_consecutiveFailures;
        if (failures <= StreamConstants.ErrorBackoffThreshold)
        {
            // A ring, the poll or a recovery ends it; the base interval, since a read that fails says nothing of the doorbell.
            return _waiter.WaitAsync(_context.PollInterval, _cancelationToken);
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

    private void LogFetchFailed(Exception ex)
    {
        if (ex is not TimeoutException)
        {
            LogFetchLoopError(ex);
        }
        else if (++_timeouts is var timeouts && timeouts % StreamConstants.TimeoutsPerWarning == 1)
        {
            LogFetchTimedOut(ex, _context.Topic, timeouts);
        }
        else
        {
            LogFetchTimedOutAgain(ex, _context.Topic);
        }
    }

    private Task<StreamEntry[]> ReadAsync() => ReadAsync(StreamConstants.UndeliveredMessages);

    private Task<StreamEntry[]> ReadAsync(RedisValue position)
    {
        if (!_context.AutoAck || position != StreamConstants.UndeliveredMessages)
        {
            return _stream.ReadGroupAsync(position, noAck: false);
        }

        _autoAckReadInFlight = true;
        return _stream.ReadGroupAsync(position, noAck: true);
    }

    /// <summary>Checks for a gap and reads in one script, so a trim cannot land between the check and the read.</summary>
    /// <param name="full">Take the group's position from the server; otherwise check against the last one known.</param>
    private async Task<StreamEntry[]> CheckAndReadAsync(bool full)
    {
        _checkForGap = false;
        RedisResult reply;
        _autoAckReadInFlight = _context.AutoAck;
        try
        {
            reply = await _stream.CheckAndReadAsync(full).ConfigureAwait(false);
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
            _readSkipped = true;
            _autoAckReadInFlight = false;
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
                ReportGap(StreamIds.Gap(stream, group.LastDelivered, group.EntriesRead, ref _readCount, _createdOnEmpty, _context.MaxLength));
            }
        }
        else if (!_position.IsNull)
        {
            ReportGap(StreamIds.Gap(stream, _position, null, ref _readCount, _createdOnEmpty, _context.MaxLength));
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
            stream = await _stream.InfoAsync().ConfigureAwait(false);
            groups = await _stream.GroupInfoAsync().ConfigureAwait(false);
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
            ReportGap(StreamIds.Gap(stream, group, ref _readCount, _createdOnEmpty, _context.MaxLength));
        }
    }

    private async Task DispatchEventsAsync(StreamEntry[] events, bool acknowledge)
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
            if (_dispatchLost)
            {
                _dispatchLost = false;
                ReportMessagesMissed();
            }

            if (ids.Count > 0)
            {
                if (acknowledge)
                {
                    await _stream.AcknowledgeAsync([.. ids]).ConfigureAwait(false);
                }

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

        // A pending entry since deleted from the stream comes back without fields, and would stay pending for good.
        if (@event.Values is not { Length: > 0 })
        {
            ids.Add(@event.Id);
            return default;
        }

        try
        {
            T? ev;
            try
            {
                ev = _formatter.Decode((ReadOnlyMemory<byte>)@event[_context.FieldName]);
            }
            catch (Exception ex)
            {
                HandleUndecodableEvent(@event, ids, ex);
                return default;
            }

            if (ev is null)
            {
                HandleUndecodableEvent(@event, ids, null);
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
            ids.Add(@event.Id);
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
            if (_context.AutoAck)
            {
                // Read with NOACK: the entry is gone and will not be retried. Reported once for the batch.
                _dispatchLost = true;
            }
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

    /// <summary>Drops an entry no retry would decode: acknowledged, since a bad payload is not a lost invalidation.</summary>
    private void HandleUndecodableEvent(StreamEntry @event, List<RedisValue> ids, Exception? ex)
    {
        // Acknowledged first, so a throw below cannot leave the entry pending.
        ids.Add(@event.Id);
        _cachingTelemetryProvider.TryTrackEvent(EventUndecodable,
        [
            new(PropTopicKey, _context.Topic.ToString()),
            new(PropTransportId, @event.Id.ToString()),
        ]);
        if (++_undecodable is var count && count % StreamConstants.UndecodableEntriesPerWarning == 1)
        {
            LogEventUndecodable(ex, _context.Topic, @event.Id, count);
        }
        else
        {
            LogEventUndecodableAgain(ex, _context.Topic, @event.Id);
        }
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Topic {Topic}: the entries a new group skipped could not be read back, so they are reported as missed.")]
    private partial void LogSkippedEntriesUnchecked(Exception ex, RedisKey topic);

    [LoggerMessage(Level = LogLevel.Error, Message = "Fetch events loop error")]
    private partial void LogFetchLoopError(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading topic {Topic} timed out ({Timeouts} times so far); the entries of a read that timed out are read again from the pending list.")]
    private partial void LogFetchTimedOut(Exception ex, RedisKey topic, long timeouts);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Reading topic {Topic} timed out")]
    private partial void LogFetchTimedOutAgain(Exception ex, RedisKey topic);

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Entry {StreamId} of topic {Topic} could not be decoded and was dropped ({Count} so far).")]
    private partial void LogEventUndecodable(Exception? ex, RedisKey topic, RedisValue streamId, long count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Entry {StreamId} of topic {Topic} could not be decoded and was dropped")]
    private partial void LogEventUndecodableAgain(Exception? ex, RedisKey topic, RedisValue streamId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Topic {Topic}: {Count} pending entries were deleted from the stream before this consumer read them; their invalidations are lost.")]
    private partial void LogPendingEntriesDeleted(RedisKey topic, int count);

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

