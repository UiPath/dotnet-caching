namespace UiPath.Caching.Broadcast;

public interface IEventFormatterProxy<T>
     where T : IEvent
{
    T? Decode(ReadOnlyMemory<byte> body);

    ReadOnlyMemory<byte> Encode(T @event);
}
