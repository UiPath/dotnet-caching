using StackExchange.Redis;

namespace UiPath.Caching.Migration;

public sealed class RouterBuilder
{
    private readonly List<Func<RouterCore, (string Name, Type Type, object Cache)>> _registrations = [];
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _excluded = new(StringComparer.Ordinal);

    internal IReadOnlyList<Func<RouterCore, (string Name, Type Type, object Cache)>> Registrations => _registrations;

    internal IReadOnlyDictionary<string, string> Excluded => _excluded;

    internal Func<string, bool>? LibraryPredicate { get; private set; }

    internal ISerializerProxy<byte[]>? Envelope { get; private set; }

    internal TimeSpan? SettleBound { get; private set; }

    internal Func<bool>? BypassFlag { get; private set; }

    internal string? NodeIdentity { get; private set; }

    internal TimeSpan? SourceTimeout { get; private set; }

    internal TimeSpan CleanupBound { get; set; } = TimeSpan.FromSeconds(10);

    internal Func<IDatabase>? SourceDatabase { get; private set; }

    internal IChannelBridge? Bridge { get; private set; }

    public RouterBuilder Add<T>(RouterCacheRegistration<T> registration)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.Group);
        ArgumentOutOfRangeException.ThrowIfLessThan(registration.LocalCap, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(registration.PendingWindow, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(registration.PendingWindow, LibrarySettleBound.Horizon);
        // Read once: the value checked here is the one the cache's events use.
        var version = registration.Version is { } provider
            ? provider() ?? throw new ArgumentException($"The version provider of cache '{registration.Name}' returned null.", nameof(registration))
            : null;
        if (version is not null && (string.IsNullOrWhiteSpace(version) || version.AsSpan().IndexOfAny("@+={} ") >= 0))
        {
            throw new ArgumentException($"The version of cache '{registration.Name}' must be non-empty and hold no '@', '+', '=', braces or spaces.", nameof(registration));
        }

        if (_excluded.ContainsKey(registration.Name))
        {
            throw new ArgumentException($"Cache '{registration.Name}' is listed as excluded, so it cannot also be registered.", nameof(registration));
        }

        if (!_names.Add(registration.Name))
        {
            throw new ArgumentException($"A cache named '{registration.Name}' is already registered.", nameof(registration));
        }

        _registrations.Add(core => (
            registration.Name,
            typeof(T),
            new RoutedCache<T>(
                core,
                registration,
                version)));
        return this;
    }

    /// <summary>The adapter over the older client's invalidation channel: the router publishes every key write, delete and clear on it and evicts on what it receives.</summary>
    public RouterBuilder UseChannelBridge(IChannelBridge bridge)
    {
        Bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        return this;
    }

    /// <summary>
    /// Lists a cache that stays on the application's own client, outside the router: one the library cannot match, such as a lock or another
    /// primitive the two stacks implement differently, or a map cache with field semantics the library does not keep. The startup log names it,
    /// and asking the router for it fails with <paramref name="reason"/>.
    /// </summary>
    public RouterBuilder Exclude(string cacheName, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheName);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (_names.Contains(cacheName))
        {
            throw new ArgumentException($"Cache '{cacheName}' is registered, so it cannot also be excluded.", nameof(cacheName));
        }

        if (!_excluded.TryAdd(cacheName, reason))
        {
            throw new ArgumentException($"Cache '{cacheName}' is already excluded.", nameof(cacheName));
        }

        return this;
    }

    /// <summary>A live setting read on every call: true puts every call on the source path, and it wins over <see cref="UseLibraryWhen"/>.</summary>
    public RouterBuilder UseBypassWhen(Func<bool> flag)
    {
        BypassFlag = flag ?? throw new ArgumentNullException(nameof(flag));
        return this;
    }

    /// <summary>Names this process in the router's source identity; without it the process id and a random part are used.</summary>
    public RouterBuilder UseNodeIdentity(string processId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        if (processId.Length > 64 || !processId.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'))
        {
            throw new ArgumentException("The identity holds letters, digits, dot, dash and underscore only, at most 64 characters.", nameof(processId));
        }

        NodeIdentity = processId;
        return this;
    }

    /// <summary>The longest a source-path call runs, which bounds a mode change's waits; default 30 seconds.</summary>
    public RouterBuilder UseSourceTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        SourceTimeout = timeout;
        return this;
    }

    /// <summary>The predicate takes a group name and answers true for the library path; it is read once per call, and an exception means the source path.</summary>
    public RouterBuilder UseLibraryWhen(Func<string, bool> predicate)
    {
        LibraryPredicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
        return this;
    }

    /// <summary>The serializer that writes and reads the older client's value envelope.</summary>
    public RouterBuilder UseEnvelope(ISerializerProxy<byte[]> envelope)
    {
        Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
        return this;
    }

    /// <summary>The longest a library write can stay in flight after its caller stopped waiting, which replaces the bound derived from the connection's configuration; at most one day.</summary>
    public RouterBuilder UseLibrarySettleBound(TimeSpan bound)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bound, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bound, LibrarySettleBound.Horizon);
        SettleBound = bound;
        return this;
    }

    /// <summary>The source client's database, returned at once and with no key prefix of its own, which carries the router's node advertisement while the library is down.</summary>
    public RouterBuilder UseSourceConnection(Func<IDatabase> database)
    {
        SourceDatabase = database ?? throw new ArgumentNullException(nameof(database));
        return this;
    }
}
