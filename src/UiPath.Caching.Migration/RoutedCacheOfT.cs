using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Broadcast;

namespace UiPath.Caching.Migration;

internal sealed class RoutedCache<T> : IRouterParticipant, IRoutedCache<T>, IDisposable
    where T : class
{
    /// <summary>A command that changed nothing (a create that found the key held): neither retried on the other client nor settled.</summary>
    private const WriteOutcome Unchanged = (WriteOutcome)(-1);

    private static readonly Route ClearRoute = new(RoutePath.Source, Route.Flag);

    /// <summary>What the local tier holds for a cached null, which it cannot hold itself.</summary>
    private static readonly object NullMarker = new();


    private readonly RouterCore _core;
    private readonly RouterCacheRegistration<T> _registration;
    private readonly IPathClient<T> _source;
    private readonly TopicKey _topic;
    private readonly string _eventName;
    private readonly CacheEpoch _epoch = new();
    private readonly ConcurrentDictionary<string, Flight> _flights = new(StringComparer.Ordinal);
    private readonly KeyGates _keys = new();
    private readonly WriteFence _fence = new();
    private readonly PendingMutations _pending;
    private readonly InvalidationRetrier _retrier;
    private volatile IPathClient<T> _library = UnavailablePathClient<T>.Instance;
    private volatile CacheEventPublisher? _publisher;
    private IDisposable? _subscription;
    private ITopic<ICacheEvent>? _topicHandle;
    private int _attached;

    /// <summary>A cache whose library is not there yet: <see cref="Prepare(LibraryParts)"/> brings it in.</summary>
    public RoutedCache(RouterCore core, RouterCacheRegistration<T> registration)
    {
        _core = core;
        _registration = registration;
        _source = new SourcePathClient<T>(registration.Source, registration.CacheNulls);
        _eventName = registration.Version is { } version ? $"{registration.Name}@{version()}" : registration.Name;
        _topic = new TopicKey($"router:{_eventName}");
        _pending = new PendingMutations(core, registration.Name, key => AnnounceRemovalAsync(key, ClearRoute), DetachKey);
        _retrier = new InvalidationRetrier(core, registration.LocalCap, PublishRemovalOnceAsync, PublishClearOnceAsync, CountPublishFailure);
        core.Register(this);
    }

    public RoutedCache(
        RouterCore core,
        RouterCacheRegistration<T> registration,
        IPathClient<T> library,
        ITopicProvider topics,
        ICacheEventFactory events,
        Uri ownSource)
        : this(core, registration) => Prepare(library, topics, events, ownSource).Commit();

    public TopicKey? Topic => _topicHandle is null ? null : _topic;

    public bool IsSubscribed => _topicHandle is not null && (_topicHandle is not ITopicSubscriptionState state || state.IsSubscribed);

    internal CacheEpoch Epoch => _epoch;

    internal RouterCacheRegistration<T> Registration => _registration;

    internal InvalidationRetrier Retrier => _retrier;

    internal int HeldKeys => _keys.Count;

    public async ValueTask<T?> GetOrAddAsync(string key, Func<CancellationToken, Task<T?>> factory, CacheExpiry expiry = default, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);
        using var admission = _core.Admit(_registration.Group, out var route);
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
        using var admission = _core.Admit(_registration.Group, out var route);
        var local = new LocalKey(_registration.Name, key);
        if (TryLocal(local, route, out var hit))
        {
            return hit;
        }

        var mark = _core.Tier.Capture(local, _epoch);
        var read = await ReadAsync(key, local, route, mark, cancellationToken).ConfigureAwait(false);
        if (!read.Found)
        {
            RouterTelemetry.CountMiss(_registration.Name, _registration.Group, route);
        }

        return read.Value;
    }

    public async ValueTask<WriteOutcome> SetAsync(string key, T value, CacheExpiry expiry = default, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        using var admission = _core.Admit(_registration.Group, out var route);
        return await WriteAsync(key, value, expiry, route, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Added settles like a write; an entry already there changes nothing, so nothing is evicted or announced. A deadline already passed creates nothing.</summary>
    public async ValueTask<AddOutcome> TryAddAsync(string key, T value, CacheExpiry expiry = default, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        using var admission = _core.Admit(_registration.Group, out var route);
        var deadline = Deadline(expiry);
        if (deadline is { } at && at <= _core.Clock.GetUtcNow())
        {
            return AddOutcome.NotSent;
        }

        var reported = AddOutcome.NotSent;
        var outcome = await MutateAsync(
            key,
            route,
            async (client, token) =>
            {
                var result = await client.TryAddAsync(key, value, deadline, token).ConfigureAwait(false);
                reported = result.Outcome;
                return result.Outcome switch
                {
                    AddOutcome.Added => WriteOutcome.Committed,
                    AddOutcome.Exists => Unchanged,
                    AddOutcome.NotSent => WriteOutcome.NotSent,
                    _ => result.SettleWithin is { } within ? WriteResult.UnknownWithin(within) : WriteOutcome.Unknown,
                };
            },
            deadline ?? DateTimeOffset.MaxValue,
            null,
            cancellationToken).ConfigureAwait(false);
        return outcome == WriteOutcome.NotSent ? AddOutcome.NotSent : reported;
    }

    public async ValueTask<WriteOutcome> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        using var admission = _core.Admit(_registration.Group, out var route);
        return await MutateAsync(key, route, (client, token) => client.RemoveAsync(key, token), null, null, cancellationToken).ConfigureAwait(false);
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

    public void Dispose() => _subscription?.Dispose();

    public void ResetLocal() => ClearLocal();

    public IRouterParticipant.IAttachment Prepare(LibraryParts library) =>
        Prepare(new LibraryPathClient<T>(library.Store, library.Connection, _registration.RedisKey, library.LibrarySettleWithin) { CacheNulls = _registration.CacheNulls }, library.Topics, library.Events, library.Source);

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

    private async ValueTask<bool> TryPublishAsync(Func<ValueTask<bool>> publish)
    {
        try
        {
            // A publish that hangs counts as failed, so a stuck library cannot hold a write, a clear or a stop.
            return await publish().AsTask().WaitAsync(_core.SourceTimeout, _core.Clock, _core.StopToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private LibraryAttachment Prepare(IPathClient<T> library, ITopicProvider topics, ICacheEventFactory events, Uri ownSource)
    {
        var publisher = new CacheEventPublisher(_eventName, topics, events, NullLogger.Instance);
        var topic = topics.Create(_topic);
        return new LibraryAttachment(this, library, publisher, topic, topic.Subscribe(new PeerObserver(ownSource, EvictLocal, ClearLocal)));
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
        if (route.Tier && _registration.LocalCap > TimeSpan.Zero && _core.Tier.TryGet(local, _epoch, out var found) && (found is T || ReferenceEquals(found, NullMarker)))
        {
            RouterTelemetry.CountLocalHit(_registration.Name, _registration.Group, route);
            value = found as T;
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
            if (existing.Found)
            {
                return existing.Value;
            }

            RouterTelemetry.CountMiss(_registration.Name, _registration.Group, route);
            RouterTelemetry.CountFactoryRun(_registration.Name, _registration.Group, route);
            var value = await factory(cancellationToken).ConfigureAwait(false);

            // Checked under the key's lock and the write fence, where this node's writes and clears of the key cannot slip in before the send.
            if (value is not null)
            {
                await WriteAsync(key, value, expiry, existing.Write, current => current.Generation == generation && _epoch.Value == mark.Epoch, cancellationToken).ConfigureAwait(false);
            }

            return value;
        }
        finally
        {
            _keys.Release(key, state);
        }
    }

    /// <summary>A hit, filled into the local tier; null for a miss. Throws when the call has no answer and must not run the factory.</summary>
    private async Task<Loaded> ReadAsync(string key, LocalKey local, Route route, FillMark mark, CancellationToken cancellationToken)
    {
        var name = _registration.Name;
        var group = _registration.Group;
        var readStart = _core.Clock.GetUtcNow();
        var first = await Client(route.Path).ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (first.Kind == ReadOutcome.Found)
        {
            return new Loaded(Hit(first, local, mark, readStart, route.Path, route.Reason, route.Tier), route, Found: true);
        }

        if (first.Kind == ReadOutcome.Absent)
        {
            return new Loaded(null, route);
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
                return new Loaded(null, route);
            }
        }

        // In bypass the library is not asked, whatever the source said.
        if (route.Bypass)
        {
            return new Loaded(null, route);
        }

        var second = await Client(route.Other).ReadAsync(key, cancellationToken).ConfigureAwait(false);
        var firstUnavailable = first.Kind == ReadOutcome.Unavailable;
        switch (second.Kind)
        {
            case ReadOutcome.Found:
                return new Loaded(Hit(second, local, mark, readStart, route.Other, Route.OtherClientRead, route.Tier), route, Found: true);
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

        // The library just failed, so what the factory makes goes to the source first; the library stays the fallback when the source refuses to send.
        return new Loaded(null, firstUnavailable && route.Path == RoutePath.Library ? new Route(RoutePath.Source, Route.OtherClientRead) : route);
    }

    private T? Hit(PathRead<T> read, LocalKey local, FillMark mark, DateTimeOffset readStart, RoutePath path, string reason, bool fill)
    {
        RouterTelemetry.CountRedisHit(_registration.Name, _registration.Group, path, reason);
        var value = read.Value;
        var cap = _registration.LocalCap;
        if (fill && cap > TimeSpan.Zero)
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
                _core.Tier.TryFill(local, mark, _epoch, (object?)value ?? NullMarker, readStart + lifetime);
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
        var announcement = new Announcement();
        try
        {
            return await MutateLockedAsync(key, route, command, validate, announcement, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The key lock and the write slot are free by now: a slow publish holds back neither the next write of the key nor a clear.
            if (announcement.Route is { } announced)
            {
                if (announced.Bypass)
                {
                    AnnounceFromBypass(key, announced);
                }
                else
                {
                    await PublishKeyAsync(key, setDeadline, announced).ConfigureAwait(false);
                }
            }
        }
    }

    private async ValueTask<WriteOutcome> MutateLockedAsync(string key, Route route, Func<IPathClient<T>, CancellationToken, ValueTask<WriteResult>> command, Func<KeyState, bool>? validate, Announcement announcement, CancellationToken cancellationToken)
    {
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
                        : await SendAsync(key, route, command, state, announcement, cancellationToken).ConfigureAwait(false);
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

    private async ValueTask<WriteOutcome> SendAsync(string key, Route route, Func<IPathClient<T>, CancellationToken, ValueTask<WriteResult>> command, KeyState state, Announcement announcement, CancellationToken cancellationToken)
    {
        var result = await CommandAsync(Client(route.Path), key, route, command, state, announcement, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == WriteOutcome.NotSent && !route.Bypass)
        {
            // A token cancelled before the library sent anything must not reach the other client: that command would be sent already cancelled.
            cancellationToken.ThrowIfCancellationRequested();
            result = await CommandAsync(Client(route.Other), key, route, command, state, announcement, cancellationToken).ConfigureAwait(false);
        }

        Settle(key, result, route, state, announcement);
        return result.Outcome;
    }

    private async ValueTask<WriteResult> CommandAsync(IPathClient<T> client, string key, Route route, Func<IPathClient<T>, CancellationToken, ValueTask<WriteResult>> command, KeyState state, Announcement announcement, CancellationToken cancellationToken)
    {
        try
        {
            return await command(client, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled in flight: the command may still land, for as long as its client says.
            Settle(key, client.SettleWithin is { } within ? WriteResult.UnknownWithin(within) : WriteOutcome.Unknown, route, state, announcement);
            throw;
        }
    }

    /// <summary>Evict on this node and advance the mark; a write of unknown outcome stays off the local tier until it can no longer land, then does both again. The caller publishes once the key's lock is released.</summary>
    private void Settle(string key, WriteResult result, Route route, KeyState state, Announcement announcement)
    {
        if (result.Outcome is WriteOutcome.NotSent or Unchanged)
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
        announcement.Route = route;
    }
    private async ValueTask PublishKeyAsync(string key, DateTimeOffset? setDeadline, Route route)
    {
        var options = new RouterEntryOptions(key, _topic, setDeadline ?? _core.Clock.GetUtcNow());
        var publisher = _publisher;
        var published = publisher is not null && await TryPublishAsync(() => setDeadline is null ? publisher.CacheRemovedAsync(options) : publisher.CacheSetAsync(options)).ConfigureAwait(false);
        if (!published)
        {
            CountPublishFailure(route);
            _retrier.ReportKey(key, route);
        }
    }

    /// <summary>Hands the announcement to the retrier worker, so the caller never waits on the library.</summary>
    private void AnnounceFromBypass(string key, Route route)
    {
        // No local copy on any node of this cache (the cap is the cache's own setting), so there is nothing to evict elsewhere.
        if (_registration.LocalCap <= TimeSpan.Zero)
        {
            return;
        }

        if (_publisher is null)
        {
            CountPublishFailure(route);
            return;
        }

        _retrier.ReportKey(key, route);
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

    // Without a started library nothing can be published; the failure is counted and retried like any other.
    private ValueTask<bool> PublishRemovalOnceAsync(string key) =>
        _publisher is { } publisher
            ? TryPublishAsync(() => publisher.CacheRemovedAsync(new RouterEntryOptions(key, _topic, _core.Clock.GetUtcNow())))
            : ValueTask.FromResult(false);

    private ValueTask<bool> PublishClearOnceAsync() =>
        _publisher is { } publisher ? TryPublishAsync(() => publisher.TopicInvalidatedAsync(_topic)) : ValueTask.FromResult(false);

    private void CountPublishFailure(Route route)
    {
        RouterTelemetry.CountPublishFailed(_registration.Name, _registration.Group, route);
        _core.LogPublishFailed(_registration.Name);
    }

    private readonly record struct Loaded(T? Value, Route Write, bool Found = false);

    private sealed class Announcement
    {
        public Route? Route { get; set; }
    }

    private sealed class Flight(Task<T?> task, CancellationToken leaderToken)
    {
        public Task<T?> Task { get; } = task;

        public CancellationToken LeaderToken { get; } = leaderToken;
    }

    private sealed class LibraryAttachment(RoutedCache<T> owner, IPathClient<T> library, CacheEventPublisher publisher, ITopic<ICacheEvent> topic, IDisposable subscription) : IRouterParticipant.IAttachment
    {
        private bool _committed;

        public void Commit()
        {
            if (Interlocked.CompareExchange(ref owner._attached, 1, 0) != 0)
            {
                subscription.Dispose();
                return;
            }

            _committed = true;
            owner._subscription = subscription;
            owner._topicHandle = topic;
            owner._publisher = publisher;
            owner._library = library;
        }

        public void Dispose()
        {
            if (!_committed)
            {
                subscription.Dispose();
            }
        }
    }
}
