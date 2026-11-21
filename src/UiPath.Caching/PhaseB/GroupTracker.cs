namespace UiPath.Caching.PhaseB;

/// <summary>The mutable record of one group on one node: its last answers, the state it last acted on, and the identity it acknowledged.</summary>
internal sealed class GroupTracker(GroupRegistration registration, TimeProvider time)
{
    private readonly object _gate = new();
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

    /// <summary>A definitive read: a state, or its absence.</summary>
    public void Read(StateRecord? record)
    {
        lock (_gate)
        {
            var now = time.GetTimestamp();
            _answer = record is null ? StateAnswer.Absent : StateAnswer.Present;
            _state = record;
            _readAt = now;
            _regressed = false;
            _everPresent |= record is not null;
            if (record is null)
            {
                _durZeroIdentity = null;
                _durZeroSince = null;
                return;
            }

            if (record.Durable)
            {
                _durZeroIdentity = null;
                _durZeroSince = null;
            }
            else if (_durZeroIdentity != record.Identity)
            {
                _durZeroIdentity = record.Identity;
                _durZeroSince = now;
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
            _recordFound |= sample is not null;
        }
    }

    /// <summary>Records the acknowledgement, only for the identity the node acts under.</summary>
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
            var conflict = _regressed ? "identity_conflict" : (_state is { } state ? SampleConflict(state, _sample) : null);
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
                time.GetElapsedTime(_startedAt, now));
        }
    }

    // A record above the state, or at its epoch under another id, means the hash was rolled back or conflicts; a committed record under an uncommitted state means the commit marker of the state was lost.
    private static string? SampleConflict(StateRecord state, ControlSample? sample) => sample switch
    {
        null => null,
        { Conflicting: true } => "identity_conflict",
        _ when sample.Ep > state.Ep => "identity_conflict",
        _ when sample.Ep == state.Ep && sample.Id != state.Id => "identity_conflict",
        _ when sample.Ep == state.Ep && sample.Durable && !state.Durable => "commit_mismatch",
        _ => null,
    };
}
