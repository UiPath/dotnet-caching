namespace UiPath.Caching.Broadcast;

public interface IEventFormatterProxy<T>
     where T : IEvent
{
    [Obsolete("Use Decode(ReadOnlyMemory<byte>); decoding from the RedisValue payload bytes avoids a UTF-16 transcode.")]
    T? Decode(string body) =>
        Decode(new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(body)));

    T? Decode(ReadOnlyMemory<byte> body);

    ReadOnlyMemory<byte> Encode(T @event);

    [Obsolete("Use Encode(T); publishing the encoded bytes directly avoids a UTF-16 transcode.")]
    string? EncodeAsString(T @event) =>
        Encoding.UTF8.GetString(Encode(@event).Span);
}
