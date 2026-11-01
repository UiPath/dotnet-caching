using System.Diagnostics;
using UiPath.Caching.Policies;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Broadcast.Redis;

public sealed partial class RedisStreamsTopic<T> : ITopic<T>
     where T : IEvent
{
    private const int DoorbellIdle = 0;
    private const int DoorbellSending = 1;
    private const int DoorbellRingAgain = 2;
    private const int MaxDoorbellRetries = 2;
    // Where the doorbell goes relative to the XADD; decided from the topology once it is known, and again after a reconnect.
    private const int RingUndecided = 0;
    private const int RingAfterReply = 1;
    private const int RingBehindAdd = 2;

    private readonly IRedisStreamKeyStrategy _redisStreamKeyStrategy;
    private readonly RedisStreamSubjectWriter<T> _subscriber;
    private readonly CancellationTokenSource _stopTokenSource;
    private readonly RedisStreamContext _context;
    private readonly IEventSubject<T> _subject;
    private readonly IConnectionState _connectionState;
    private readonly IRedisConnector _redis;
    private readonly IEventFormatterProxy<T> _formatter;
    private readonly IResiliencePipeline _write;
    private readonly ILogger _logger;
    private readonly ICachingTelemetryProvider _cachingTelemetryProvider;
    private readonly RedisStreamsTopicOptions _streamOptions;
    private readonly EventDispatcher<T> _dispatcher;
    private readonly RedisStreamNotifyChannel? _notifyChannel;
    private readonly RedisChannel? _notifyRedisChannel;
    private readonly SignalingFetchWaiter _waiter;
    // Half of MaxLength; 0 = no trim.
    private readonly long _wakeEvery;
    private long _published;
    private int _doorbell;
    private int _doorbellFailures;
    private long _lastDoorbell;
    private int _ringMode;
    private bool _disposed;

    public RedisStreamsTopic(
        TopicKey topicKey,
        IConnectionState connectionState,
        IRedisConnector redis,
        Func<IEventSubject<T>> subjectFactory,
        IEventFormatterProxy<T> formatter,
        IResiliencePipelineProvider resiliencePipelineProvider,
        RedisStreamsTopicOptions streamOptions,
        CacheOptions cacheOptions,
        ILogger<RedisStreamsTopic<T>> logger,
        ICachingTelemetryProvider cachingTelemetryProvider,
        IRedisProfiler redisProfiler,
        CancellationToken stopToken)
    {
        RedisStreamsTopicOptionsValidator.ThrowIfInvalid(streamOptions);
        TopicKey = topicKey;
        _connectionState = connectionState;
        _formatter = formatter;
        _write = resiliencePipelineProvider.Get(ResiliencePipelineNames.Write);
        _stopTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        _redis = redis;
        _logger = logger;
        _cachingTelemetryProvider = cachingTelemetryProvider;
        _streamOptions = streamOptions;
        _subject = subjectFactory();
        _redisStreamKeyStrategy = streamOptions.RedisStreamKeyStrategy ?? new PrefixStrategy(RedisKeyspaces.Streams, cacheOptions);
        _context = GetContext(topicKey, cacheOptions, streamOptions);
        _waiter = new SignalingFetchWaiter(streamOptions.PollInterval, streamOptions.EffectiveNotifiedPollInterval);
        _wakeEvery = streamOptions.MaxLength is { } maxLength ? Math.Max(1, maxLength / 2) : 0;
        DoorbellSpacing = streamOptions.EffectiveNotifyMinInterval;
        if (_streamOptions.NotifyEnabled)
        {
            var notifyChannelStrategy = streamOptions.NotifyChannelStrategy
                ?? StreamSuffixChannel.Create(_redisStreamKeyStrategy, cacheOptions, streamOptions.NotifyChannelName, streamOptions.NotifyShardedPubSub);
            _notifyRedisChannel = notifyChannelStrategy.GetRedisChannel(topicKey);
            _notifyChannel = new RedisStreamNotifyChannel(
                _notifyRedisChannel.Value,
                _redis,
                _logger,
                _waiter,
                streamOptions.NotifySubscriberTimeout,
                streamOptions.NotifySubscriberDueTime,
                _context.ConsumerName);
            // A new connection may be to another topology.
            _redis.OnReconnected += OnReconnected;
        }
        var capacity = ChannelHelper.CalculateBoundedCapacity(streamOptions.ConsumerCapacity, streamOptions.PollBatchSize);
        // Run later on the pool, by which time the writer the drop reports through is in place.
        var dropped = new DroppedEvents(topicKey, () => _subscriber!.ReportLoss(), _logger);
        var channel = ChannelHelper.Create<T>(streamOptions.ConsumerCapacity < 0, capacity, streamOptions.FullMode, _ => dropped.Dropped());
        _subscriber = new RedisStreamSubjectWriter<T>(_context, _connectionState, _redis, channel, _formatter, _logger, _cachingTelemetryProvider, redisProfiler, _waiter, () => _subject.Invalidate(MissedEventsReason.Lost), _stopTokenSource.Token);
        _dispatcher = new EventDispatcher<T>(topicKey, channel, _subject, _logger, _stopTokenSource.Token);
    }

    public TopicKey TopicKey { get; }

    public EventHandler? OnDisposed { get; set; }

    // A doorbell is a hint: one stalled on Redis must not hold the rings behind it for the whole command timeout.
    internal TimeSpan DoorbellTimeout { get; set; } = TimeSpan.FromSeconds(1);

    internal TimeSpan DoorbellSpacing { get; set; }

    /// <summary>Whether this topic's doorbell subscription is live.</summary>
    internal bool NotifyLive => _waiter.NotifyLive;

    public IDisposable Subscribe(IObserver<T> observer)
    {
        this.ThrowIfDisposed(_disposed);

        // Marked first, so a loss reported next expires this observer too; one reported while it joined may have expired
        // the others before it, so it is told itself.
        var mark = _subscriber.MarkSubscribed();
        var subscription = _subject.Subscribe(observer);
        if (_subscriber.LostSince(mark) && observer is IMissedEventsObserver missed)
        {
            TellMissed(missed, MissedEventsReason.Lost);
        }

        return subscription;
    }

    public async ValueTask<bool> PublishAsync(T @event, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();

        if (!_connectionState.IsConnected)
        {
            return false;
        }

        try
        {
            RedisValue message = _formatter.Encode(@event);
            var behindAdd = _notifyRedisChannel.HasValue && RingsBehindAdd();
            var id = await _write.ExecuteAsync(static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                var added = s.Self._redis.Database.StreamAddAsync(
                    s.Self._context.Topic,
                    s.Self._context.FieldName,
                    s.Message,
                    maxLength: s.Self._streamOptions.MaxLength,
                    useApproximateMaxLength: true,
                    flags: CommandFlags.DemandMaster);
                if (s.BehindAdd)
                {
                    // Enqueued behind the XADD rather than after its reply: the one connection to a single primary runs them in
                    // order, so a consumer the doorbell wakes finds the entry. Each attempt rings for itself, so a retried XADD is
                    // followed by its own doorbell.
                    s.Self.RingDoorbell();
                }

                return added.AsValueTask();
            },
            (Self: this, Message: message, BehindAdd: behindAdd),
            defaultValue: RedisValue.Null,
            token).ConfigureAwait(false);
            _cachingTelemetryProvider.TrackTopicWriteMetric(_context.Topic.ToString(), id);
            if (!id.IsNull)
            {
                Ring(doorbell: !behindAdd);
            }

            LogPublished(TopicKey, @event.Id, id);
            return !id.IsNull;
        }
        catch (Exception ex)
        {
            LogPublishError(ex, TopicKey, @event.Id);
            return false;
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
        _stopTokenSource?.Cancel();
        _stopTokenSource?.Dispose();
        _dispatcher.Dispose();
        _subject.Dispose();
        _subscriber.Dispose();
        _notifyChannel?.Dispose();
        _waiter.Dispose();
        OnDisposed?.Invoke(this, EventArgs.Empty);
    }

    internal RedisStreamsTopicOptions GetResolvedOptionsForTests() => _streamOptions;

    /// <summary>Wakes the other nodes unless the doorbell went out behind the XADD, and this one every <see cref="_wakeEvery"/> own entries: nothing else rings for those, and a burst of them could be trimmed unread.</summary>
    private void Ring(bool doorbell)
    {
        if (doorbell && _notifyRedisChannel.HasValue)
        {
            RingDoorbell();
        }

        if (_wakeEvery > 0 && Interlocked.Increment(ref _published) % _wakeEvery == 0)
        {
            _waiter.Signal();
        }
    }

    /// <summary>Whether the doorbell may go out behind the XADD rather than after its reply: true with a standalone primary, whose one connection carries both in order. A cluster or a proxy may route the stream and the channel to different nodes, and an unknown topology is treated the same way.</summary>
    private bool RingsBehindAdd()
    {
        var mode = Volatile.Read(ref _ringMode);
        if (mode == RingUndecided)
        {
            mode = DecideRingMode();
        }

        return mode == RingBehindAdd;
    }

    // Undecided while the connector has no primary to show, so a later publish asks again.
    private int DecideRingMode()
    {
        var mode = RingUndecided;
        try
        {
            foreach (var primary in _redis.GetPrimaries())
            {
                if (primary.ServerType != ServerType.Standalone)
                {
                    mode = RingAfterReply;
                    break;
                }

                mode = RingBehindAdd;
            }
        }
        catch (Exception ex)
        {
            LogTopologyUnknown(ex, TopicKey);
            mode = RingAfterReply;
        }

        if (mode != RingUndecided)
        {
            Volatile.Write(ref _ringMode, mode);
        }

        return mode;
    }

    private void OnReconnected(object? sender, EventArgs e) => Volatile.Write(ref _ringMode, RingUndecided);

    /// <summary>One doorbell in flight; the rings meanwhile coalesce into one more.</summary>
    // A doorbell per event in a burst would flood the subscribers' output buffers.
    private void RingDoorbell()
    {
        while (true)
        {
            var state = Volatile.Read(ref _doorbell);
            if (state == DoorbellIdle)
            {
                if (Interlocked.CompareExchange(ref _doorbell, DoorbellSending, DoorbellIdle) == DoorbellIdle)
                {
                    _ = SendDoorbellsAsync();
                    return;
                }
            }
            else if (state == DoorbellSending)
            {
                if (Interlocked.CompareExchange(ref _doorbell, DoorbellRingAgain, DoorbellSending) == DoorbellSending)
                {
                    return;
                }
            }
            else
            {
                return;
            }
        }
    }

    private async Task SendDoorbellsAsync()
    {
        // Taken once: Dispose cancels and then disposes the source, which a later read of Token would throw on.
        CancellationToken stop;
        try
        {
            stop = _stopTokenSource.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        // Rings folded into a doorbell that failed are retried a few times, so the consumers they were for are still woken.
        var owed = false;
        var retries = 0;
        do
        {
            if (stop.IsCancellationRequested)
            {
                return;
            }

            bool sent;
            try
            {
                if (await WaitDoorbellSpacingAsync(stop).ConfigureAwait(false))
                {
                    owed = true;
                    retries = 0;
                }

                sent = await PublishDoorbellAsync(stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }

            if (!RingAgain(sent, ref owed, ref retries))
            {
                return;
            }
        }
        while (true);
    }

    // False once no ring is left to answer; otherwise the state is Sending again for the next doorbell.
    private bool RingAgain(bool sent, ref bool owed, ref int retries)
    {
        if (sent)
        {
            owed = false;
            retries = 0;
        }

        // A ring that lands after the budget is spent is a new one and gets its own retries.
        var retried = !sent && owed && retries < MaxDoorbellRetries
            && Interlocked.CompareExchange(ref _doorbell, DoorbellRingAgain, DoorbellSending) == DoorbellSending;
        if (retried)
        {
            retries++;
        }

        if (Interlocked.CompareExchange(ref _doorbell, DoorbellIdle, DoorbellSending) == DoorbellSending
            || Interlocked.Exchange(ref _doorbell, DoorbellSending) != DoorbellRingAgain)
        {
            return false;
        }

        if (!retried)
        {
            owed = true;
            retries = 0;
        }

        return true;
    }

    // True when rings came during the wait: the doorbell about to go out answers them.
    private async Task<bool> WaitDoorbellSpacingAsync(CancellationToken stop)
    {
        // Rings that arrive while this wait runs fold into the doorbell it lets through.
        var wait = DoorbellSpacing - Stopwatch.GetElapsedTime(Volatile.Read(ref _lastDoorbell));
        if (wait <= TimeSpan.Zero)
        {
            return false;
        }

        await Task.Delay(wait, stop).ConfigureAwait(false);
        return Interlocked.Exchange(ref _doorbell, DoorbellSending) == DoorbellRingAgain;
    }

    private async Task<bool> PublishDoorbellAsync(CancellationToken stop)
    {
        Task<long>? publish = null;
        try
        {
            Volatile.Write(ref _lastDoorbell, Stopwatch.GetTimestamp());
            // On the primary, like the XADD: a doorbell behind it on the same connection cannot overtake the entry.
            publish = _redis.Database.PublishAsync(_notifyRedisChannel!.Value, _context.ConsumerName, CommandFlags.DemandMaster);
            await publish.WaitAsync(DoorbellTimeout, stop).ConfigureAwait(false);
            Volatile.Write(ref _doorbellFailures, 0);
            return true;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            publish?.Forget();
            throw;
        }
        catch (TimeoutException ex)
        {
            publish?.Forget();
            DoorbellFailed(ex);
            return false;
        }
        catch (Exception ex)
        {
            DoorbellFailed(ex);
            return false;
        }
    }

    // Once per streak at warning: a stalled Redis would otherwise log every second for every topic.
    private void DoorbellFailed(Exception ex)
    {
        var failures = Interlocked.Increment(ref _doorbellFailures);
        if (failures == 1)
        {
            LogNotifyPublishError(ex, TopicKey);
        }
        else
        {
            LogNotifyPublishErrorAgain(ex, TopicKey, failures);
        }
    }

    /// <summary>Isolated like the subject's own notifications, so a throwing observer cannot fail the subscribe that registered it.</summary>
    private void TellMissed(IMissedEventsObserver missed, MissedEventsReason reason)
    {
        try
        {
            missed.OnEventsMissed(reason);
        }
        catch (Exception ex)
        {
            LogObserverMissedEventsFailed(ex, TopicKey);
        }
    }

    private RedisStreamContext GetContext(
        TopicKey topicKey,
        CacheOptions cacheOptions,
        RedisStreamsTopicOptions options)
    {
        var sourceUri = cacheOptions.SourceUri ?? CacheOptions.MachineUri;
        var sourceUriAsString = sourceUri.ToString().ToLowerInvariant();
        return new RedisStreamContext(
            Topic: _redisStreamKeyStrategy.GetRedisKey(topicKey),
            FieldName: options.FieldName,
            ConsumerName: sourceUriAsString,
            ConsumerGroup: sourceUriAsString,
            SourceUri: sourceUri,
            PollBatchSize: options.PollBatchSize,
            PollInterval: options.PollInterval,
            ProfilerEnabled: options.ProfilerEnabled,
            EmitStreamReceivedEvent: options.EmitStreamReceivedEvent,
            AutoAck: options.AutoAck,
            MaxLength: options.MaxLength
            );
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Published to topic {TopicKey} event {EventId} stream id {StreamId}")]
    private partial void LogPublished(TopicKey topicKey, string? eventId, RedisValue streamId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Observer threw in OnEventsMissed on topic {TopicKey}; it stays subscribed.")]
    private partial void LogObserverMissedEventsFailed(Exception ex, TopicKey topicKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error when publishing to topic {TopicKey} event {EventId}")]
    private partial void LogPublishError(Exception ex, TopicKey topicKey, string? eventId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Best-effort notify doorbell publish failed for topic {TopicKey}")]
    private partial void LogNotifyPublishError(Exception ex, TopicKey topicKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Best-effort notify doorbell publish failed again for topic {TopicKey} ({Failures} in a row)")]
    private partial void LogNotifyPublishErrorAgain(Exception ex, TopicKey topicKey, int failures);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The topology behind topic {TopicKey} could not be read; the doorbell waits for the XADD reply")]
    private partial void LogTopologyUnknown(Exception ex, TopicKey topicKey);
}
