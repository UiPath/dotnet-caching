namespace UiPath.Caching.Negotiation;

/// <summary>An answer that does not depend on the node list: <see cref="Always"/> for a node that writes what it is configured to, <see cref="Never"/> for one set to write JSON.</summary>
internal sealed class FixedEventWrites : IEventWrites
{
    private FixedEventWrites(bool writes) => Writes = writes;

    public static FixedEventWrites Always { get; } = new(true);

    public static FixedEventWrites Never { get; } = new(false);

    public bool Writes { get; }
}
