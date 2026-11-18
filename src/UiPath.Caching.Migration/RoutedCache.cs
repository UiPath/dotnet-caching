using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Broadcast;

namespace UiPath.Caching.Migration;

internal sealed class RoutedCache<T> : IRoutedCache<T>, IStoppable, IDisposable
    where T : class
{
    private static readonly Route ClearRoute = new(RoutePath.Source, Route.Flag);

    private readonly RouterCore _core;
    private readonly RouterCacheRegistration<T> _registration;
    private readonly IPathClient<T> _source;
    private readonly IPathClient<T> _library;
    private readonly CacheEventPublisher _publisher;
    private readonly TopicKey _topic;
    private readonly CacheEpoch _epoch = new();
    private readonly ConcurrentDictionary<string, Flight> _flights = new(StringComparer.Ordinal);
    private readonly KeyGates _keys = new();
    private readonly WriteFence _fence = new();
    private readonly PendingMutations _pending;
    private readonly InvalidationRetrier _retrier;
    private readonly IDisposable _subscription;

    public RoutedCache(
        RouterCore core,
        RouterCacheRegistration<T> registration,
        IPathClient<T> library,
        ITopicProvider topics,
        ICacheEventFactory events,
        Uri ownSource)
    {
        _core = core;
        _registration = registration;
        _library = library;
        _source = new SourcePathClient<T>(registration.Source);
        _topic = new TopicKey($"router:{registration.Name}");
        _publisher = new CacheEventPublisher(registration.Name, topics, events, NullLogger.Instance);
        _pending = new PendingMutations(core, registration.Name, key => AnnounceRemovalAsync(key, ClearRoute), DetachKey);
        _retrier = new InvalidationRetrier(core, registration.LocalCap, PublishRemovalOnceAsync, PublishClearOnceAsync, CountPublishFailure);
        _subscription = topics.Create(_topic).Subscribe(new PeerObserver(ownSource, EvictLocal, ClearLocal));
    }

    internal CacheEpoch Epoch => _epoch;

    internal InvalidationRetrier Retrier => _retrier;

    internal int HeldKeys => _keys.Count;

    public async ValueTask<T?> GetOrAddAsync(string key, Func<CancellationToken, Task<T?>> factory, CacheExpiry expiry = default, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);
        using var admission = _core.Admit();
        var route = _core.ChoosePath(_registration.Group);
        var local = new LocalKey(_registration.Name, key);
        if (TryLocal(local, route, out var hit))
        {
            return hit;
        }

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
                var value = await LoadAsync(key, local, route, factory, expiry, cancellationToken).ConfigureAwait(false);
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

    public async ValueTask<T?> TryGetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        using var admission = _core.Admit();
        var route = _core.ChoosePath(_registration.Group);
        var local = new LocalKey(_registration.Name, key);
        if (TryLocal(local, route, out var hit))
        {
            return hit;
        }

        var mark = _core.Tier.Capture(local, _epoch);
        var read = await ReadAsync(key, local, route, mark, cancellationToken).ConfigureAwait(false);
        if (read is null)
        {
            RouterTelemetry.CountMiss(_registration.Name, _registration.Group, route);
        }

        return read;
    }

    public async ValueTask<WriteOutcome> SetAsync(string key, T value, CacheExpiry expiry = default, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        using var admission = _core.Admit();
        return await WriteAsync(key, value, expiry, _core.ChoosePath(_registration.Group), null, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<WriteOutcome> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        using var admission = _core.Admit();
        return await MutateAsync(key, _core.ChoosePath(_registration.Group), (client, token) => client.RemoveAsync(key, token), null, null, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        using var admission = _core.Admit();
        cancellationToken.ThrowIfCancellationRequested();
        ClearLocal();
        await PublishClearAsync().ConfigureAwait(false);
        try
        {
            // Writes of this cache that are under way finish first and none starts until the clear ends, so none lands in the middle of the scan.
            await _fence.EnterClearAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _registration.Source.ClearAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _fence.ExitClear();
            }
        }
        finally
        {
            ClearLocal();
            await PublishClearAsync().ConfigureAwait(false);
        }
    }

    public void Dispose() => _subscription.Dispose();

    public Task WhenPendingSettledAsync() => _pending.WhenEmptyAsync();

    public async Task InvalidateAsync()
    {
        ClearLocal();
        await PublishClearOnceAsync().ConfigureAwait(false);
    }

    public async Task FlushAsync()
    {
        await _pending.FlushAsync().ConfigureAwait(false);
        await _retrier.FlushAsync().ConfigureAwait(false);
    }

    private static async ValueTask<bool> TryPublishAsync(Func<ValueTask<bool>> publish)
    {
        try
        {
            return await publish().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private void EvictLocal(string key)
    {
        _core.Tier.Evict(new LocalKey(_registration.Name, key));
        DetachKey(key);
    }

    /// <summary>A caller that starts now must not join a load that began before, and a load that began before must not write.</summary>
    private void DetachKey(string key)
    {
        _flights.TryRemove(key, out _);
        _keys.Advance(key);
    }

    private void ClearLocal()
    {
        _epoch.Advance();
        _flights.Clear();
    }

    private IPathClient<T> Client(RoutePath path) => path == RoutePath.Library ? _library : _source;

    private bool TryLocal(LocalKey local, Route route, out T? value)
    {
        value = null;
        if (_registration.LocalCap > TimeSpan.Zero && _core.Tier.TryGet(local, _epoch, out var found) && found is T typed)
        {
            RouterTelemetry.CountLocalHit(_registration.Name, _registration.Group, route);
            value = typed;
            return true;
        }

        return false;
    }

    private async Task<T?> LoadAsync(string key, LocalKey local, Route route, Func<CancellationToken, Task<T?>> factory, CacheExpiry expiry, CancellationToken cancellationToken)
    {
        var state = _keys.Acquire(key);
        try
        {
            var generation = state.Generation;
            var mark = _core.Tier.Capture(local, _epoch);
            var existing = await ReadAsync(key, local, route, mark, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return existing;
            }

            RouterTelemetry.CountMiss(_registration.Name, _registration.Group, route);
            RouterTelemetry.CountFactoryRun(_registration.Name, _registration.Group, route);
            var value = await factory(cancellationToken).ConfigureAwait(false);

            // Checked under the key's lock and the write fence, where this node's writes and clears of the key cannot slip in before the send.
            if (value is not null)
            {
                await WriteAsync(key, value, expiry, route, current => current.Generation == generation && _epoch.Value == mark.Epoch, cancellationToken).ConfigureAwait(false);
            }

            return value;
        }
        finally
        {
            _keys.Release(key, state);
        }
    }

    /// <summary>A hit, filled into the local tier; null for a miss. Throws when the call has no answer and must not run the factory.</summary>
    private async Task<T?> ReadAsync(string key, LocalKey local, Route route, FillMark mark, CancellationToken cancellationToken)
    {
        var name = _registration.Name;
        var group = _registration.Group;
        var readStart = _core.Clock.GetUtcNow();
        var first = await Client(route.Path).ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (first.Kind == ReadOutcome.Found)
        {
            return Hit(first, local, mark, readStart, route.Path, route.Reason);
        }

        if (first.Kind == ReadOutcome.Absent)
        {
            return null;
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
                return null;
            }
        }

        var second = await Client(route.Other).ReadAsync(key, cancellationToken).ConfigureAwait(false);
        var firstUnavailable = first.Kind == ReadOutcome.Unavailable;
        switch (second.Kind)
        {
            case ReadOutcome.Found:
                return Hit(second, local, mark, readStart, route.Other, Route.OtherClientRead);
            case ReadOutcome.Unreadable:
                RouterTelemetry.CountUnreadable(name, group, route.Other, Route.OtherClientUnreadable);
                break;
            case ReadOutcome.Unavailable:
                RouterTelemetry.CountUnavailable(name, group, route.Other, Route.OtherClientRead);
                break;
        }

        // The library could not answer and the source gave no answer either, or one that cannot be told from a miss: no factory run.
        if (firstUnavailable && second.Kind is ReadOutcome.Unreadable or ReadOutcome.Unavailable)
        {
            throw new CacheUnavailableException($"Neither client of cache '{name}' could answer.");
        }

        return null;
    }

    private T Hit(PathRead<T> read, LocalKey local, FillMark mark, DateTimeOffset readStart, RoutePath path, string reason)
    {
        RouterTelemetry.CountRedisHit(_registration.Name, _registration.Group, path, reason);
        var value = read.Value!;
        var cap = _registration.LocalCap;
        if (cap > TimeSpan.Zero)
        {
            var lifetime = cap;
            if (read.Expiration is { } expiration)
            {
                // The deadline is as of the snapshot's reply; measured from the read start it can only be earlier than Redis's own.
                var remaining = expiration - _core.Clock.GetUtcNow();
                lifetime = remaining < cap ? remaining : cap;
            }

            if (lifetime > TimeSpan.Zero)
            {
                _core.Tier.TryFill(local, mark, _epoch, value, readStart + lifetime);
            }
        }

        return value;
    }

    private ValueTask<WriteOutcome> WriteAsync(string key, T value, CacheExpiry expiry, Route route, Func<KeyState, bool>? validate, CancellationToken cancellationToken)
    {
        var deadline = Deadline(expiry);
        if (deadline is { } at && at <= _core.Clock.GetUtcNow())
        {
            // A deadline already passed is a delete, as the older client ends up doing.
            return MutateAsync(key, route, (client, token) => client.RemoveAsync(key, token), null, validate, cancellationToken);
        }

        return MutateAsync(key, route, (client, token) => client.SetAsync(key, value, deadline, token), deadline ?? DateTimeOffset.MaxValue, validate, cancellationToken);
    }

    private DateTimeOffset? Deadline(CacheExpiry expiry)
    {
        if (expiry.Deadline is { } absolute)
        {
            return absolute;
        }

        var deadline = _core.Clock.ToDateTimeOffset(expiry.Relative ?? _registration.DefaultExpiry);
        return deadline == DateTimeOffset.MaxValue ? null : deadline;
    }

    private async ValueTask<WriteOutcome> MutateAsync(string key, Route route, Func<IPathClient<T>, CancellationToken, ValueTask<WriteResult>> command, DateTimeOffset? setDeadline, Func<KeyState, bool>? validate, CancellationToken cancellationToken)
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
                        : await SendAsync(key, route, command, setDeadline, state, cancellationToken).ConfigureAwait(false);
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

    private async ValueTask<WriteOutcome> SendAsync(string key, Route route, Func<IPathClient<T>, CancellationToken, ValueTask<WriteResult>> command, DateTimeOffset? setDeadline, KeyState state, CancellationToken cancellationToken)
    {
        var client = Client(route.Path);
        WriteResult result;
        try
        {
            result = await command(client, cancellationToken).ConfigureAwait(false);
            if (result.Outcome == WriteOutcome.NotSent)
            {
                client = Client(route.Other);
                result = await command(client, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled in flight: the command may still land, for as long as its client says.
            await SettleAsync(key, client.SettleWithin is { } within ? WriteResult.UnknownWithin(within) : WriteOutcome.Unknown, route, setDeadline, state).ConfigureAwait(false);
            throw;
        }

        await SettleAsync(key, result, route, setDeadline, state).ConfigureAwait(false);
        return result.Outcome;
    }

    /// <summary>Evict on this node and advance the mark, then publish; a write of unknown outcome stays off the local tier until it can no longer land, then does both again.</summary>
    private async ValueTask SettleAsync(string key, WriteResult result, Route route, DateTimeOffset? setDeadline, KeyState state)
    {
        if (result.Outcome == WriteOutcome.NotSent)
        {
            return;
        }

        if (result.Outcome == WriteOutcome.Unknown)
        {
            var window = result.SettleWithin is { } settle && settle > _registration.PendingWindow ? settle : _registration.PendingWindow;
            _pending.Begin(key, window);
            RouterTelemetry.CountPending(_registration.Name, _registration.Group, route);
        }

        _core.Tier.Evict(new LocalKey(_registration.Name, key));
        state.Advance();

        // A caller that starts now must not join a load that began before this write.
        _flights.TryRemove(key, out _);
        await PublishKeyAsync(key, setDeadline, route).ConfigureAwait(false);
    }

    private async ValueTask PublishKeyAsync(string key, DateTimeOffset? setDeadline, Route route)
    {
        var options = new RouterEntryOptions(key, _topic, setDeadline ?? _core.Clock.GetUtcNow());
        var published = await TryPublishAsync(() => setDeadline is null ? _publisher.CacheRemovedAsync(options) : _publisher.CacheSetAsync(options)).ConfigureAwait(false);
        if (!published)
        {
            CountPublishFailure(route);
            _retrier.ReportKey(key, route);
        }
    }

    private async ValueTask AnnounceRemovalAsync(string key, Route route)
    {
        if (!await PublishRemovalOnceAsync(key).ConfigureAwait(false))
        {
            CountPublishFailure(route);
            _retrier.ReportKey(key, route);
        }
    }

    private async ValueTask PublishClearAsync()
    {
        if (!await PublishClearOnceAsync().ConfigureAwait(false))
        {
            CountPublishFailure(ClearRoute);
            _retrier.ReportClear(ClearRoute);
        }
    }

    private ValueTask<bool> PublishRemovalOnceAsync(string key) =>
        TryPublishAsync(() => _publisher.CacheRemovedAsync(new RouterEntryOptions(key, _topic, _core.Clock.GetUtcNow())));

    private ValueTask<bool> PublishClearOnceAsync() => TryPublishAsync(() => _publisher.TopicInvalidatedAsync(_topic));

    private void CountPublishFailure(Route route)
    {
        RouterTelemetry.CountPublishFailed(_registration.Name, _registration.Group, route);
        _core.LogPublishFailed(_registration.Name);
    }

    private sealed class Flight(Task<T?> task, CancellationToken leaderToken)
    {
        public Task<T?> Task { get; } = task;

        public CancellationToken LeaderToken { get; } = leaderToken;
    }
}
