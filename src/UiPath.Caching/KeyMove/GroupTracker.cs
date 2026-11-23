namespace UiPath.Caching.KeyMove;

internal sealed class GroupTracker(GroupRegistration registration, TimeProvider time)
{
    private const string IdentityConflict = "identity_conflict";

#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif
    private readonly long _startedAt = time.GetTimestamp();
    private StateAnswer _answer;
    private StateRecord? _state;
    private bool _recordFound;
    private bool _everPresent;
    private ActedState? _lastKnown;
    private ActedState? _acknowledged;
    private ControlSample? _sample;
    private bool _regressed;
    private long? _readAt;
    private long? _durZeroSince;
    private string? _durZeroIdentity;
    private bool _dstReadFailed;
    private bool _sampled;
    private long? _sampleDurZeroSince;
    private string? _sampleDurZeroIdentity;

    public GroupRegistration Registration { get; } = registration;

    public HashSet<string> Alerted { get; } = [];

    public ActedState? Acknowledged
    {
        get
        {
            lock (_gate)
            {
                return _acknowledged;
            }
        }
    }

    public void Read(StateRecord? record)
    {
        lock (_gate)
        {
            var now = time.GetTimestamp();
            _answer = record is null ? StateAnswer.Absent : StateAnswer.Present;
            _state = record;
            _readAt = now;
            _dstReadFailed = false;
            _regressed = false;
            _everPresent |= record is not null;
            TrackPendingIdentity(record, now);
            if (record is null)
            {
                return;
            }

            _regressed = _lastKnown is { } known && (record.Ep < known.Ep || (record.Ep == known.Ep && record.Id != known.Id));
            if (!record.Durable || _regressed || SampleConflict(record, _sample) is not null)
            {
                return;
            }

            if (_lastKnown is null || record.Ep > _lastKnown.Ep)
            {
                _acknowledged = null;
            }

            _lastKnown = ActedState.From(record);
        }
    }

    /// <summary>The control record found at the probe slot, null for none; a record makes the group relevant for good.</summary>
    public void Probed(ControlSample? sample)
    {
        lock (_gate)
        {
            _sample = sample;
            _sampled = true;
            _recordFound |= sample is not null;
            if (sample is null || sample.Durable)
            {
                _sampleDurZeroIdentity = null;
                _sampleDurZeroSince = null;
            }
            else if (_sampleDurZeroIdentity != $"{sample.Ep}:{sample.Id}")
            {
                _sampleDurZeroIdentity = $"{sample.Ep}:{sample.Id}";
                _sampleDurZeroSince = time.GetTimestamp();
            }
        }
    }

    // After a failed Dst read, a newer Src sample may be a pending transition.
    public void ReadFailed()
    {
        lock (_gate)
        {
            _dstReadFailed = true;
        }
    }

    public bool Acknowledge(long ep, string id)
    {
        lock (_gate)
        {
            if (_lastKnown is not { } known || known.Ep != ep || known.Id != id)
            {
                return false;
            }

            _acknowledged = known;
            return true;
        }
    }

    public GroupView View()
    {
        lock (_gate)
        {
            var now = time.GetTimestamp();
            var pending = Registration.InstanceMove && _dstReadFailed;
            var conflict = _regressed ? IdentityConflict : StateConflict(_state, _sample, pending);
            return new GroupView(
                Registration,
                _answer,
                _state,
                _recordFound,
                _everPresent,
                _lastKnown,
                _sample,
                conflict,
                _readAt is { } read ? time.GetElapsedTime(read, now) : null,
                _durZeroSince is { } since ? time.GetElapsedTime(since, now) : null,
                time.GetElapsedTime(_startedAt, now),
                _dstReadFailed,
                _sampled,
                _sampleDurZeroSince is { } sampleSince ? time.GetElapsedTime(sampleSince, now) : null);
        }
    }

    // A newer or conflicting record reveals a rollback. A committed record can also outlive the state commit marker.
    private static string? StateConflict(StateRecord? state, ControlSample? sample, bool newerIsPending) =>
        state is null ? null : SampleConflict(state, sample, newerIsPending);

    private static string? SampleConflict(StateRecord state, ControlSample? sample, bool newerIsPending = false) => sample switch
    {
        null => null,
        { Conflicting: true } => IdentityConflict,
        _ when sample.Ep > state.Ep && !newerIsPending => IdentityConflict,
        _ when sample.Ep == state.Ep && sample.Id != state.Id => IdentityConflict,
        _ when sample.Ep == state.Ep && sample.Durable && !state.Durable => "commit_mismatch",
        _ => null,
    };

    private void TrackPendingIdentity(StateRecord? record, long now)
    {
        if (record is null || record.Durable)
        {
            _durZeroIdentity = null;
            _durZeroSince = null;
            return;
        }

        if (_durZeroIdentity != record.Identity)
        {
            _durZeroIdentity = record.Identity;
            _durZeroSince = now;
        }
    }
}
