using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>The router's Clear of one cache under key move: the gate protocol of the core around the cache's own two events and sweep.</summary>
/// <remarks>
/// <see cref="ClearOutcome.Unavailable"/> (a transition without a durable state, or another Clear holding the lock) is tried again after a short wait, up to <see cref="KeyMoveStateOptions.ClearRetries"/> attempts;
/// <see cref="ClearOutcome.Failed"/> and the end of the attempts throw <see cref="CacheUnavailableException"/>. Nothing here is wired into the router yet.
/// </remarks>
internal sealed class KeyMoveClear
{
    private static readonly TimeSpan DefaultBackoff = TimeSpan.FromMilliseconds(200);

    private readonly string _group;
    private readonly string _cacheId;
    private readonly Func<string, string, ClearHooks, CancellationToken, Task<ClearOutcome>> _gated;
    private readonly Func<string, string, Func<bool>, CancellationToken, Task<long>> _sweepN;
    private readonly Func<CancellationToken, ValueTask> _sourceClear;
    private readonly Action _clearLocal;
    private readonly Func<ValueTask> _publishClear;
    private readonly int _attempts;
    private readonly TimeSpan _backoff;
    private readonly TimeProvider _time;

    /// <param name="gated">The gate protocol: <c>KeyMoveRuntime.Clear.ClearAsync</c>.</param>
    /// <param name="sweepN">The sweep of N: <c>KeyMoveRuntime.Sweep.SweepAsync</c>.</param>
    /// <param name="sourceClear">The source's own Clear, which empties L.</param>
    /// <param name="clearLocal">Empties this node's local tier of the cache; with <paramref name="publishClear"/> it is each of the two events, as <c>RoutedCache</c> does around its source clear.</param>
    /// <param name="publishClear">Announces the clear to the peers.</param>
    /// <param name="attempts"><see cref="KeyMoveStateOptions.ClearRetries"/>.</param>
    /// <param name="backoff">The wait before a retry, doubled each time; null for the default.</param>
    public KeyMoveClear(
        string group,
        string cacheId,
        Func<string, string, ClearHooks, CancellationToken, Task<ClearOutcome>> gated,
        Func<string, string, Func<bool>, CancellationToken, Task<long>> sweepN,
        Func<CancellationToken, ValueTask> sourceClear,
        Action clearLocal,
        Func<ValueTask> publishClear,
        int attempts,
        TimeProvider time,
        TimeSpan? backoff = null)
    {
        _group = group;
        _cacheId = cacheId;
        _gated = gated;
        _sweepN = sweepN;
        _sourceClear = sourceClear;
        _clearLocal = clearLocal;
        _publishClear = publishClear;
        _attempts = Math.Max(1, attempts);
        _backoff = backoff ?? DefaultBackoff;
        _time = time;
    }

    public static KeyMoveClear Over(KeyMoveRuntime runtime, string group, string cacheId, Func<CancellationToken, ValueTask> sourceClear, Action clearLocal, Func<ValueTask> publishClear, KeyMoveStateOptions options, TimeProvider time) =>
        new(group, cacheId, runtime.Clear.ClearAsync, runtime.Sweep.SweepAsync, sourceClear, clearLocal, publishClear, options.ClearRetries, time);

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        var hooks = new ClearHooks(EventAsync, token => SweepAsync(() => false, token), EventAsync) { GuardedSweep = SweepAsync };
        var wait = _backoff;
        var attempt = 0;
        while (true)
        {
            attempt++;
            ClearOutcome outcome;
            try
            {
                outcome = await _gated(_group, _cacheId, hooks, cancellationToken).ConfigureAwait(false);
            }
            catch (AmbiguousKeyException ex)
            {
                throw new CacheUnavailableException($"The Clear of cache '{_cacheId}' cannot sweep its keys: {ex.Message}", ex);
            }

            switch (outcome)
            {
                case ClearOutcome.Cleared:
                    return;
                case ClearOutcome.Failed:
                    throw new CacheUnavailableException($"The Clear of cache '{_cacheId}' could not be made safe while its group moves: a gate was replaced on every try, the lock was lost, or a gate could not be closed.");
                case ClearOutcome.Unavailable when attempt >= _attempts:
                    throw new CacheUnavailableException($"The Clear of cache '{_cacheId}' is unavailable while its group moves: a transition is in flight or another Clear of the cache is running.");
                case ClearOutcome.Unavailable:
                    await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
                    wait += wait;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown Clear outcome {outcome}.");
            }
        }
    }

    private async Task EventAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _clearLocal();
        await _publishClear().ConfigureAwait(false);
    }

    // L through the source's own Clear, then N; both under the closed gate.
    private async Task SweepAsync(Func<bool> lockLost, CancellationToken cancellationToken)
    {
        await _sourceClear(cancellationToken).ConfigureAwait(false);
        await _sweepN(_group, _cacheId, lockLost, cancellationToken).ConfigureAwait(false);
    }
}
