using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Broadcast.Redis;

/// <summary>
/// The topic of a Streams provider that may have to leave Streams. It starts on Streams, as the plain Streams topic does, and
/// publishes and listens over it while <see cref="BroadcastCapability"/> probes in the background; a server that turns out not to
/// run Streams moves it to the Pub/Sub fallback, or switches it off. Its observers never move; one bridge observer moves between
/// the topics beneath it, so a switch costs them one <see cref="MissedEventsReason.Lost"/> and no resubscribing.
/// </summary>
internal sealed partial class StreamsFallbackTopic : ITopic<ICacheEvent>, ITopicSubscriptionState
{
    // A publish may meet a refusal on Streams and another on the Pub/Sub it moves to: it follows both, ending where the topic is off.
    private const int MaxSwitches = 2;

    private readonly BroadcastCapability _capability;
    private readonly StreamsUnsupportedAction _action;
    private readonly IPubSubFallback? _pubSub;
    private readonly Func<Action, ITopic<ICacheEvent>> _createStreams;
    private readonly IEventSubject<ICacheEvent> _subject;
    private readonly Action? _onLeftStreams;
    private readonly ILogger _logger;
    private readonly ICachingTelemetryProvider? _telemetry;
    private readonly Lock _gate = new();
    private State _state = State.Initial;
    private volatile bool _worked;

    /// <param name="createStreams">Makes the Streams topic, which the topic starts on unless the server is known not to run Streams. It is given the action the topic calls on its first successful read.</param>
    /// <param name="telemetry">Told once for each switch, by <see cref="Metrics.TransportChanged"/>.</param>
    /// <param name="onLeftStreams">Called when the topic stops being on Streams, by a switch or by dispose; more than once is harmless.</param>
    public StreamsFallbackTopic(
        TopicKey topicKey,
        BroadcastCapability capability,
        StreamsProbeSettings settings,
        IPubSubFallback? pubSub,
        Func<Action, ITopic<ICacheEvent>> createStreams,
        IEventSubject<ICacheEvent> subject,
        ILogger logger,
        Action? onLeftStreams = null,
        ICachingTelemetryProvider? telemetry = null)
    {
        TopicKey = topicKey;
        _capability = capability;
        _action = settings.Action;
        _pubSub = pubSub;
        _createStreams = createStreams;
        _subject = subject;
        _logger = logger;
        _onLeftStreams = onLeftStreams;
        _telemetry = telemetry;
        // Before the first switch: a Pub/Sub topic made there can meet its refusal at once, and the refusal is logged by these settings.
        _capability.Attach(settings);
        _capability.Changed += OnCapabilityChanged;
        SwitchTo(Wanted() ?? Mode.Streams);
        _ = _capability.Start(settings);
        Decide();
    }

    private enum Mode
    {
        Undecided,
        Streams,
        PubSub,
        Off,
        Disposed,
    }

    public TopicKey TopicKey { get; }

    public EventHandler? OnDisposed { get; set; }

    /// <summary>The topic's stream is the one its events travel on, for the node list.</summary>
    public bool OnStreams => Volatile.Read(ref _state).Mode == Mode.Streams;

    /// <summary>What the topic's events travel over now; a topic not yet decided is on Streams, as it starts there.</summary>
    string? ITopic<ICacheEvent>.Transport => TransportName(Volatile.Read(ref _state).Mode);

    public bool IsSubscribed
    {
        get
        {
            var state = Volatile.Read(ref _state);
            return state.Mode is (Mode.Streams or Mode.PubSub) && state.Inner is ITopicSubscriptionState { IsSubscribed: true };
        }
    }

    public IDisposable Subscribe(IObserver<ICacheEvent> observer)
    {
        this.ThrowIfDisposed(Volatile.Read(ref _state).Mode == Mode.Disposed);
        return _subject.Subscribe(observer);
    }

    public async ValueTask<bool> PublishAsync(ICacheEvent @event, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var state = Volatile.Read(ref _state);
        this.ThrowIfDisposed(state.Mode == Mode.Disposed);
        var attempt = 0;
        while (true)
        {
            if (state.Inner is null)
            {
                // Off publishes true, like NullTopic; a disposed topic has nothing to publish to.
                return state.Mode == Mode.Off;
            }

            if (await TryPublishAsync(state, @event, attempt, token).ConfigureAwait(false))
            {
                return true;
            }

            if (attempt >= MaxSwitches)
            {
                return false;
            }

            // A refusal that failed this publish has switched the topic, or is about to.
            Decide();
            var current = Volatile.Read(ref _state);
            if (ReferenceEquals(current, state))
            {
                return false;
            }

            state = current;
            attempt++;
        }
    }

    public void Dispose()
    {
        State previous;
        lock (_gate)
        {
            previous = _state;
            if (previous.Mode == Mode.Disposed)
            {
                return;
            }

            _capability.Changed -= OnCapabilityChanged;
            Volatile.Write(ref _state, State.Gone);
        }

        previous.Bridge?.Retire();
        _subject.Dispose();
        if (previous.Mode == Mode.Streams)
        {
            previous.Inner?.Dispose();
        }

        _onLeftStreams?.Invoke();
        OnDisposed?.Invoke(this, EventArgs.Empty);
    }

    private static string TransportName(Mode mode) => mode switch
    {
        Mode.PubSub => Metrics.TransportPubSub,
        Mode.Off or Mode.Disposed => Metrics.TransportOff,
        _ => Metrics.TransportStreams,
    };

    private async ValueTask<bool> TryPublishAsync(State state, ICacheEvent @event, int attempt, CancellationToken token)
    {
        try
        {
            if (!await state.Inner!.PublishAsync(@event, token).ConfigureAwait(false))
            {
                return false;
            }

            if (state.Mode == Mode.Streams && !_worked)
            {
                _worked = true;
            }

            return true;
        }
        catch (ObjectDisposedException) when (attempt < MaxSwitches && !ReferenceEquals(Volatile.Read(ref _state), state))
        {
            // The switch disposed the topic this publish went to: again, over what it is now.
            return false;
        }
    }

    // Whether the Streams topic ever read or published is what makes leaving it a loss; a read happens once per batch, so this costs nothing per event.
    private void OnStreamsRead() => _worked = true;

    // Off the thread that decided, which may be a topic's own reader or publisher: that topic is disposed by the switch.
    private void OnCapabilityChanged() => ThreadPool.UnsafeQueueUserWorkItem(static self => self.Decide(), this, preferLocal: false);

    private void Decide()
    {
        if (Wanted() is { } mode && mode != Volatile.Read(ref _state).Mode)
        {
            SwitchTo(mode);
        }
    }

    // The mode the capability calls for, null while it leaves the topic as it is: Supported and Unknown change nothing. Off and the fallback are never left.
    private Mode? Wanted()
    {
        if (_capability.State != StreamsState.Unsupported)
        {
            return null;
        }

        var offered = _pubSub is not null && _action == StreamsUnsupportedAction.PubSub && !_capability.PubSubIsRefused;
        return offered ? Mode.PubSub : Mode.Off;
    }

    private void SwitchTo(Mode mode)
    {
        State previous;
        var switchFailed = false;
        lock (_gate)
        {
            previous = _state;
            if (previous.Mode is Mode.Disposed or Mode.Off || mode == previous.Mode)
            {
                return;
            }

            ITopic<ICacheEvent>? inner = null;
            Bridge? bridge = null;
            try
            {
                inner = mode switch
                {
                    Mode.Streams => _createStreams(OnStreamsRead),
                    Mode.PubSub => _pubSub!.Create(TopicKey),
                    _ => null,
                };
                if (inner is not null)
                {
                    bridge = new Bridge(this);
                    bridge.Attach(inner.Subscribe(bridge));
                    if (mode == Mode.PubSub)
                    {
                        var source = inner;

                        // Listens to the topic, which may be shared with an app that chose Pub/Sub: its behaviour stays as it was.
                        source.Refused += _capability.PubSubRefused;
                        bridge.OnRetire(() => source.Refused -= _capability.PubSubRefused);
                    }
                }
            }
            catch (Exception ex)
            {
                LogSwitchFailed(ex, TopicKey, mode);
                bridge?.Retire();
                if (mode == Mode.Streams)
                {
                    // Made here and nobody else's: its fetch loop would run on with no one to stop it.
                    inner?.Dispose();
                }

                inner = null;
                bridge = null;
                mode = Mode.Off;
                switchFailed = true;
            }

            Volatile.Write(ref _state, new State(mode, inner, bridge));
        }

        // The new topic is in place before the old one goes, so no event falls between them; what the old one held is lost.
        previous.Bridge?.Retire();
        if (previous.Mode == Mode.Streams)
        {
            previous.Inner?.Dispose();
            _onLeftStreams?.Invoke();
        }

        // Not on the first attach: the topic beneath tells a subscriber what it missed, and a gap here would expire what an early reader just wrote.
        // Nor on a switch off Streams that never worked: nothing was lost that a first attach would not have, and the tiers keep what they hold.
        var lost = previous.Mode switch
        {
            Mode.Undecided => mode == Mode.Off,
            Mode.Streams => _worked,
            _ => true,
        };
        if (lost)
        {
            _subject.Invalidate(MissedEventsReason.Lost);
        }

        LogSwitched(TopicKey, previous.Mode, mode);
        TrackSwitched(previous.Mode, mode, switchFailed);
    }

    // One event per switch, not for the first attach onto Streams, which is where the topic always starts; a topic that starts elsewhere
    // because the capability was decided already reports leaving Streams.
    private void TrackSwitched(Mode from, Mode to, bool switchFailed)
    {
        if (_telemetry is null || (from == Mode.Undecided && to == Mode.Streams))
        {
            return;
        }

        var reason = switchFailed ? "the new transport could not be created" : _capability.Reason ?? "the server cannot run Streams";
        _telemetry.TryTrackEvent(
            Metrics.TransportChanged,
            [
                new(Metrics.TopicName, TopicKey),
                new(Metrics.TransportFrom, TransportName(from == Mode.Undecided ? Mode.Streams : from)),
                new(Metrics.TransportTo, TransportName(to)),
                new(Metrics.TransportReason, reason),
            ]);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Topic {TopicKey} broadcasts by {From} no longer: {To}")]
    private partial void LogSwitched(TopicKey topicKey, Mode from, Mode to);

    [LoggerMessage(Level = LogLevel.Error, Message = "Topic {TopicKey} could not move to {Mode}; its broadcast is turned off")]
    private partial void LogSwitchFailed(Exception ex, TopicKey topicKey, Mode mode);

    /// <summary>What the topic is on, kept together so a reader sees a mode and the topic of that mode, never one of each from different moments.</summary>
    private sealed record State(Mode Mode, ITopic<ICacheEvent>? Inner, Bridge? Bridge)
    {
        public static readonly State Initial = new(Mode.Undecided, null, null);

        public static readonly State Gone = new(Mode.Disposed, null, null);
    }

    /// <summary>The one observer on the topic beneath, passing what it hears on to the observers of the topic above.</summary>
    private sealed class Bridge(StreamsFallbackTopic owner) : IObserver<ICacheEvent>, IMissedEventsObserver
    {
        private IDisposable? _subscription;
        private Action? _onRetire;
        private volatile bool _retired;

        public void Attach(IDisposable subscription) => _subscription = subscription;

        public void OnRetire(Action action) => _onRetire = action;

        // A retired bridge still hears what its topic had in flight, and none of it is the observers' to see.
        public void Retire()
        {
            _retired = true;
            _subscription?.Dispose();
            _onRetire?.Invoke();
        }

        public void OnNext(ICacheEvent value)
        {
            if (!_retired)
            {
                owner._subject.OnNext(value);
            }
        }

        public void OnEventsMissed(MissedEventsReason reason)
        {
            if (!_retired)
            {
                owner._subject.Invalidate(reason);
            }
        }

        public void OnCompleted()
        {
            if (!_retired)
            {
                owner._subject.OnCompleted();
            }
        }

        public void OnError(Exception error)
        {
        }
    }
}
