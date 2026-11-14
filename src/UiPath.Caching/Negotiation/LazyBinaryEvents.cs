namespace UiPath.Caching.Negotiation;

/// <summary>
/// Asks the negotiation on first use, not when the formatter is built: the topic providers that need the formatter are what the negotiation is built from.
/// </summary>
internal sealed class LazyBinaryEvents(BinaryEventSettings settings, Func<FormatNegotiation?> negotiation) : IBinaryEvents
{
    private IBinaryEvents? _inner;

    public bool Writes => (Volatile.Read(ref _inner) ?? Resolve()).Writes;

    private IBinaryEvents Resolve()
    {
        // A node set to write JSON has nothing to ask the node list, and one with no negotiation to ask has nothing that proves binary is safe.
        var resolved = settings.Write && negotiation() is { } resolvedNegotiation ? resolvedNegotiation.EventWrites(settings) : FixedBinaryEvents.Never;
        return Interlocked.CompareExchange(ref _inner, resolved, null) ?? resolved;
    }
}
