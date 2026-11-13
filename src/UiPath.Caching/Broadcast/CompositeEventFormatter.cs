namespace UiPath.Caching.Broadcast;

/// <summary>Reads a body by its first byte, the format id of a binary event or else JSON; writes binary only when told to.</summary>
internal sealed class CompositeEventFormatter(
    IEventFormatterProxy<ICacheEvent> json,
    IEventFormatterProxy<ICacheEvent> binary,
    bool writeBinary) : IEventFormatterProxy<ICacheEvent>, IEventSourceMatcherProvider
{
    public ICacheEvent? Decode(ReadOnlyMemory<byte> body) =>
        IsBinary(body) ? binary.Decode(body) : json.Decode(body);

    public ReadOnlyMemory<byte> Encode(ICacheEvent @event) =>
        writeBinary ? binary.Encode(@event) : json.Encode(@event);

    /// <summary>Each side's matcher, picked by the first byte; a side without one leaves its bodies to be decoded and compared.</summary>
    public IEventSourceMatcher? CreateSourceMatcher(Uri source)
    {
        var jsonMatcher = (json as IEventSourceMatcherProvider)?.CreateSourceMatcher(source);
        var binaryMatcher = (binary as IEventSourceMatcherProvider)?.CreateSourceMatcher(source);
        return jsonMatcher is null && binaryMatcher is null ? null : new MatcherByFormat(jsonMatcher, binaryMatcher);
    }

    private static bool IsBinary(ReadOnlyMemory<byte> body) =>
        !body.IsEmpty && body.Span[0] == CacheValueFormats.MessagePack;

    private sealed class MatcherByFormat(IEventSourceMatcher? json, IEventSourceMatcher? binary) : IEventSourceMatcher
    {
        public bool IsFromSource(ReadOnlyMemory<byte> payload) =>
            (IsBinary(payload) ? binary : json)?.IsFromSource(payload) ?? false;
    }
}
