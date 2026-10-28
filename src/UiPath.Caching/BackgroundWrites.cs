using UiPath.Caching.Telemetry;

namespace UiPath.Caching;

/// <summary>Detached inner-tier writes, at most <c>limit</c> at once, in issue order per key with the awaited ones.</summary>
internal sealed class BackgroundWrites(int limit, TimeSpan drainTimeout, string cacheName, ILogger logger, ICachingTelemetryProvider telemetry, TimeProvider clock, Action<string>? onIssued = null)
{
    private const string FailedEvent = "cache.background_write.failed";
    private const int Stripes = 1024;
    private const long Untracked = -1;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Tail> _tails = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    // Changes per stripe of keys, issued or applied locally; a fill from a read started before one would put back the older value.
    // Allocated, and the order tracked, from the first background write on; a cache that never takes one pays nothing.
    private long[] _changes = [];
    private Lock[] _stripes = [];
    private volatile bool _active;
    private int _inFlight;
    private bool _closed;
    private bool _drained;
    private TaskCompletionSource? _idle;
    private Task? _drain;

    public bool Active => _active;

    internal int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>Tracks the awaited writes from now on, for a cache that must know the order its writes landed in even when it takes no background write.</summary>
    public void Activate()
    {
        lock (_gate)
        {
            ActivateLocked();
        }
    }

    /// <summary>False when no place is left or the cache is closing; the caller then writes in the foreground.</summary>
    public bool TryReserve()
    {
        lock (_gate)
        {
            if (_closed || _inFlight >= limit)
            {
                return false;
            }

            ActivateLocked();
            _inFlight++;
            return true;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            ReleaseLocked();
        }
    }

    /// <summary>Makes a reserved write's local write and starts its inner one; returns what <paramref name="local"/> returned.</summary>
    public bool Run<TState>(string key, TState state, Func<TState, bool> local, Func<TState, CancellationToken, ValueTask> work)
    {
        var mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? previous;
        Tail started;
        long issued;
        lock (_gate)
        {
            if (!_tails.TryGetValue(key, out var tail))
            {
                _tails[key] = tail = new Tail();
            }

            if (tail.Awaited is { Count: > 0 } awaited)
            {
                foreach (var each in awaited)
                {
                    (each.Superseded ??= new(StringComparer.Ordinal)).Add(key);
                }

                previous = Task.WhenAll(tail.Background is null ? awaited.Select(static t => t.Task) : awaited.Select(static t => t.Task).Append(tail.Background.Task));
            }
            else
            {
                previous = tail.Background?.Task;
            }
            tail.Background = mine;
            tail.Awaited = null;
            started = tail;
            issued = ++tail.Issued;
            Interlocked.Increment(ref _changes[Stripe(key)]);
        }

        try
        {
            onIssued?.Invoke(key);

            // Two writes of the key can reach the gate out of issue order; the later one's value stands.
            lock (started.Gate)
            {
                if (issued < started.Applied)
                {
                    return true;
                }

                started.Applied = issued;
                return Apply(key, state, local);
            }
        }
        finally
        {
            _ = ExecuteAsync(key, mine, previous, state, work);
        }
    }

    /// <summary>Places an awaited write of the key after the background writes issued before it.</summary>
    public Turn Enter(string key)
    {
        if (!_active)
        {
            return default;
        }

        var mine = new Awaited();
        Task? previous;
        lock (_gate)
        {
            previous = EnterLocked(key, mine);
        }

        return new Turn(this, key, null, mine, previous ?? Task.CompletedTask);
    }

    public Turn Enter(IEnumerable<string> keys)
    {
        if (!_active)
        {
            return default;
        }

        var all = keys as string[] ?? keys.ToArray();
        var mine = new Awaited();
        List<Task>? pending = null;
        lock (_gate)
        {
            foreach (var key in all)
            {
                if (EnterLocked(key, mine) is { } previous)
                {
                    (pending ??= []).Add(previous);
                }
            }
        }

        return new Turn(this, null, all, mine, pending is null ? Task.CompletedTask : Task.WhenAll(pending));
    }

    /// <summary>Stops taking writes and waits up to the drain timeout for those running, then cancels them.</summary>
    public Task DrainAsync()
    {
        lock (_gate)
        {
            return _drain ??= DrainOnceAsync();
        }
    }

    public void Drain() => DrainAsync().GetAwaiter().GetResult();

    /// <summary>Taken before an inner read whose value <see cref="Fill"/> may keep locally.</summary>
    public long Mark(string key) => _active ? Volatile.Read(ref _changes[Stripe(key)]) : Untracked;

    /// <summary>Null, and nothing kept, when the key or one sharing its stripe changed since <paramref name="mark"/>, or a write of the key is in flight; otherwise what <paramref name="set"/> returned.</summary>
    public bool? Fill<TState>(string key, long mark, TState state, Func<TState, bool> set)
    {
        if (!_active)
        {
            return set(state);
        }

        // Read before the first background write: its order is not known.
        if (mark == Untracked)
        {
            return null;
        }

        var stripe = Stripe(key);
        lock (_stripes[stripe])
        {
            if (Volatile.Read(ref _changes[stripe]) != mark)
            {
                return null;
            }

            lock (_gate)
            {
                // The inner tier may change under a read while a write is in flight; its end moves the count.
                if (_tails.ContainsKey(key))
                {
                    return null;
                }
            }

            return set(state);
        }
    }

    /// <summary>A local change of the key that a fill from an earlier read must not undo.</summary>
    public bool Apply<TState>(string key, TState state, Func<TState, bool> change)
    {
        if (!_active)
        {
            return change(state);
        }

        var stripe = Stripe(key);
        lock (_stripes[stripe])
        {
            Interlocked.Increment(ref _changes[stripe]);
            return change(state);
        }
    }

    public void Failed(string stage) =>
        telemetry.TryTrackEvent(FailedEvent, [new("cache.name", cacheName), new("stage", stage)]);

    internal static int Stripe(string key) => (int)((uint)StringComparer.Ordinal.GetHashCode(key) % Stripes);

    /// <summary>Tells the key's writes in flight which version this one took: a write of the key that returns after it, with a lower version, took its place in the hash earlier.</summary>
    internal void RecordReturned(string key, long version)
    {
        if (version == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (!_tails.TryGetValue(key, out var tail))
            {
                return;
            }

            if (tail.MaxReturned == 0 || tail.MaxReturned >> HashVersion.CounterBits == version >> HashVersion.CounterBits)
            {
                tail.MaxReturned = Math.Max(tail.MaxReturned, version);
            }
            else
            {
                tail.Mixed = true;
            }
        }
    }

    private void ActivateLocked()
    {
        if (!_active)
        {
            // Awaited writes and fills already in flight are not in the order: they can land around the first background write.
            _changes = new long[Stripes];
            _stripes = Enumerable.Range(0, Stripes).Select(static _ => new Lock()).ToArray();
            _active = true;
        }
    }

    private async Task DrainOnceAsync()
    {
        Task idle;
        lock (_gate)
        {
            _closed = true;
            if (_inFlight == 0)
            {
                EndLocked();
                return;
            }

            idle = (_idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        var timedOut = false;
        try
        {
            await idle.WaitAsync(drainTimeout, clock, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            timedOut = true;
        }

        if (timedOut)
        {
            logger.LogWarning("{Cache} closed with {Count} background write(s) still running after {Timeout}; they are cancelled.", cacheName, InFlight, drainTimeout);
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }

        lock (_gate)
        {
            EndLocked();
        }
    }

    private async Task ExecuteAsync<TState>(string key, TaskCompletionSource mine, Task? previous, TState state, Func<TState, CancellationToken, ValueTask> work)
    {
        var drainCancelled = false;
        try
        {
            if (previous is not null)
            {
                // An awaited write ahead of it can outlast the drain, which must still get this place back.
                await previous.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            }

            await work(state, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            drainCancelled = true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A background write in {Cache} failed.", cacheName);
            Failed("unexpected");
        }
        finally
        {
            // Before the tail goes, so a fill sees either the write in flight or its end.
            Changed(key);
            lock (_gate)
            {
                if (_tails.TryGetValue(key, out var tail) && ReferenceEquals(tail.Background, mine))
                {
                    tail.Background = null;
                    RemoveIfDoneLocked(key, tail);
                }

                ReleaseLocked();
            }

            mine.TrySetResult();
        }

        // A deliberate cancel at close, not a failure: logged without its stack.
        if (drainCancelled)
        {
            logger.LogInformation("A background write in {Cache} was cancelled by the drain; its key stays local only.", cacheName);
            Failed("drain_cancelled");
        }
    }

    private Task? EnterLocked(string key, Awaited mine)
    {
        if (!_tails.TryGetValue(key, out var tail))
        {
            _tails[key] = tail = new Tail();
        }

        // Only the background writes before it: awaited writes of one key do not wait for each other.
        (tail.Awaited ??= []).Add(mine);
        Interlocked.Increment(ref _changes[Stripe(key)]);
        return tail.Background?.Task;
    }

    private bool KeepLocal<TState>(Awaited mine, string key, TState state, Func<TState, bool> set, Func<TState, bool>? whenSuperseded = null, long version = 0)
    {
        Tail? tail;
        lock (_gate)
        {
            _tails.TryGetValue(key, out tail);
        }

        if (tail is null)
        {
            return Apply(key, state, set);
        }

        lock (tail.Gate)
        {
            bool superseded;
            bool overtaken;
            lock (_gate)
            {
                // A background write issued after it holds the newer value, and promotes it when it lands.
                superseded = mine.Superseded?.Contains(key) == true;
                overtaken = version != 0 && (tail.Mixed || (tail.MaxReturned > version && tail.MaxReturned >> HashVersion.CounterBits == version >> HashVersion.CounterBits));
            }

            if (overtaken && whenSuperseded is not null)
            {
                return Apply(key, state, whenSuperseded);
            }

            if (superseded)
            {
                return whenSuperseded is null || Apply(key, state, whenSuperseded);
            }

            return Apply(key, state, set);
        }
    }

    private void Leave(string? key, string[]? keys, Awaited mine)
    {
        if (key is not null)
        {
            Changed(key);
        }
        else
        {
            foreach (var each in keys!)
            {
                Changed(each);
            }
        }

        lock (_gate)
        {
            if (key is not null)
            {
                LeaveLocked(key, mine);
            }
            else
            {
                foreach (var each in keys!)
                {
                    LeaveLocked(each, mine);
                }
            }
        }

        mine.TrySetResult();
    }

    private void Changed(string key)
    {
        if (!_active)
        {
            return;
        }

        var stripe = Stripe(key);
        lock (_stripes[stripe])
        {
            Interlocked.Increment(ref _changes[stripe]);
        }
    }

    private void LeaveLocked(string key, Awaited mine)
    {
        if (_tails.TryGetValue(key, out var tail) && tail.Awaited?.Remove(mine) == true)
        {
            RemoveIfDoneLocked(key, tail);
        }
    }

    private void RemoveIfDoneLocked(string key, Tail tail)
    {
        if (tail.Background is null && tail.Awaited is not { Count: > 0 })
        {
            _tails.Remove(key);
        }
    }

    private void ReleaseLocked()
    {
        if (--_inFlight == 0)
        {
            _idle?.TrySetResult();
            if (_drained)
            {
                _lifetime.Dispose();
            }
        }
    }

    // A write still running after a timed-out drain reads the token, so the lifetime outlives it.
    private void EndLocked()
    {
        _drained = true;
        if (_inFlight == 0)
        {
            _lifetime.Dispose();
        }
    }

    internal readonly struct Turn(BackgroundWrites? owner, string? key, string[]? keys, Awaited? mine, Task previous) : IDisposable
    {
        public bool Tracked => owner is not null;

        public Task Previous => previous ?? Task.CompletedTask;

        /// <summary>Skipped, and counted as kept, when a background write of the key was issued after this one; <paramref name="whenSuperseded"/> then runs instead, under the same exclusion as a keep.</summary>
        public bool KeepLocal<TState>(string of, TState state, Func<TState, bool> set, Func<TState, bool>? whenSuperseded = null, long version = 0) =>
            owner is null ? set(state) : owner.KeepLocal(mine!, of, state, set, whenSuperseded, version);

        public void Returned(string of, long version) => owner?.RecordReturned(of, version);

        public void Dispose() => owner?.Leave(key, keys, mine!);
    }

    internal sealed class Awaited() : TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        // Under _gate.
        public HashSet<string>? Superseded;
    }

    private sealed class Tail
    {
        public readonly Lock Gate = new();
        // Under _gate, except Applied, which is under Gate.
        public TaskCompletionSource? Background;
        public List<Awaited>? Awaited;
        public long Issued;
        public long Applied;

        // Under _gate: the highest version a write of the key returned while the tail lived, and whether the returns came from more than one epoch of the hash.
        public long MaxReturned;
        public bool Mixed;
    }
}
