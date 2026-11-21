namespace UiPath.Caching.Migration;

/// <summary>
/// One group's pin on this node. Applied (the setting says pin, or the group is switched) it means the library path and never the source; acknowledged
/// once the calls that were admitted before it applied have ended. A switch seen once is kept for the life of the process.
/// </summary>
internal sealed class GroupPin(string group)
{
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif
    private int _applied;
    private int _acked;
    private int _sourceCalls;
    private int _switched;
    private int _durable;
    private bool _pinned;
    private long _knownAt;
    private int _known;
    private int _unconfirmed;
    private long _withdrawals;
    private long _ackGeneration;
    private long _publishedGeneration;

    public string Group { get; } = group;

    public bool Applied => Volatile.Read(ref _applied) == 1;

    public bool Acked => Volatile.Read(ref _acked) == 1;

    public bool Switched => Volatile.Read(ref _switched) == 1;

    /// <summary>The switch is recorded in Redis, as far as this node last saw.</summary>
    public bool Durable => Volatile.Read(ref _durable) == 1;

    /// <summary>Counts the withdrawals of an acknowledged pin; a state read confirms only the withdrawal that began before it.</summary>
    public long Withdrawals => Volatile.Read(ref _withdrawals);

    /// <summary>Counts every change of the acknowledgement: odd while acknowledged, even otherwise. Redis holds the last one this node published.</summary>
    public long AckGeneration => Volatile.Read(ref _ackGeneration);

    /// <summary>The generation the store last took from this node, or an earlier one; never ahead of <see cref="AckGeneration"/>.</summary>
    public long PublishedGeneration
    {
        get => Volatile.Read(ref _publishedGeneration);
        set => Volatile.Write(ref _publishedGeneration, value);
    }

    internal int SourceCalls => Volatile.Read(ref _sourceCalls);

    /// <summary>True when the call was counted: the pin was not applied before it, so it may still reach the source.</summary>
    public bool TryBeginSource()
    {
        if (Applied)
        {
            return false;
        }

        Interlocked.Increment(ref _sourceCalls);

        // Read again after counting, so a pin applied in between is never missed by the drain.
        if (Applied)
        {
            Interlocked.Decrement(ref _sourceCalls);
            return false;
        }

        return true;
    }

    public void EndSource() => Interlocked.Decrement(ref _sourceCalls);

    /// <summary>Whether a state read succeeded within <paramref name="validity"/>, and no withdrawn acknowledgement is waiting for its read.</summary>
    public bool StateKnown(TimeProvider clock, TimeSpan validity) =>
        Volatile.Read(ref _unconfirmed) == 0 && Volatile.Read(ref _known) == 1 && clock.GetElapsedTime(Volatile.Read(ref _knownAt)) <= validity;

    /// <summary>
    /// A read of the durable state, begun after the withdrawal of generation <paramref name="generation"/> was recorded in the store as number <paramref name="withdrawals"/>,
    /// found no switch: no peer can record one against the old acknowledgement any more, so the source may be used again.
    /// </summary>
    public void ConfirmUnswitched(long withdrawals, long generation)
    {
        lock (_gate)
        {
            if (withdrawals == _withdrawals && generation == _ackGeneration && !Switched)
            {
                Volatile.Write(ref _unconfirmed, 0);
            }
        }
    }

    public void StateRead(TimeProvider clock)
    {
        Volatile.Write(ref _knownAt, clock.GetTimestamp());
        Volatile.Write(ref _known, 1);
    }

    /// <summary>The setting's answer; returns whether the acknowledgement changed.</summary>
    public bool SetPinned(bool pinned)
    {
        lock (_gate)
        {
            _pinned = pinned;
            return Recompute();
        }
    }

    public bool LatchSwitched(bool durable)
    {
        lock (_gate)
        {
            Volatile.Write(ref _switched, 1);
            Volatile.Write(ref _durable, durable ? 1 : 0);
            return Recompute();
        }
    }

    public void SetDurable(bool durable) => Volatile.Write(ref _durable, durable ? 1 : 0);

    /// <summary>Acknowledges once nothing admitted before the pin applied is still running; returns whether it changed.</summary>
    public bool UpdateAck()
    {
        lock (_gate)
        {
            return Recompute();
        }
    }

    private bool Recompute()
    {
        var applied = _pinned || Switched;
        var before = Acked;
        if (before && !applied)
        {
            // A peer may hold the acknowledgement it read and record the switch: the source stays closed until a read made after this shows none, and the flag is set before the source can be admitted.
            _withdrawals++;
            Volatile.Write(ref _unconfirmed, 1);
        }

        Interlocked.Exchange(ref _applied, applied ? 1 : 0);

        // The drain reads the counter after the flag is published; a call that counts after that sees the flag and never counts.
        var acked = applied && (before || Volatile.Read(ref _sourceCalls) == 0);
        Volatile.Write(ref _acked, acked ? 1 : 0);
        if (acked != before)
        {
            Interlocked.Increment(ref _ackGeneration);
        }

        return acked != before;
    }
}
