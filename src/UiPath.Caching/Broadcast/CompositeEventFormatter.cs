using UiPath.Caching.Negotiation;

namespace UiPath.Caching.Broadcast;

/// <summary>Reads a body by its first byte, the format id of a binary event or else JSON; writes binary only while <paramref name="writes"/> says so, asked on every event.</summary>
internal sealed class CompositeEventFormatter(
    IEventFormatterProxy<ICacheEvent> json,
    IEventFormatterProxy<ICacheEvent> binary,
    IBinaryEvents writes) : IEventFormatterProxy<ICacheEvent>, IEventSourceMatcherProvider
{
    public CompositeEventFormatter(IEventFormatterProxy<ICacheEvent> json, IEventFormatterProxy<ICacheEvent> binary, bool writeBinary)
        : this(json, binary, writeBinary ? FixedBinaryEvents.Always : FixedBinaryEvents.Never)
    {
    }

    public ICacheEvent? Decode(ReadOnlyMemory<byte> body) =>
        IsBinary(body) ? binary.Decode(body) : json.Decode(body);

    public ReadOnlyMemory<byte> Encode(ICacheEvent @event) =>
        writes.Writes ? binary.Encode(@event) : json.Encode(@event);

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
