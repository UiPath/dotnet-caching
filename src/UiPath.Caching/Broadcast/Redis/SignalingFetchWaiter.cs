namespace UiPath.Caching.Broadcast.Redis;

internal sealed class SignalingFetchWaiter(TimeSpan pollInterval, TimeSpan? notifiedPollInterval = null, TimeProvider? timeProvider = null) : IFetchWaiter
{
    // One late entry may be a doorbell that raced the poll; two in a row, with no doorbell delivering between them, are not.
    private const int MissesToShorten = 2;
    // Notified intervals without an unannounced entry before a delivering doorbell lengthens the waits again.
    private const int QuietIntervalsToRecover = 12;

    private readonly SemaphoreSlim _semaphore = new(0, 1);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    // The doorbell's health: the reader observes it and the connection events reset it, each under this gate.
    private readonly Lock _health = new();
    private volatile bool _disposed;
    private volatile bool _notifyLive;
    // A doorbell of another node arrived since the last observed read.
    private int _rang;
    // Entries of other nodes arrived unannounced while the doorbell read as live: the waits are the poll interval.
    private volatile bool _shortened;
    // A new subscription ended a fallback; the next observed read reports it.
    private bool _resetWhileShortened;
    // Bumped by every new subscription, so a wait that ran under the old one is not counted against it.
    private int _subscription;
    private int _waitSubscription;
    // The reader's own: whether its last idle wait ran its whole interval, and the unannounced entries it has seen.
    private bool _timedOut;
    private int _misses;
    private long _lastMiss;

    public bool ObservesReads => _notifyLive && notifiedPollInterval is { } notified && notified > pollInterval;

    /// <summary>Whether the doorbell is live, so a wait lasts the notified interval.</summary>
    internal bool NotifyLive => _notifyLive;

    /// <summary>Whether a live doorbell missed entries, so a wait lasts the poll interval all the same.</summary>
    internal bool Shortened => _shortened;

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        _timedOut = false;
        _waitSubscription = Volatile.Read(ref _subscription);
        // Only a wait that began with the doorbell live counts: one that began before could not have been woken by it.
        var live = _notifyLive && notifiedPollInterval is not null;
        var woken = await WaitCoreAsync(live && !_shortened ? notifiedPollInterval!.Value : pollInterval, cancellationToken).ConfigureAwait(false);
        _timedOut = live && !woken;
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        // A retry or a disconnected wait: what the next read finds says nothing about the doorbell.
        _timedOut = false;
        return WaitCoreAsync(timeout, cancellationToken);
    }

    /// <summary>While live, a wait lasts the notified interval; losing it wakes the wait in progress so the next one is the short one.</summary>
    public void SetNotifyLive(bool live)
    {
        if (_notifyLive == live)
        {
            return;
        }

        if (live)
        {
            // A subscription taken again has missed nothing yet.
            lock (_health)
            {
                _subscription++;
                _misses = 0;
                if (_shortened)
                {
                    _shortened = false;
                    _resetWhileShortened = true;
                }
            }
        }

        _notifyLive = live;
        if (!live)
        {
            Signal();
        }
    }

    /// <summary>A doorbell of another node: ends the wait like <see cref="Signal"/>, and shows that doorbells arrive.</summary>
    public void Ring()
    {
        Volatile.Write(ref _rang, 1);
        Signal();
    }

    public DoorbellHealth ObserveRead(bool fromOthers)
    {
        var timedOut = _timedOut;
        _timedOut = false;
        var rang = Interlocked.Exchange(ref _rang, 0) != 0;
        lock (_health)
        {
            if (_resetWhileShortened)
            {
                _resetWhileShortened = false;
                return DoorbellHealth.Recovered;
            }

            if (!ObservesReads)
            {
                return DoorbellHealth.Unchanged;
            }

            // A doorbell arrived, whatever its read found: doorbells reach this node.
            if (rang)
            {
                return Rang();
            }

            if (!timedOut || !fromOthers || _waitSubscription != _subscription)
            {
                return DoorbellHealth.Unchanged;
            }

            // The wait ran its whole interval under this subscription and nothing rang, yet another node's entry was there.
            _lastMiss = _clock.GetTimestamp();
            if (_shortened || ++_misses < MissesToShorten)
            {
                return DoorbellHealth.Unchanged;
            }

            _shortened = true;
            return DoorbellHealth.Missed;
        }
    }

    public void Signal()
    {
        // A pending wake-up is the common case under churn; returning here avoids a thrown SemaphoreFullException per signal.
        if (_disposed || _semaphore.CurrentCount != 0)
        {
            return;
        }
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // Another signal raced in after the check; collapsing to one is the desired behavior.
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently with Signal — nothing to wake up.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _semaphore.Dispose();
    }

    /// <summary>Under <see cref="_health"/>: a doorbell arrived, so the count restarts, and a fallback ends once no entry came unannounced for a while.</summary>
    private DoorbellHealth Rang()
    {
        if (!_shortened)
        {
            _misses = 0;
            return DoorbellHealth.Unchanged;
        }

        if (_clock.GetElapsedTime(_lastMiss) < notifiedPollInterval!.Value * QuietIntervalsToRecover)
        {
            return DoorbellHealth.Unchanged;
        }

        _misses = 0;
        _shortened = false;
        return DoorbellHealth.Recovered;
    }

    /// <summary>Whether the wait was woken rather than run to its end.</summary>
    private async Task<bool> WaitCoreAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return true;
        }
        try
        {
            if (timeProvider is null || timeProvider == TimeProvider.System)
            {
                return await _semaphore.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }

            return await WaitOnClockAsync(timeout, timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently with Wait — treat as a normal wake-up so the fetch loop can observe disposal.
            return true;
        }
    }

    private async Task<bool> WaitOnClockAsync(TimeSpan timeout, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (timeout == TimeSpan.Zero)
        {
            return await _semaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false);
        }

        using var timeoutCts = new CancellationTokenSource(timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await _semaphore.WaitAsync(linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The clock-driven timeout elapsed, which is a normal return.
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
