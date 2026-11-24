using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// Counts the calls to a target, the moves among them, and what a test has asked it to do.
internal sealed class CountingTarget(IMoveTarget inner) : IMoveTarget
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _calls = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _moved = new();

    public int Total => _calls.Values.Sum();

    public int TotalMoved => _moved.Values.Sum();

    public string CacheId => inner.CacheId;

    public bool NoOrigin => inner.NoOrigin;

    public Func<string, int, Task<MoveResult?>>? Before { get; init; }

    public int Calls(string key) => _calls.GetValueOrDefault(key);

    public int Moved(string key) => _moved.GetValueOrDefault(key);

    public ValueTask<DryRunProbe> ProbeAsync(string key, TimeSpan minTtl, bool decode, CancellationToken cancellationToken) => inner.ProbeAsync(key, minTtl, decode, cancellationToken);

    public async ValueTask<MoveResult> MoveAsync(string key, TimeSpan minTtl, string identity, IMoveOwnership ownership, CancellationToken cancellationToken)
    {
        var n = _calls.AddOrUpdate(key, 1, (_, c) => c + 1);
        if (Before is not null && await Before(key, n) is { } answer)
        {
            return answer;
        }

        var result = await inner.MoveAsync(key, minTtl, identity, ownership, cancellationToken);
        if (result.Moved)
        {
            _moved.AddOrUpdate(key, 1, (_, c) => c + 1);
        }

        return result;
    }
}

// Serves the real units with their servers replaced, so a test can record or break the scans.
internal sealed class ProxyUnits(IScanUnits inner, Func<ScanUnit, ScanUnit> map) : IScanUnits
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ScanUnit> _mapped = new();

    public async Task<IReadOnlyList<ScanUnit>> ListAsync(CancellationToken cancellationToken) => [.. (await inner.ListAsync(cancellationToken)).Select(u => _mapped.GetOrAdd(u.Id, _ => map(u)))];
}

// Does a writer's work at the first publish of the mover, between its capture and its publish.
internal sealed class WriterAtPublish(Func<Task> write) : ISchedulePoint
{
    private int _done;

    public async ValueTask AtAsync(SchedulePointStep step)
    {
        if (step == SchedulePointStep.Publish && Interlocked.Exchange(ref _done, 1) == 0)
        {
            await write();
        }
    }
}

// Holds the first promotion that reaches a step until a test lets it go, so a test can change the world in the middle of one key.
internal sealed class PausingSchedule(SchedulePointStep target) : ISchedulePoint
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _done;

    public Task ReachedAsync => _reached.Task;

    public void Release() => _released.TrySetResult();

    public async ValueTask AtAsync(SchedulePointStep step)
    {
        if (step == target && Interlocked.Exchange(ref _done, 1) == 0)
        {
            _reached.TrySetResult();
            await _released.Task;
        }
    }
}

// Tells a test when a key's promotion has returned, and with what.
internal sealed class DoneTarget(IMoveTarget inner) : IMoveTarget
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<MoveResult> _results = new();

    public IReadOnlyCollection<MoveResult> Results => _results;

    public string CacheId => inner.CacheId;

    public bool NoOrigin => inner.NoOrigin;

    public ValueTask<DryRunProbe> ProbeAsync(string key, TimeSpan minTtl, bool decode, CancellationToken cancellationToken) => inner.ProbeAsync(key, minTtl, decode, cancellationToken);

    public async ValueTask<MoveResult> MoveAsync(string key, TimeSpan minTtl, string identity, IMoveOwnership ownership, CancellationToken cancellationToken)
    {
        var result = await inner.MoveAsync(key, minTtl, identity, ownership, cancellationToken);
        _results.Enqueue(result);
        return result;
    }
}
