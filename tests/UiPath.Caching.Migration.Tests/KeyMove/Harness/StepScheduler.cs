namespace UiPath.Caching.Migration.Tests.KeyMove.Harness;

// Runs actors as real tasks and lets exactly one of them take its next step at a time: an actor parks at every schedule point, and the driver picks which parked actor goes on.
internal sealed class StepScheduler
{
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif
    private readonly Dictionary<int, TaskCompletionSource> _parked = [];
    private readonly SemaphoreSlim _changed = new(0);
    private readonly AsyncLocal<int> _actor = new();

    // The schedule hook: an actor with an id parks, anything else (setup, the final read) goes through.
    public Task ParkAsync()
    {
        var id = _actor.Value;
        if (id == 0)
        {
            return Task.CompletedTask;
        }

        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _parked[id] = go;
        }

        _changed.Release();
        return go.Task;
    }

    /// <returns>For each decision, the option chosen and how many there were.</returns>
    public async Task<List<(int Chosen, int Options)>> RunAsync(IReadOnlyList<Func<Task>> actors, Func<int, int, int> choose)
    {
        var tasks = StartActors(actors);
        var trace = new List<(int, int)>();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var ready = ParkedActors(tasks);
            if (ready is null)
            {
                break;
            }

            if (ready.Length == 0)
            {
                if (!await _changed.WaitAsync(TimeSpan.FromSeconds(30)))
                {
                    throw new TimeoutException("An actor neither parked nor finished.");
                }

                continue;
            }

            var pick = Math.Clamp(choose(trace.Count, ready.Length), 0, ready.Length - 1);
            trace.Add((pick, ready.Length));
            TaskCompletionSource go;
            lock (_gate)
            {
                go = _parked[ready[pick]];
                _parked.Remove(ready[pick]);
            }

            go.SetResult();
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The schedule did not end.");
            }
        }

        await Task.WhenAll(tasks);
        return trace;
    }

    private Task[] StartActors(IReadOnlyList<Func<Task>> actors)
    {
        var tasks = new Task[actors.Count];
        for (var i = 0; i < actors.Count; i++)
        {
            var run = actors[i];
            _actor.Value = i + 1;
            tasks[i] = Task.Run(run);
            _ = tasks[i].ContinueWith(_ => _changed.Release(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        _actor.Value = 0;
        return tasks;
    }

    private int[]? ParkedActors(Task[] tasks)
    {
        lock (_gate)
        {
            var open = Enumerable.Range(0, tasks.Length).Where(i => !tasks[i].IsCompleted).ToArray();
            if (open.Length == 0)
            {
                return null;
            }

            return open.All(i => _parked.ContainsKey(i + 1)) ? [.. open.Select(i => i + 1).Order()] : [];
        }
    }
}
