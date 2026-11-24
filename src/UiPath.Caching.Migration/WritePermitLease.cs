using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>The permit of one logical write, kept across its reruns and settled once, when the call returns.</summary>
internal sealed class WritePermitLease(WritePermits permits, TimeSpan skewBound, TimeSpan fallbackHold)
{
    private bool _held;
    private DateTimeOffset? _deadline;
    private Func<bool>? _connectionChanged;
    private DateTimeOffset _now;

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (!_held)
        {
            await permits.WaitAsync(cancellationToken).ConfigureAwait(false);
            _held = true;
        }
    }

    /// <summary>A marked set got no reply and may still apply. The script refuses it after its deadline; a transaction has no deadline, so it is waited out until the connection that carried it has changed.</summary>
    public void Unanswered(DateTimeOffset deadline, DateTimeOffset now, KeyMoveData data)
    {
        _now = now;
        _deadline = _deadline is { } held && held > deadline ? held : deadline;
        if (!data.Scripts.UsesScripts)
        {
            var events = data.ConnectionEvents();
            _connectionChanged = () => data.ConnectionEvents() != events;
        }
    }

    public void Settle()
    {
        if (!_held)
        {
            return;
        }

        _held = false;
        if (_deadline is not { } deadline)
        {
            permits.Release();
        }
        else if (_connectionChanged is { } changed)
        {
            permits.ReleaseWhen(changed, _now + fallbackHold);
        }
        else
        {
            permits.ReleaseAt(deadline + skewBound);
        }
    }
}
