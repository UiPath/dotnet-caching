using System.Collections.Concurrent;

namespace UiPath.Caching;

/// <summary>One run of the work per key at a time: concurrent callers for the same key join the run in progress and share its result or its failure.</summary>
/// <remarks>The shared work runs on its own token, cancelled once every caller still waiting has cancelled; a caller that cancels stops waiting without cancelling it for the others.</remarks>
internal sealed class InFlight<TKey, TResult>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Flight> _flights = new();

    /// <summary>Runs in the table, for tests.</summary>
    internal int Count => _flights.Count;

    public async ValueTask<TResult> RunAsync<TState>(TKey key, TState state, Func<TState, IInFlightRun, ValueTask<TResult>> work, CancellationToken token)
    {
        // A caller cancelled before it arrives starts no work and joins none.
        token.ThrowIfCancellationRequested();
        var flight = Join(key, state, work);
        if (!token.CanBeCanceled)
        {
            return await flight.Result.ConfigureAwait(false);
        }

        try
        {
            return await flight.Result.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Left here rather than from a token callback: WaitAsync's own callback can resume this caller before a registration of ours would run.
            flight.Leave();
            throw;
        }
    }

    private Flight Join<TState>(TKey key, TState state, Func<TState, IInFlightRun, ValueTask<TResult>> work)
    {
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

            var started = new Flight(this, key);
            if (_flights.TryAdd(key, started))
            {
                _ = started.RunAsync(state, work);
                return started;
            }

            started.Dispose();
        }
    }

    private sealed class Flight(InFlight<TKey, TResult> owner, TKey key) : IInFlightRun, IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource<TResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private int _waiting = 1;

        public Task<TResult> Result => _result.Task;

        public CancellationToken Token => _cancellation.Token;

        /// <summary>False once every caller has left, since the work may already be cancelled.</summary>
        public bool TryJoin()
        {
            lock (_gate)
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
            lock (_gate)
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
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The work finished first.
            }
        }

        public bool TryCommit<TArg>(TArg arg, Action<TArg> commit)
        {
            // Under the gate Leave takes, so work every caller has left cannot commit after a fresh run has started.
            lock (_gate)
            {
                if (_waiting == 0)
                {
                    return false;
                }

                commit(arg);
                return true;
            }
        }

        public void Dispose() => _cancellation.Dispose();

        public async Task RunAsync<TState>(TState state, Func<TState, IInFlightRun, ValueTask<TResult>> work)
        {
            TResult result = default!;
            Exception? failure = null;
            try
            {
                result = await work(state, this).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            // Out of the table before anyone sees the outcome, so a caller that arrives next starts a fresh run.
            owner._flights.TryRemove(new KeyValuePair<TKey, Flight>(key, this));
            if (failure is OperationCanceledException && _cancellation.IsCancellationRequested)
            {
                _result.TrySetCanceled(_cancellation.Token);
            }
            else if (failure is not null)
            {
                _result.TrySetException(failure);

                // Every caller may have left already, leaving nothing to observe the failure.
                _result.Task.Forget();
            }
            else
            {
                _result.TrySetResult(result);
            }

            Dispose();
        }
    }
}
