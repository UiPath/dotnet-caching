using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace UiPath.Caching;

/// <summary>One run of the work per key at a time: concurrent callers for the same key join the run in progress and share its result or its failure.</summary>
/// <remarks>The shared work runs on its own token, cancelled once every caller still waiting has cancelled; a caller that cancels stops waiting without cancelling it for the others.</remarks>
internal sealed class InFlight<TKey, TResult>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Flight> _flights;

    public InFlight(IEqualityComparer<TKey>? comparer = null) => _flights = new ConcurrentDictionary<TKey, Flight>(comparer);

    /// <summary>Runs in the table, for tests.</summary>
    internal int Count => _flights.Count;

    public ValueTask<TResult> RunAsync<TState>(TKey key, TState state, Func<TState, IInFlightRun, ValueTask<TResult>> work, CancellationToken token) =>
        RunAsync(key, state, work, token, out _);

    /// <summary>As the overload without it, also handing back the run, so a caller can read what the work recorded on it once the task completes.</summary>
    public ValueTask<TResult> RunAsync<TState>(TKey key, TState state, Func<TState, IInFlightRun, ValueTask<TResult>> work, CancellationToken token, out IInFlightRun run)
    {
        // A caller cancelled before it arrives starts no work and joins none.
        token.ThrowIfCancellationRequested();
        var flight = Join(key, state, work, token.CanBeCanceled, workEnds: false, out var started);
        run = flight;
        return Wait(flight, started, token);
    }

    /// <summary>As <see cref="RunAsync{TState}(TKey, TState, Func{TState, IInFlightRun, ValueTask{TResult}}, CancellationToken)"/>, for work that ends the run itself (see <see cref="IInFlightEnd{TResult}"/>): the caller that started it then awaits the work's own task, with no task of the run between, and <paramref name="started"/> tells it so.</summary>
    public ValueTask<TResult> StartOrJoinAsync<TState>(TKey key, TState state, Func<TState, IInFlightEnd<TResult>, ValueTask<TResult>> work, CancellationToken token, out bool started)
    {
        token.ThrowIfCancellationRequested();
        var flight = Join(key, state, work, token.CanBeCanceled, workEnds: true, out started);
        return Wait(flight, started, token);
    }

    /// <summary>As <see cref="StartOrJoinAsync{TState}(TKey, TState, Func{TState, IInFlightEnd{TResult}, ValueTask{TResult}}, CancellationToken, out bool)"/>, also handing back the run, so a caller can read what the work recorded on it once the task completes.</summary>
    public ValueTask<TResult> StartOrJoinAsync<TState>(TKey key, TState state, Func<TState, IInFlightEnd<TResult>, ValueTask<TResult>> work, CancellationToken token, out bool started, out IInFlightRun run)
    {
        token.ThrowIfCancellationRequested();
        var flight = Join(key, state, work, token.CanBeCanceled, workEnds: true, out started);
        run = flight;
        return Wait(flight, started, token);
    }

    /// <summary>Joins the run in progress for <paramref name="key"/>; false when none is, starting nothing. The caller that stops waiting for any reason other than its token must <see cref="Joined.Withdraw"/>, or it keeps counting as a waiter.</summary>
    public bool TryJoin(TKey key, CancellationToken token, [NotNullWhen(true)] out Joined? joined)
    {
        token.ThrowIfCancellationRequested();
        if (_flights.TryGetValue(key, out var running) && running.TryJoin())
        {
            joined = new Joined(running);
            joined.Result = WaitAsync(running, joined, token).AsTask();
            return true;
        }

        joined = null;
        return false;
    }

    /// <summary>Takes the key for work the caller runs itself, for several keys at once; false when a run holds it. Callers can join it, and it ends only through the reservation.</summary>
    public bool TryReserve(TKey key, [NotNullWhen(true)] out IInFlightReservation<TResult>? reservation)
    {
        var flight = new Flight(this, key, reserved: true);
        if (_flights.TryAdd(key, flight))
        {
            reservation = flight;
            return true;
        }

        flight.Dispose();
        reservation = null;
        return false;
    }

    /// <summary>When each run in progress ends, for tests.</summary>
    internal Task[] Completions() => [.. _flights.Values.Select(f => f.Completion)];

    private static ValueTask<TResult> Wait(Flight flight, bool started, CancellationToken token)
    {
        // The caller that started the work waits on the work itself: the shared task would resume it through a thread-pool hop.
        // A joiner has no work of its own to wait on, and neither has a starter whose work succeeded before it first yielded.
        var ownWork = started ? flight.Starter : null;
        if (ownWork is null)
        {
            return WaitAsync(flight, joined: null, token);
        }

        return token.CanBeCanceled ? WaitStarterCancellableAsync(flight, ownWork, token) : new ValueTask<TResult>(ownWork);
    }

    private static ValueTask<TResult> WaitAsync(Flight flight, Joined? joined, CancellationToken token)
    {
        // A run that finished before its caller came to wait needs no task.
        if (flight.TryGetValue(out var value))
        {
            return new ValueTask<TResult>(value);
        }

        return token.CanBeCanceled ? WaitCancellableAsync(flight, joined, token) : new ValueTask<TResult>(flight.Result);
    }

    private static async ValueTask<TResult> WaitCancellableAsync(Flight flight, Joined? joined, CancellationToken token)
    {
        try
        {
            return await flight.Result.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Left here rather than from a token callback: WaitAsync's own callback can resume this caller before a registration of ours would run.
            if (joined is null)
            {
                flight.Leave();
            }
            else
            {
                joined.Withdraw();
            }

            throw;
        }
    }

    private static async ValueTask<TResult> WaitStarterCancellableAsync(Flight flight, Task<TResult> own, CancellationToken token)
    {
        try
        {
            return await own.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The run goes on for the callers that joined it, and a failure of it that nothing awaits any more is observed.
            own.Forget();
            flight.Leave();
            throw;
        }
    }

    // The work's second parameter is the flight itself, under whichever of its interfaces the caller's delegate names.
    private Flight Join<TState>(TKey key, TState state, Func<TState, Flight, ValueTask<TResult>> work, bool starterCanCancel, bool workEnds, out bool startedHere)
    {
        startedHere = false;
        while (true)
        {
            if (_flights.TryGetValue(key, out var running))
            {
                if (running.TryJoin())
                {
                    return running;
                }

                // Every caller has left it, so it may be cancelled: start a fresh one.
                _flights.TryRemove(new KeyValuePair<TKey, Flight>(key, running));
                continue;
            }

            // A starter that cannot cancel never leaves, so nothing can cancel the run, and it needs no source.
            var started = new Flight(this, key, cancellable: starterCanCancel);
            if (_flights.TryAdd(key, started))
            {
                started.Run(state, work, workEnds);
                startedHere = true;
                return started;
            }

            started.Dispose();
        }
    }

    /// <summary>A caller waiting on a run it did not start, which can stop waiting without cancelling its token.</summary>
    internal sealed class Joined
    {
        private readonly Flight _flight;
        private int _left;

        internal Joined(Flight flight) => _flight = flight;

        public Task<TResult> Result { get; internal set; } = null!;

        /// <summary>Stops counting as a waiter, once; the run is cancelled when none is left.</summary>
        public void Withdraw()
        {
            if (Interlocked.Exchange(ref _left, 1) == 0)
            {
                _flight.Leave();
            }
        }
    }

    /// <remarks>Locks on itself: the class is internal and sealed, reaches other code only through its interfaces, and nothing else locks a run, so a separate lock object would only cost an allocation per run.</remarks>
    internal sealed class Flight(InFlight<TKey, TResult> owner, TKey key, bool reserved = false, bool cancellable = true) : IInFlightEnd<TResult>, IInFlightReservation<TResult>, IDisposable
    {
        private readonly CancellationTokenSource? _cancellation = reserved || !cancellable ? null : new();
        private int _waiting = 1;
        private TaskCompletionSource<TResult>? _source;
        private int _state;
        private TResult? _value;
        private Exception? _failure;
        private CancellationToken _canceledBy;

        private enum Outcome
        {
            Running,
            Succeeded,
            Failed,
            Canceled,
        }

        /// <summary>The run's task, made when a caller first needs one: a run that finishes before its starter waits never builds it.</summary>
        public Task<TResult> Result
        {
            get
            {
                lock (this)
                {
                    if (_source is null)
                    {
                        _source = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                        if (Current != Outcome.Running)
                        {
                            Publish(_source);
                        }
                    }

                    return _source.Task;
                }
            }
        }

        public Task Completion => Result;

        public bool IsCompleted => Current != Outcome.Running;

        /// <summary>True once every caller has left: the work no longer has anyone to commit for.</summary>
        public bool IsAbandoned
        {
            get
            {
                lock (this)
                {
                    return !reserved && _waiting == 0;
                }
            }
        }

        public CancellationToken Token => _cancellation?.Token ?? default;

        /// <summary>The work as its starter awaits it, ending with the work's own failure; null when the work succeeded before it first yielded.</summary>
        public Task<TResult>? Starter { get; private set; }

        public DateTimeOffset? Expiration { get; set; }

        public bool ExpirationUnread { get; set; }

        private Outcome Current => (Outcome)Volatile.Read(ref _state);

        /// <summary>The value of a run that has already succeeded.</summary>
        public bool TryGetValue([MaybeNullWhen(false)] out TResult value)
        {
            if (Current == Outcome.Succeeded)
            {
                value = _value!;
                return true;
            }

            value = default;
            return false;
        }

        /// <summary>False once every caller has left, since the work may already be cancelled.</summary>
        public bool TryJoin()
        {
            if (reserved)
            {
                return true;
            }

            lock (this)
            {
                if (_waiting == 0)
                {
                    return false;
                }

                _waiting++;
                return true;
            }
        }

        public void Leave()
        {
            if (reserved)
            {
                return;
            }

            lock (this)
            {
                if (--_waiting > 0)
                {
                    return;
                }

                // Out of the table now, not when the work ends: work that ignores its token would otherwise hold the entry.
                owner._flights.TryRemove(new KeyValuePair<TKey, Flight>(key, this));
            }

            try
            {
                _cancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The work finished first.
            }
        }

        public bool TryCommit<TArg>(TArg arg, Action<TArg> commit)
        {
            // Under the gate Leave takes, so work every caller has left cannot commit after a fresh run has started.
            lock (this)
            {
                if (_waiting == 0)
                {
                    return false;
                }

                commit(arg);
                return true;
            }
        }

        public void Dispose() => _cancellation?.Dispose();

        void IInFlightReservation<TResult>.Complete(TResult result)
        {
            owner._flights.TryRemove(new KeyValuePair<TKey, Flight>(key, this));
            Settle(Outcome.Succeeded, result, null);
            Dispose();
        }

        void IInFlightReservation<TResult>.Fail(Exception failure)
        {
            owner._flights.TryRemove(new KeyValuePair<TKey, Flight>(key, this));
            Settle(Outcome.Failed, default, failure);
            Dispose();
        }

        void IInFlightEnd<TResult>.Complete(TResult result) => Finish(result, null);

        void IInFlightEnd<TResult>.Fail(Exception failure) => Finish(default!, failure);

        /// <summary>Starts the work. One that succeeds before it first yields completes here, without the state machine an async method would build.</summary>
        /// <param name="workEnds">The work ends the run itself before its task completes, so that task is the starter's; otherwise the run ends it when it completes.</param>
        public void Run<TState>(TState state, Func<TState, Flight, ValueTask<TResult>> work, bool workEnds)
        {
            ValueTask<TResult> pending;
            try
            {
                pending = work(state, this);
            }
            catch (Exception ex)
            {
                Finish(default!, ex);
                return;
            }

            if (pending.IsCompletedSuccessfully)
            {
                // Work that ends the run has ended it already; a second Finish settles nothing.
                Finish(pending.Result, null);
                return;
            }

            Starter = workEnds ? pending.AsTask() : FinishAsync(pending);
        }

        private async Task<TResult> FinishAsync(ValueTask<TResult> pending)
        {
            TResult result = default!;
            Exception? failure = null;
            try
            {
                result = await pending.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            Finish(result, failure);
            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            return result;
        }

        private void Finish(TResult result, Exception? failure)
        {
            // Out of the table before anyone sees the outcome, so a caller that arrives next starts a fresh run. Idempotent: Settle keeps the first outcome.
            owner._flights.TryRemove(new KeyValuePair<TKey, Flight>(key, this));
            if (failure is OperationCanceledException && _cancellation is { IsCancellationRequested: true })
            {
                Settle(Outcome.Canceled, default, failure);
            }
            else if (failure is not null)
            {
                Settle(Outcome.Failed, default, failure);
            }
            else
            {
                Settle(Outcome.Succeeded, result, null);
            }

            Dispose();
        }

        private void Settle(Outcome outcome, TResult? value, Exception? failure)
        {
            lock (this)
            {
                if (Current != Outcome.Running)
                {
                    return;
                }

                _value = value;
                _failure = failure;
                _canceledBy = outcome == Outcome.Canceled ? _cancellation?.Token ?? default : default;
                Volatile.Write(ref _state, (int)outcome);
                if (_source is not null)
                {
                    Publish(_source);
                }
            }
        }

        private void Publish(TaskCompletionSource<TResult> source)
        {
            switch (Current)
            {
                case Outcome.Succeeded:
                    source.TrySetResult(_value!);
                    break;
                case Outcome.Canceled:
                    source.TrySetCanceled(_canceledBy);
                    break;
                default:
                    source.TrySetException(_failure!);

                    // Every caller may have left already, leaving nothing to observe the failure.
                    source.Task.Forget();
                    break;
            }
        }
    }
}
