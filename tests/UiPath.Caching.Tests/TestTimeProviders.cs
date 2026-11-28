using Microsoft.Extensions.Internal;

namespace UiPath.Caching.Tests;

/// <summary>A <see cref="TimeProvider"/> stopped at one instant.</summary>
public sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>A <see cref="TimeProvider"/> over a substituted <see cref="ISystemClock"/>, so a test can hand the same clock to a <c>MemoryCache</c> it builds; its timestamps follow the same clock.</summary>
public sealed class SystemClockTimeProvider(ISystemClock clock) : TimeProvider
{
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => clock.UtcNow;

    public override long GetTimestamp() => clock.UtcNow.UtcTicks;
}

/// <summary>A <see cref="TimeProvider"/> that moves only when a test advances it, firing the timers that fall due.</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly Fakes.TestSignal _timerCreated = new();
    private readonly List<(int Count, TaskCompletionSource Done)> _timerWaiters = [];
    private readonly List<(Func<bool> Condition, TaskCompletionSource Done)> _conditions = [];
    private TimeSpan _elapsed;
    private int _created;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ManualTimeProvider() : this(DateTimeOffset.UnixEpoch) { }

    public event Action? TimerScheduled;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>How many timers were ever created, so a test can tell a new timer from one that already existed.</summary>
    public int TimersCreated => Volatile.Read(ref _created);

    /// <summary>How many timers are waiting to fire.</summary>
    public int TimerCount
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count;
            }
        }
    }

    /// <summary>Whether a timer is waiting to fire.</summary>
    public bool HasTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count > 0;
            }
        }
    }

    public TimeSpan NextTimerDueIn
    {
        get
        {
            lock (_gate)
            {
                return _timers.Min(timer => timer.Due) - _elapsed;
            }
        }
    }

    public async Task WaitForTimerAsync(CancellationToken token)
    {
        var scheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnTimerScheduled() => scheduled.TrySetResult();
        TimerScheduled += OnTimerScheduled;
        try
        {
            if (!HasTimers)
            {
                await scheduled.Task.WaitAsync(token);
            }
        }
        finally
        {
            TimerScheduled -= OnTimerScheduled;
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return start + _elapsed;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _elapsed.Ticks;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            timer.Schedule(dueTime, period);
            Interlocked.Increment(ref _created);
            WakeWaiters();
        }
        Armed();
        return timer;
    }

    /// <summary>Waits until more than <paramref name="count"/> timers were ever created.</summary>
    public Task WhenTimersCreatedAsync(int count) =>
        _timerCreated.UntilAsync(() => TimersCreated > count, TestContext.Current.CancellationToken);

    public Task WhenTimerScheduledAsync() =>
        _timerCreated.UntilAsync(() => HasTimers, TestContext.Current.CancellationToken);

    public bool HasTimerDueIn(TimeSpan remaining)
    {
        lock (_gate)
        {
            return _timers.Any(timer => timer.Due - _elapsed == remaining);
        }
    }

    public Task WhenTimerAsync(Func<bool> condition)
    {
        lock (_gate)
        {
            if (condition())
            {
                return Task.CompletedTask;
            }
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _conditions.Add((condition, done));
            return done.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
    }

    public Task WaitForTimersAsync(int count)
    {
        lock (_gate)
        {
            if (_created >= count)
            {
                return Task.CompletedTask;
            }

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _timerWaiters.Add((count, done));
            return done.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Advances to the earliest pending timer; <c>false</c> when none is pending.</summary>
    public bool TryAdvanceToNextTimer()
    {
        TimeSpan by;
        lock (_gate)
        {
            if (_timers.Count == 0)
            {
                return false;
            }

            by = _timers.Min(timer => timer.Due) - _elapsed;
        }

        Advance(by);
        return true;
    }

    public void Advance(TimeSpan by)
    {
        ManualTimer[] due;
        lock (_gate)
        {
            _elapsed += by;
            due = [.. _timers.Where(t => t.Due <= _elapsed)];
            foreach (var timer in due)
            {
                timer.Fired(_elapsed);
            }
        }

        foreach (var timer in due)
        {
            timer.Run();
        }
    }

    public async Task WaitForTimersAsync(Func<bool> condition)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (condition())
                {
                    return;
                }

                changed = _changed.Task;
            }

            await changed.WaitAsync(TestContext.Current.CancellationToken);
        }
    }

    private void Armed()
    {
        TimerScheduled?.Invoke();
        _timerCreated.Pulse();
        Broadcast.OperationSignals.Shared.Completed();
    }

    private void WakeWaiters()
    {
        for (var i = _conditions.Count - 1; i >= 0; i--)
        {
            if (_conditions[i].Condition())
            {
                _conditions[i].Done.TrySetResult();
                _conditions.RemoveAt(i);
            }
        }
        for (var i = _timerWaiters.Count - 1; i >= 0; i--)
        {
            if (_timerWaiters[i].Count <= _created)
            {
                _timerWaiters[i].Done.TrySetResult();
                _timerWaiters.RemoveAt(i);
            }
        }
    }

    private void Changed()
    {
        WakeWaiters();
        var previous = _changed;
        _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period;

        public TimeSpan Due { get; private set; } = TimeSpan.MaxValue;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Schedule(dueTime, period);
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                owner.Armed();
            }
            return true;
        }

        /// <summary>Arms the timer under the provider's lock and wakes the waiters that check state, not creation.</summary>
        public void Schedule(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                _period = period;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    Due = TimeSpan.MaxValue;
                }
                else
                {
                    Due = owner._elapsed + dueTime;
                    owner._timers.Add(this);
                }

                owner.Changed();
            }
        }

        /// <summary>Under the provider's lock: reschedules a periodic timer, retires a one-shot one.</summary>
        public void Fired(TimeSpan now)
        {
            owner._timers.Remove(this);
            if (_period > TimeSpan.Zero)
            {
                Due = now + _period;
                owner._timers.Add(this);
            }
            else
            {
                Due = TimeSpan.MaxValue;
            }
        }

        public void Run() => callback(state);

        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                owner.Changed();
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class TimeProviderClock(TimeProvider clock) : ISystemClock
{
    public DateTimeOffset UtcNow => clock.GetUtcNow();
}
