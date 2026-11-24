namespace UiPath.Caching.Migration;

internal sealed partial class RoutedHashCache<T>
{
    private delegate ValueTask<WriteResult> Command(IHashPathClient<T> client, CancellationToken token);

    /// <summary>The hash as the active path reads it, falling back as the string path does; throws when the call has no answer and must not run a generator.</summary>
    private async Task<Loaded> ReadAsync(string key, string[]? fields, Route route, CancellationToken cancellationToken)
    {
        var name = _registration.Name;
        var group = _registration.Group;
        var first = await Client(route).ReadAsync(key, fields, cancellationToken).ConfigureAwait(false);
        if (first.Kind is ReadOutcome.Found or ReadOutcome.Absent)
        {
            return Answered(first, route);
        }

        if (first.Kind == ReadOutcome.Unreadable)
        {
            RouterTelemetry.CountUnreadable(name, group, route.Path, route.Reason);
        }
        else
        {
            RouterTelemetry.CountUnavailable(name, group, route.Path, route.Reason);
            if (route.Path == RoutePath.Source)
            {
                return new Loaded(HashRead<T>.Of(ReadOutcome.Absent), route);
            }
        }

        // In bypass the library is not asked, whatever the source said.
        if (route.Bypass)
        {
            return new Loaded(HashRead<T>.Of(ReadOutcome.Absent), route);
        }

        if (route.NoFallback || Client(route).NoFallback || !MayFallBack(route))
        {
            return first.Kind == ReadOutcome.Unavailable ? throw new CacheUnavailableException($"The library cannot answer for the pinned group of cache '{name}'.") : new Loaded(HashRead<T>.Of(ReadOutcome.Absent), route);
        }

        return await ReadOtherAsync(key, fields, route, first.Kind == ReadOutcome.Unavailable, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Loaded> ReadOtherAsync(string key, string[]? fields, Route route, bool firstUnavailable, CancellationToken cancellationToken)
    {
        var second = await Client(route.Other).ReadAsync(key, fields, cancellationToken).ConfigureAwait(false);
        if (second.Kind == ReadOutcome.Found)
        {
            RouterTelemetry.CountRedisHit(_registration.Name, _registration.Group, route.Other, Route.OtherClientRead);
            return new Loaded(second, route);
        }

        if (second.Kind == ReadOutcome.Unreadable)
        {
            RouterTelemetry.CountUnreadable(_registration.Name, _registration.Group, route.Other, Route.OtherClientUnreadable);
        }
        else if (second.Kind == ReadOutcome.Unavailable)
        {
            RouterTelemetry.CountUnavailable(_registration.Name, _registration.Group, route.Other, Route.OtherClientRead);
        }

        // The library could not answer and the source gave no answer either, or one that cannot be told from a miss: no generator run.
        if (firstUnavailable && second.Kind is ReadOutcome.Unreadable or ReadOutcome.Unavailable)
        {
            throw new CacheUnavailableException($"Neither client of cache '{_registration.Name}' could answer.");
        }

        // The library just failed, so what the generator makes goes to the source first; the library stays the fallback when the source refuses to send.
        var write = firstUnavailable && route.Path == RoutePath.Library && MayFallBack(route) ? new Route(RoutePath.Source, Route.OtherClientRead) : route;
        return new Loaded(HashRead<T>.Of(ReadOutcome.Absent), write);
    }

    private Loaded Answered(HashRead<T> read, Route route)
    {
        if (read.Kind == ReadOutcome.Found)
        {
            RouterTelemetry.CountRedisHit(_registration.Name, _registration.Group, route.Path, route.Reason);
        }

        return new Loaded(read, route);
    }

    private async ValueTask<WriteOutcome> MutateAsync(string key, Route route, Command command, Func<KeyState, bool>? validate, CancellationToken cancellationToken, Func<bool>? lostFill = null)
    {
        // Before anything is sent: nothing to settle.
        cancellationToken.ThrowIfCancellationRequested();
        await _fence.EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _keys.Acquire(key);
            try
            {
                await state.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return validate is not null && !validate(state)
                        ? WriteOutcome.NotSent
                        : await SendAsync(key, route, command, state, lostFill, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    state.Lock.Release();
                }
            }
            finally
            {
                _keys.Release(key, state);
            }
        }
        finally
        {
            _fence.ExitWrite();
        }
    }

    private async ValueTask<WriteOutcome> SendAsync(string key, Route captured, Command command, KeyState state, Func<bool>? lostFill, CancellationToken cancellationToken)
    {
        var route = _core.Revalidate(_pin, captured);
        var result = await CommandAsync(Client(route), key, route, command, state, cancellationToken).ConfigureAwait(false);

        // A fill that lost to a stored hash was sent and answered: the other client must not write over the winner.
        if (result.Outcome == WriteOutcome.NotSent && !route.Bypass && !route.NoFallback && !Client(route).NoFallback && lostFill?.Invoke() != true && MayFallBack(route))
        {
            // A token cancelled before the library sent anything must not reach the other client: that command would be sent already cancelled.
            cancellationToken.ThrowIfCancellationRequested();
            result = await CommandAsync(Client(route.Other), key, route, command, state, cancellationToken).ConfigureAwait(false);
        }

        Settle(key, result, route, state);
        return result.Outcome;
    }

    private async ValueTask<WriteResult> CommandAsync(IHashPathClient<T> client, string key, Route route, Command command, KeyState state, CancellationToken cancellationToken)
    {
        try
        {
            return await command(client, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled in flight: the command may still land, for as long as its client says.
            Settle(key, client.SettleWithin is { } within ? WriteResult.UnknownWithin(within) : WriteOutcome.Unknown, route, state);
            throw;
        }
    }

    /// <summary>A write of unknown outcome keeps the key pending until it can no longer land; any write that was sent ends the loads that began before it.</summary>
    private void Settle(string key, WriteResult result, Route route, KeyState state)
    {
        if (result.Outcome == WriteOutcome.NotSent)
        {
            return;
        }

        if (result.Outcome == WriteOutcome.Unknown)
        {
            var window = result.SettleWithin is { } settle && settle > _registration.PendingWindow ? settle : _registration.PendingWindow;
            var held = Client(route).SettledAsync(key);

            // The sentinel is told by reference: no real task, even a completed one, stands for it.
            _pending.Begin(key, window, ReferenceEquals(held, PathClientTasks.NoPendingRetry) ? null : held);
            RouterTelemetry.CountPending(_registration.Name, _registration.Group, route);
        }

        _library.KeyChanged(key);
        state.Advance();
        _flights.TryRemove(key, out _);
    }
}
