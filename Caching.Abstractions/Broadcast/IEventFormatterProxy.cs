namespace UiPath.Platform.Caching.Broadcast;

public interface IEventFormatterProxy<T>
     where T : IEvent
{
    T? Decode(string body) =>
        Decode(new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(body)));

    T? Decode(ReadOnlyMemory<byte> body);

    ReadOnlyMemory<byte> Encode(T @event);

    string? EncodeAsString(T @event) =>
        Encoding.UTF8.GetString(Encode(@event).Span);
}
