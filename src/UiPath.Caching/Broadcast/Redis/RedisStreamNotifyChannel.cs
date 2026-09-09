namespace UiPath.Caching.Broadcast.Redis;

internal sealed partial class RedisStreamNotifyChannel : IDisposable
{
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly RedisChannel _channel;
    private readonly IRedisConnector _redis;
    private readonly SignalingFetchWaiter _waiter;
    private readonly ILogger _logger;
    // _waiter ownership lives with the topic; this class only signals it and never disposes it.
    private readonly Action<RedisChannel, RedisValue> _handler;
    private readonly Timer _subscribeTimer;
    private readonly TimeSpan _timerPeriod;
    private readonly TimeSpan _timerDueTime;
    private readonly ManualResetEventSlim _subscribingDone = new(initialState: true);
    private Action? _unsubscribe;
    private int _subscribing;
    private int _resubscribeRequested;
    private volatile bool _disposed;

    public RedisStreamNotifyChannel(
        RedisChannel channel,
        IRedisConnector redis,
        ILogger logger,
        SignalingFetchWaiter waiter,
        TimeSpan? subscriberTimeout,
        TimeSpan? subscriberDueTime)
    {
        _channel = channel;
        _redis = redis;
        _logger = logger;
        _waiter = waiter;
        _redis.OnReconnected += OnReconnected;
        _handler = (_, _) => _waiter.Signal();
        _timerPeriod = subscriberTimeout > TimeSpan.Zero
            ? subscriberTimeout.Value
            : TimeSpan.FromMilliseconds(_redis.Subscriber.Multiplexer.TimeoutMilliseconds);
        _timerDueTime = subscriberDueTime ?? _timerPeriod.Multiply(0.5);
        _subscribeTimer = new Timer(Subscribe, null, _timerDueTime, _timerPeriod);
    }

    private void Subscribe(object? state)
    {
        if (_disposed)
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
        try
        {
            try
            {
                _unsubscribe?.Invoke();
            }
            catch (Exception ex)
            {
                LogUnsubscribeError(ex, _channel);
            }
            _unsubscribe = null;
            var subscriber = _redis.Subscriber;
            subscriber.Subscribe(_channel, _handler);
            _unsubscribe = () => subscriber.Unsubscribe(_channel, _handler, CommandFlags.FireAndForget);
            if (_disposed)
            {
                try
                {
                    _unsubscribe();
                }
                catch (Exception ex)
                {
                    LogUnsubscribeError(ex, _channel);
                }
                _unsubscribe = null;
                return;
            }
            try
            {
                // Idle until something asks for another attempt. A reconnect that raced this one is
                // honored below, once the interlock is free and a fresh callback can get through.
                _subscribeTimer.Change(Timeout.InfiniteTimeSpan, _timerPeriod);
            }
            catch (ObjectDisposedException)
            {
                // Disposed concurrently with the successful subscribe; _unsubscribe will fire from Dispose.
            }
            LogSubscribed(_channel);
            _waiter.Signal();
        }
        catch (Exception ex)
        {
            LogSubscribeError(ex, _channel);
        }
        finally
        {
            Interlocked.Exchange(ref _subscribing, 0);
            // Only now can a scheduled callback get past the contention check, so a reconnect that
            // arrived during this attempt — or one whose own callback bailed on it — is armed here.
            if (Volatile.Read(ref _resubscribeRequested) != 0)
            {
                try
                {
                    _subscribeTimer.Change(_timerDueTime, _timerPeriod);
                }
                catch (ObjectDisposedException)
                {
                    // Disposed concurrently; nothing left to reschedule.
                }
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

    private void OnReconnected(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }
        Volatile.Write(ref _resubscribeRequested, 1);
        try
        {
            _subscribeTimer.Change(_timerDueTime, _timerPeriod);
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently with reconnect notification — nothing to reschedule.
        }
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
        try
        {
            _unsubscribe?.Invoke();
        }
        catch (Exception ex)
        {
            LogUnsubscribeError(ex, _channel);
        }
        _subscribingDone.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Stream notify subscribed: {Channel}")]
    private partial void LogSubscribed(RedisChannel channel);

    [LoggerMessage(Level = LogLevel.Error, Message = "Stream notify subscribe error: {Channel}")]
    private partial void LogSubscribeError(Exception ex, RedisChannel channel);

    [LoggerMessage(Level = LogLevel.Error, Message = "Stream notify unsubscribe error: {Channel}")]
    private partial void LogUnsubscribeError(Exception ex, RedisChannel channel);
}
