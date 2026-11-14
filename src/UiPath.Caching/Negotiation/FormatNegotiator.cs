using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Negotiation;

/// <summary>
/// Turns what the node list shows into which serializers are written now, whatever policy asks for them. A format is written only while every
/// advertisement lists it and no live node is unaccounted for, and only after that has held for <see cref="FormatNegotiationOptions.UpgradeHold"/>
/// since the reading nodes last gained a member. The first node that cannot read it stops the writes at once; a node that leaves never does.
/// </summary>
internal sealed class FormatNegotiator : IEffectiveFormat
{
    public const string ReasonUnaccountedGroup = "unaccounted_group";
    public const string ReasonMissingFormat = "missing_format";
    public const string ReasonNoView = "no_view";
    public const string ReasonViewStale = "view_stale";
    public const string ReasonTransportUnknown = "transport_unknown";

    private readonly DesiredWrite[] _desired;
    private readonly FormatNegotiationOptions _options;
    private readonly TimeProvider _clock;
    private readonly ICachingTelemetryProvider _telemetry;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly long?[] _candidateSince;
    private readonly string?[] _reasons;
    private HashSet<string> _readers = [];
    private volatile Snapshot _snapshot;
    private long _lastView;
    private long? _graceStart;
    private bool _hasView;
    private bool _failed;

    public FormatNegotiator(
        IEnumerable<DesiredWrite> desired,
        FormatNegotiationOptions options,
        TimeProvider clock,
        ICachingTelemetryProvider? telemetry = null,
        ILogger? logger = null)
    {
        options.Validate();
        _desired = [.. desired];
        _options = options;
        _clock = clock;
        _telemetry = telemetry ?? NullTelemetryProvider.Instance;
        _logger = logger ?? NullLogger.Instance;
        _candidateSince = new long?[_desired.Length];
        _reasons = new string?[_desired.Length];
        Array.Fill(_reasons, ReasonNoView);
        _snapshot = Publish(new bool[_desired.Length], 0);
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
            var joined = view.Readers.Any(reader => !_readers.Contains(reader));
            _readers = [.. view.Readers];
            _hasView = true;
            _lastView = now;
            var allowed = new bool[_desired.Length];
            var previous = _snapshot.Writers;
            for (var i = 0; i < _desired.Length; i++)
            {
                _reasons[i] = Block(_desired[i], view);
                if (_reasons[i] is ReasonNoView or ReasonUnaccountedGroup && grace && previous[i].Allowed)
                {
                    _reasons[i] = null;
                    allowed[i] = true;
                }
                else if (_reasons[i] is not null)
                {
                    _candidateSince[i] = null;
                }
                else if (previous[i].Allowed)
                {
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
            Array.Fill(_reasons, ReasonViewStale);
            var allowed = new bool[_desired.Length];
            Transition(_snapshot.Writers, allowed);
            _snapshot = Publish(allowed, _snapshot.Stamp);
        }
    }

    private string? Block(DesiredWrite want, NodeListView view)
    {
        // What an advertisement says it cannot read comes first: no grace after an outage and no attestation excuses it.
        foreach (var stored in view.Ads)
        {
            var ad = stored.Advertisement;
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
        return view.Blockers.Count > 0 && !attested ? ReasonUnaccountedGroup : null;
    }

    private void Transition(WriterState[] previous, bool[] allowed)
    {
        for (var i = 0; i < allowed.Length; i++)
        {
            var policies = string.Join(',', _desired[i].Policies);
            if (previous[i].Allowed && !allowed[i])
            {
                _telemetry.TrackMetric(Metrics.FormatFallback, 1, [new("reason", _reasons[i] ?? ReasonNoView), new("policy", policies)]);
                _logger.LogWarning("Value format {Format} falls back to JSON: {Reason}.", string.Join('+', _desired[i].Tokens), _reasons[i]);
            }
            else if (!previous[i].Allowed && allowed[i])
            {
                _logger.LogInformation("Value format {Format} is now written.", string.Join('+', _desired[i].Tokens));
            }
        }
    }

    private void Report(NodeListView view)
    {
        _telemetry.TrackMetric(Metrics.FormatViewAge, 0);
        _telemetry.TrackMetric(Metrics.FormatNodes, view.Ads.Count, [new("state", "live")]);
        _telemetry.TrackMetric(Metrics.FormatNodes, view.Blockers.Count, [new("state", "blocking")]);
        if (view.Blockers.Count > 0)
        {
            _logger.LogWarning("Nodes live on a stream without an advertisement, so not sent a binary format: {Nodes}.", string.Join(", ", view.Blockers));
        }

        var current = _snapshot.Writers;
        for (var i = 0; i < _desired.Length; i++)
        {
            _telemetry.TrackMetric(
                Metrics.FormatEffective,
                current[i].Allowed ? 1 : 0,
                [new("kind", "value"), new("policy", string.Join(',', _desired[i].Policies)), new("format", string.Join('+', _desired[i].Tokens))]);
        }
    }

    private Snapshot Publish(bool[] allowed, long stamp)
    {
        var writers = new WriterState[_desired.Length];
        for (var i = 0; i < writers.Length; i++)
        {
            writers[i] = new(_desired[i].Writer, allowed[i]);
        }

        return new(writers, stamp);
    }

    private sealed record WriterState(ICacheValueSerializer Writer, bool Allowed);

    private sealed record Snapshot(WriterState[] Writers, long Stamp);
}
