using System.Text.Json;
using CloudNative.CloudEvents.SystemTextJson;

namespace UiPath.Caching.CloudEvents;

public class CacheEventFormatterProxy(CloudEventFormatter formatter) : IEventFormatterProxy<ICacheEvent>, IEventSourceMatcherProvider
{
    private FastCloudEventCodec? _fast;

    public ICacheEvent? Decode(ReadOnlyMemory<byte> body)
    {
        if (body.IsEmpty)
        {
            return null;
        }

        if (_fast is not null && _fast.TryDecode(body.Span, out var decoded))
        {
            return decoded;
        }

        return new CacheCloudEventWrapper(formatter.DecodeStructuredModeMessage(body, null, null));
    }

    public ReadOnlyMemory<byte> Encode(ICacheEvent pubSubEvent)
    {
        if (pubSubEvent is not CacheCloudEventWrapper wrapper)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        if (_fast is not null && _fast.TryEncode(wrapper.CloudEvent, out var encoded))
        {
            return encoded;
        }

        return formatter.EncodeStructuredModeMessage(wrapper.CloudEvent, out _);
    }

    /// <summary>Matches the structured-mode <c>source</c> attribute; null unless the formatter is a JSON one.</summary>
    public IEventSourceMatcher? CreateSourceMatcher(Uri source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return formatter is JsonEventFormatter ? JsonSourceMatcher.Create("source"u8, source, text => Decode(Probe(text))) : null;
    }

    internal static CacheEventFormatterProxy CreateDefault() =>
        new(new JsonEventFormatter<CacheEventData>()) { _fast = new FastCloudEventCodec() };

    private static byte[] Probe(string source) => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
    {
        ["specversion"] = "1.0",
        ["id"] = "probe",
        ["source"] = source,
        ["type"] = "probe",
        ["datacontenttype"] = "application/json",
        ["data"] = new CacheEventData("probe"),
    });
}
