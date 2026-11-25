using Microsoft.Extensions.Options;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>The adaptive rate of the mover: a rise on green while the unit moves, a halving on amber, a pause on red.</summary>
internal sealed class AimdMoveRate : IMoveRate
{
    private readonly IKeyMoveHealthSource _health;
    private readonly IMoverMemory? _memory;
    private readonly LastGoodOptions _options;
    private readonly TimeProvider _time;
    private readonly MoverLatency? _latency;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly Flight<KeyMoveHealth> _healthFlight = new();
    private readonly Flight<string?> _memoryFlight = new();
    private readonly Dictionary<string, UnitRate> _units = new(StringComparer.Ordinal);
    private bool _evaluated;
    private DateTimeOffset _evaluatedAt;
    private KeyMoveHealthState _state = KeyMoveHealthState.Green;
    private string? _memoryPause;
    private DateTimeOffset _oomUntil;
    private DateTimeOffset _globalNext;
    private string? _lastPause;

    public AimdMoveRate(IKeyMoveHealthSource health, IMoverMemory? memory, IOptionsMonitor<KeyMoveMoverOptions> options, TimeProvider time, MoverLatency? latency = null)
    {
        _health = health ?? throw new ArgumentNullException(nameof(health));
        (_memory, _options, _time, _latency) = (memory, LastGoodOptions.Over(options), time ?? throw new ArgumentNullException(nameof(time)), latency);
    }

    internal Task HealthObservation => _healthFlight.Current?.Observation ?? Task.CompletedTask;

    internal Task HealthCancellation => _healthFlight.Current?.Cancellation ?? Task.CompletedTask;

    /// <summary>The rate the mover uses with no other source: <c>INFO</c> of the units' nodes, and the mover's own measurements.</summary>
    public static AimdMoveRate CreateDefault(IScanUnits units, IOptionsMonitor<KeyMoveMoverOptions> options, TimeProvider time) =>
        Compose(RedisInfoWatch.Reader(units), options, time);

    /// <summary>The current rate of a unit, in keys per second and bytes per second; the start rate for a unit not seen yet.</summary>
    public (double KeysPerSecond, double BytesPerSecond) RateOf(string unit)
    {
        var o = Options();
        lock (_lock)
        {
            var rate = UnitOf(unit, o);
            return (rate.Keys, rate.Bytes);
        }
    }

    public async ValueTask<string?> PauseReasonAsync(string unit, CancellationToken cancellationToken)
    {
        var o = Options();
        lock (_lock)
        {
            _ = UnitOf(unit, o);
        }

        await RefreshAsync(o, cancellationToken).ConfigureAwait(false);
        string? reason;
        lock (_lock)
        {
            reason = CurrentPause(Options(), _time.GetUtcNow());
            if (reason != _lastPause)
            {
                _lastPause = reason;
                if (reason is not null)
                {
                    RouterTelemetry.CountMoverPaused(reason);
                }
            }
        }

        return reason;
    }

    public void Observed(string unit, MoveResult result, TimeSpan elapsed)
    {
        var o = Options();
        _latency?.Record(elapsed);
        if (string.Equals(result.SkipReason, "timeout", StringComparison.Ordinal))
        {
            _latency?.RecordTimeout();
        }

        lock (_lock)
        {
            if (result.Moved)
            {
                UnitOf(unit, o).Moved = true;
            }
            else if (string.Equals(result.SkipReason, "oom", StringComparison.Ordinal))
            {
                _oomUntil = _time.GetUtcNow() + o.OomBackoff;
            }
        }
    }

    public void Owe(string unit, long bytes)
    {
        var o = Options();
        lock (_lock)
        {
            UnitOf(unit, o).Debt += bytes;
        }
    }

    public long TakeDebt(string unit)
    {
        var o = Options();
        lock (_lock)
        {
            var rate = UnitOf(unit, o);
            var debt = rate.Debt;
            rate.Debt = 0;
            return debt;
        }
    }

    public TimeSpan Reserve(string unit, int keys, long bytes)
    {
        var o = Options();
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            var rate = UnitOf(unit, o);
            var unitCost = UnitCost(rate, o, keys, bytes);
            rate.Next = Max(rate.Next, now) + unitCost;
            var until = rate.Next;
            var globalCost = TimeSpan.Zero;
            var globalUntil = DateTimeOffset.MinValue;
            if (o.GlobalKeysPerSecond > 0 || o.GlobalBytesPerSecond > 0)
            {
                globalCost = Cost(keys, bytes, o.GlobalKeysPerSecond, o.GlobalBytesPerSecond);
                _globalNext = Max(_globalNext, now) + globalCost;
                globalUntil = _globalNext;
                until = Max(until, globalUntil);
            }

            // The reservation keeps its own deadline: the queues' tails move on when others reserve, and this wait does not move with them.
            rate.Pending = new Pending(keys, bytes, unitCost, globalCost, rate.Next, globalUntil);
            return until - now;
        }
    }

    public TimeSpan? Remaining(string unit)
    {
        var o = Options();
        lock (_lock)
        {
            var rate = UnitOf(unit, o);
            if (rate.Pending is not { } pending)
            {
                return TimeSpan.Zero;
            }

            // The reservation is costed again at the rates now in force: a reload or a halving moves the end of the wait by the difference.
            var unitCost = UnitCost(rate, o, pending.Keys, pending.Bytes);
            var globalCost = o.GlobalKeysPerSecond > 0 || o.GlobalBytesPerSecond > 0 ? Cost(pending.Keys, pending.Bytes, o.GlobalKeysPerSecond, o.GlobalBytesPerSecond) : TimeSpan.Zero;
            var unitDelta = unitCost - pending.ReservedUnitCost;
            var globalDelta = globalCost - pending.GlobalCost;
            rate.Next += unitDelta;
            _globalNext += globalDelta;
            pending = pending with { ReservedUnitCost = unitCost, GlobalCost = globalCost, UnitUntil = pending.UnitUntil + unitDelta, GlobalUntil = pending.GlobalUntil + globalDelta };
            rate.Pending = pending;
            var until = pending.UnitUntil;
            if (o.GlobalKeysPerSecond > 0 || o.GlobalBytesPerSecond > 0)
            {
                until = Max(until, pending.GlobalUntil);
            }

            var left = until - _time.GetUtcNow();
            if (left > TimeSpan.Zero)
            {
                return left;
            }

            rate.Pending = null;
            return TimeSpan.Zero;
        }
    }

    /// <summary>The default rate over any reader of the nodes; the watch, the source and the rate share one validated snapshot of the options.</summary>
    internal static AimdMoveRate Compose(Func<CancellationToken, Task<IReadOnlyList<NodeInfo>>> read, IOptionsMonitor<KeyMoveMoverOptions> options, TimeProvider time)
    {
        var shared = LastGoodOptions.Over(options);
        var watch = new RedisInfoWatch(read, shared, time);
        var latency = new MoverLatency(time);
        return new AimdMoveRate(new RedisInfoHealthSource(watch, latency, shared), watch, shared, time, latency);
    }

    private static Call<T> Start<T>(Func<CancellationToken, ValueTask<T>> work, CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<T> task;
        try
        {
            task = work(source.Token).AsTask();
        }
        catch
        {
            source.Dispose();
            throw;
        }

        var call = new Call<T>(task, source);

        // The source goes with the work and its cancellation, not before: the work may still hold its token, and a failure after the timeout is observed here.
        call.Observation = task.ContinueWith(
            static (done, state) =>
            {
                _ = done.Exception;
                ((Call<T>)state!).WorkCompleted();
            },
            call,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return call;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    // The time the batch costs at the rates; a rate of zero or less is no limit. The validator keeps every rate at or above its floor, so a wait is bounded by one batch at the floor.
    private static TimeSpan Cost(int keys, long bytes, double keysPerSecond, double bytesPerSecond)
    {
        var seconds = Math.Max(keysPerSecond > 0 ? keys / keysPerSecond : 0, bytesPerSecond > 0 ? bytes / bytesPerSecond : 0);
        return seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
    }

    private static TimeSpan UnitCost(UnitRate rate, KeyMoveMoverOptions o, int keys, long bytes) =>
        Cost(keys, bytes, Math.Min(rate.Keys, o.MaxKeysPerSecond), Math.Min(rate.Bytes, o.MaxBytesPerSecond));

    private KeyMoveMoverOptions Options() => _options.CurrentValue;

    // Callers hold the lock.
    private UnitRate UnitOf(string unit, KeyMoveMoverOptions o)
    {
        if (!_units.TryGetValue(unit, out var rate))
        {
            _units[unit] = rate = new UnitRate { Keys = Math.Min(o.StartKeysPerSecond, o.MaxKeysPerSecond), Bytes = Math.Min(o.StartBytesPerSecond, o.MaxBytesPerSecond), LastIncrease = _time.GetUtcNow() };
        }

        return rate;
    }

    // Callers hold the lock.
    private string? CurrentPause(KeyMoveMoverOptions options, DateTimeOffset now)
    {
        if (now < _oomUntil)
        {
            return "oom_backoff";
        }

        if (options.Windows.Count > 0 && !options.Windows.Any(w => w.Contains(now)))
        {
            return "schedule";
        }

        return _memoryPause ?? (_state == KeyMoveHealthState.Green || _state == KeyMoveHealthState.Amber ? null : "red");
    }

    private async ValueTask RefreshAsync(KeyMoveMoverOptions o, CancellationToken cancellationToken)
    {
        if (!Due(o.EvaluationInterval))
        {
            return;
        }

        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Due(o.EvaluationInterval))
            {
                return;
            }

            var (health, failed) = await ReadHealthAsync(o, cancellationToken).ConfigureAwait(false);
            string? memory = null;

            // A failed health read is red already; a second read that would wait for the same outage adds nothing.
            if (_memory is not null && !failed)
            {
                try
                {
                    memory = await BoundedAsync(_memoryFlight, _memory.PauseReasonAsync, o.HealthTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not OutOfMemoryException)
                {
                    // The memory rules never fail open: memory that cannot be read is not safe, so the mover waits as on red.
                    health = new KeyMoveHealth(KeyMoveHealthState.Red, "memory unreadable: " + ex.Message);
                }
            }

            Apply(health.State, memory, o);
        }
        finally
        {
            _refresh.Release();
        }
    }

    // The source's answer, or red when it throws, times out, answers null or answers a state that is not defined; only the caller's own cancellation escapes.
    private async ValueTask<(KeyMoveHealth Health, bool Failed)> ReadHealthAsync(KeyMoveMoverOptions o, CancellationToken cancellationToken)
    {
        try
        {
            var health = await BoundedAsync(_healthFlight, _health.GetHealthAsync, o.HealthTimeout, cancellationToken).ConfigureAwait(false);
            if (health is null)
            {
                return (new KeyMoveHealth(KeyMoveHealthState.Red, "the health source gave no verdict"), true);
            }

            return health.State is KeyMoveHealthState.Green or KeyMoveHealthState.Amber or KeyMoveHealthState.Red
                ? (health, false)
                : (new KeyMoveHealth(KeyMoveHealthState.Red, $"the health source gave an undefined state {(int)health.State}"), true);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            return (new KeyMoveHealth(KeyMoveHealthState.Red, "health unavailable: " + ex.Message), true);
        }
    }

    // Waits for the work no longer than the timeout on the rate's clock, whether or not it honours its token. On expiry the work is cancelled, off this gate, and left to finish
    // on its own, its result and its failure ignored; while it runs no second call to the same source is made, and each evaluation reads red at once, so a source that never
    // answers holds neither the refresh gate nor a growing number of tasks.
    private async ValueTask<T> BoundedAsync<T>(Flight<T> flight, Func<CancellationToken, ValueTask<T>> work, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var call = flight.Current;
        if (call is { Finished: false, TimedOut: true })
        {
            throw new TimeoutException("The previous call to the source has not returned.");
        }

        if (call is null || call.Finished)
        {
            call = flight.Current = Start(work, cancellationToken);
        }

        try
        {
            return await call.Task.WaitAsync(timeout, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!call.WorkDone)
        {
            call.TimedOut = true;

            // Cancelling runs the callbacks the work registered on its token, which can block: never on this thread, never while the gate is held.
            call.Cancel();
            throw;
        }
    }

    private bool Due(TimeSpan interval)
    {
        lock (_lock)
        {
            return !_evaluated || _time.GetUtcNow() - _evaluatedAt >= interval;
        }
    }

    private void Apply(KeyMoveHealthState state, string? memory, KeyMoveMoverOptions o)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            (_evaluated, _evaluatedAt, _state, _memoryPause) = (true, now, state, memory);
            RouterTelemetry.CountMoverHealth(state.ToString().ToLowerInvariant());

            // The rate rises only while the unit is moving keys and nothing holds it: a schedule, an OOM backoff, a memory rule or red.
            var held = CurrentPause(o, now) is not null;
            foreach (var rate in _units.Values)
            {
                switch (state)
                {
                    case KeyMoveHealthState.Green:
                        if (held || !rate.Moved)
                        {
                            rate.LastIncrease = now;
                        }
                        else if (now - rate.LastIncrease >= o.IncreaseInterval)
                        {
                            rate.Keys = Math.Min(o.MaxKeysPerSecond, rate.Keys * (1 + o.IncreaseStep));
                            rate.Bytes = Math.Min(o.MaxBytesPerSecond, rate.Bytes * (1 + o.IncreaseStep));
                            rate.LastIncrease = now;
                            RouterTelemetry.CountMoverRateChange("up");
                        }

                        break;
                    case KeyMoveHealthState.Amber:
                        rate.Keys = Math.Max(o.MinKeysPerSecond, rate.Keys / 2);
                        rate.Bytes = Math.Max(o.MinBytesPerSecond, rate.Bytes / 2);
                        rate.LastIncrease = now;
                        RouterTelemetry.CountMoverRateChange("down");
                        break;
                    default:
                        rate.LastIncrease = now;
                        break;
                }

                rate.Moved = false;
            }
        }
    }

    private readonly record struct Pending(int Keys, long Bytes, TimeSpan ReservedUnitCost, TimeSpan GlobalCost, DateTimeOffset UnitUntil, DateTimeOffset GlobalUntil);

    private sealed class UnitRate
    {
        public double Keys { get; set; }

        public double Bytes { get; set; }

        public DateTimeOffset LastIncrease { get; set; }

        public DateTimeOffset Next { get; set; }

        // The last reservation, kept while its wait runs, so the wait can be costed again.
        public Pending? Pending { get; set; }

        // Bytes published and not yet paid for.
        public long Debt { get; set; }

        // Whether the unit moved a key since the last evaluation.
        public bool Moved { get; set; }
    }

    // The one call to a source that may be outstanding; used under the refresh gate.
    private sealed class Flight<T>
    {
        public Call<T>? Current { get; set; }
    }

    // One call to a source: its work, the cancellation of it, and the source of its token. The call is finished, and a new one may start, only when the work has returned
    // and the callbacks of any cancellation have run; completion, the start of the cancellation and the disposal are decided under one lock.
    private sealed class Call<T>(Task<T> task, CancellationTokenSource source)
    {
#if NET9_0_OR_GREATER
        private readonly Lock _gate = new();
#else
        private readonly object _gate = new();
#endif
        private Task? _cancelling;
        private bool _workDone;

        public Task<T> Task { get; } = task;

        public Task Observation { get; set; } = System.Threading.Tasks.Task.CompletedTask;

        public Task Cancellation
        {
            get
            {
                lock (_gate)
                {
                    return _cancelling ?? System.Threading.Tasks.Task.CompletedTask;
                }
            }
        }

        public bool TimedOut { get; set; }

        public bool WorkDone
        {
            get
            {
                lock (_gate)
                {
                    return _workDone;
                }
            }
        }

        public bool Finished
        {
            get
            {
                lock (_gate)
                {
                    return _workDone && (_cancelling?.IsCompleted ?? true);
                }
            }
        }

        public void Cancel()
        {
            lock (_gate)
            {
                if (_workDone || _cancelling is not null)
                {
                    return;
                }

                _cancelling = System.Threading.Tasks.Task.Run(
                    () =>
                    {
                        try
                        {
                            source.Cancel();
                        }
                        catch (AggregateException ex)
                        {
                            _ = ex;
                        }
                    },
                    CancellationToken.None);
            }
        }

        public void WorkCompleted()
        {
            Task? cancelling;
            lock (_gate)
            {
                _workDone = true;
                cancelling = _cancelling;
            }

            if (cancelling is null)
            {
                source.Dispose();
            }
            else
            {
                _ = cancelling.ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Dispose(), source, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }
}
