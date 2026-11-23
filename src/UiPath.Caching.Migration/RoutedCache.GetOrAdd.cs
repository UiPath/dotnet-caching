namespace UiPath.Caching.Migration;

internal sealed partial class RoutedCache<T>
{
    // The closure is made after the local tier has answered, so a hit allocates none.
    private async ValueTask<T?> GetOrAddCoreAsync<TArg>(string key, TArg argument, Func<TArg, CancellationToken, Task<T?>>? plain, Func<TArg, CancellationToken, Task<GeneratedValue<T>>>? expiring, CacheExpiry expiry, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (plain is null && expiring is null)
        {
            throw new ArgumentNullException(nameof(plain));
        }

        using var admission = Admit(out var route);
        var local = new LocalKey(_registration.Name, key);
        if (TryLocal(local, route, out var hit))
        {
            return hit;
        }

        return await LoadSingleFlightAsync(key, local, route, argument, plain, expiring, expiry, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<T?> LoadSingleFlightAsync<TArg>(string key, LocalKey local, Route route, TArg argument, Func<TArg, CancellationToken, Task<T?>>? plain, Func<TArg, CancellationToken, Task<GeneratedValue<T>>>? expiring, CacheExpiry expiry, CancellationToken cancellationToken)
    {
        // A generator that names the life of what it made: the life is known only after it ran, so the write asks for it then.
        CacheExpiry made = default;
        Func<CancellationToken, Task<T?>> factory = expiring is null
            ? token => (plain ?? throw new InvalidOperationException("A fill generator is required."))(argument, token)
            : async token =>
            {
                var generated = await expiring(argument, token).ConfigureAwait(false);
                made = generated.Expiration is { } at ? CacheExpiry.At(at) : default;
                return generated.Value;
            };
        Func<CacheExpiry>? late = expiring is null ? null : () => made;
        while (true)
        {
            var load = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var mine = new Flight(load.Task, cancellationToken);
            var flight = _flights.GetOrAdd(key, mine);
            if (!ReferenceEquals(flight, mine))
            {
                try
                {
                    return await flight.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && flight.LeaderToken.IsCancellationRequested)
                {
                    // The caller that ran the load was cancelled and this one is not, so it loads for itself.
                    continue;
                }
            }

            try
            {
                var value = await LoadAsync(key, local, route, factory, expiry, late, cancellationToken).ConfigureAwait(false);
                _flights.TryRemove(new KeyValuePair<string, Flight>(key, mine));
                load.SetResult(value);
                return value;
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
}
