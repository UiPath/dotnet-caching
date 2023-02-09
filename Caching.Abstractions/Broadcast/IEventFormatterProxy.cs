namespace UiPath.Platform.Caching.Broadcast;

public interface IEventFormatterProxy<T> where T : class, IPubSubEvent
{
    T? Decode(string body) =>
        Decode(new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(body.ToString())));

    T? Decode(ReadOnlyMemory<byte> body);

    ReadOnlyMemory<byte> Encode(T @event);

    string? EncodeAsString(T @event) =>
        Encoding.UTF8.GetString(Encode(@event).Span);
}
