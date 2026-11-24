using System.Collections.Concurrent;
using UiPath.Caching.Broadcast;

namespace UiPath.Caching.Migration;

internal sealed partial class RoutedHashCache<T> : IRouterParticipant, IRouterClear, IHashCache<T>, ISpanKeyHashCache<T>, IDisposable
    where T : class
{
    private static readonly CacheEntryFactory Entries = new();

    private readonly RouterCore _core;
    private readonly RouterHashCacheRegistration<T> _registration;
    private readonly GroupPin? _pin;
    private readonly IHashPathClient<T> _source;
    private readonly ConcurrentDictionary<string, Flight> _flights = new(StringComparer.Ordinal);
    private readonly CacheEpoch _epoch = new();
    private readonly KeyGates _keys = new();
    private readonly WriteFence _fence = new();
    private readonly PendingMutations _pending;
    private volatile IHashPathClient<T> _library = UnavailableHashPathClient<T>.Instance;
    private int _attached;

    /// <summary>A cache whose library is not there yet: <see cref="Prepare(LibraryParts)"/> brings it in.</summary>
    public RoutedHashCache(RouterCore core, RouterHashCacheRegistration<T> registration)
    {
        _core = core;
        _registration = registration;
        _pin = core.PinFor(registration.Group);
        _source = new SourceHashPathClient<T>(registration.Source, registration.Name);
        _pending = new PendingMutations(core, registration.Name, _ => ValueTask.CompletedTask, DetachKey);
        core.Register(this);
    }

    public RoutedHashCache(RouterCore core, RouterHashCacheRegistration<T> registration, IHashPathClient<T> library)
        : this(core, registration) => Attach(library);

    public bool IsSubscribed => Volatile.Read(ref _attached) == 1;

    public TopicKey? Topic => null;

    /// <summary>The library path in force: the key move client once the library is up and the cache is bound to a key move group.</summary>
    internal IHashPathClient<T> Library => _library;

    internal int HeldKeys => _keys.Count;

    internal int PendingCount => _pending.Count;

    internal PendingMutations Pending => _pending;

    public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        if (_library.Moving)
        {
            throw new NotSupportedException($"Cache '{_registration.Name}' cannot be cleared by the router while its group's keys move: the move has its own clear, which closes the cache's gate on both sides.");
        }

        using var admission = _core.Admit();
        cancellationToken.ThrowIfCancellationRequested();
        Forget();

        // Writes of this cache that are under way finish first and none starts until the clear ends, so none lands in the middle of the scan.
        await _fence.EnterClearAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _registration.Source.ClearAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fence.ExitClear();
            Forget();
        }
    }

    public void Dispose() => (_library as IDisposable)?.Dispose();

    public void ResetLocal() => Forget();

    public void EvictLocal(string key) => DetachKey(key);

    public IReadOnlyList<(string Cache, string Key, DateTimeOffset Until)> PendingKeys() => [.. _pending.Snapshot().Select(p => (_registration.Name, p.Key, p.Until))];

    public IRouterParticipant.IAttachment Prepare(LibraryParts library) => new LibraryAttachment(this, LibraryClient(library));

    public Task WhenPendingSettledAsync() => _pending.WhenEmptyAsync();

    public Task StopRetriesAsync() => _library.StopRetriesAsync();

    public void WarnRetriesInFlight()
    {
        var keys = _library.InFlightKeys;
        if (keys.Count > 0)
        {
            _core.LogRetriesInFlight(_registration.Name, string.Join(", ", keys));
        }
    }

    public Task InvalidateAsync() => Task.CompletedTask;

    public Task FlushAsync() => _pending.FlushAsync();

    private IHashPathClient<T> LibraryClient(LibraryParts library)
    {
        var store = library.HashStore ?? throw new InvalidOperationException($"The router has no library hash cache for '{_registration.Name}'.");
        var plain = new LibraryHashPathClient<T>(store, library.Connection, _registration.RedisKey, library.LibrarySettleWithin) { Cache = library.HashCache, Tier = library.HashTier };

        // A registration that carries its own binding keeps it; otherwise the key move setup binds the library's codecs.
        var binding = _registration.KeyMove ?? library.Binder?.BindHash<T>(_registration.Name, _registration.MaxHashFields);
        if (library.KeyMove is not { } plane || binding is null)
        {
            return plain;
        }

        // The inventory judges the cache's hashes by the binding in force, whichever way it came.
        library.Binder?.RegisterHash(_registration.Name, binding);
        var client = new KeyMoveHashClient<T>(KeyMoveHashClientParts<T>.From(_registration.Group, _registration.Name, binding, plain, plane, _core.Clock, library.LibrarySettleWithin));

        // The mover promotes through the client's own promotion, so it helps a marked key the way the client does.
        library.Movers?.Add(_registration.Group, new HashMover<T>(client.Parts, client.Promotion));
        return client;
    }

    private bool Attach(IHashPathClient<T> library)
    {
        if (Interlocked.CompareExchange(ref _attached, 1, 0) != 0)
        {
            return false;
        }

        _library = library;
        return true;
    }

    /// <summary>A load that began before this point must not write, and a caller that starts now must not join it.</summary>
    private void Forget()
    {
        _library.Gap();
        _epoch.Advance();
        _flights.Clear();
    }

    /// <summary>A caller that starts now must not join a load that began before, and a load that began before must not write.</summary>
    private void DetachKey(string key)
    {
        _library.KeyChanged(key);
        _flights.TryRemove(key, out _);
        _keys.Advance(key);
    }

    private IHashPathClient<T> Client(RoutePath path) => path == RoutePath.Library ? _library : _source;

    private IHashPathClient<T> Client(Route route) => route.Hold ? UnavailableHashPathClient<T>.Instance : Client(route.Path);

    private bool MayFallBack(Route route) => route.Other != RoutePath.Source || _core.MaySourceFallback(_pin);

    private Admission Admit(out Route route) => _pin is null ? _core.Admit(_registration.Group, out route) : _core.Admit(_pin, _registration.Group, out route);

    private DateTimeOffset? Deadline(CacheExpiry expiry)
    {
        if (expiry.Deadline is { } absolute)
        {
            return absolute == DateTimeOffset.MaxValue ? null : absolute;
        }

        var deadline = _core.Clock.ToDateTimeOffset(expiry.Relative ?? _registration.DefaultExpiry);
        return deadline == DateTimeOffset.MaxValue ? null : deadline;
    }

    private readonly record struct Loaded(HashRead<T> Read, Route Write);

    private sealed class Flight(Task<IDictionary<string, T?>> task, CancellationToken leaderToken)
    {
        public Task<IDictionary<string, T?>> Task { get; } = task;

        public CancellationToken LeaderToken { get; } = leaderToken;
    }

    private sealed class LibraryAttachment(RoutedHashCache<T> owner, IHashPathClient<T> library) : IRouterParticipant.IAttachment
    {
        private bool _committed;

        public void Commit() => _committed = owner.Attach(library);

        public void Dispose()
        {
            if (!_committed)
            {
                (library as IDisposable)?.Dispose();
            }
        }
    }
}
