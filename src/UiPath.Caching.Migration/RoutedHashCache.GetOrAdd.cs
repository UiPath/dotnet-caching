namespace UiPath.Caching.Migration;

// The get-or-add forms of the routed hash cache: each fills the whole hash, never a field.
internal sealed partial class RoutedHashCache<T>
{
    private static readonly Func<Func<CancellationToken, Task<IDictionary<string, T?>>>, CancellationToken, Task<IDictionary<string, T?>>> Plain = static (generator, token) => generator(token);

    ValueTask<IDictionary<string, T?>> IHashCache<T>.GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token) => GetOrAddAsync(KeyOf(cacheKey), generator, default, token);

    ValueTask<IDictionary<string, T?>> IHashCache<T>.GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CancellationToken token) => GetOrAddAsync(KeyOf(cacheKey), generator, Rel(expiration), token);

    ValueTask<IDictionary<string, T?>> IHashCache<T>.GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CancellationToken token) => GetOrAddAsync(KeyOf(cacheKey), generator, Abs(expiration), token);

    ValueTask<IDictionary<string, T?>> IHashCache<T>.GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token) => GetOrAddAsync(KeyOf(cacheKey), state, generator, default, token);

    ValueTask<IDictionary<string, T?>> IHashCache<T>.GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CancellationToken token) => GetOrAddAsync(KeyOf(cacheKey), state, generator, Rel(expiration), token);

    ValueTask<IDictionary<string, T?>> IHashCache<T>.GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CancellationToken token) => GetOrAddAsync(KeyOf(cacheKey), state, generator, Abs(expiration), token);

    private static Dictionary<string, T?> Copy(IDictionary<string, T?> hash) => new(hash, StringComparer.Ordinal);

    private static IDictionary<string, T?>? Winner(HashFillBox<T>? box)
    {
        if (box is not { Lost: true })
        {
            return null;
        }

        return box.Stored ?? (box.CachedEmpty ? Empty() : null);
    }

    private ValueTask<IDictionary<string, T?>> GetOrAddAsync(string key, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CacheExpiry expiry, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddAsync(key, generator, Plain, expiry, token);
    }

    private async ValueTask<IDictionary<string, T?>> GetOrAddAsync<TArg>(string key, TArg argument, Func<TArg, CancellationToken, Task<IDictionary<string, T?>>> generator, CacheExpiry expiry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(generator);
        using var admission = Admit(out var route);
        Task<IDictionary<string, T?>> Factory(CancellationToken token) => generator(argument, token);
        while (true)
        {
            var load = new TaskCompletionSource<IDictionary<string, T?>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var mine = new Flight(load.Task, cancellationToken);
            var flight = _flights.GetOrAdd(key, mine);
            if (!ReferenceEquals(flight, mine))
            {
                try
                {
                    // Each caller gets a hash of its own, copied from a snapshot that nobody holds a reference to.
                    return Copy(await flight.Task.WaitAsync(cancellationToken).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && flight.LeaderToken.IsCancellationRequested)
                {
                    // The caller that ran the load was cancelled and this one is not, so it loads for itself.
                    continue;
                }
            }

            try
            {
                var value = Copy(await LoadAsync(key, route, Factory, expiry, cancellationToken).ConfigureAwait(false));
                _flights.TryRemove(new KeyValuePair<string, Flight>(key, mine));
                load.SetResult(value);
                return Copy(value);
            }
            catch (Exception ex)
            {
                _flights.TryRemove(new KeyValuePair<string, Flight>(key, mine));
                load.SetException(ex);
                _ = load.Task.Exception;
                throw;
            }
        }
    }

    private async Task<IDictionary<string, T?>> LoadAsync(string key, Route route, Func<CancellationToken, Task<IDictionary<string, T?>>> factory, CacheExpiry expiry, CancellationToken cancellationToken)
    {
        var state = _keys.Acquire(key);
        try
        {
            var generation = state.Generation;
            var epoch = _epoch.Value;
            var existing = await ReadAsync(key, null, route, cancellationToken).ConfigureAwait(false);
            if (existing.Read is { Kind: ReadOutcome.Found, Fields: { } hit })
            {
                return hit;
            }

            // A hash cached empty is the answer: no generator runs.
            if (existing.Read.Stops)
            {
                return Empty();
            }

            RouterTelemetry.CountMiss(_registration.Name, _registration.Group, route);
            RouterTelemetry.CountFactoryRun(_registration.Name, _registration.Group, route);
            var made = await factory(cancellationToken).ConfigureAwait(false) ?? Empty();

            // Nothing is stored for an empty hash: the path has no form that caches one.
            if (made.Count == 0)
            {
                return made;
            }

            HashFields.ValidateForWrite(made);
            var box = _library.KeepsStoredOnFill ? new HashFillBox<T>() : null;
            await FillAsync(key, made, expiry, existing.Write, (generation, epoch), box, cancellationToken).ConfigureAwait(false);

            // A fill that lost leaves the stored hash, which is the one the call returns.
            return Winner(box) ?? made;
        }
        finally
        {
            _keys.Release(key, state);
        }
    }

    private ValueTask<WriteOutcome> FillAsync(string key, IDictionary<string, T?> made, CacheExpiry expiry, Route route, (long Generation, long Epoch) mark, HashFillBox<T>? box, CancellationToken cancellationToken)
    {
        var deadline = Deadline(expiry);

        // A fill never deletes: a hash that is already dead is not stored.
        if (deadline is { } at && at <= _core.Clock.GetUtcNow())
        {
            return ValueTask.FromResult(WriteOutcome.NotSent);
        }

        var options = new HashWriteOptions(HashCacheSetOption.KeyReplace, deadline);
        return MutateAsync(key, route, (client, token) => client.FillAsync(key, made, options, box, token), current => current.Generation == mark.Generation && _epoch.Value == mark.Epoch, cancellationToken, box is null ? null : () => box.Lost);
    }
}
