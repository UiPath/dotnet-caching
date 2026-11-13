using System.Net;
using System.Threading.Channels;
using UiPath.Caching.Serialization;

namespace UiPath.Caching.Broadcast.Redis;

internal sealed partial class RedisPubSubSubjectWriter<T> : IDisposable
    where T : IEvent
{
    private readonly Uri _sourceUri;
    private readonly RedisChannel _redisChannel;
    private readonly IRedisConnector _redis;
    private readonly ChannelWriter<T> _channelWriter;
    private readonly IEventFormatterProxy<T> _formatter;
    private readonly ILogger _logger;
    private readonly Action<RedisChannel, RedisValue> _handler;
    private readonly TimeSpan _timerPeriod;
    private readonly TimeSpan _timerDueTime;
    private readonly Timer _subscribeTimer;
    private readonly Action _onSubscriptionGap;
    private readonly Action _onDropped;
    private readonly Lock _gate = new();
    // Null when the formatter cannot tell a payload's source without decoding it.
    private readonly IEventSourceMatcher? _ownSource;

    // Under _gate: the subscription connections down now.
    private readonly HashSet<EndPoint?> _subscriptionsDown = [];
    private bool _disposed;
    private Action? _unsubscribe;
    private int _subscribing;
    private long _undecodable;

    // Under _gate: each reconnect starts a generation, and only a subscription made in the current one stops the timer.
    private int _generation;
    private int _activeGeneration = -1;
    private bool _gap;

    // Under _gate: whether a source that names no connection reported a failure.
    private bool _unnamedFailure;

    // Under _gate: the connector's multiplexer as of the last reconnect; events from any other one no longer count.
    private IConnectionMultiplexer? _current;
    private int _activations;

    public RedisPubSubSubjectWriter(
        Uri sourceUri,
        RedisChannel redisChannel,
        IRedisConnector redis,
        ChannelWriter<T> channelWriter,
        IEventFormatterProxy<T> formatter,
        RedisPubSubTopicOptions options,
        ILogger logger,
        Action onSubscriptionGap,
        Action onDropped)
    {
        _onSubscriptionGap = onSubscriptionGap;
        _onDropped = onDropped;
        _redis = redis;
        _channelWriter = channelWriter;
        _formatter = formatter;
        _logger = logger;
        _sourceUri = sourceUri;
        _redisChannel = redisChannel;
        _ownSource = (formatter as IEventSourceMatcherProvider)?.CreateSourceMatcher(sourceUri);
        _redis.OnReconnected += OnReconnected;
        _redis.OnConnectionFailed += OnConnectionFailed;
        _redis.OnConnectionRestored += OnConnectionRestored;
        _handler = (_, value) => OnMessage(value);
        _timerPeriod = options.SubscriberTimeout > TimeSpan.Zero ? options.SubscriberTimeout.Value : TimeSpan.FromMilliseconds(_redis.Subscriber.Multiplexer.TimeoutMilliseconds);
        _timerDueTime = options.SubscriberDueTime == null ? _timerPeriod.Multiply(0.5) : options.SubscriberDueTime.Value;
        _subscribeTimer = new Timer(Subscribe, null, _timerDueTime, _timerPeriod);
    }

    public void Dispose()
    {
        Action? unsubscribe;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // A subscribe still in flight finds the writer disposed once it completes, and undoes itself.
            _disposed = true;
            unsubscribe = _unsubscribe;
            _unsubscribe = null;
        }

        _redis.OnReconnected -= OnReconnected;
        _redis.OnConnectionFailed -= OnConnectionFailed;
        _redis.OnConnectionRestored -= OnConnectionRestored;
        _subscribeTimer.Dispose();
        Unsubscribe(unsubscribe);
    }

    /// <summary>The topic is gaining an observer, whose entry a publication before the subscription is in place could leave stale.</summary>
    /// <returns>A mark for <see cref="ActivatedSince"/>.</returns>
    internal int MarkSubscribed()
    {
        lock (_gate)
        {
            _gap |= _activeGeneration != _generation;
            return _activations;
        }
    }

    /// <summary>Whether a subscription went in place since <paramref name="mark"/>, expiring the observers already there.</summary>
    internal bool ActivatedSince(int mark)
    {
        lock (_gate)
        {
            return _activations != mark;
        }
    }

    /// <summary>A body that starts with a format id is one this node should have read, so what it carried is a lost invalidation; any other that does not decode is junk.</summary>
    private static bool IsKnownFormat(ReadOnlyMemory<byte> payload) => ValueHeader.IsKnownFormat(payload.Span);

    private void Subscribe(object? state)
    {
        if (Interlocked.CompareExchange(ref _subscribing, 1, 0) != 0)
        {
            return;
        }

        int generation;
        Action? previous;
        lock (_gate)
        {
            // A tick already queued when the timer stopped finds the writer disposed, or this generation in place, and leaves it.
            if (_disposed || _activeGeneration == _generation)
            {
                Interlocked.Exchange(ref _subscribing, 0);
                return;
            }

            generation = _generation;
            previous = _unsubscribe;
            _unsubscribe = null;
        }

        LogSubscribeChannel(_redisChannel);
        // A subscriber left holding this writer's handler with nothing to undo it; unregistered before another attempt can start.
        ISubscriber? unowned = null;
        try
        {
            previous?.Invoke();
            var subscriber = _redis.Subscriber;
            // Set first: the multiplexer adds the handler before it sends the command, and keeps it, resubscribing on reconnect, if the command fails.
            unowned = subscriber;
            subscriber.Subscribe(_redisChannel, _handler);
            bool gap;
            lock (_gate)
            {
                // Dispose ran during the subscribe and had nothing to undo yet.
                if (_disposed)
                {
                    return;
                }

                unowned = null;
                _unsubscribe = () => subscriber.Unsubscribe(_redisChannel, _handler, CommandFlags.FireAndForget);
                // A reconnect during this attempt left the timer armed, so it subscribes again on the new connection.
                if (generation != _generation)
                {
                    return;
                }

                _subscribeTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _activeGeneration = generation;
                _activations++;
                gap = _gap;
                _gap = false;
            }

            // Entries cached before this subscription was in place may have missed what was published meanwhile.
            if (gap)
            {
                _onSubscriptionGap();
            }
        }
        catch (Exception ex)
        {
            LogSubscribeError(ex, _redisChannel);
        }
        finally
        {
            if (unowned is not null)
            {
                Unregister(unowned);
            }

            Interlocked.Exchange(ref _subscribing, 0);
        }
    }

    private void Unregister(ISubscriber subscriber)
    {
        try
        {
            subscriber.Unsubscribe(_redisChannel, _handler, CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            LogUnsubscribeError(ex, _redisChannel);
        }
    }

    private void OnReconnected(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            // Checked under the gate, so the timer is never changed after Dispose stopped it.
            if (_disposed)
            {
                return;
            }

            // The retired connection will not report its restores; the resubscribe this arms covers what it dropped.
            _subscriptionsDown.Clear();
            _unnamedFailure = false;
            // Swapped in before this event, so a subscribe already on it is not mistaken for the retired one; read under the lock, so a later reconnect's wins.
            _current = CurrentMultiplexer();
            _generation++;
            _gap = true;
            _subscribeTimer.Change(_timerDueTime, _timerPeriod);
        }
    }

    // Null, which counts every sender as current, when the connector cannot say: the resubscribe must still be armed.
    private IConnectionMultiplexer? CurrentMultiplexer()
    {
        try
        {
            return _redis.Subscriber.Multiplexer;
        }
        catch (Exception ex)
        {
            LogSubscribeError(ex, _redisChannel);
            return null;
        }
    }

    private void OnMessage(RedisValue value)
    {
        if (!_disposed)
        {
            try
            {
                var payload = (ReadOnlyMemory<byte>)value;
                if (IsOwnUndecoded(payload))
                {
                    return;
                }

                T? ev;
                try
                {
                    ev = _formatter.Decode(payload);
                }
                catch (Exception ex) when (IsKnownFormat(payload))
                {
                    LogUndecodable(ex);
                    _onDropped();
                    return;
                }

                if (ev is null)
                {
                    if (IsKnownFormat(payload))
                    {
                        LogUndecodable(null);
                        _onDropped();
                    }

                    return;
                }

                LogEventReceived(ev.Id, _redisChannel);
                if (ev.IsValid())
                {
                    if (ev.SameSource(_sourceUri))
                    {
                        LogEventFromCurrentSource(ev.Id, _redisChannel);
                    }
                    else
                    {
                        Write(ev);
                    }
                }
                else
                {
                    LogEventInvalid(ev.Id, _redisChannel);
                    if (IsKnownFormat(payload))
                    {
                        _onDropped();
                    }
                }
            }
            catch (Exception ex)
            {
                LogOnMessageError(ex, _redisChannel);
            }
        }
    }

    private void LogUndecodable(Exception? ex)
    {
        if (Interlocked.Increment(ref _undecodable) is var count && count % StreamConstants.UndecodableEntriesPerWarning == 1)
        {
            LogEventUndecodableWarning(ex, _redisChannel, count);
        }
        else
        {
            LogEventUndecodableAgain(ex, _redisChannel);
        }
    }

    /// <summary>
    /// Whether the payload is this node's and may be dropped unread: only while its two logs, which name the event id, are off.
    /// </summary>
    private bool IsOwnUndecoded(ReadOnlyMemory<byte> payload) =>
        _ownSource is { } own
        && !_logger.IsEnabled(LogLevel.Debug)
        && !_logger.IsEnabled(LogLevel.Trace)
        && own.IsFromSource(payload);

    private void Write(T ev)
    {
        // A full channel in Wait mode refuses the write; the event, and the invalidation it carried, is gone.
        if (!_channelWriter.TryWrite(ev))
        {
            _onDropped();
        }
    }

    private void OnConnectionFailed(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (IsRetired(sender))
            {
                return;
            }

            // Only the subscription connection carries publications; a failed command connection loses none of them.
            if (e is ConnectionFailedEventArgs failed)
            {
                if (failed.ConnectionType == ConnectionType.Subscription)
                {
                    _subscriptionsDown.Add(failed.EndPoint);
                }
            }
            else
            {
                _unnamedFailure = true;
            }
        }
    }

    private void OnConnectionRestored(object? sender, EventArgs e)
    {
        bool lost = false;
        lock (_gate)
        {
            if (IsRetired(sender))
            {
                return;
            }

            if (e is ConnectionFailedEventArgs restored)
            {
                // Expired once the last subscription connection that went down is back, not on an unrelated restore.
                if (restored.ConnectionType == ConnectionType.Subscription
                    && (_subscriptionsDown.Remove(restored.EndPoint) || _unnamedFailure)
                    && _subscriptionsDown.Count == 0)
                {
                    lost = true;
                    _unnamedFailure = false;
                }
            }
            else if (_unnamedFailure || _subscriptionsDown.Count > 0)
            {
                lost = true;
                _unnamedFailure = false;
                _subscriptionsDown.Clear();
            }
        }

        // Publications while the connection was down never arrived. Expired off the connection's own event thread.
        if (lost && !_disposed)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static self => self._onSubscriptionGap(), this, preferLocal: false);
        }
    }

    private bool IsRetired(object? sender) => _current is not null && sender is IConnectionMultiplexer multiplexer && !ReferenceEquals(multiplexer, _current);

    private void Unsubscribe(Action? unsubscribe)
    {
        LogUnsubscribeChannel(_redisChannel);
        try
        {
            unsubscribe?.Invoke();
            _channelWriter.TryComplete();
        }
        catch (Exception ex)
        {
            LogUnsubscribeError(ex, _redisChannel);
        }
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "Subscribe channel: {RedisChannel}")]
    private partial void LogSubscribeChannel(RedisChannel redisChannel);

    [LoggerMessage(Level = LogLevel.Error, Message = "Subscribe error. Channel: {RedisChannel}")]
    private partial void LogSubscribeError(Exception ex, RedisChannel redisChannel);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Event received. Id {EventId}  Channel : {RedisChannel}")]
    private partial void LogEventReceived(string? eventId, RedisChannel redisChannel);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Event from current source. Id {EventId}  Channel : {RedisChannel}")]
    private partial void LogEventFromCurrentSource(string? eventId, RedisChannel redisChannel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event invalid. Id {EventId}  Channel : {RedisChannel}")]
    private partial void LogEventInvalid(string? eventId, RedisChannel redisChannel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A message on channel {RedisChannel} could not be decoded and was dropped ({Count} so far).")]
    private partial void LogEventUndecodableWarning(Exception? ex, RedisChannel redisChannel, long count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "A message on channel {RedisChannel} could not be decoded and was dropped")]
    private partial void LogEventUndecodableAgain(Exception? ex, RedisChannel redisChannel);

    [LoggerMessage(Level = LogLevel.Error, Message = "OnMessage error. Channel : {RedisChannel}")]
    private partial void LogOnMessageError(Exception ex, RedisChannel redisChannel);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Unsubscribe channel: {RedisChannel}")]
    private partial void LogUnsubscribeChannel(RedisChannel redisChannel);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unsubscribe error. Channel : {RedisChannel}")]
    private partial void LogUnsubscribeError(Exception ex, RedisChannel redisChannel);
}
