namespace UiPath.Caching.Negotiation;

/// <summary>
/// Asks the negotiation on first use, not when the formatter is built: the topic providers that need the formatter are what the negotiation is built from.
/// </summary>
internal sealed class LazyEventWrites(EventFormatRegistry registry, Func<FormatNegotiation?> negotiation) : IEventWrites
{
    private IEventWrites? _inner;

    public bool Writes => (Volatile.Read(ref _inner) ?? Resolve()).Writes;

    private IEventWrites Resolve()
    {
        // A node set to write JSON has nothing to ask the node list, and one with no negotiation to ask has nothing that proves its event format is safe.
        var resolved = registry.Writer is not null && negotiation() is { } resolvedNegotiation ? resolvedNegotiation.EventWrites(registry) : FixedEventWrites.Never;
        return Interlocked.CompareExchange(ref _inner, resolved, null) ?? resolved;
    }
}
