using System.Collections.Concurrent;

namespace UiPath.Caching.Migration;

/// <summary>The bounded set of <c>migration.move.retry</c>: a trailing step that failed is retried with backoff until it succeeds, its write's life has passed, or the node stops.</summary>
/// <param name="exhausted">Told of a key whose retry gave up; a failure of the callback never stops the retries.</param>
/// <param name="background">False leaves every attempt to <see cref="RunDueAsync"/>, which a test drives.</param>
/// <param name="dropped">Told of a key whose waiting retry was dropped at stop.</param>
internal sealed class MoveRetrySet(TimeProvider clock, KeyMoveOptions options, Action<string> exhausted, bool background = true, Action<string>? dropped = null) : IDisposable
{
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, KeyGroup> _groups = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource? _idle;
    private Task? _loop;
    private TaskCompletionSource? _shutdown;
    private int _active;

    private enum Registration
    {
        Accepted,
        Stopped,
        Full,
    }

    public int Count => _entries.Count;

    /// <summary>The keys with an attempt that has not returned or been dropped.</summary>
    public IReadOnlyCollection<string> OutstandingKeys
    {
        get
        {
            lock (_gate)
            {
                return [.. _groups.Keys];
            }
        }
    }

    public bool IsPending(string key) => _entries.ContainsKey(key);

    /// <summary>
    /// Completes when every attempt of the key has returned or been dropped: the ones waiting, the one running, and one that a newer write superseded while it ran.
    /// The same task serves the key while any is outstanding, so a newer write's hold never replaces the protection a running attempt still needs. Null when none is outstanding.
    /// </summary>
    public Task? SettledAsync(string key)
    {
        lock (_gate)
        {
            return _groups.TryGetValue(key, out var group) ? group.Done.Task : null;
        }
    }

    /// <param name="attempt">True when the step is done and nothing is left to retry.</param>
    public void Add(string key, DateTimeOffset giveUpAt, Func<CancellationToken, Task<bool>> attempt)
    {
        var entry = new Entry(key, giveUpAt, attempt, options.RetryInitial, clock.GetTimestamp() + Ticks(options.RetryInitial));
        switch (Register(entry))
        {
            case Registration.Stopped:
                Drop(key);
                return;
            case Registration.Full:
                Exhaust(key);
                return;
        }
    }

    /// <summary>A newer write of the key on this node does every step again, so the old trailing step must not run after it; one already in flight runs to its end.</summary>
    public void Supersede(string key)
    {
        lock (_gate)
        {
            if (_entries.TryRemove(key, out var entry))
            {
                Retire(entry);
            }
        }
    }

    public void Clear()
    {
        foreach (var key in _entries.Keys)
        {
            Supersede(key);
        }
    }

    /// <summary>One attempt for every entry that is due, or past its life; tests call it directly.</summary>
    public async Task RunDueAsync(bool all = false)
    {
        foreach (var pair in _entries.ToArray())
        {
            var entry = pair.Value;
            if (!TryBeginAttempt(entry, all, out var token))
            {
                continue;
            }

            var done = false;
            try
            {
                done = await entry.Attempt(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                done = false;
            }
            finally
            {
                Finish(pair.Key, entry, done);
            }
        }
    }

    /// <summary>No attempt is scheduled any more: what is waiting is dropped and counted as superseded, so the write stays Unknown, which is allowed. An attempt already running is not abandoned: its key settles when it returns.</summary>
    public void Stop() => _ = StopAsync();

    /// <summary>Stops scheduling retries and waits for in-flight attempts and cancellation callbacks before disposing the token source.</summary>
    public Task StopAsync()
    {
        if (!TryBeginStop(out var loop, out var attempts, out var shutdown))
        {
            return shutdown.Task;
        }

        var cancellation = _stop.CancelAsync();
        DropWaiting();
        _ = DisposeWhenDoneAsync(loop, attempts, cancellation, shutdown);
        return shutdown.Task;
    }

    public void Dispose() => Stop();

    private bool TryBeginStop(out Task loop, out Task attempts, out TaskCompletionSource shutdown)
    {
        lock (_gate)
        {
            loop = _loop ?? Task.CompletedTask;
            attempts = Task.CompletedTask;
            if (_shutdown is { } started)
            {
                shutdown = started;
                return false;
            }

            shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdown = shutdown;
            if (_active > 0)
            {
                _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                attempts = _idle.Task;
            }

            return true;
        }
    }

    private async Task DisposeWhenDoneAsync(Task loop, Task attempts, Task cancellation, TaskCompletionSource shutdown)
    {
        try
        {
            await Task.WhenAll(loop, attempts, cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The loop was detached. Only a cancellation callback's failure belongs to the stop caller.
        }
        finally
        {
            _stop.Dispose();
            if (cancellation.Exception is { } failure)
            {
                shutdown.TrySetException(failure.InnerExceptions);
            }
            else
            {
                shutdown.TrySetResult();
            }
        }
    }

    private void DropWaiting()
    {
        foreach (var key in _entries.Keys)
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var entry) && !entry.Active && _entries.TryRemove(key, out _))
                {
                    Retire(entry);
                    Drop(key);
                }
            }
        }
    }

    private Registration Register(Entry entry)
    {
        // Reserve capacity and insert under the same lock.
        lock (_gate)
        {
            if (_shutdown is not null)
            {
                return Registration.Stopped;
            }

            _entries.TryGetValue(entry.Key, out var replaced);
            if (replaced is null && _entries.Count >= options.RetryCapacity)
            {
                return Registration.Full;
            }

            _entries[entry.Key] = entry;
            Enter(entry);
            if (replaced is not null)
            {
                Retire(replaced);
            }

            if (background && _loop is null or { IsCompleted: true })
            {
                var token = _stop.Token;
                _loop = Task.Run(() => LoopAsync(token), CancellationToken.None);
            }

            return Registration.Accepted;
        }
    }

    private bool TryBeginAttempt(Entry entry, bool all, out CancellationToken token)
    {
        token = default;
        if (clock.GetUtcNow() >= entry.GiveUpAt)
        {
            Expire(entry);
            return false;
        }

        if (!all && clock.GetTimestamp() < entry.Due)
        {
            return false;
        }

        lock (_gate)
        {
            if (_shutdown is not null || entry.Removed || entry.Active)
            {
                return false;
            }

            token = _stop.Token;
            entry.Active = true;
            _active++;
            return true;
        }
    }

    private void Expire(Entry entry)
    {
        lock (_gate)
        {
            if (!entry.Active && _entries.TryRemove(new KeyValuePair<string, Entry>(entry.Key, entry)))
            {
                Retire(entry);
                Exhaust(entry.Key);
            }
        }
    }

    // Under the gate: the entry is out of the set; its key stops waiting for it now, or when its running attempt returns.
    private void Retire(Entry entry)
    {
        entry.Removed = true;
        if (!entry.Active)
        {
            Leave(entry);
        }
    }

    private void Enter(Entry entry)
    {
        if (!_groups.TryGetValue(entry.Key, out var group))
        {
            group = new KeyGroup();
            _groups[entry.Key] = group;
        }

        group.Outstanding++;
        entry.Group = group;
    }

    private void Leave(Entry entry)
    {
        if (entry.Group is not { } group)
        {
            return;
        }

        entry.Group = null;
        if (--group.Outstanding == 0)
        {
            group.Done.TrySetResult();
            if (_groups.TryGetValue(entry.Key, out var held) && ReferenceEquals(held, group))
            {
                _groups.Remove(entry.Key);
            }
        }
    }

    private void Finish(string key, Entry entry, bool done)
    {
        lock (_gate)
        {
            entry.Active = false;
            _active--;
            if (done || _shutdown is not null || entry.Removed)
            {
                _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
                entry.Removed = true;
                Leave(entry);
                if (!done && _shutdown is not null)
                {
                    Drop(key);
                }
            }
            else
            {
                entry.Backoff = TimeSpan.FromTicks(Math.Min(entry.Backoff.Ticks * 2, options.RetryMax.Ticks));
                entry.Due = clock.GetTimestamp() + Ticks(entry.Backoff);
            }

            if (_active == 0)
            {
                _idle?.TrySetResult();
                _idle = null;
            }
        }
    }

    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * clock.TimestampFrequency);

    private void Drop(string key)
    {
        try
        {
            dropped?.Invoke(key);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _ = ex;
        }
    }

    // The callback belongs to the app: its failure is swallowed so it cannot end the loop.
    private void Exhaust(string key)
    {
        try
        {
            exhausted(key);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _ = ex;
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(options.RetryInitial, clock, token).ConfigureAwait(false);
                try
                {
                    await RunDueAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
                {
                    // Whatever one pass threw, the next one runs.
                    _ = ex;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped: what is left is the residue of a given-up step.
        }
    }

    private sealed class KeyGroup
    {
        public int Outstanding { get; set; }

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Entry(string key, DateTimeOffset giveUpAt, Func<CancellationToken, Task<bool>> attempt, TimeSpan backoff, long due)
    {
        public string Key { get; } = key;

        public KeyGroup? Group { get; set; }

        public DateTimeOffset GiveUpAt { get; } = giveUpAt;

        public Func<CancellationToken, Task<bool>> Attempt { get; } = attempt;

        public TimeSpan Backoff { get; set; } = backoff;

        public long Due { get; set; } = due;

        public bool Active { get; set; }

        public bool Removed { get; set; }
    }
}
