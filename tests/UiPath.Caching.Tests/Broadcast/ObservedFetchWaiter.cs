using System.Reflection;
using System.Threading.Channels;

namespace UiPath.Caching.Tests.Broadcast;

internal sealed class ObservedFetchWaiter(SignalingFetchWaiter inner) : IFetchWaiter
{
    private readonly SignalingFetchWaiter _inner = inner;
    private readonly object _gate = new();
    private readonly List<(int Version, TaskCompletionSource Done)> _nextIdle = [];
    private readonly Channel<bool> _idle = Channel.CreateUnbounded<bool>();

    private int _requested;

    public ObservedFetchWaiter(TimeSpan pollInterval, TimeProvider clock)
        : this(new SignalingFetchWaiter(pollInterval, timeProvider: clock))
    {
    }

    public bool ObservesReads => _inner.ObservesReads;

    public static ObservedFetchWaiter Attach(RedisStreamsTopic<ICacheEvent> topic)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var subscriber = typeof(RedisStreamsTopic<ICacheEvent>).GetField("_subscriber", fields)!.GetValue(topic)!;
        var field = subscriber.GetType().GetField("_waiter", fields)!;
        var observer = new ObservedFetchWaiter((SignalingFetchWaiter)field.GetValue(subscriber)!);
        field.SetValue(subscriber, observer);
        return observer;
    }

    public Task NextIdleAsync()
    {
        lock (_gate)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _nextIdle.Add((++_requested, done));
            return done.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
    }

    public async Task WaitForIdleAsync() => await _idle.Reader.ReadAsync(TestContext.Current.CancellationToken);

    public Task ReadAsync()
    {
        lock (_gate)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _nextIdle.Add((++_requested, done));
            _inner.Signal();
            return done.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
    }

    public Task WaitAsync(CancellationToken cancellationToken) => BeginWait(() => _inner.WaitAsync(cancellationToken));

    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => BeginWait(() => _inner.WaitAsync(timeout, cancellationToken));

    public void Signal() => _inner.Signal();

    public DoorbellHealth ObserveRead(bool fromOthers) => _inner.ObserveRead(fromOthers);

    public void Dispose() => _inner.Dispose();

    private Task BeginWait(Func<Task> start)
    {
        var version = Volatile.Read(ref _requested);
        var wait = start();
        if (!wait.IsCompleted)
        {
            _idle.Writer.TryWrite(true);
            lock (_gate)
            {
                _nextIdle.RemoveAll(item => item.Version <= version && item.Done.TrySetResult());
            }
        }
        return wait;
    }
}
