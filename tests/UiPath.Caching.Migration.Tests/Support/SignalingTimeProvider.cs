using System.Threading.Channels;

namespace UiPath.Caching.Migration.Tests.Support;

internal sealed class SignalingTimeProvider(ManualTimeProvider clock) : TimeProvider
{
    private readonly Channel<ScheduledTimer> _scheduled = Channel.CreateUnbounded<ScheduledTimer>();
    private readonly List<ScheduledTimer> _unmatched = [];

    public SignalingTimeProvider(DateTimeOffset start)
        : this(new ManualTimeProvider(start))
    {
    }

    public override long TimestampFrequency => clock.TimestampFrequency;

    public override long GetTimestamp() => clock.GetTimestamp();

    public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();

    public void Advance(TimeSpan by) => clock.Advance(by);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = clock.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var scheduled = new ScheduledTimer(timer, _scheduled.Writer);
        scheduled.Change(dueTime, period);
        return scheduled;
    }

    public Task WaitForTimerAsync(TimeSpan dueTime, CancellationToken token) => WaitForTimerAsync(delay => delay == dueTime, token);

    public async Task WaitForTimerAsync(Func<TimeSpan, bool> matches, CancellationToken token)
    {
        _unmatched.RemoveAll(timer => !timer.Active);
        var index = _unmatched.FindIndex(timer => matches(timer.DueTime));
        if (index >= 0)
        {
            _unmatched.RemoveAt(index);
            return;
        }

        while (true)
        {
            var timer = await _scheduled.Reader.ReadAsync(token);
            if (!timer.Active)
            {
                continue;
            }

            if (matches(timer.DueTime))
            {
                return;
            }

            _unmatched.Add(timer);
        }
    }

    public async Task WaitForTimerWhileRunningAsync(TimeSpan dueTime, Task operation, CancellationToken token)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token);
        var armed = WaitForTimerAsync(dueTime, waiting.Token);
        await Task.WhenAny(armed, operation);
        if (!armed.IsCompleted)
        {
            await waiting.CancelAsync();
            await operation;
            Assert.Fail("The operation completed before the expected timer was created.");
        }

        await armed;
    }

    public async Task AdvanceUntilAsync(Task completion, TimeSpan step, CancellationToken token)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token);
        while (!completion.IsCompleted)
        {
            if (_unmatched.Count > 0)
            {
                var next = _unmatched[0];
                _unmatched.RemoveAt(0);
                AdvanceTo(next, step);
                continue;
            }

            var scheduled = _scheduled.Reader.ReadAsync(waiting.Token).AsTask();
            if (await Task.WhenAny(completion, scheduled) == completion)
            {
                await waiting.CancelAsync();
                break;
            }

            AdvanceTo(await scheduled, step);
        }

        await completion.WaitAsync(token);
    }

    /// <summary>Runs the timers scheduled as due at once, until <paramref name="completion"/> ends. The clock does not move.</summary>
    public async Task RunDueTimersUntilAsync(Task completion, CancellationToken token)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token);
        while (!completion.IsCompleted)
        {
            var due = WaitForTimerAsync(TimeSpan.Zero, waiting.Token);
            if (await Task.WhenAny(completion, due) == completion)
            {
                await waiting.CancelAsync();
                break;
            }

            await due;
            Advance(TimeSpan.Zero);
        }

        await completion.WaitAsync(token);
    }

    private void AdvanceTo(ScheduledTimer timer, TimeSpan step)
    {
        if (timer.Active)
        {
            Advance(step < timer.DueTime ? timer.DueTime : step);
        }
    }

    private sealed class ScheduledTimer(ITimer timer, ChannelWriter<ScheduledTimer> notifications) : ITimer
    {
        private int _active;

        public bool Active => Volatile.Read(ref _active) != 0;

        public TimeSpan DueTime { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            var changed = timer.Change(dueTime, period);
            Volatile.Write(ref _active, dueTime == Timeout.InfiniteTimeSpan ? 0 : 1);
            if (Active)
            {
                notifications.TryWrite(this);
            }

            return changed;
        }

        public void Dispose()
        {
            Volatile.Write(ref _active, 0);
            timer.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
