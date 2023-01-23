namespace UiPath.Platform.Caching.Broadcast;

public interface IEventFormatterProxy
{
    IClearCacheEvent? Decode(string body) =>
        Decode(new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(body.ToString())));

    IClearCacheEvent? Decode(ReadOnlyMemory<byte> body);

    ReadOnlyMemory<byte> Encode(IClearCacheEvent clearCacheEvent);

    string? EncodeAsString(IClearCacheEvent clearCacheEvent) =>
        Encoding.UTF8.GetString(Encode(clearCacheEvent).Span);
}
