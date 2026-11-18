namespace UiPath.Caching.Migration;

public sealed class RouterBuilder
{
    private readonly List<Func<RouterContext, (string Name, Type Type, object Cache)>> _registrations = [];
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);

    internal IReadOnlyList<Func<RouterContext, (string Name, Type Type, object Cache)>> Registrations => _registrations;

    internal Func<string, bool>? LibraryPredicate { get; private set; }

    internal ISerializerProxy<byte[]>? Envelope { get; private set; }

    public RouterBuilder Add<T>(RouterCacheRegistration<T> registration)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.Group);
        ArgumentOutOfRangeException.ThrowIfLessThan(registration.LocalCap, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(registration.PendingWindow, TimeSpan.Zero);
        if (!_names.Add(registration.Name))
        {
            throw new ArgumentException($"A cache named '{registration.Name}' is already registered.", nameof(registration));
        }

        _registrations.Add(context => (
            registration.Name,
            typeof(T),
            new RoutedCache<T>(
                context.Core,
                registration,
                new LibraryPathClient<T>(context.Store, context.Connection, registration.RedisKey, context.LibrarySettleWithin),
                context.Topics,
                context.Events,
                context.Source)));
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
}
