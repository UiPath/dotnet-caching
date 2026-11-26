using System.Threading.Channels;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

internal sealed class ObservedClock(ManualTimeProvider manual) : TimeProvider
{
    private readonly Channel<(TimeSpan Delay, long Due)> _timers = Channel.CreateUnbounded<(TimeSpan, long)>();

    public ObservedClock(DateTimeOffset start)
        : this(new ManualTimeProvider(start))
    {
    }

    public ManualTimeProvider Manual { get; } = manual;

    public TimeSpan UtcOffset { get; set; }

    public override long TimestampFrequency => Manual.TimestampFrequency;

    public override long GetTimestamp() => Manual.GetTimestamp();

    public override DateTimeOffset GetUtcNow() => Manual.GetUtcNow() + UtcOffset;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var due = GetTimestamp() + dueTime.Ticks;
        var timer = Manual.CreateTimer(callback, state, dueTime, period);
        _timers.Writer.TryWrite((dueTime, due));
        return timer;
    }

    public ValueTask<(TimeSpan Delay, long Due)> NextAlarmAsync(CancellationToken cancellationToken) => _timers.Reader.ReadAsync(cancellationToken);

    public async ValueTask<TimeSpan> NextTimerAsync(CancellationToken cancellationToken) =>
        (await NextAlarmAsync(cancellationToken)).Delay;

    public async Task<TimeSpan> NextTimerAsync(Func<TimeSpan, bool> matches, CancellationToken cancellationToken)
    {
        while (true)
        {
            var due = await NextTimerAsync(cancellationToken);
            if (matches(due))
            {
                return due;
            }
        }
    }

    public async Task AdvanceNextTimerAsync(CancellationToken cancellationToken) =>
        Advance(await NextTimerAsync(cancellationToken));

    public Task<TimeSpan> NextTimerWhileRunningAsync(Task operation, CancellationToken cancellationToken) =>
        TimerSignal.ReadWhileRunningAsync(token => NextTimerAsync(token).AsTask(), operation, cancellationToken);

    public Task<TimeSpan> NextTimerWhileRunningAsync(
        Func<TimeSpan, bool> matches, Task operation, CancellationToken cancellationToken) =>
        TimerSignal.ReadWhileRunningAsync(token => NextTimerAsync(matches, token), operation, cancellationToken);

    public async Task AdvanceNextTimerWhileRunningAsync(Task operation, CancellationToken cancellationToken) =>
        Advance(await NextTimerWhileRunningAsync(operation, cancellationToken));

    public void Advance(TimeSpan elapsed) => Manual.Advance(elapsed);
}
