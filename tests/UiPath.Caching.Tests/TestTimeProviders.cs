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
    private TimeSpan _elapsed;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

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
        timer.Change(dueTime, period);
        return timer;
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

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period;

        public TimeSpan Due { get; private set; } = TimeSpan.MaxValue;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                _period = period;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    Due = TimeSpan.MaxValue;
                    return true;
                }

                Due = owner._elapsed + dueTime;
                owner._timers.Add(this);
                return true;
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
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
