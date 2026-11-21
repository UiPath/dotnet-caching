namespace UiPath.Caching.Negotiation;

/// <summary>Whether a serializer's format may be written now; a format that may not is written as JSON.</summary>
internal interface IEffectiveFormat
{
    /// <summary>An outside gate decides the write too, so even a custom proxy has its writes held, falling to what the proxy writes.</summary>
    bool Gated => false;

    /// <summary>Reads one volatile reference and the injected clock, with no allocation and no call to Redis.</summary>
    bool Allows(ICacheValueSerializer writer);
}
