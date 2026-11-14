namespace UiPath.Caching.Negotiation;

/// <summary>Whether this node writes binary events now; events are JSON otherwise.</summary>
internal interface IBinaryEvents
{
    /// <summary>Reads one volatile reference and the injected clock, with no allocation and no call to Redis.</summary>
    bool Writes { get; }
}
