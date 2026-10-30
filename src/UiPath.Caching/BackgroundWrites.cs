using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching;

/// <summary>Detached inner-tier writes, at most <c>limit</c> at once, in issue order per key with the awaited ones.</summary>
internal sealed class BackgroundWrites(int limit, TimeSpan drainTimeout, string cacheName, ILogger logger, ICachingTelemetryProvider telemetry, TimeProvider clock, Action<string>? onIssued = null)
{
    private const string FailedEvent = "cache.background_write.failed";
    private const string FillRefusedMetric = "cache.background_write.fill_refused";
    private const string TagCache = "cache.name";
    private const string TagReason = "reason";
    // Keys share counters by stripe; locks are shared by more stripes than counters are, so a cache with many stripes keeps few locks.
    private const int Stripes = 8192;
    private const int StripeLocks = 1024;
    private const long Untracked = -1;

    private readonly Lock _gate = new();
    private readonly TelemetryTags<string> _refusedChanged = [new(TagCache, cacheName), new(TagReason, "changed")];
    private readonly TelemetryTags<string> _refusedInFlight = [new(TagCache, cacheName), new(TagReason, "in_flight")];
    private readonly TelemetryTags<string> _refusedUntracked = [new(TagCache, cacheName), new(TagReason, "untracked")];
    private readonly Dictionary<string, Tail> _tails = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    // Changes per stripe of keys, issued or applied locally or heard from a peer; a fill from a read started before one would put back the older value.
    // Allocated when counting starts, at the first background write or a peer's write being watched for; a cache that needs neither pays nothing.
    private long[] _changes = [];
    // Unordered writes in flight per stripe: the inner tier may change under a read until each ends.
    private int[] _writing = [];
    private Lock[] _stripes = [];
    private volatile bool _counting;
    // The write order, from the first background write on.
    private volatile bool _active;
    private int _inFlight;
    private bool _closed;
    private bool _drained;
    private TaskCompletionSource? _idle;
    private Task? _drain;

    /// <summary>Whether writes are ordered per key: from the first background write, or <see cref="Activate"/>.</summary>
    public bool Active => _active;

    /// <summary>Whether changes are counted per stripe, so a fill from a read begun before one is refused.</summary>
    public bool Counting => _counting;

    internal int InFlight => Volatile.Read(ref _inFlight);


    /// <summary>Tracks the awaited writes from now on, for a cache that must know the order its writes landed in even when it takes no background write.</summary>
    public void Activate()
    {
        lock (_gate)
        {
            ActivateLocked();
        }
    }

    /// <summary>Counts changes per stripe from now on, without ordering the writes.</summary>
    public void Track()
    {
        if (_counting)
        {
            return;
        }

        lock (_gate)
        {
            CountLocked();
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
            return EnterUnordered(key, null);
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
            return _counting ? EnterUnordered(null, keys as string[] ?? keys.ToArray()) : default;
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

    /// <summary>Writes to the inner tier after the background writes issued before, then keeps locally in that order; <paramref name="before"/> runs first, in the turn. Exceptions leave through the caller.</summary>
    public ValueTask<TResult> WriteAsync<TState, TWritten, TResult>(string key, TState state, Action<TState>? before, Func<TState, Keeper, ValueTask<TWritten>> write, Func<TState, TWritten, Keeper, TResult> keep, CancellationToken token) =>
        WriteInTurnAsync(Enter(key), state, before, write, keep, token);

    public ValueTask<TResult> WriteAsync<TState, TWritten, TResult>(IEnumerable<string> keys, TState state, Action<TState>? before, Func<TState, Keeper, ValueTask<TWritten>> write, Func<TState, TWritten, Keeper, TResult> keep, CancellationToken token) =>
        WriteInTurnAsync(Enter(keys), state, before, write, keep, token);

    /// <summary>For a write that keeps nothing locally, a removal or a refresh.</summary>
    public ValueTask<TResult> WriteAsync<TState, TResult>(string key, TState state, Action<TState>? before, Func<TState, ValueTask<TResult>> write, CancellationToken token) =>
        WriteInTurnAsync(Enter(key), state, before, write, token);

    public ValueTask<TResult> WriteAsync<TState, TResult>(IEnumerable<string> keys, TState state, Action<TState>? before, Func<TState, ValueTask<TResult>> write, CancellationToken token) =>
        WriteInTurnAsync(Enter(keys), state, before, write, token);

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
    public long Mark(string key) => _counting ? Volatile.Read(ref _changes[Stripe(key)]) : Untracked;

    /// <summary>Null, and nothing kept, when the key or one sharing its stripe changed since <paramref name="mark"/>, or a write of the key is in flight; otherwise what <paramref name="set"/> returned.</summary>
    public bool? Fill<TState>(string key, long mark, TState state, Func<TState, bool> set)
    {
        if (!_counting)
        {
            return set(state);
        }

        // Read before counting began: its order is not known.
        if (mark == Untracked)
        {
            FillRefused(_refusedUntracked);
            return null;
        }

        var stripe = Stripe(key);
        TelemetryTags<string> refused;
        lock (StripeLock(stripe))
        {
            if (Volatile.Read(ref _changes[stripe]) != mark)
            {
                refused = _refusedChanged;
            }
            else if (Volatile.Read(ref _writing[stripe]) != 0)
            {
                refused = _refusedInFlight;
            }
            else if (_active && InTails(key))
            {
                // The inner tier may change under a read while a write is in flight; its end moves the count.
                refused = _refusedInFlight;
            }
            else
            {
                return set(state);
            }
        }

        FillRefused(refused);
        return null;
    }

    /// <summary>Keeps the read's value locally as <see cref="Fill{TState}(string, long, TState, Func{TState, bool})"/> does, and calls <paramref name="notKept"/> when a change since <paramref name="mark"/> kept it out.</summary>
    public void Fill<TState>(string key, long mark, TState state, Func<TState, bool> set, Action<TState> notKept)
    {
        if (Fill(key, mark, state, set) is null)
        {
            notKept(state);
        }
    }

    /// <summary>A local change of the key that a fill from an earlier read must not undo.</summary>
    public bool Apply<TState>(string key, TState state, Func<TState, bool> change)
    {
        if (!_counting)
        {
            return change(state);
        }

        var stripe = Stripe(key);
        lock (StripeLock(stripe))
        {
            Interlocked.Increment(ref _changes[stripe]);
            return change(state);
        }
    }

    /// <summary>A peer's change of the key: a fill from a read begun before it is refused.</summary>
    public void Touch(string key)
    {
        if (!_counting)
        {
            return;
        }

        var stripe = Stripe(key);
        lock (StripeLock(stripe))
        {
            Interlocked.Increment(ref _changes[stripe]);
        }
    }

    /// <summary>Changes a peer made that were not heard: every fill from a read begun before is refused.</summary>
    public void TouchAll()
    {
        if (!_counting)
        {
            return;
        }

        for (var held = 0; held < StripeLocks; held++)
        {
            lock (_stripes[held])
            {
                for (var stripe = held; stripe < Stripes; stripe += StripeLocks)
                {
                    Interlocked.Increment(ref _changes[stripe]);
                }
            }
        }
    }

    public void Failed(string stage) =>
        telemetry.TryTrackEvent(FailedEvent, [new(TagCache, cacheName), new("stage", stage)]);

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

    // The earlier writes' tasks only ever complete successfully, so this throws nothing but a cancellation of token.
    private static ValueTask Settled(Task pending, CancellationToken token) => pending.IsCompleted ? default : new ValueTask(pending.WaitAsync(token));

    // Synchronous while nothing is pending and the write completes at once, so the caller's own frame is the only async one.
    private static ValueTask<TResult> WriteInTurnAsync<TState, TWritten, TResult>(Turn turn, TState state, Action<TState>? before, Func<TState, Keeper, ValueTask<TWritten>> write, Func<TState, TWritten, Keeper, TResult> keep, CancellationToken token)
    {
        var leave = true;
        try
        {
            before?.Invoke(state);
            if (!turn.Previous.IsCompleted)
            {
                leave = false;
                return SettleThenWriteAsync(turn, state, write, keep, token);
            }

            var writing = write(state, turn.Keeper);
            if (writing.IsCompletedSuccessfully)
            {
                return new ValueTask<TResult>(keep(state, writing.Result, turn.Keeper));
            }

            leave = false;
            return KeepAfterAsync(turn, writing, state, keep);
        }
        catch (Exception ex)
        {
            return Faulted<TResult>(ExceptionDispatchInfo.Capture(ex));
        }
        finally
        {
            if (leave)
            {
                turn.Dispose();
            }
        }
    }

    private static ValueTask<TResult> WriteInTurnAsync<TState, TResult>(Turn turn, TState state, Action<TState>? before, Func<TState, ValueTask<TResult>> write, CancellationToken token)
    {
        var leave = true;
        try
        {
            before?.Invoke(state);
            if (!turn.Previous.IsCompleted)
            {
                leave = false;
                return SettleThenWriteAsync(turn, state, write, token);
            }

            var writing = write(state);
            if (writing.IsCompleted)
            {
                // Completed, faulted ones included: their exception surfaces when the caller awaits, as from an async method.
                return writing;
            }

            leave = false;
            return LeaveAfterAsync(turn, writing);
        }
        catch (Exception ex)
        {
            return Faulted<TResult>(ExceptionDispatchInfo.Capture(ex));
        }
        finally
        {
            if (leave)
            {
                turn.Dispose();
            }
        }
    }

    [AsyncMethodBuilder(typeof(Pooled<>))]

    private static async ValueTask<TResult> SettleThenWriteAsync<TState, TWritten, TResult>(Turn turn, TState state, Func<TState, Keeper, ValueTask<TWritten>> write, Func<TState, TWritten, Keeper, TResult> keep, CancellationToken token)
    {
        using (turn)
        {
            await Settled(turn.Previous, token).ConfigureAwait(false);
            var written = await write(state, turn.Keeper).ConfigureAwait(false);
            return keep(state, written, turn.Keeper);
        }
    }

    [AsyncMethodBuilder(typeof(Pooled<>))]

    private static async ValueTask<TResult> SettleThenWriteAsync<TState, TResult>(Turn turn, TState state, Func<TState, ValueTask<TResult>> write, CancellationToken token)
    {
        using (turn)
        {
            await Settled(turn.Previous, token).ConfigureAwait(false);
            return await write(state).ConfigureAwait(false);
        }
    }

    [AsyncMethodBuilder(typeof(Pooled<>))]

    private static async ValueTask<TResult> KeepAfterAsync<TState, TWritten, TResult>(Turn turn, ValueTask<TWritten> writing, TState state, Func<TState, TWritten, Keeper, TResult> keep)
    {
        using (turn)
        {
            var written = await writing.ConfigureAwait(false);
            return keep(state, written, turn.Keeper);
        }
    }

    // As an async method would: a cancellation ends the task cancelled, anything else faulted.
    private static async ValueTask<TResult> Faulted<TResult>(ExceptionDispatchInfo thrown)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        thrown.Throw();
        return default;
    }

    [AsyncMethodBuilder(typeof(Pooled<>))]

    private static async ValueTask<TResult> LeaveAfterAsync<TResult>(Turn turn, ValueTask<TResult> writing)
    {
        using (turn)
        {
            return await writing.ConfigureAwait(false);
        }
    }

    /// <summary>A write outside the order, while only changes are counted: its stripes read as written until it ends, and its keep and its end move their counts.</summary>
    private Turn EnterUnordered(string? key, string[]? keys)
    {
        if (!_counting)
        {
            return default;
        }

        if (key is not null)
        {
            Interlocked.Increment(ref _writing[Stripe(key)]);
        }
        else
        {
            foreach (var each in keys!)
            {
                Interlocked.Increment(ref _writing[Stripe(each)]);
            }
        }

        return new Turn(this, key, keys, null, Task.CompletedTask);
    }

    private void LeaveUnordered(string? key, string[]? keys)
    {
        if (key is not null)
        {
            Ended(key);
            return;
        }

        foreach (var each in keys!)
        {
            Ended(each);
        }
    }

    // Moved before the stripe reads as free: a fill that finds it free finds the count moved.
    private void Ended(string key)
    {
        var stripe = Stripe(key);
        lock (StripeLock(stripe))
        {
            Interlocked.Increment(ref _changes[stripe]);
        }

        Interlocked.Decrement(ref _writing[stripe]);
    }

    private Lock StripeLock(int stripe) => _stripes[stripe % StripeLocks];

    private bool InTails(string key)
    {
        lock (_gate)
        {
            return _tails.ContainsKey(key);
        }
    }

    private void FillRefused(TelemetryTags<string> tags)
    {
        try
        {
            telemetry.TrackMetric(FillRefusedMetric, 1, tags);
        }
        catch (Exception ex)
        {
            telemetry.TryTrackException(ex);
        }
    }

    private void ActivateLocked()
    {
        // Awaited writes already in flight are not in the order: they can land around the first background write.
        CountLocked();
        _active = true;
    }

    private void CountLocked()
    {
        if (!_counting)
        {
            _changes = new long[Stripes];
            _writing = new int[Stripes];
            _stripes = Enumerable.Range(0, StripeLocks).Select(static _ => new Lock()).ToArray();
            _counting = true;
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
            Touch(key);
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
            Touch(key);
        }
        else
        {
            foreach (var each in keys!)
            {
                Touch(each);
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
        public Task Previous => previous ?? Task.CompletedTask;

        public Keeper Keeper => new(owner, mine);

        public void Dispose()
        {
            if (mine is not null)
            {
                owner?.Leave(key, keys, mine);
            }
            else
            {
                owner?.LeaveUnordered(key, keys);
            }
        }
    }

    /// <summary>What a write's callbacks keep locally and report through; valid only inside them.</summary>
    internal readonly struct Keeper(BackgroundWrites? owner, Awaited? mine)
    {
        /// <summary>Skipped, and counted as kept, when a background write of the key was issued after this one; <paramref name="whenSuperseded"/> then runs instead, under the same exclusion as a keep.</summary>
        public bool Local<TState>(string of, TState state, Func<TState, bool> set, Func<TState, bool>? whenSuperseded = null, long version = 0)
        {
            if (owner is null)
            {
                return set(state);
            }

            return mine is null ? owner.Apply(of, state, set) : owner.KeepLocal(mine, of, state, set, whenSuperseded, version);
        }

        public void Returned(string of, long version)
        {
            if (mine is not null)
            {
                owner?.RecordReturned(of, version);
            }
        }
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
