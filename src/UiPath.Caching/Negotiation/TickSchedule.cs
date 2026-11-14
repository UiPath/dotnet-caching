namespace UiPath.Caching.Negotiation;

/// <summary>
/// When this node advertises and when it reads the node list, each on its own interval. A read is always preceded by an advertisement, and the
/// advertisement that comes with it counts as the next one.
/// </summary>
internal sealed class TickSchedule
{
    private readonly TimeSpan _advertise;
    private readonly TimeSpan _view;
    private readonly Func<double> _jitter;
    private TimeSpan _advertiseDue;
    private TimeSpan _viewDue;

    public TickSchedule(TimeSpan advertise, TimeSpan view, Func<double>? jitter = null)
    {
        _advertise = advertise;
        _view = view;
        _jitter = jitter ?? (() => 0.8 + (Random.Shared.NextDouble() * 0.4));
        Reschedule(TimeSpan.Zero, advertisement: true, nodeList: true);
    }

    public TimeSpan Wait(TimeSpan now)
    {
        var due = _advertiseDue < _viewDue ? _advertiseDue : _viewDue;
        return due > now ? due - now : TimeSpan.Zero;
    }

    /// <summary>What is due at <paramref name="now"/>, and counts it done; a due node list read brings the advertisement with it.</summary>
    public (bool Advertise, bool Refresh) Take(TimeSpan now)
    {
        if (now >= _viewDue)
        {
            Reschedule(now, advertisement: true, nodeList: true);
            return (true, true);
        }

        if (now >= _advertiseDue)
        {
            Reschedule(now, advertisement: true, nodeList: false);
            return (true, false);
        }

        return (false, false);
    }

    private void Reschedule(TimeSpan now, bool advertisement, bool nodeList)
    {
        if (advertisement)
        {
            _advertiseDue = now + (_advertise * _jitter());
        }

        if (nodeList)
        {
            _viewDue = now + (_view * _jitter());
        }
    }
}
