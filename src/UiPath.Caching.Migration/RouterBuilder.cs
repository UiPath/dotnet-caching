using System.Buffers;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

public sealed class RouterBuilder
{
    private static readonly SearchValues<char> ForbiddenVersionCharacters = SearchValues.Create("@+={} ");

    private readonly List<Func<RouterCore, (string Name, Type Type, object Cache)>> _registrations = [];
    private readonly Dictionary<string, (string Group, Func<string, string> InPlaceKey)> _routed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _excluded = new(StringComparer.Ordinal);
    private (IMigrationStageSource Stages, KeyMoveBuilder Builder)? _keyMove;

    internal IReadOnlyList<Func<RouterCore, (string Name, Type Type, object Cache)>> Registrations => _registrations;

    internal IReadOnlyDictionary<string, string> Excluded => _excluded;

    internal Func<string, bool>? LibraryPredicate { get; private set; }

    internal Func<string, bool>? PinPredicate { get; private set; }

    internal ISerializerProxy<byte[]>? Envelope { get; private set; }

    internal bool InPlaceKeepsSystemFields => Envelope is null;

    internal Func<string, string?>? WriteFormatPredicate { get; private set; }

    internal Action<ICachingBuilder>? ValueSerializers { get; private set; }

    internal TimeSpan? SettleBound { get; private set; }

    internal Func<bool>? BypassFlag { get; private set; }

    internal string? NodeIdentity { get; private set; }

    internal TimeSpan? SourceTimeout { get; private set; }

    internal TimeSpan CleanupBound { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The options of the mover hosts the router runs; null for the defaults.</summary>
    internal MoverOptions? MoverOptions { get; set; }

    /// <summary>The schedule point of the mover's promotions, which tests hold steps at; null for none.</summary>
    internal ISchedulePoint? MoverSchedule { get; set; }

    internal Func<IDatabase>? SourceDatabase { get; private set; }

    internal IChannelBridge? Bridge { get; private set; }

    internal int BridgeMaxPending { get; private set; } = BridgeHost.DefaultMaxPending;

    internal KeyMoveSetup? KeyMove { get; private set; }

    internal IReadOnlyDictionary<string, (string Group, Func<string, string> InPlaceKey)> Routed => _routed;

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
        if (version is not null && (string.IsNullOrWhiteSpace(version) || version.AsSpan().IndexOfAny(ForbiddenVersionCharacters) >= 0))
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

        _routed[registration.Name] = (registration.Group, registration.RedisKey);
        _registrations.Add(core => (
            registration.Name,
            typeof(T),
            new RoutedCache<T>(
                core,
                registration,
                version)));
        return this;
    }

    /// <summary>The adapter over the older client's invalidation channel: the router publishes every key write, delete and clear on it and evicts on what it receives. At most <paramref name="maxPendingPublishes"/> adapter calls are in flight; a publish beyond that is dropped and counted.</summary>
    public RouterBuilder UseChannelBridge(IChannelBridge bridge, int maxPendingPublishes = 16)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPendingPublishes, 1);
        Bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        BridgeMaxPending = maxPendingPublishes;
        return this;
    }

    /// <summary>Lists a cache that stays on the application's own client, outside the router: one the library cannot match, such as a lock or another primitive the two stacks implement differently, or a map cache with field semantics the library does not keep. The startup log names it, and asking the router for it fails with <paramref name="reason"/>.</summary>
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

    /// <summary>A live setting that pins groups to the library path, never the source; a group is switched once a format write needs it and every node has the pin. Group names hold none of <c>+ @ = { }</c>.</summary>
    public RouterBuilder UsePinWhen(Func<string, bool> pinned)
    {
        PinPredicate = pinned ?? throw new ArgumentNullException(nameof(pinned));
        return this;
    }

    /// <summary>Optional: the serializer of the older client's value envelope, which the in-place keys hold; without it they hold the library's own format. It must never start a value with a format header byte, and a write that does fails.</summary>
    public RouterBuilder UseEnvelope(ISerializerProxy<byte[]> envelope)
    {
        Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
        return this;
    }

    /// <summary>Registers value serializers, such as MessagePack, in the router's own stack, so the stack reads them and advertises them in the node list.</summary>
    /// <remarks>Needed by <see cref="UseWriteFormatWhen"/>. The stack is separate from the application's, so a serializer registered there is not seen by the router.</remarks>
    public RouterBuilder UseValueSerializers(Action<ICachingBuilder> register)
    {
        ValueSerializers = register ?? throw new ArgumentNullException(nameof(register));
        return this;
    }

    /// <summary>A live setting read on every library write: the group's value format, <c>"messagepack"</c>, <c>"json"</c> or null for <c>"json"</c>.</summary>
    /// <remarks>
    /// <c>"json"</c> writes the older client's envelope, or the library's own JSON without one. <c>"messagepack"</c> writes MessagePack only while the group is pinned, acknowledged and switched, every node reads it, and the gate
    /// over the groups that ask for it is open; until then the group writes the envelope. Setting it back to <c>"json"</c> downgrades at once, and the group never returns to the older client.
    /// An answer that throws or is not one of these names writes the envelope. Needs <see cref="UsePinWhen"/> and <see cref="UseValueSerializers"/>.
    /// </remarks>
    public RouterBuilder UseWriteFormatWhen(Func<string, string?> format)
    {
        WriteFormatPredicate = format ?? throw new ArgumentNullException(nameof(format));
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

    /// <summary>Registers key move groups and caches with their traits and key maps; without it nothing of key move runs and nothing of it is advertised.</summary>
    public RouterBuilder UseKeyMove(IMigrationStageSource stageSource, Action<KeyMoveBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(stageSource);
        ArgumentNullException.ThrowIfNull(configure);
        if (_keyMove is not null)
        {
            throw new InvalidOperationException("UseKeyMove was already called; register every key move group and cache in one call.");
        }

        var builder = new KeyMoveBuilder();
        configure(builder);
        _keyMove = (stageSource, builder);
        return this;
    }

    internal RouterBuilder UseKeyMoveSetup(KeyMoveSetup setup)
    {
        KeyMove = setup ?? throw new ArgumentNullException(nameof(setup));
        return this;
    }

    /// <summary>Checks the key move registration against the router's and builds its setup; null when none was given.</summary>
    internal KeyMoveSetup? BuildKeyMove()
    {
        if (_keyMove is { } given)
        {
            KeyMove = given.Builder.Build(given.Stages, _routed);
        }

        return KeyMove;
    }
}
