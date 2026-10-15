namespace UiPath.Caching.Broadcast.Redis;

internal sealed partial class RedisStreamNotifyChannel : IDisposable
{
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(5);
    // StackExchange.Redis's default timeout, until the connection is up and can tell its own.
    private static readonly TimeSpan DefaultPeriod = TimeSpan.FromSeconds(5);

    private readonly RedisChannel _channel;
    private readonly IRedisConnector _redis;
    private readonly SignalingFetchWaiter _waiter;
    private readonly ILogger _logger;
    // _waiter ownership lives with the topic; this class only signals it and never disposes it.
    private readonly Action<RedisChannel, RedisValue> _handler;
    private readonly Timer _subscribeTimer;
    private readonly TimeSpan _timerDueTime;
    private readonly ManualResetEventSlim _subscribingDone = new(initialState: true);
    // The multiplexer's timeout, read once the connection is up, unless configured.
    private TimeSpan _timerPeriod;
    private bool _periodKnown;
    private Action? _unsubscribe;
    private int _subscribing;
    private int _resubscribeRequested;
    private int _failures;
    // The server refuses the subscription for good (ACL, command disabled): this topic polls only.
    private volatile bool _denied;
    private volatile bool _disposed;

    public RedisStreamNotifyChannel(
        RedisChannel channel,
        IRedisConnector redis,
        ILogger logger,
        SignalingFetchWaiter waiter,
        TimeSpan? subscriberTimeout,
        TimeSpan? subscriberDueTime,
        RedisValue self)
    {
        _channel = channel;
        _redis = redis;
        _logger = logger;
        _waiter = waiter;
        _redis.OnReconnected += OnReconnected;
        // Skip our own ring: this node never dispatches its own entries; the poll acknowledges them.
        _handler = (_, publisher) =>
        {
            if (publisher != self)
            {
                _waiter.Signal();
            }
        };
        _periodKnown = subscriberTimeout > TimeSpan.Zero;
        _timerPeriod = _periodKnown ? subscriberTimeout!.Value : DefaultPeriod;
        _timerDueTime = subscriberDueTime ?? _timerPeriod.Multiply(0.5);
        _subscribeTimer = new Timer(Subscribe, null, _timerDueTime, _timerPeriod);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _redis.OnReconnected -= OnReconnected;
        _subscribeTimer.Dispose();
        _subscribingDone.Wait(DisposeDrainTimeout);
        Unsubscribe();
        _subscribingDone.Dispose();
    }

    private void Subscribe(object? state)
    {
        if (_disposed || _denied)
        {
            return;
        }
        if (Interlocked.CompareExchange(ref _subscribing, 1, 0) != 0)
        {
            // The attempt already in flight re-arms the timer for whoever asked, so this one can drop out.
            return;
        }
        Volatile.Write(ref _resubscribeRequested, 0);
        try
        {
            _subscribingDone.Reset();
        }
        catch (ObjectDisposedException)
        {
            // Dispose ran between the _disposed check and here; release the interlock and bail.
            Interlocked.Exchange(ref _subscribing, 0);
            return;
        }
        _ = SubscribeAsync();
    }

    // Off the timer's pool thread: a slow server or a cold connection holds no thread for the subscribe's round trip.
    private async Task SubscribeAsync()
    {
        try
        {
            Unsubscribe();
            // Awaited first: asked for before the connection is up, the subscriber would block this thread until it is.
            await _redis.ConnectAsync().ConfigureAwait(false);
            var subscriber = _redis.Subscriber;
            if (!_periodKnown)
            {
                _periodKnown = true;
                _timerPeriod = TimeSpan.FromMilliseconds(subscriber.Multiplexer.TimeoutMilliseconds);
            }

            await subscriber.SubscribeAsync(_channel, _handler).ConfigureAwait(false);
            _unsubscribe = () => subscriber.Unsubscribe(_channel, _handler, CommandFlags.FireAndForget);
            if (_disposed)
            {
                Unsubscribe();
                return;
            }
            // Idle until something asks for another attempt. A reconnect that raced this one is
            // honored below, once the interlock is free and a fresh callback can get through.
            Reschedule(Timeout.InfiniteTimeSpan);
            _failures = 0;
            LogSubscribed(_channel);
            _waiter.Signal();
        }
        catch (Exception ex) when (RedisUtils.IsDenied(ex) || RedisUtils.IsUnsupportedCommand(ex))
        {
            _denied = true;
            Reschedule(Timeout.InfiniteTimeSpan);
            LogSubscribeDenied(ex, _channel);
        }
        catch (Exception ex)
        {
            // The first failure of a streak is worth a warning; the retries, one per timer period, are not.
            LogSubscribeError(++_failures == 1 ? LogLevel.Warning : LogLevel.Debug, ex, _channel);
            Reschedule(_timerPeriod);
        }
        finally
        {
            Interlocked.Exchange(ref _subscribing, 0);
            // Only now can a scheduled callback get past the contention check, so a reconnect that
            // arrived during this attempt — or one whose own callback bailed on it — is armed here.
            if (Volatile.Read(ref _resubscribeRequested) != 0 && !_denied)
            {
                Reschedule(_timerDueTime);
            }
            try
            {
                _subscribingDone.Set();
            }
            catch (ObjectDisposedException)
            {
                // Dispose timed out waiting for this callback and disposed the gate; nothing to signal.
            }
        }
    }

    private void Unsubscribe()
    {
        try
        {
            // Taken and cleared in one step: a subscription landing between the two would stay registered.
            Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
        }
        catch (Exception ex)
        {
            LogUnsubscribeError(ex, _channel);
        }
    }

    private void Reschedule(TimeSpan dueTime)
    {
        try
        {
            _subscribeTimer.Change(dueTime, _timerPeriod);
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently; nothing left to reschedule.
        }
    }

    private void OnReconnected(object? sender, EventArgs e)
    {
        if (_disposed || _denied)
        {
            return;
        }
        Volatile.Write(ref _resubscribeRequested, 1);
        Reschedule(_timerDueTime);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Stream notify subscribed: {Channel}")]
    private partial void LogSubscribed(RedisChannel channel);

    [LoggerMessage(Message = "Stream notify subscribe error: {Channel}; retrying")]
    private partial void LogSubscribeError(LogLevel level, Exception ex, RedisChannel channel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stream notify subscription refused by the server for {Channel}; this topic polls only. Grant the connection user SUBSCRIBE and the channel (ACL &pattern), or set NotifyEnabled: false.")]
    private partial void LogSubscribeDenied(Exception ex, RedisChannel channel);

    [LoggerMessage(Level = LogLevel.Error, Message = "Stream notify unsubscribe error: {Channel}")]
    private partial void LogUnsubscribeError(Exception ex, RedisChannel channel);
}
