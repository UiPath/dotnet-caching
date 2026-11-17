namespace UiPath.Caching.Broadcast;

/// <summary>Decode gets the whole body, id byte included, and returns <see langword="null"/> for one it does not own; typed properties use the <c>CacheEventPropertyNames</c> keys with their documented CLR types.</summary>
public interface IEventFormatterProxy<T>
     where T : IEvent
{
    T? Decode(ReadOnlyMemory<byte> body);

    ReadOnlyMemory<byte> Encode(T @event);
}
