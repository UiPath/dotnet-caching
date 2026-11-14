using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Negotiation;

/// <summary>
/// Turns what the node list shows into which serializers, and whether the binary event format, are written now, whatever policy asks for them. A format
/// is written only while every advertisement lists it and no live node is unaccounted for, and only after that has held for
/// <see cref="FormatNegotiationOptions.UpgradeHold"/> since the reading nodes last gained a member. The first node that cannot read it stops the
/// writes at once; a node that leaves never does.
/// </summary>
internal sealed class FormatNegotiator : IEffectiveFormat, IBinaryEvents
{
    public const string ReasonUnaccountedGroup = "unaccounted_group";
    public const string ReasonMissingFormat = "missing_format";
    public const string ReasonNoView = "no_view";
    public const string ReasonViewStale = "view_stale";
    public const string ReasonTransportUnknown = "transport_unknown";
    public const string ReasonUnaccountedSubscriber = "unaccounted_subscriber";
    public const string ReasonSubscriberDeficit = "subscriber_deficit";

    // Not a reason to fall back: the Pub/Sub count has not yet shown the same thing twice, so a format in use stays and one not in use waits.
    private const string ReasonSettling = "settling";

    private readonly Slot[] _slots;
    private readonly int _eventSlot;
    private readonly FormatNegotiationOptions _options;
    private readonly TimeProvider _clock;
    private readonly ICachingTelemetryProvider _telemetry;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly long?[] _candidateSince;
    private readonly bool[] _suspended;
    private readonly string?[] _reasons;
    private HashSet<string> _readers = [];
    private volatile Snapshot _snapshot;
    private long _lastView;
    private long? _graceStart;
    private bool _hasView;
    private bool _failed;
    private string _blocking = string.Empty;
    private string? _uncountable;

    public FormatNegotiator(
        IEnumerable<DesiredWrite> desired,
        FormatNegotiationOptions options,
        TimeProvider clock,
        ICachingTelemetryProvider? telemetry = null,
        ILogger? logger = null,
        DesiredEvents? events = null)
    {
        options.Validate();
        _slots = [.. desired.Select(d => new Slot(d.Writer, d.Tokens, d.Policies, d.Others)), .. events is null ? [] : new[] { new Slot(null, events.Tokens, [], []) }];
        _eventSlot = events is null ? -1 : _slots.Length - 1;
        _options = options;
        _clock = clock;
        _telemetry = telemetry ?? NullTelemetryProvider.Instance;
        _logger = logger ?? NullLogger.Instance;
        _candidateSince = new long?[_slots.Length];
        _suspended = new bool[_slots.Length];
        _reasons = new string?[_slots.Length];
        Array.Fill(_reasons, ReasonNoView);
        _snapshot = Publish(new bool[_slots.Length], 0);
    }

    /// <summary>Whether binary events are written now; always <see langword="false"/> for a node not set to write them.</summary>
    public bool Writes
    {
        get
        {
            var snapshot = _snapshot;
            return _eventSlot >= 0 && snapshot.Writers[_eventSlot].Allowed && _clock.GetElapsedTime(snapshot.Stamp) <= _options.MaxViewAge;
        }
    }

    public bool Allows(ICacheValueSerializer writer)
    {
        var snapshot = _snapshot;
        var writers = snapshot.Writers;
        for (var i = 0; i < writers.Length; i++)
        {
            if (ReferenceEquals(writers[i].Writer, writer))
            {
                // A view that stopped refreshing, without failing, decays on its own: the age is checked here, off the cached stamp and the injected clock.
                return writers[i].Allowed && _clock.GetElapsedTime(snapshot.Stamp) <= _options.MaxViewAge;
            }
        }

        return false;
    }

    public void Observe(NodeListView view)
    {
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            if (_failed)
            {
                // Peers need a moment to advertise again after Redis came back; what they failed to say meanwhile is not an answer.
                _graceStart = now;
                _failed = false;
            }

            var grace = _graceStart is { } start && _clock.GetElapsedTime(start, now) < TimeSpan.FromTicks(_options.AdvertiseInterval.Ticks * 2);
            // Something that subscribed to take the place of a claim that was still fresh is a join too, though it advertised nothing.
            var joined = view.Readers.Any(reader => !_readers.Contains(reader)) || view.Subscribers is { Replaced: true };
            _readers = [.. view.Readers];
            _hasView = true;
            _lastView = now;
            var allowed = new bool[_slots.Length];
            var previous = _snapshot.Writers;
            for (var i = 0; i < _slots.Length; i++)
            {
                // A node that joins ends what a departure held back: the format then waits for the hold like any other.
                _suspended[i] &= !joined;
                _reasons[i] = Block(_slots[i], view);
                if (_reasons[i] is ReasonNoView or ReasonUnaccountedGroup or ReasonUnaccountedSubscriber && grace && previous[i].Allowed)
                {
                    _reasons[i] = null;
                    allowed[i] = true;
                }
                else if (_reasons[i] is ReasonSettling)
                {
                    // A blip pauses the hold, it does not restart it: only a join, or a format a node cannot read, does.
                    _reasons[i] = null;
                    if (joined)
                    {
                        _candidateSince[i] = null;
                    }

                    allowed[i] = previous[i].Allowed;
                }
                else if (_reasons[i] is ReasonSubscriberDeficit)
                {
                    // A departure, not a join: the format is not written, and the hold it had earned is kept for when the count adds up again.
                    _suspended[i] |= previous[i].Allowed && !joined;
                    if (joined)
                    {
                        _candidateSince[i] = null;
                    }
                }
                else if (_reasons[i] is not null)
                {
                    _suspended[i] = false;
                    _candidateSince[i] = null;
                }
                else if (previous[i].Allowed)
                {
                    allowed[i] = true;
                }
                else if (_suspended[i] && !joined)
                {
                    // The count adds up again: what was written before the node went away resumes, without a new hold.
                    _suspended[i] = false;
                    allowed[i] = true;
                }
                else
                {
                    if (_candidateSince[i] is null || joined)
                    {
                        _candidateSince[i] = now;
                    }

                    allowed[i] = _clock.GetElapsedTime(_candidateSince[i]!.Value, now) >= _options.UpgradeHold;
                }
            }

            Transition(previous, allowed);
            _snapshot = Publish(allowed, now);
            Report(view);
        }
    }

    /// <summary>The node list could not be read: the decision stands until <see cref="FormatNegotiationOptions.MaxViewAge"/> has passed since the last view, then JSON.</summary>
    public void ObservationFailed()
    {
        lock (_gate)
        {
            if (!_hasView)
            {
                return;
            }

            _failed = true;
            var age = _clock.GetElapsedTime(_lastView);
            _telemetry.TrackMetric(Metrics.FormatViewAge, age.TotalSeconds);
            Array.Fill(_candidateSince, null);
            if (age <= _options.MaxViewAge)
            {
                return;
            }

            _hasView = false;
            Array.Fill(_suspended, false);
            Array.Fill(_reasons, ReasonViewStale);
            var allowed = new bool[_slots.Length];
            Transition(_snapshot.Writers, allowed);
            _snapshot = Publish(allowed, _snapshot.Stamp);
        }
    }

    private string? Block(Slot want, NodeListView view)
    {
        // What an advertisement says it cannot read comes first: no grace after an outage and no attestation excuses it.
        foreach (var stored in view.Ads)
        {
            var ad = stored.Advertisement;
            if (want.IsEvent)
            {
                // An advertisement that lists no event formats is from a build that did not know them, so it reads JSON at most.
                if (ad.EventReads is null || !want.Tokens.All(ad.EventReads.Contains))
                {
                    return ReasonMissingFormat;
                }

                continue;
            }

            if (!want.Tokens.All(ad.ValueReads.Contains))
            {
                return ReasonMissingFormat;
            }

            foreach (var (policy, list) in ad.Strict)
            {
                if (list.Contains(NodeAdvertisement.NoSniff) || (!want.Others.Contains(policy) && !want.Tokens.All(list.Contains)))
                {
                    return ReasonMissingFormat;
                }
            }
        }

        var attested = _options.AssumeAllNodesCanRead;
        if (!view.Complete)
        {
            if (!attested)
            {
                return ReasonTransportUnknown;
            }
        }
        else if (view.Ads.Count == 0)
        {
            return ReasonNoView;
        }

        // The attestation covers nodes that say nothing; a node that advertises without the format is not one of them.
        if (view.Blockers.Count > 0 && !attested)
        {
            return ReasonUnaccountedGroup;
        }

        return view.Subscribers is { } subscribers && !attested
            ? subscribers switch
            {
                { Known: false } => ReasonTransportUnknown,
                { Unaccounted: true } => ReasonUnaccountedSubscriber,
                { Short: true } => ReasonSubscriberDeficit,
                { Settled: false } => ReasonSettling,
                _ => null,
            }
            : null;
    }

    private void Transition(WriterState[] previous, bool[] allowed)
    {
        for (var i = 0; i < allowed.Length; i++)
        {
            var slot = _slots[i];
            var kind = slot.IsEvent ? "event" : "value";
            var format = string.Join('+', slot.Tokens);
            if (previous[i].Allowed && !allowed[i])
            {
                _telemetry.TrackMetric(
                    Metrics.FormatFallback,
                    1,
                    slot.IsEvent
                        ? [new("reason", _reasons[i] ?? ReasonNoView), new("kind", kind)]
                        : [new("reason", _reasons[i] ?? ReasonNoView), new("kind", kind), new("policy", string.Join(',', slot.Policies))]);
                _logger.LogWarning("The {Kind} format {Format} falls back to JSON: {Reason}.", kind, format, _reasons[i]);
            }
            else if (!previous[i].Allowed && allowed[i])
            {
                _logger.LogInformation("The {Kind} format {Format} is now written.", kind, format);
            }
        }
    }

    private void Report(NodeListView view)
    {
        _telemetry.TrackMetric(Metrics.FormatViewAge, 0);
        _telemetry.TrackMetric(Metrics.FormatNodes, view.Ads.Count, [new("state", "live")]);
        var unknown = view.Subscribers?.Surplus ?? 0;
        _telemetry.TrackMetric(Metrics.FormatNodes, view.Blockers.Count + unknown, [new("state", "blocking")]);
        ReportBlocking(view);
        var current = _snapshot.Writers;
        for (var i = 0; i < _slots.Length; i++)
        {
            var slot = _slots[i];
            var format = new KeyValuePair<string, string>("format", string.Join('+', slot.Tokens));
            _telemetry.TrackMetric(
                Metrics.FormatEffective,
                current[i].Allowed ? 1 : 0,
                slot.IsEvent
                    ? [new("kind", "event"), format]
                    : [new("kind", "value"), new("policy", string.Join(',', slot.Policies)), format]);
        }
    }

    // Who blocks goes to the log and to Caching.Stats.FormatBlockers when the set changes, and never to the cache.format.* metrics, whose tags are bounded:
    // node ids are not, so a backend that turns a metric's tags into dimensions would see as many as there are nodes.
    private void ReportBlocking(NodeListView view)
    {
        ReportUncountable(view.Subscribers);
        var blocking = string.Join(", ", view.Blockers);
        var channels = string.Join(", ", view.Subscribers?.Channels ?? []);
        var missing = string.Join(", ", view.Subscribers?.ShortChannels ?? []);
        var key = blocking + "|" + channels + "|" + missing;
        if (key == _blocking)
        {
            return;
        }

        _blocking = key;
        if (blocking.Length > 0)
        {
            _logger.LogWarning("Nodes live on a stream without an advertisement, so not sent a binary format: {Nodes}.", blocking);
            _telemetry.TrackMetric(Metrics.FormatBlockers, view.Blockers.Count, [new("nodes", blocking)]);
        }

        if (channels.Length > 0)
        {
            _logger.LogWarning("Subscribers no advertisement accounts for on Pub/Sub channels or patterns, so not sent a binary format: {Channels}. Pattern subscriptions are counted for the whole server, other apps' included; set FormatNegotiation:AssumeAllNodesCanRead to attest that every node reads binary formats.", channels);
            _telemetry.TrackMetric(Metrics.FormatBlockers, view.Subscribers!.Surplus, [new("channels", channels)]);
        }

        if (missing.Length > 0)
        {
            _logger.LogWarning("Pub/Sub channels with fewer subscribers than the advertisements claim, so not sent a binary format until the claims expire: {Channels}.", missing);
            _telemetry.TrackMetric(Metrics.FormatBlockers, view.Subscribers!.ShortChannels!.Count, [new("short_channels", missing)]);
        }
    }

    // Once per change: a node that cannot count Pub/Sub subscribers stays on JSON, and nothing else says why.
    private void ReportUncountable(SubscriberReading? subscribers)
    {
        var why = subscribers is { Known: false } ? subscribers.Why ?? "the subscribers cannot be counted" : null;
        if (why == _uncountable)
        {
            return;
        }

        if (why is not null)
        {
            _logger.LogWarning("Pub/Sub subscribers cannot be counted ({Why}), so binary formats stay JSON. Set FormatNegotiation:AssumeAllNodesCanRead to attest that every node reads them.", why);
        }
        else
        {
            _logger.LogInformation("Pub/Sub subscribers can be counted again.");
        }

        _uncountable = why;
    }

    private Snapshot Publish(bool[] allowed, long stamp)
    {
        var writers = new WriterState[_slots.Length];
        for (var i = 0; i < writers.Length; i++)
        {
            writers[i] = new(_slots[i].Writer, allowed[i]);
        }

        return new(writers, stamp);
    }

    /// <summary>A serializer, or the binary event format when <paramref name="Writer"/> is <see langword="null"/>, with what a node must read to be sent it.</summary>
    private sealed record Slot(ICacheValueSerializer? Writer, IReadOnlyList<string> Tokens, IReadOnlyCollection<string> Policies, IReadOnlyCollection<string> Others)
    {
        public bool IsEvent => Writer is null;
    }

    private sealed record WriterState(ICacheValueSerializer? Writer, bool Allowed);

    private sealed record Snapshot(WriterState[] Writers, long Stamp);
}
